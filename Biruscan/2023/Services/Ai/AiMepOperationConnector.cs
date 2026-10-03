using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Biruscan.AI.DeepLearning;

namespace Biruscan.Services.Ai
{
    /// <summary>
    /// After AI places each pipe/duct segment separately, connect them with the
    /// same operation tools the user runs (Elbow, Tee, Union, Reducer, Cross)
    /// so sacred far-end / near-end / double-anchor rules are applied.
    /// Successful fittings are taught back into the in-process weights.
    /// </summary>
    public static class AiMepOperationConnector
    {
        private enum OpKind { None, Elbow, Tee, Union, Reducer }

        public static int ConnectPlacedRuns(Document doc, List<MEPCurve> placed, List<XYZ> roiPoints, string service)
        {
            if (doc == null || placed == null || placed.Count < 2) return 0;

            var owned = new HashSet<long>(placed.Where(c => c != null).Select(c => (long)c.Id.IntegerValue));
            var ops = new MepOperationService(doc);
            var engine = new MepPointNetNeuralEngine();
            int fittings = 0;
            var usedPairs = new HashSet<string>();

            AutoTrainingManager.IsInternalTransaction = true;
            try
            {
                // Tees first (need an unsplit through-run), then elbows, then linear couplings.
                fittings += RunPass(doc, ops, engine, owned, roiPoints, service, usedPairs, OpKind.Tee);
                fittings += RunPass(doc, ops, engine, owned, roiPoints, service, usedPairs, OpKind.Elbow);
                fittings += RunPass(doc, ops, engine, owned, roiPoints, service, usedPairs, OpKind.Reducer);
                fittings += RunPass(doc, ops, engine, owned, roiPoints, service, usedPairs, OpKind.Union);
                NeuralWeightsDataset.SaveCurrentWeights();
            }
            finally
            {
                AutoTrainingManager.IsInternalTransaction = false;
            }

            return fittings;
        }

        private static int RunPass(Document doc, MepOperationService ops, MepPointNetNeuralEngine engine,
            HashSet<long> owned, List<XYZ> roiPoints, string service, HashSet<string> usedPairs, OpKind want)
        {
            int made = 0;
            for (int round = 0; round < 4; round++)
            {
                var curves = Refresh(doc, owned);
                bool progress = false;
                for (int i = 0; i < curves.Count; i++)
                {
                    for (int j = i + 1; j < curves.Count; j++)
                    {
                        MEPCurve a = curves[i], b = curves[j];
                        if (a == null || b == null || a.Id == b.Id) continue;
                        string pairKey = PairKey(a.Id.IntegerValue, b.Id.IntegerValue);
                        if (usedPairs.Contains(pairKey)) continue;

                        OpKind kind = Classify(a, b, out MEPCurve main, out MEPCurve branch);
                        if (kind != want) continue;

                        MepOperationResult result;
                        switch (kind)
                        {
                            case OpKind.Tee:
                                result = ops.ConnectTee(main, branch);
                                break;
                            case OpKind.Elbow:
                                MeetClicks(a, b, out XYZ c1, out XYZ c2);
                                result = ops.ConnectElbow(a, b, c1, c2);
                                break;
                            case OpKind.Reducer:
                                result = ops.ConnectReducer(a, b);
                                break;
                            case OpKind.Union:
                                result = ops.ConnectUnion(a, b);
                                break;
                            default:
                                continue;
                        }

                        usedPairs.Add(pairKey);
                        if (result == null || !result.Success) continue;

                        made++;
                        progress = true;
                        AdoptNearbyNewCurves(doc, owned);
                        Teach(doc, engine, result.CreatedFitting, kind, roiPoints, service);
                    }
                }
                if (!progress) break;
            }
            return made;
        }

        /// <summary>
        /// Same geometric tests the operation tools require:
        /// elbow = angled runs whose near ends meet; tee = branch end on a through-run;
        /// union/reducer = near-collinear near-ends (same vs different size).
        /// </summary>
        private static OpKind Classify(MEPCurve a, MEPCurve b, out MEPCurve main, out MEPCurve branch)
        {
            main = a;
            branch = b;
            if (!TryLine(a, out XYZ a0, out XYZ a1) || !TryLine(b, out XYZ b0, out XYZ b1))
                return OpKind.None;

            XYZ da = Norm(a1 - a0);
            XYZ db = Norm(b1 - b0);
            double dot = Math.Abs(da.DotProduct(db));
            if (!LineClosestApproach(a0, da, b0, db, out double ta, out double tb, out double gap, out _, out _))
                return OpKind.None;

            double la = a0.DistanceTo(a1);
            double lb = b0.DistanceTo(b1);
            if (la < 0.15 || lb < 0.15) return OpKind.None;

            bool aInterior = ta > 0.28 && ta < la - 0.28;
            bool bInterior = tb > 0.28 && tb < lb - 0.28;
            bool aEnd = ta < 0.40 || ta > la - 0.40 || ta < 0 || ta > la;
            bool bEnd = tb < 0.40 || tb > lb - 0.40 || tb < 0 || tb > lb;

            if (dot > 0.97 && gap < 0.55 && aEnd && bEnd)
            {
                double daMm = DiameterMm(a);
                double dbMm = DiameterMm(b);
                if (daMm > 1 && dbMm > 1 && Math.Abs(daMm - dbMm) > Math.Max(daMm, dbMm) * 0.12)
                    return OpKind.Reducer;
                return OpKind.Union;
            }

            if (dot < 0.94 && gap < 0.90)
            {
                if (aInterior && bEnd) { main = a; branch = b; return OpKind.Tee; }
                if (bInterior && aEnd) { main = b; branch = a; return OpKind.Tee; }
                if (aEnd && bEnd) return OpKind.Elbow;
            }

            return OpKind.None;
        }

        private static void Teach(Document doc, MepPointNetNeuralEngine engine, FamilyInstance fitting,
            OpKind kind, List<XYZ> roiPoints, string service)
        {
            try
            {
                string tool = kind == OpKind.Tee ? "AI Auto Tee"
                    : kind == OpKind.Elbow ? "AI Auto Elbow"
                    : kind == OpKind.Reducer ? "AI Auto Reducer"
                    : "AI Auto Union";

                XYZ center = XYZ.Zero;
                if (fitting != null)
                {
                    AutoTrainingManager.Instance.RecordFittedElement(doc, fitting.Id, service ?? "Piping", tool, roiPoints);
                    center = (fitting.Location as LocationPoint)?.Point ?? XYZ.Zero;
                    if (center.GetLength() < 1e-6)
                    {
                        double sx = 0, sy = 0, sz = 0; int n = 0;
                        if (fitting.MEPModel?.ConnectorManager != null)
                        {
                            foreach (Connector c in fitting.MEPModel.ConnectorManager.Connectors)
                            { sx += c.Origin.X; sy += c.Origin.Y; sz += c.Origin.Z; n++; }
                            if (n > 0) center = new XYZ(sx / n, sy / n, sz / n);
                        }
                    }
                }

                if (roiPoints == null || roiPoints.Count < 4) return;
                var near = new List<XYZ>();
                foreach (var p in roiPoints)
                {
                    if (p.DistanceTo(center) < 0.85) near.Add(p);
                    if (near.Count >= 400) break;
                }
                if (near.Count < 4) near = roiPoints.Take(80).ToList();

                MepSemanticClass cls = kind == OpKind.Tee ? MepSemanticClass.PipingTee
                    : kind == OpKind.Elbow ? MepSemanticClass.PipingElbow
                    : MepSemanticClass.PipingCylinder;
                engine.TrainPositiveSample(near, cls, 50f, XYZ.Zero);
            }
            catch { }
        }

        private static List<MEPCurve> Refresh(Document doc, HashSet<long> owned)
        {
            var list = new List<MEPCurve>();
            foreach (long id in owned.ToList())
            {
                var c = doc.GetElement(new ElementId((int)id)) as MEPCurve;
                if (c != null && c.IsValidObject)
                    list.Add(c);
                else
                    owned.Remove(id);
            }
            return list;
        }

        private static void AdoptNearbyNewCurves(Document doc, HashSet<long> owned)
        {
            var existing = Refresh(doc, owned);
            if (existing.Count == 0) return;
            var all = new FilteredElementCollector(doc).OfClass(typeof(MEPCurve)).WhereElementIsNotElementType();
            foreach (MEPCurve c in all.OfType<MEPCurve>())
            {
                if (owned.Contains(c.Id.IntegerValue)) continue;
                if (!TryLine(c, out XYZ p0, out XYZ p1)) continue;
                XYZ mid = (p0 + p1) * 0.5;
                foreach (var e in existing)
                {
                    if (!TryLine(e, out XYZ e0, out XYZ e1)) continue;
                    if (mid.DistanceTo(e0) < 2.2 || mid.DistanceTo(e1) < 2.2 || mid.DistanceTo((e0 + e1) * 0.5) < 2.2)
                    {
                        owned.Add(c.Id.IntegerValue);
                        break;
                    }
                }
            }
        }

        private static void MeetClicks(MEPCurve a, MEPCurve b, out XYZ c1, out XYZ c2)
        {
            c1 = c2 = XYZ.Zero;
            if (!TryLine(a, out XYZ a0, out XYZ a1) || !TryLine(b, out XYZ b0, out XYZ b1)) return;
            LineClosestApproach(a0, Norm(a1 - a0), b0, Norm(b1 - b0), out _, out _, out _, out c1, out c2);
        }

        private static bool TryLine(MEPCurve c, out XYZ p0, out XYZ p1)
        {
            p0 = p1 = XYZ.Zero;
            if (!(c?.Location is LocationCurve lc) || !(lc.Curve is Line line)) return false;
            p0 = line.GetEndPoint(0);
            p1 = line.GetEndPoint(1);
            return p0.DistanceTo(p1) > 1e-4;
        }

        private static double DiameterMm(MEPCurve c)
        {
            try
            {
                Parameter p = c.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)
                    ?? c.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM)
                    ?? c.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
                if (p != null && p.StorageType == StorageType.Double)
                    return UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.Millimeters);
            }
            catch { }
            return 0;
        }

        private static string PairKey(long a, long b) => a < b ? a + ":" + b : b + ":" + a;

        private static XYZ Norm(XYZ v)
        {
            double l = v.GetLength();
            return l < 1e-12 ? XYZ.BasisZ : v.Multiply(1.0 / l);
        }

        private static bool LineClosestApproach(XYZ p1, XYZ d1, XYZ p2, XYZ d2,
            out double t, out double s, out double gap, out XYZ on1, out XYZ on2)
        {
            t = s = 0;
            gap = double.MaxValue;
            on1 = p1; on2 = p2;
            XYZ cross = d1.CrossProduct(d2);
            double den = cross.DotProduct(cross);
            if (den < 1e-12)
            {
                XYZ diff = p2 - p1;
                t = diff.DotProduct(d1);
                s = 0;
                on1 = p1 + d1.Multiply(t);
                on2 = p2;
                gap = on1.DistanceTo(on2);
                return true;
            }
            XYZ diff2 = p2 - p1;
            t = diff2.CrossProduct(d2).DotProduct(cross) / den;
            s = diff2.CrossProduct(d1).DotProduct(cross) / den;
            on1 = p1 + d1.Multiply(t);
            on2 = p2 + d2.Multiply(s);
            gap = on1.DistanceTo(on2);
            return true;
        }
    }
}
