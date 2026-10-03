using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Biruscan.Services;
using Biruscan.Services.Ai;
using Biruscan.UI.Ai;

namespace Biruscan.Commands
{
    /// <summary>
    /// Pipeline mode for the shared AI/Fit MEP-generation command.
    /// </summary>
    public enum AiPipelineMode
    {
        Fit,            // /infer-mep-fit  (geometric fit — "Fit Generate MEP")
        Custom,         // /infer-mep
        Combined,       // Dual-Engine Fusion (Standard Ensemble + Active Learning Weights)
        Open3d,         // /infer-mep-open3d
        TrainedHybrid,  // /infer-mep-hybrid ("Generate AI (Trained)")
        TrainedPureDl,
        StandardAi
    }

    /// <summary>
    /// "AI Generate MEP" — unified entry point for Standard AI, Trained AI, and Combined Dual-Pipeline AI.
    /// Follows the Birooni workflow: configure 3-tab dialog
    /// (Standard AI / Trained Model / Combined Fusion) → AI inference → place chosen MEP elements.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class AiGenerateMepCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
            => ExecuteInternal(commandData, ref message, AiPipelineMode.Combined);

        public static Result ExecuteInternal(ExternalCommandData commandData, ref string message, AiPipelineMode initialMode)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;
            string toolName = initialMode == AiPipelineMode.Fit ? "Generate MEP" : "AI Generate MEP";

            try
            {
                var pcService = new PointCloudService(doc);
                if (pcService.GetFirstPointCloudInstance() == null)
                {
                    TaskDialog.Show(toolName, "No point cloud found. Load a point cloud first.");
                    return Result.Cancelled;
                }

                MepDialogMode dialogMode = initialMode == AiPipelineMode.Fit ? MepDialogMode.FitGenerate
                    : (initialMode == AiPipelineMode.TrainedHybrid || initialMode == AiPipelineMode.TrainedPureDl)
                        ? MepDialogMode.TrainedAi : MepDialogMode.AiGenerate;
                var dlg = new AiGenerateMepWindow(doc, dialogMode);
                try { new System.Windows.Interop.WindowInteropHelper(dlg).Owner = commandData.Application.MainWindowHandle; }
                catch { }
                if (dlg.ShowDialog() != true)
                    return Result.Cancelled;

                AiPipelineMode mode = dlg.SelectedPipelineMode;

                if (dlg.SelectedPrimaryTypeId == ElementId.InvalidElementId)
                {
                    TaskDialog.Show(toolName,
                        $"No '{dlg.SelectedService}' types exist in this project. Load a {dlg.SelectedService} family first.");
                    return Result.Cancelled;
                }

                string edgePipeline = mode == AiPipelineMode.Fit ? "fit"
                    : mode == AiPipelineMode.TrainedPureDl ? "trained_pure"
                    : (mode == AiPipelineMode.TrainedHybrid) ? "trained"
                    : mode == AiPipelineMode.Combined ? "combined"
                    : "standard";
                var edge = new EdgewiseMepExtractor().Extract(doc, dlg.SelectedService, dlg.DiameterOverrideMm, edgePipeline);
                List<BirooniAiElement> elements = ApplyLearnedWeights(
                    edge.Elements, edge.WorldPoints, dlg.SelectedService, mode);
                string pipelineNote = edge.Note + $" · {edge.RawPoints} pts · {NeuralWeightLabel(mode)}";

                var segs = elements
                    .Where(e => e.Start != null && e.End != null && e.Start.Length == 3 && e.End.Length == 3)
                    .ToList();
                if (segs.Count == 0)
                {
                    TaskDialog.Show(toolName,
                        $"No {dlg.SelectedService} runs found.\n\n" +
                        "Tips:\n" +
                        (initialMode == AiPipelineMode.Fit
                            ? "• No clip is required — Edgewise tiles the whole point cloud and fits pipes locally.\n" +
                              "• Optional clip still works for a tighter region.\n" +
                              "• Use a 3D isometric view showing the curved pipe face (half/quarter scans are supported).\n" +
                              "• Fit is geometric only (no AI weights)."
                            : "• No clip is required — Edgewise scans the whole point cloud.\n" +
                              "• Optional clip still works for a tighter region.\n" +
                              "• Use a 3D isometric view showing the curved pipe face (half/quarter scans are supported).\n" +
                              "• Train AI on a few modeled pipes, then run Trained / Combined."));
                    return Result.Cancelled;
                }

                // Native and Biruscan.AI use world coordinates directly
                XYZ shift = XYZ.Zero;

                List<XYZ> roiWorld = edge.WorldPoints != null && edge.WorldPoints.Count > 2500
                    ? DownsampleXyz(edge.WorldPoints, 2500)
                    : (edge.WorldPoints ?? new List<XYZ>());
                AutoTrainingManager.IsInternalTransaction = true;
                int placed;
                try
                {
                    placed = PlaceElements(doc, dlg.SelectedService, elements, shift,
                        dlg.SelectedPrimaryTypeId, dlg.SelectedSystemTypeId, dlg.SelectedLevelId,
                        dlg.DiameterOverrideMm, dlg.GenerateWithoutFittings, roiWorld, out string placeDiag);
                    TaskDialog.Show(toolName,
                        $"{pipelineNote}\nService: {dlg.SelectedService} · Type: {dlg.SelectedPrimaryTypeName}\n" +
                        $"Detected {segs.Count} run(s); placed {placed}.\n\n{placeDiag}");
                }
                finally
                {
                    AutoTrainingManager.IsInternalTransaction = false;
                }
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                TaskDialog.Show(toolName + " — Error", ex.Message);
                return Result.Failed;
            }
        }

        // ---- helpers ----

        private static string ServiceKey(string service)
            => service == "Cable Tray" ? "CableTray" : service;

        private static string NeuralWeightLabel(AiPipelineMode mode)
        {
            switch (mode)
            {
                case AiPipelineMode.Fit: return "geometric only";
                case AiPipelineMode.TrainedHybrid:
                case AiPipelineMode.TrainedPureDl:
                    return "trained weights as wall rejector";
                case AiPipelineMode.Combined:
                    return "standard+trained wall rejector";
                default:
                    return "pretrained weights as wall rejector";
            }
        }

        private static List<XYZ> DownsampleXyz(List<XYZ> pts, int target)
        {
            if (pts == null || pts.Count <= target) return pts ?? new List<XYZ>();
            var result = new List<XYZ>(target);
            double step = (double)pts.Count / target;
            for (double i = 0; i < pts.Count; i += step)
                result.Add(pts[(int)i]);
            return result;
        }

        /// <summary>
        /// Geometric Edgewise finds cylinders; learned weights only drop wall/tray false positives.
        /// Fit = geometry only. Standard = pretrained head. Trained = live fine-tuned head.
        /// Combined = drop only when both heads agree it is a wall.
        /// </summary>
        private static List<BirooniAiElement> ApplyLearnedWeights(
            List<BirooniAiElement> elems, List<XYZ> cloud, string service, AiPipelineMode mode)
        {
            if (elems == null || elems.Count == 0) return elems ?? new List<BirooniAiElement>();
            if (mode == AiPipelineMode.Fit) return elems;
            if (cloud == null || cloud.Count < 20) return elems;

            bool useFine = mode == AiPipelineMode.TrainedHybrid
                || mode == AiPipelineMode.TrainedPureDl
                || mode == AiPipelineMode.Combined;
            bool combined = mode == AiPipelineMode.Combined;
            var kept = new List<BirooniAiElement>(elems.Count);

            foreach (var e in elems)
            {
                if (e.Start == null || e.End == null || e.Start.Length != 3 || e.End.Length != 3)
                    continue;
                List<XYZ> near = GatherNearRun(e, cloud, 80);
                if (near.Count < 8)
                {
                    kept.Add(e);
                    continue;
                }

                XYZ s = new XYZ(e.Start[0], e.Start[1], e.Start[2]);
                XYZ en = new XYZ(e.End[0], e.End[1], e.End[2]);
                double r = 0.15;
                if (e.DiameterMm.HasValue && e.DiameterMm.Value > 0)
                    r = e.DiameterMm.Value / 304.8 * 0.5;
                bool vertical = (en - s).GetLength() > 1e-6 && Math.Abs((en - s).Normalize().Z) > 0.82;
                if (vertical)
                {
                    if (GeometricMepFitService.IsWallPaintedVertical(s, en, r, cloud))
                        continue;
                    kept.Add(e);
                    continue;
                }
                if (GeometricMepFitService.IsWallEmbedded(s, en, r, cloud))
                    continue;
                // Geometry already accepted this as a hollow pipe shell (TOP-view L-runs
                // look planar to the net). Do not drop it as wall/tray clutter.
                if (GeometricMepFitService.LooksLikePipeShell(s, en, r, near, lenient: true))
                {
                    kept.Add(e);
                    continue;
                }

                float[] feat = Biruscan.AI.DeepLearning.MepPointNetNeuralEngine.ExtractClusterFeatures(near);
                Biruscan.AI.DeepLearning.NeuralWeightsDataset.Classify(feat, useFine, out int cls, out float conf);
                bool liveWall = cls == (int)Biruscan.AI.DeepLearning.MepSemanticClass.WallStructureClutter;

                if (combined)
                {
                    Biruscan.AI.DeepLearning.NeuralWeightsDataset.Classify(feat, false, out int clsB, out float confB);
                    bool baseWall = clsB == (int)Biruscan.AI.DeepLearning.MepSemanticClass.WallStructureClutter && confB >= 0.50f;
                    bool fineWall = liveWall && conf >= 0.50f;
                    if (baseWall && fineWall) continue;
                    kept.Add(e);
                    continue;
                }

                float wallCut = useFine ? 0.42f : 0.55f;
                if (liveWall && conf >= wallCut) continue;
                kept.Add(e);
            }
            return kept;
        }

        private static List<XYZ> GatherNearRun(BirooniAiElement e, List<XYZ> cloud, int cap)
        {
            XYZ s = new XYZ(e.Start[0], e.Start[1], e.Start[2]);
            XYZ en = new XYZ(e.End[0], e.End[1], e.End[2]);
            XYZ axis = en - s;
            double len = axis.GetLength();
            if (len < 1e-6) return new List<XYZ>();
            axis = axis.Normalize();
            double r = 0.15;
            if (e.DiameterMm.HasValue && e.DiameterMm.Value > 0)
                r = e.DiameterMm.Value / 304.8 * 0.5;
            double band = Math.Max(0.25, r * 1.6);
            var near = new List<XYZ>();
            foreach (XYZ p in cloud)
            {
                XYZ rel = p - s;
                double t = rel.DotProduct(axis);
                if (t < -0.2 || t > len + 0.2) continue;
                if ((rel - axis.Multiply(t)).GetLength() > band) continue;
                near.Add(p);
                if (near.Count >= cap) break;
            }
            return near;
        }

        private static XYZ Centroid(List<XYZ> pts)
        {
            double x = 0, y = 0, z = 0;
            foreach (var p in pts) { x += p.X; y += p.Y; z += p.Z; }
            return new XYZ(x / pts.Count, y / pts.Count, z / pts.Count);
        }

        internal static int PlaceElements(Document doc, string service, List<BirooniAiElement> allElems, XYZ shift,
            ElementId primaryTypeId, ElementId systemTypeId, ElementId levelId, double? diaOverrideMm, bool generateWithoutFittings, List<XYZ> roiPoints, out string diag)
        {
            diag = "";
            if (levelId == ElementId.InvalidElementId)
            {
                var lvl = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault();
                if (lvl == null) { diag = "No Level in the project - can't place."; return 0; }
                levelId = lvl.Id;
            }

            var segs = allElems.Where(e => e.Start != null && e.End != null && e.Start.Length == 3 && e.End.Length == 3).ToList();
            int placed = 0, designLimit = 0, fittings = 0;
            var placedPipes = new List<Pipe>();

            using (Transaction t = new Transaction(doc, "Generate MEP"))
            {
                MepOperationService.AllowNetworkDisconnects(t);
                t.Start();
                foreach (var e in segs)
                {
                    XYZ s = new XYZ(e.Start[0] + shift.X, e.Start[1] + shift.Y, e.Start[2] + shift.Z);
                    XYZ en = new XYZ(e.End[0] + shift.X, e.End[1] + shift.Y, e.End[2] + shift.Z);
                    if (s.DistanceTo(en) < 0.1) continue;

                    double diaMm = diaOverrideMm ?? (e.DiameterMm ?? 0);
                    double diaFt = diaMm > 0 ? diaMm / 304.8 : 0;

                    double widthMm = e.WidthMm ?? diaMm;
                    double heightMm = e.HeightMm ?? diaMm;
                    double widthFt = widthMm > 0 ? widthMm / 304.8 : diaFt;
                    double heightFt = heightMm > 0 ? heightMm / 304.8 : diaFt;

                    try
                    {
                        Element created = null;
                        switch (service)
                        {
                            case "Piping":
                                var pipe = Pipe.Create(doc, systemTypeId, primaryTypeId, levelId, s, en);
                                SetParam(pipe, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM, diaFt);
                                MepLog(doc, pipe, diaMm, diaFt);
                                placedPipes.Add(pipe); created = pipe;
                                break;
                            case "Duct":
                                var duct = Duct.Create(doc, systemTypeId, primaryTypeId, levelId, s, en);
                                var dParam = duct.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
                                if (dParam != null && !dParam.IsReadOnly) dParam.Set(diaFt);
                                var wParam = duct.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                                if (wParam != null && !wParam.IsReadOnly) wParam.Set(widthFt);
                                var hParam = duct.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
                                if (hParam != null && !hParam.IsReadOnly) hParam.Set(heightFt);
                                created = duct;
                                break;
                            case "Conduit":
                                var con = Conduit.Create(doc, primaryTypeId, s, en, levelId);
                                SetParam(con, BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM, diaFt);
                                created = con;
                                break;
                            case "Cable Tray":
                                var tray = CableTray.Create(doc, primaryTypeId, s, en, levelId);
                                var twParam = tray.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
                                if (twParam != null && !twParam.IsReadOnly) twParam.Set(widthFt);
                                var thParam = tray.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
                                if (thParam != null && !thParam.IsReadOnly) thParam.Set(heightFt);
                                created = tray;
                                break;
                        }
                        if (created != null)
                        {
                            placed++;
                            try { AutoTrainingManager.Instance.RecordFittedElement(doc, created.Id, service, "AI Generate MEP", roiPoints); } catch { }
                        }
                    }
                    catch (Autodesk.Revit.Exceptions.ArgumentException) { designLimit++; }
                    catch { }
                }

                if (service == "Piping" && placedPipes.Count > 0 && !generateWithoutFittings)
                {
                    try
                    {
                        doc.Regenerate();
                        fittings += CreateJunctionFittings(doc, placedPipes, allElems, shift);
                        fittings += CreateAutoElbows(doc, placedPipes);
                        fittings += CreateAutoTees(doc, placedPipes);
                    }
                    catch { }
                }

                t.Commit();
            }

            if (designLimit > 0)
                diag = $"{designLimit} run(s) could not be placed — the cloud is far from the project origin " +
                       "(beyond Revit's ~20-mile geometry limit). Near-origin clouds place fine.";
            else
                diag = $"Fittings placed: {fittings}.";
            return placed;
        }

        // ---- fitting placement (ported from Birooni) ----

        private static int MinConnectorsFor(string kind)
        {
            switch (kind) { case "cross": return 4; case "tee": return 3; default: return 2; }
        }

        private static int CreateJunctionFittings(Document doc, List<Pipe> pipes, List<BirooniAiElement> allElems, XYZ shift)
        {
            const double tol = 0.65; // ~200 mm for tee/cross junction centers
            int Priority(string k)
            {
                switch (k) { case "cross": return 0; case "tee": return 1; case "reducer": return 2; case "union": return 3; case "elbow": return 4; default: return 99; }
            }

            var juncs = allElems
                .Where(e => e.Center != null && e.Center.Length == 3 && !string.IsNullOrEmpty(e.Kind)
                            && !e.Kind.Equals("pipe", StringComparison.OrdinalIgnoreCase)
                            && !e.Kind.Equals("endcap", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => Priority(e.Kind.ToLowerInvariant()))
                .ToList();

            int placed = 0;
            foreach (var el in juncs)
            {
                string kind = el.Kind.ToLowerInvariant();
                XYZ center = new XYZ(el.Center[0] + shift.X, el.Center[1] + shift.Y, el.Center[2] + shift.Z);
                int needed = MinConnectorsFor(kind);

                var candidates = new List<(Connector c, double d)>();
                foreach (var pipe in pipes)
                {
                    if (pipe?.ConnectorManager == null) continue;
                    foreach (Connector c in pipe.ConnectorManager.Connectors)
                    {
                        try
                        {
                            if (c.IsConnected) continue;
                            double d = c.Origin.DistanceTo(center);
                            if (d < tol) candidates.Add((c, d));
                        }
                        catch { }
                    }
                }
                candidates.Sort((a, b) => a.d.CompareTo(b.d));

                var nearby = new List<Connector>();
                var usedPipes = new HashSet<ElementId>();
                foreach (var (c, _) in candidates)
                {
                    if (usedPipes.Contains(c.Owner.Id)) continue;
                    nearby.Add(c); usedPipes.Add(c.Owner.Id);
                    if (nearby.Count >= needed) break;
                }
                if (nearby.Count == 0) continue;

                try
                {
                    FamilyInstance f = null;
                    switch (kind)
                    {
                        case "elbow":
                            if (nearby.Count >= 2) f = doc.Create.NewElbowFitting(nearby[0], nearby[1]);
                            break;
                        case "tee":
                            if (nearby.Count >= 3)
                            {
                                Connector branch = PickBranchConnector(nearby);
                                var mains = new List<Connector>(nearby); mains.Remove(branch);
                                f = doc.Create.NewTeeFitting(mains[0], mains[1], branch);
                            }
                            else if (nearby.Count == 1)
                            {
                                f = TryBranchOffThroughPipe(doc, pipes, center, nearby[0]);
                            }
                            break;
                        case "cross":
                            if (nearby.Count >= 4) f = doc.Create.NewCrossFitting(nearby[0], nearby[1], nearby[2], nearby[3]);
                            break;
                        case "reducer":
                            if (nearby.Count >= 2) f = doc.Create.NewTransitionFitting(nearby[0], nearby[1]);
                            break;
                        case "union":
                            if (nearby.Count >= 2) f = doc.Create.NewUnionFitting(nearby[0], nearby[1]);
                            break;
                    }
                    if (f != null) placed++;
                }
                catch { }
            }
            return placed;
        }

        /// <summary>Tee where the main pipe runs THROUGH the branch point: break it then tee.</summary>
        private static FamilyInstance TryBranchOffThroughPipe(Document doc, List<Pipe> pipes, XYZ center, Connector branchConn)
        {
            MEPCurve main = FindThroughPipeAt(pipes, center, branchConn);
            if (main == null) return null;
            try { return doc.Create.NewTakeoffFitting(branchConn, main); } catch { }
            try
            {
                ElementId newId = PlumbingUtils.BreakCurve(doc, main.Id, center);
                if (newId == ElementId.InvalidElementId) return null;
                Pipe newSeg = doc.GetElement(newId) as Pipe;
                if (newSeg != null) pipes.Add(newSeg);
                doc.Regenerate();
                Connector c1 = FindOpenConnectorNearPoint(main.ConnectorManager, center, 0.5);
                Connector c2 = newSeg != null ? FindOpenConnectorNearPoint(newSeg.ConnectorManager, center, 0.5) : null;
                if (c1 != null && c2 != null && !c1.IsConnected && !c2.IsConnected)
                    return doc.Create.NewTeeFitting(c1, c2, branchConn);
            }
            catch { }
            return null;
        }

        private static MEPCurve FindThroughPipeAt(List<Pipe> pipes, XYZ center, Connector branchConn)
        {
            XYZ branchDir = branchConn.CoordinateSystem.BasisZ.Normalize();
            ElementId branchPipeId = branchConn.Owner.Id;
            foreach (var pipe in pipes)
            {
                if (pipe == null || pipe.Id == branchPipeId) continue;
                if (!(pipe.Location is LocationCurve lc) || !(lc.Curve is Line line)) continue;
                XYZ p0 = line.GetEndPoint(0), p1 = line.GetEndPoint(1);
                XYZ dir = (p1 - p0).Normalize();
                if (Math.Abs(dir.DotProduct(branchDir)) > 0.95) continue;
                XYZ d = center - p0;
                double tproj = d.DotProduct(dir);
                double len = p0.DistanceTo(p1);
                if (tproj < 0.1 || tproj > len - 0.1) continue;
                if ((p0 + dir * tproj).DistanceTo(center) > 0.5) continue;
                return pipe;
            }
            return null;
        }

        private static Connector PickBranchConnector(List<Connector> conns)
        {
            Connector best = conns[0];
            double bestAlign = -1.0;
            for (int i = 0; i < conns.Count; i++)
            {
                var others = new List<Connector>(conns); others.RemoveAt(i);
                if (others.Count < 2) continue;
                double align = Math.Abs(others[0].CoordinateSystem.BasisZ.DotProduct(others[1].CoordinateSystem.BasisZ));
                if (align > bestAlign) { bestAlign = align; best = conns[i]; }
            }
            return best;
        }

        private static Connector FindOpenConnectorNearPoint(ConnectorManager cm, XYZ point, double tolFt)
        {
            Connector best = null; double bestDist = double.MaxValue;
            foreach (Connector c in cm.Connectors)
            {
                if (c.IsConnected) continue;
                double d = c.Origin.DistanceTo(point);
                if (d < tolFt && d < bestDist) { bestDist = d; best = c; }
            }
            return best;
        }

        private static int CreateAutoElbows(Document doc, List<Pipe> pipes)
        {
            var open = CollectOpenConnectors(pipes);
            var fitted = new HashSet<int>();
            int placed = 0;
            for (int i = 0; i < open.Count; i++)
            {
                if (fitted.Contains(i)) continue;
                for (int j = i + 1; j < open.Count; j++)
                {
                    if (fitted.Contains(j)) continue;
                    var (ci, pi) = open[i];
                    var (cj, pj) = open[j];
                    try
                    {
                        if (pi == pj || ci.IsConnected || cj.IsConnected) continue;
                        double gap = ci.Origin.DistanceTo(cj.Origin);
                        if (gap > 0.55) continue;
                        XYZ di = ci.CoordinateSystem.BasisZ;
                        XYZ dj = cj.CoordinateSystem.BasisZ;
                        if (Math.Abs(di.DotProduct(dj)) > 0.94) continue;
                    }
                    catch { continue; }

                    if (TryPlaceElbow(doc, ci, cj))
                    {
                        fitted.Add(i);
                        fitted.Add(j);
                        placed++;
                    }
                }
            }
            return placed;
        }

        private static int CreateAutoTees(Document doc, List<Pipe> pipes)
        {
            var open = CollectOpenConnectors(pipes);
            var used = new HashSet<int>();
            int placed = 0;
            for (int i = 0; i < open.Count; i++)
            {
                if (used.Contains(i)) continue;
                var cluster = new List<int> { i };
                XYZ origin = open[i].c.Origin;
                for (int j = i + 1; j < open.Count; j++)
                {
                    if (used.Contains(j)) continue;
                    try
                    {
                        if (open[j].c.IsConnected) continue;
                        if (origin.DistanceTo(open[j].c.Origin) > 0.50) continue;
                    }
                    catch { continue; }
                    cluster.Add(j);
                }
                if (cluster.Count < 3) continue;
                var ids = new HashSet<ElementId>();
                var conns = new List<Connector>();
                foreach (int k in cluster)
                {
                    if (!ids.Add(open[k].pid)) continue;
                    conns.Add(open[k].c);
                    if (conns.Count == 3) break;
                }
                if (conns.Count < 3) continue;
                try
                {
                    Connector branch = PickBranchConnector(conns);
                    var mains = new List<Connector>(conns);
                    mains.Remove(branch);
                    FamilyInstance f = doc.Create.NewTeeFitting(mains[0], mains[1], branch);
                    if (f != null)
                    {
                        foreach (int k in cluster) used.Add(k);
                        placed++;
                    }
                }
                catch { }
            }
            return placed;
        }

        private static List<(Connector c, ElementId pid)> CollectOpenConnectors(List<Pipe> pipes)
        {
            var open = new List<(Connector c, ElementId pid)>();
            foreach (var pipe in pipes)
            {
                if (pipe?.ConnectorManager == null) continue;
                foreach (Connector c in pipe.ConnectorManager.Connectors)
                {
                    try { if (!c.IsConnected) open.Add((c, pipe.Id)); } catch { }
                }
            }
            return open;
        }

        private static bool TryPlaceElbow(Document doc, Connector ci, Connector cj)
        {
            try
            {
                FamilyInstance f = doc.Create.NewElbowFitting(ci, cj);
                if (f != null) return true;
            }
            catch { }

            ElementId idA = ci.Owner.Id;
            ElementId idB = cj.Owner.Id;
            XYZ approx = (ci.Origin + cj.Origin).Multiply(0.5);
            if (!TryMeetAtCorner(ci, cj)) return false;
            try { doc.Regenerate(); } catch { }
            var pa = doc.GetElement(idA) as Pipe;
            var pb = doc.GetElement(idB) as Pipe;
            if (pa?.ConnectorManager == null || pb?.ConnectorManager == null) return false;
            Connector a = FindOpenConnectorNearPoint(pa.ConnectorManager, approx, 0.65);
            Connector b = FindOpenConnectorNearPoint(pb.ConnectorManager, approx, 0.65);
            if (a == null || b == null) return false;
            try
            {
                FamilyInstance f = doc.Create.NewElbowFitting(a, b);
                return f != null;
            }
            catch
            {
                try { a.ConnectTo(b); return true; } catch { return false; }
            }
        }

        private static bool TryMeetAtCorner(Connector ca, Connector cb)
        {
            var pa = ca.Owner as Pipe;
            var pb = cb.Owner as Pipe;
            if (pa == null || pb == null) return false;
            var lca = pa.Location as LocationCurve;
            var lcb = pb.Location as LocationCurve;
            if (lca == null || lcb == null) return false;
            var la = lca.Curve as Line;
            var lb = lcb.Curve as Line;
            if (la == null || lb == null) return false;

            XYZ a0 = la.GetEndPoint(0), a1 = la.GetEndPoint(1);
            XYZ b0 = lb.GetEndPoint(0), b1 = lb.GetEndPoint(1);
            XYZ da = a1 - a0;
            double laLen = da.GetLength();
            if (laLen < 0.15) return false;
            da = da.Multiply(1.0 / laLen);
            XYZ db = b1 - b0;
            double lbLen = db.GetLength();
            if (lbLen < 0.15) return false;
            db = db.Multiply(1.0 / lbLen);
            if (Math.Abs(da.DotProduct(db)) > 0.94) return false;

            XYZ n = da.CrossProduct(db);
            double den = n.DotProduct(n);
            if (den < 1e-12) return false;
            XYZ diff = b0 - a0;
            double ta = diff.CrossProduct(db).DotProduct(n) / den;
            double tb = diff.CrossProduct(da).DotProduct(n) / den;
            XYZ paPt = a0 + da.Multiply(ta);
            XYZ pbPt = b0 + db.Multiply(tb);
            if (paPt.DistanceTo(pbPt) > 0.22) return false;
            XYZ corner = (paPt + pbPt).Multiply(0.5);

            double extA = ta < 0 ? -ta : ta - laLen;
            double extB = tb < 0 ? -tb : tb - lbLen;
            if (extA > 0.55 || extB > 0.55) return false;

            XYZ aKeep = ca.Origin.DistanceTo(a0) <= ca.Origin.DistanceTo(a1) ? a1 : a0;
            XYZ bKeep = cb.Origin.DistanceTo(b0) <= cb.Origin.DistanceTo(b1) ? b1 : b0;
            if (aKeep.DistanceTo(corner) < 0.12 || bKeep.DistanceTo(corner) < 0.12) return false;
            try
            {
                lca.Curve = Line.CreateBound(aKeep, corner);
                lcb.Curve = Line.CreateBound(bKeep, corner);
                return true;
            }
            catch { return false; }
        }

        private static void SetParam(Element el, BuiltInParameter bip, double valueFt)
        {
            if (valueFt <= 0 || el == null) return;
            Parameter p = el.get_Parameter(bip);
            if (p != null && !p.IsReadOnly) { try { p.Set(valueFt); } catch { } }
        }

        /// <summary>Diagnostic: log AI diameter vs the diameter Revit actually placed.</summary>
        private static void MepLog(Document doc, Pipe pipe, double aiDiaMm, double setFt)
        {
            try
            {
                double actualMm = 0;
                var dp = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                if (dp != null) actualMm = dp.AsDouble() * 304.8;
                double outerMm = 0;
                var od = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_OUTER_DIAMETER);
                if (od != null) outerMm = od.AsDouble() * 304.8;
                string typeName = doc.GetElement(pipe.GetTypeId())?.Name ?? "?";
                string line = $"{DateTime.Now:HH:mm:ss}  AI_D={aiDiaMm:0.#}mm  requested_set={setFt * 304.8:0.#}mm  " +
                              $"placed_nominal={actualMm:0.#}mm  placed_OD={outerMm:0.#}mm  type='{typeName}'";
                string path = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Biruscan_MEP.log");
                System.IO.File.AppendAllText(path, line + Environment.NewLine);
            }
            catch { }
        }

        private static void MergeGeometricPiping(List<BirooniAiElement> baseElements, List<BirooniAiElement> geomElements)
        {
            if (geomElements == null || geomElements.Count == 0 || baseElements == null) return;

            foreach (var geom in geomElements)
            {
                if (geom.Start == null || geom.End == null || geom.Start.Length != 3 || geom.End.Length != 3) continue;

                XYZ gStart = new XYZ(geom.Start[0], geom.Start[1], geom.Start[2]);
                XYZ gEnd = new XYZ(geom.End[0], geom.End[1], geom.End[2]);
                XYZ gMid = (gStart + gEnd) * 0.5;

                bool isDuplicate = false;
                foreach (var baseEl in baseElements)
                {
                    if (baseEl.Start == null || baseEl.End == null || baseEl.Start.Length != 3 || baseEl.End.Length != 3) continue;
                    XYZ bStart = new XYZ(baseEl.Start[0], baseEl.Start[1], baseEl.Start[2]);
                    XYZ bEnd = new XYZ(baseEl.End[0], baseEl.End[1], baseEl.End[2]);
                    XYZ bMid = (bStart + bEnd) * 0.5;

                    if (gMid.DistanceTo(bMid) < 0.35)
                    {
                        isDuplicate = true;
                        break;
                    }
                }

                if (!isDuplicate)
                {
                    baseElements.Add(geom);
                }
            }
        }

        private static void Orbit3dView36Axes(UIDocument uidoc, List<double[]> roiPoints)
        {
            if (uidoc == null || roiPoints == null || roiPoints.Count < 5) return;
            var view3d = uidoc.ActiveView as View3D;
            if (view3d == null || view3d.IsLocked) return;

            try
            {
                double cx = 0, cy = 0, cz = 0;
                foreach (var p in roiPoints) { cx += p[0]; cy += p[1]; cz += p[2]; }
                int n = roiPoints.Count;
                XYZ center = new XYZ(cx / n, cy / n, cz / n);

                double maxDist = 0;
                foreach (var p in roiPoints)
                {
                    double d = center.DistanceTo(new XYZ(p[0], p[1], p[2]));
                    if (d > maxDist) maxDist = d;
                }
                double orbitRadius = Math.Max(5.0, maxDist * 2.2);

                var origOrientation = view3d.GetOrientation();

                var orbitFrames = Biruscan.AI.Vision.RobotLidarScanner.Generate36AxisOrbit(center, orbitRadius);

                foreach (var frame in orbitFrames)
                {
                    view3d.SetOrientation(new ViewOrientation3D(frame.EyePosition, frame.UpDirection, frame.ForwardDirection));
                    uidoc.RefreshActiveView();
                    System.Threading.Thread.Sleep(45); // Slower orbit (45ms per frame) so user sees Lidar scanning
                }

                view3d.SetOrientation(origOrientation);
                uidoc.RefreshActiveView();
            }
            catch { }
        }
    }
}


