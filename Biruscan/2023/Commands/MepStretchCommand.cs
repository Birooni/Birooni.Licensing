using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Biruscan.UI.Views;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Biruscan.Commands
{
    /// <summary>
    /// Stretch a straight Pipe, Duct, Conduit, or Cable Tray along its axis
    /// and translate connected fittings and runs with each end so the network stays joined.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class MepStretchCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                MEPCurve mepCurve = uidoc.Selection.GetElementIds()
                    ?.Select(id => doc.GetElement(id))
                    .OfType<MEPCurve>()
                    .FirstOrDefault();

                if (mepCurve == null)
                {
                    try
                    {
                        Reference r = uidoc.Selection.PickObject(
                            ObjectType.Element,
                            new MepCurveSelectionFilter(),
                            "Pick a Pipe, Duct, Conduit, or Cable Tray to stretch. Connected fittings and runs move with each end.");
                        mepCurve = doc.GetElement(r.ElementId) as MEPCurve;
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        return Result.Cancelled;
                    }
                }

                if (mepCurve == null)
                {
                    TaskDialog.Show("Stretch", "Select a Pipe, Duct, Conduit, or Cable Tray.");
                    return Result.Cancelled;
                }

                if (!(mepCurve.Location is LocationCurve loc) || !(loc.Curve is Line line) || !line.IsBound)
                {
                    TaskDialog.Show("Stretch", "Stretch works on a straight Pipe, Duct, Conduit, or Cable Tray.");
                    return Result.Cancelled;
                }

                XYZ orig0 = line.GetEndPoint(0);
                XYZ orig1 = line.GetEndPoint(1);
                XYZ axis = orig1 - orig0;
                if (axis.GetLength() < 1e-6)
                {
                    TaskDialog.Show("Stretch", "The selected run is too short to stretch.");
                    return Result.Cancelled;
                }
                XYZ dir = axis.Normalize();
                ElementId curveId = mepCurve.Id;

                Connector c0 = FindEndConnector(mepCurve, orig0);
                Connector c1 = FindEndConnector(mepCurve, orig1);
                HashSet<ElementId> side0 = CollectConnected(c0, curveId);
                HashSet<ElementId> side1 = CollectConnected(c1, curveId);
                var both = side0.Intersect(side1).ToList();
                foreach (ElementId id in both)
                {
                    side0.Remove(id);
                    side1.Remove(id);
                }

                HashSet<ElementId> directFittings;
                List<AdjacentCurve> adjacentCurves;
                CollectPlaneNeighbors(mepCurve, orig0, orig1, out directFittings, out adjacentCurves);
                bool alongPlaneEnabled = FittingsAllowAlongPlane(doc, directFittings, dir);
                XYZ planeDir = alongPlaneEnabled
                    ? FittingPlaneDirection(mepCurve, dir, orig0, orig1, directFittings, uidoc.ActiveView)
                    : XYZ.Zero;
                var adjIds = new HashSet<ElementId>(adjacentCurves.Select(a => a.Id));
                var side0Move = new HashSet<ElementId>(side0.Where(id => !adjIds.Contains(id)));
                var side1Move = new HashSet<ElementId>(side1.Where(id => !adjIds.Contains(id)));

                ForgeTypeId lengthUnit = doc.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId();
                double fiveMetersInternal = UnitUtils.ConvertToInternalUnits(5.0, UnitTypeId.Meters);
                double maxDisplay = UnitUtils.ConvertFromInternalUnits(fiveMetersInternal, lengthUnit);
                string unitSymbol = UnitSymbol(lengthUnit);

                double lastD0 = 0;
                double lastD1 = 0;
                double lastPlane = 0;

                using (TransactionGroup tg = new TransactionGroup(doc, "Stretch"))
                {
                    tg.Start();

                    Action<double, double, double> onChanged = (end1Display, end2Display, planeDisplay) =>
                    {
                        double d0 = UnitUtils.ConvertToInternalUnits(end1Display, lengthUnit);
                        double d1 = UnitUtils.ConvertToInternalUnits(end2Display, lengthUnit);
                        double plane = UnitUtils.ConvertToInternalUnits(planeDisplay, lengthUnit);
                        double origLen = orig0.DistanceTo(orig1);
                        if (origLen + d0 + d1 < 0.02)
                            return;
                        if (Math.Abs(d0 - lastD0) < 1e-9 && Math.Abs(d1 - lastD1) < 1e-9 && Math.Abs(plane - lastPlane) < 1e-9)
                            return;
                        XYZ axisDelta0 = -dir * (d0 - lastD0);
                        XYZ axisDelta1 = dir * (d1 - lastD1);
                        XYZ planeDelta = planeDir * (plane - lastPlane);
                        using (Transaction t = new Transaction(doc, "Preview Stretch"))
                        {
                            SuppressWarnings(t);
                            t.Start();
                            try
                            {
                                if (axisDelta0.GetLength() >= 1e-9)
                                    MoveIds(doc, side0Move, axisDelta0);
                                if (axisDelta1.GetLength() >= 1e-9)
                                    MoveIds(doc, side1Move, axisDelta1);
                                if (planeDelta.GetLength() >= 1e-9)
                                    MoveIds(doc, directFittings, planeDelta);

                                try { doc.Regenerate(); } catch { }

                                XYZ p0 = orig0 - dir * d0 + planeDir * plane;
                                XYZ p1 = orig1 + dir * d1 + planeDir * plane;
                                SetCurve(doc, curveId, p0, p1);

                                foreach (AdjacentCurve adj in adjacentCurves)
                                {
                                    XYZ axisShift = adj.OnSide0 ? (-dir * d0) : (dir * d1);
                                    XYZ n0 = adj.Orig0 + axisShift;
                                    XYZ n1 = adj.Orig1 + axisShift;
                                    if (adj.End0IsNear) n0 = n0 + planeDir * plane;
                                    else n1 = n1 + planeDir * plane;
                                    SetCurve(doc, adj.Id, n0, n1);
                                }

                                t.Commit();
                                lastD0 = d0;
                                lastD1 = d1;
                                lastPlane = plane;
                                uidoc.RefreshActiveView();
                            }
                            catch
                            {
                                if (t.HasStarted()) t.RollBack();
                            }
                        }
                    };

                    var win = new MepStretchWindow(onChanged, maxDisplay, unitSymbol, alongPlaneEnabled);
                    try { new System.Windows.Interop.WindowInteropHelper(win).Owner = commandData.Application.MainWindowHandle; } catch { }

                    if (win.ShowDialog() == true)
                    {
                        tg.Assimilate();
                        return Result.Succeeded;
                    }

                    tg.RollBack();
                    return Result.Cancelled;
                }
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        private static bool FittingsAllowAlongPlane(Document doc, HashSet<ElementId> fittingIds, XYZ axis)
        {
            if (doc == null || fittingIds == null || fittingIds.Count == 0) return false;

            var normals = new List<XYZ>();
            var angles = new List<double>();
            foreach (ElementId id in fittingIds)
            {
                FamilyInstance fi = doc.GetElement(id) as FamilyInstance;
                XYZ n = FittingNormal(fi, axis);
                if (n == null || n.GetLength() < 1e-6) return false;
                normals.Add(n.Normalize());
                double ang = FittingBendAngle(fi);
                if (ang >= 0) angles.Add(ang);
            }

            const double planeDotMin = 0.985;
            for (int i = 1; i < normals.Count; i++)
            {
                if (Math.Abs(normals[0].DotProduct(normals[i])) < planeDotMin)
                    return false;
            }

            const double angleTolDeg = 2.0;
            for (int i = 1; i < angles.Count; i++)
            {
                if (Math.Abs(angles[i] - angles[0]) > angleTolDeg)
                    return false;
            }

            return true;
        }

        private static double FittingBendAngle(FamilyInstance fi)
        {
            List<Connector> cons = PhysicalConnectors(fi);
            if (cons.Count != 2) return -1;
            try
            {
                XYZ z0 = cons[0].CoordinateSystem?.BasisZ;
                XYZ z1 = cons[1].CoordinateSystem?.BasisZ;
                if (z0 == null || z1 == null || z0.GetLength() < 1e-6 || z1.GetLength() < 1e-6)
                    return -1;
                z0 = z0.Normalize();
                z1 = z1.Normalize();
                double dot = z0.DotProduct(z1);
                if (dot > 1) dot = 1;
                if (dot < -1) dot = -1;
                double deg = Math.Acos(dot) * 180.0 / Math.PI;
                if (deg > 90) deg = 180 - deg;
                return deg;
            }
            catch
            {
                return -1;
            }
        }

        private static XYZ FittingPlaneDirection(
            MEPCurve curve,
            XYZ axis,
            XYZ orig0,
            XYZ orig1,
            HashSet<ElementId> fittingIds,
            View view)
        {
            XYZ fittingNormal = TryFittingPlaneNormal(curve.Document, fittingIds, axis);
            if (fittingNormal != null && fittingNormal.GetLength() >= 1e-6)
            {
                XYZ planeDir = fittingNormal.CrossProduct(axis);
                if (planeDir.GetLength() < 1e-6)
                    planeDir = axis.CrossProduct(fittingNormal);
                if (planeDir.GetLength() >= 1e-6)
                {
                    planeDir = planeDir.Normalize();
                    XYZ hint = FittingInPlaneHint(curve, axis, fittingIds);
                    if (hint != null && planeDir.DotProduct(hint) < 0)
                        planeDir = -planeDir;
                    return planeDir;
                }
            }
            return ViewPlaneDirection(axis, view);
        }

        private static XYZ TryFittingPlaneNormal(Document doc, HashSet<ElementId> fittingIds, XYZ axis)
        {
            if (doc == null || fittingIds == null || fittingIds.Count == 0) return null;
            var normals = new List<XYZ>();
            foreach (ElementId id in fittingIds)
            {
                FamilyInstance fi = doc.GetElement(id) as FamilyInstance;
                XYZ n = FittingNormal(fi, axis);
                if (n != null && n.GetLength() >= 1e-6)
                    normals.Add(n.Normalize());
            }
            if (normals.Count == 0) return null;
            XYZ acc = normals[0];
            for (int i = 1; i < normals.Count; i++)
            {
                XYZ n = normals[i];
                if (n.DotProduct(acc) < 0) n = -n;
                acc = acc + n;
            }
            return acc.GetLength() >= 1e-6 ? acc.Normalize() : normals[0];
        }

        private static XYZ FittingNormal(FamilyInstance fi, XYZ axis)
        {
            List<Connector> cons = PhysicalConnectors(fi);
            if (cons.Count == 0) return null;

            var origins = new List<XYZ>();
            var zs = new List<XYZ>();
            foreach (Connector c in cons)
            {
                try { origins.Add(c.Origin); } catch { }
                try
                {
                    XYZ z = c.CoordinateSystem?.BasisZ;
                    if (z != null && z.GetLength() >= 1e-6)
                        zs.Add(z.Normalize());
                }
                catch { }
            }

            if (origins.Count >= 3)
            {
                XYZ nPts = (origins[1] - origins[0]).CrossProduct(origins[2] - origins[0]);
                if (nPts.GetLength() >= 1e-6) return nPts.Normalize();
            }

            double best = 0;
            XYZ bestN = null;
            for (int i = 0; i < zs.Count; i++)
            {
                for (int j = i + 1; j < zs.Count; j++)
                {
                    XYZ n = zs[i].CrossProduct(zs[j]);
                    double len = n.GetLength();
                    if (len > best)
                    {
                        best = len;
                        bestN = n;
                    }
                }
            }
            if (best >= 1e-6) return bestN.Normalize();

            if (origins.Count >= 2)
            {
                XYZ along = origins[1] - origins[0];
                XYZ n = along.CrossProduct(axis);
                if (n.GetLength() < 1e-6) n = along.CrossProduct(XYZ.BasisZ);
                if (n.GetLength() < 1e-6) n = along.CrossProduct(XYZ.BasisX);
                if (n.GetLength() >= 1e-6) return n.Normalize();
            }

            if (zs.Count >= 1 && axis != null)
            {
                XYZ n = zs[0].CrossProduct(axis);
                if (n.GetLength() >= 1e-6) return n.Normalize();
            }
            return null;
        }

        private static XYZ FittingInPlaneHint(
            MEPCurve curve,
            XYZ axis,
            HashSet<ElementId> fittingIds)
        {
            if (curve?.ConnectorManager == null || fittingIds == null) return null;
            foreach (Connector c in curve.ConnectorManager.Connectors)
            {
                if (c.ConnectorType == ConnectorType.Logical) continue;
                try
                {
                    foreach (Connector r in c.AllRefs)
                    {
                        if (r?.Owner == null || r.Owner.Id == curve.Id) continue;
                        if (!fittingIds.Contains(r.Owner.Id)) continue;
                        List<Connector> cons = PhysicalConnectors(r.Owner);
                        Connector other = cons.FirstOrDefault(oc => oc.Id != r.Id);
                        if (other == null) continue;
                        XYZ hint = other.Origin - c.Origin;
                        hint = hint - axis * hint.DotProduct(axis);
                        if (hint.GetLength() >= 1e-6) return hint.Normalize();
                    }
                }
                catch { }
            }
            return null;
        }

        private static List<Connector> PhysicalConnectors(Element e)
        {
            var list = new List<Connector>();
            ConnectorSet set = GetConnectors(e);
            if (set == null) return list;
            foreach (Connector c in set)
            {
                if (c.ConnectorType == ConnectorType.Logical) continue;
                list.Add(c);
            }
            return list;
        }

        private static XYZ ViewPlaneDirection(XYZ axis, View view)
        {
            XYZ fallback = axis.CrossProduct(XYZ.BasisZ);
            if (fallback.GetLength() < 1e-6)
                fallback = axis.CrossProduct(XYZ.BasisX);
            if (fallback.GetLength() < 1e-6)
                return XYZ.BasisY;
            fallback = fallback.Normalize();

            if (view == null) return fallback;
            XYZ viewNormal = view.ViewDirection;
            XYZ planeDir = axis.CrossProduct(viewNormal);
            if (planeDir.GetLength() < 1e-6)
            {
                try { planeDir = view.RightDirection; } catch { planeDir = fallback; }
            }
            if (planeDir.GetLength() < 1e-6) return fallback;
            planeDir = planeDir.Normalize();
            try
            {
                XYZ right = view.RightDirection;
                if (right != null && planeDir.DotProduct(right) < 0)
                    planeDir = -planeDir;
            }
            catch { }
            return planeDir;
        }

        private struct AdjacentCurve
        {
            public ElementId Id;
            public XYZ Orig0;
            public XYZ Orig1;
            public bool End0IsNear;
            public bool OnSide0;
        }

        private static void CollectPlaneNeighbors(
            MEPCurve selected,
            XYZ orig0,
            XYZ orig1,
            out HashSet<ElementId> directFittings,
            out List<AdjacentCurve> adjacentCurves)
        {
            directFittings = new HashSet<ElementId>();
            adjacentCurves = new List<AdjacentCurve>();
            var adjIds = new HashSet<ElementId>();
            if (selected?.ConnectorManager == null) return;
            ElementId skip = selected.Id;

            foreach (Connector c in selected.ConnectorManager.Connectors)
            {
                if (c.ConnectorType == ConnectorType.Logical) continue;
                try
                {
                    foreach (Connector r in c.AllRefs)
                    {
                        if (r?.Owner == null || r.Owner.Id == skip) continue;
                        if (r.Owner is FamilyInstance)
                            directFittings.Add(r.Owner.Id);
                        else if (r.Owner is MEPCurve)
                            TryAddAdjacent(r.Owner as MEPCurve, orig0, orig1, skip, side0Hint: c.Origin.DistanceTo(orig0) <= c.Origin.DistanceTo(orig1), adjIds, adjacentCurves);
                    }
                }
                catch { }
            }

            foreach (ElementId fitId in directFittings.ToList())
            {
                Element fit = selected.Document.GetElement(fitId);
                ConnectorSet set = GetConnectors(fit);
                if (set == null) continue;
                bool onSide0 = true;
                try
                {
                    Location loc = fit.Location;
                    XYZ fitPt = null;
                    if (fit is FamilyInstance fi)
                        fitPt = (fi.Location as LocationPoint)?.Point;
                    if (fitPt != null)
                        onSide0 = fitPt.DistanceTo(orig0) <= fitPt.DistanceTo(orig1);
                }
                catch { }
                foreach (Connector oc in set)
                {
                    if (oc.ConnectorType == ConnectorType.Logical || !oc.IsConnected) continue;
                    try
                    {
                        foreach (Connector r in oc.AllRefs)
                        {
                            if (r?.Owner == null || r.Owner.Id == skip || r.Owner.Id == fitId) continue;
                            if (r.Owner is MEPCurve)
                                TryAddAdjacent(r.Owner as MEPCurve, orig0, orig1, skip, onSide0, adjIds, adjacentCurves);
                        }
                    }
                    catch { }
                }
            }
        }

        private static void TryAddAdjacent(
            MEPCurve curve,
            XYZ orig0,
            XYZ orig1,
            ElementId skip,
            bool side0Hint,
            HashSet<ElementId> adjIds,
            List<AdjacentCurve> adjacentCurves)
        {
            if (curve == null || curve.Id == skip) return;
            if (!adjIds.Add(curve.Id)) return;
            if (!(curve.Location is LocationCurve lc) || !(lc.Curve is Line line) || !line.IsBound) return;
            XYZ a = line.GetEndPoint(0);
            XYZ b = line.GetEndPoint(1);
            double aNear = Math.Min(a.DistanceTo(orig0), a.DistanceTo(orig1));
            double bNear = Math.Min(b.DistanceTo(orig0), b.DistanceTo(orig1));
            adjacentCurves.Add(new AdjacentCurve
            {
                Id = curve.Id,
                Orig0 = a,
                Orig1 = b,
                End0IsNear = aNear <= bNear,
                OnSide0 = side0Hint
            });
        }

        private static void SetCurve(Document doc, ElementId id, XYZ p0, XYZ p1)
        {
            if (p0 == null || p1 == null || p0.DistanceTo(p1) < 0.02) return;
            MEPCurve live = doc.GetElement(id) as MEPCurve;
            if (!(live?.Location is LocationCurve liveLoc)) return;
            try
            {
                Curve existing = liveLoc.Curve;
                if (existing != null && existing.IsBound)
                {
                    XYZ a = existing.GetEndPoint(0);
                    XYZ b = existing.GetEndPoint(1);
                    if (a.DistanceTo(p0) < 1e-7 && b.DistanceTo(p1) < 1e-7)
                        return;
                    XYZ oldDir = b - a;
                    XYZ newDir = p1 - p0;
                    if (oldDir.GetLength() >= 1e-9 && newDir.GetLength() >= 1e-9 &&
                        oldDir.DotProduct(newDir) < 0)
                    {
                        XYZ tmp = p0;
                        p0 = p1;
                        p1 = tmp;
                    }
                    if (a.DistanceTo(p0) < 1e-7 && b.DistanceTo(p1) < 1e-7)
                        return;
                }
                liveLoc.Curve = Line.CreateBound(p0, p1);
            }
            catch { }
        }

        private static Connector FindEndConnector(MEPCurve curve, XYZ pt)
        {
            if (curve?.ConnectorManager == null) return null;
            Connector best = null;
            double bestD = double.MaxValue;
            foreach (Connector c in curve.ConnectorManager.Connectors)
            {
                if (c.ConnectorType == ConnectorType.Logical) continue;
                double d = c.Origin.DistanceTo(pt);
                if (d < bestD)
                {
                    bestD = d;
                    best = c;
                }
            }
            return best;
        }

        private static HashSet<ElementId> CollectConnected(Connector start, ElementId skip)
        {
            var acc = new HashSet<ElementId>();
            if (start == null) return acc;

            var q = new Queue<Connector>();
            EnqueueRefs(start, skip, acc, q);

            int guard = 0;
            while (q.Count > 0 && guard++ < 4000)
            {
                Connector c = q.Dequeue();
                Element owner = c?.Owner;
                if (owner == null || owner.Id == skip) continue;
                if (!acc.Add(owner.Id)) continue;

                ConnectorSet set = GetConnectors(owner);
                if (set == null) continue;
                foreach (Connector oc in set)
                {
                    if (oc.ConnectorType == ConnectorType.Logical || !oc.IsConnected) continue;
                    EnqueueRefs(oc, skip, acc, q);
                }
            }
            return acc;
        }

        private static void EnqueueRefs(Connector from, ElementId skip, HashSet<ElementId> acc, Queue<Connector> q)
        {
            if (from == null || !from.IsConnected) return;
            try
            {
                foreach (Connector r in from.AllRefs)
                {
                    if (r?.Owner == null) continue;
                    if (r.Owner.Id == skip) continue;
                    if (from.Owner != null && r.Owner.Id == from.Owner.Id) continue;
                    if (acc.Contains(r.Owner.Id)) continue;
                    q.Enqueue(r);
                }
            }
            catch { }
        }

        private static ConnectorSet GetConnectors(Element e)
        {
            if (e is MEPCurve mc) return mc.ConnectorManager?.Connectors;
            if (e is FamilyInstance fi) return fi.MEPModel?.ConnectorManager?.Connectors;
            return null;
        }

        private static void MoveIds(Document doc, HashSet<ElementId> ids, XYZ delta)
        {
            if (ids == null || ids.Count == 0 || delta == null || delta.GetLength() < 1e-9) return;
            foreach (ElementId id in ids)
            {
                try
                {
                    Element e = doc.GetElement(id);
                    if (e == null || e.Pinned) continue;
                    ElementTransformUtils.MoveElement(doc, id, delta);
                }
                catch { }
            }
        }

        private static void SuppressWarnings(Transaction t)
        {
            if (t == null) return;
            FailureHandlingOptions opts = t.GetFailureHandlingOptions();
            opts.SetFailuresPreprocessor(new KeepConnectedSilencer());
            opts.SetClearAfterRollback(true);
            opts.SetForcedModalHandling(true);
            t.SetFailureHandlingOptions(opts);
        }

        private class KeepConnectedSilencer : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                IList<FailureMessageAccessor> fails = accessor.GetFailureMessages();
                if (fails == null || fails.Count == 0)
                    return FailureProcessingResult.Continue;

                foreach (FailureMessageAccessor f in fails)
                {
                    try
                    {
                        if (f.GetSeverity() == FailureSeverity.Warning)
                        {
                            accessor.DeleteWarning(f);
                            continue;
                        }

                        FailureDefinitionId fid = f.GetFailureDefinitionId();
                        string text = "";
                        try { text = f.GetDescriptionText() ?? ""; } catch { }

                        bool willDelete =
                            fid == BuiltInFailures.ConnectorFailures.DisconnectedElements
                            || fid == BuiltInFailures.ConnectorFailures.ElementsAreDisconnected
                            || text.IndexOf("will be deleted", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (willDelete)
                        {
                            if (f.HasResolutionOfType(FailureResolutionType.SkipElements))
                            {
                                f.SetCurrentResolutionType(FailureResolutionType.SkipElements);
                                accessor.ResolveFailure(f);
                            }
                            continue;
                        }

                        bool needDisconnect =
                            fid == BuiltInFailures.ConnectorFailures.ElementsNeedToBeDisconnected
                            || fid == BuiltInFailures.ConnectorFailures.NetworkValidityErrorWasDisconnected
                            || text.IndexOf("no longer keep the connectivity", StringComparison.OrdinalIgnoreCase) >= 0
                            || text.IndexOf("disconnect from the network", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (needDisconnect && f.HasResolutionOfType(FailureResolutionType.DetachElements))
                        {
                            f.SetCurrentResolutionType(FailureResolutionType.DetachElements);
                            accessor.ResolveFailure(f);
                        }
                    }
                    catch { }
                }

                return FailureProcessingResult.Continue;
            }
        }

        private static string UnitSymbol(ForgeTypeId u)
        {
            try
            {
                string id = u.TypeId;
                if (id == UnitTypeId.Millimeters.TypeId) return "mm";
                if (id == UnitTypeId.Centimeters.TypeId) return "cm";
                if (id == UnitTypeId.Decimeters.TypeId) return "dm";
                if (id == UnitTypeId.Meters.TypeId) return "m";
                if (id == UnitTypeId.Inches.TypeId) return "in";
                if (id == UnitTypeId.Feet.TypeId || id == UnitTypeId.FeetFractionalInches.TypeId) return "ft";
                return LabelUtils.GetLabelForUnit(u);
            }
            catch
            {
                return "";
            }
        }
    }
}
