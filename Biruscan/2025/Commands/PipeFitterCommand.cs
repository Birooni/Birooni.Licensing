using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Biruscan.Services;

namespace Biruscan.Commands
{
    /// <summary>
    /// Command to fit a Revit pipe to point cloud data.
    ///
    /// Workflow (CloudWorx-style):
    ///   1. User draws a rectangle (pick box) over a length of pipe in the point cloud.
    ///   2. Only the points INSIDE that rectangle (through the view depth) are read.
    ///   3. A cylinder is least-squares fitted to those points — this gives the pipe
    ///      axis, the centreline endpoints, and the DIAMETER (auto-detected).
    ///   4. A Revit pipe is created on the fitted centreline at the detected size.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class PipeFitterCommand : IExternalCommand
    {
        // Common nominal pipe sizes (inches) used to snap the measured diameter.
        private static readonly double[] NominalInches =
        {
            0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 2.5, 3.0, 3.5, 4.0, 5.0, 6.0,
            8.0, 10.0, 12.0, 14.0, 16.0, 18.0, 20.0, 24.0, 30.0, 36.0
        };

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;
            View view = uidoc.ActiveView;

            StringBuilder log = new StringBuilder();
            log.AppendLine("=== Biruscan Pipe Fitter — " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");

            try
            {
                log.AppendLine($"View: '{view.Title}'  Type={view.ViewType}");
                if (view is View3D v3d)
                {
                    log.AppendLine($"View3D IsPerspective={v3d.IsPerspective}");
                    if (v3d.IsPerspective)
                    {
                        Finish(log, "Switch to an orthographic (parallel) 3D view before using Pipe Fitter.", true);
                        return Result.Cancelled;
                    }
                }
                else
                {
                    Finish(log, "Pipe Fitter requires a 3D view. Open a 3D view that shows the round side of the pipe.", true);
                    return Result.Cancelled;
                }
                log.AppendLine($"Origin={Fmt(view.Origin)} Right={Fmt(view.RightDirection)} Up={Fmt(view.UpDirection)} ViewDir={Fmt(view.ViewDirection)}");

                PointCloudService pcs = new PointCloudService(doc);
                var pci = pcs.GetFirstPointCloudInstance();
                if (pci == null) { Finish(log, "No point cloud found. Load a point cloud first.", true); return Result.Cancelled; }

                log.AppendLine($"instances={pcs.GetAllPointCloudInstances().Count}");
                Transform ct = pci.GetTransform();
                log.AppendLine($"Cloud[0] transform identity={ct == null || ct.IsIdentity} origin={Fmt(ct?.Origin)}");
                if (ct != null) log.AppendLine($"ct X={Fmt(ct.BasisX)} Y={Fmt(ct.BasisY)} Z={Fmt(ct.BasisZ)}");
                BoundingBoxXYZ cb = pci.get_BoundingBox(null);
                if (cb != null) log.AppendLine($"bbox(null) min={Fmt(cb.Min)} max={Fmt(cb.Max)} Torig={Fmt(cb.Transform?.Origin)}");
                try { BoundingBoxXYZ cbv = pci.get_BoundingBox(view); if (cbv != null) log.AppendLine($"bbox(view) min={Fmt(cbv.Min)} max={Fmt(cbv.Max)} Torig={Fmt(cbv.Transform?.Origin)}"); } catch (Exception ex) { log.AppendLine("bbox(view) err " + ex.Message); }
                try { Transform pt = doc.ActiveProjectLocation.GetTotalTransform(); log.AppendLine($"ProjLoc O={Fmt(pt.Origin)} X={Fmt(pt.BasisX)} Y={Fmt(pt.BasisY)} Z={Fmt(pt.BasisZ)}"); } catch (Exception ex) { log.AppendLine("ProjLoc err " + ex.Message); }

                bool fittedAny = false;
                int pipeCount = 0;

                while (true)
                {
                    try
                    {
                        // 1) Draw a rectangle over a straight length of pipe.
                        PickedBox box = uidoc.Selection.PickBox(PickBoxStyle.Enclosing,
                            "Draw a rectangle over a straight length of pipe (Press ESC to finish)");
                        log.AppendLine($"PickBox Min={Fmt(box.Min)} Max={Fmt(box.Max)}");

                        // Ray-hit anchor on the visible cloud surface calibrates scan→world mapping.
                        XYZ anchor = null;
                        string rayDiag;
                        XYZ rayHit = RayHitOnCloud(view, box.Min, box.Max, out rayDiag);
                        if (rayHit != null)
                        {
                            anchor = rayHit;
                            log.AppendLine("Ray anchor: " + rayDiag);
                        }
                        else
                        {
                            anchor = (box.Min + box.Max).Multiply(0.5);
                            log.AppendLine("Ray anchor fallback (box centre): " + rayDiag);
                        }

                        // 2) Read points inside the rectangle in true model (world) coordinates.
                        string diag;
                        List<XYZ> worldPts = pcs.GetPointsInScreenRect(view, box.Min, box.Max, 25000, out diag, anchor);
                        log.AppendLine("Capture diag: " + diag);
                        log.AppendLine($"Captured (in rect): {worldPts.Count}");
                        LogBounds(log, "captured", worldPts);

                        if (worldPts.Count < 12)
                        {
                            TaskDialog.Show("Pipe Fitter", $"Only {worldPts.Count} points found inside the rectangle. Try a different pick.");
                            log.AppendLine("Skipped due to low point count.");
                            continue;
                        }

                        // Keep the surface nearest the camera so background pipes/walls in the
                        // same screen column don't dominate the fit. Uses a generous depth band
                        // so slanted pipes are not chopped.
                        int before = worldPts.Count;
                        worldPts = NearestSurface(worldPts, view, box.Min, box.Max, out string gateDiag, rayHit);
                        log.AppendLine($"After nearest-surface gate: {worldPts.Count} (was {before}) {gateDiag}");
                        LogBounds(log, "gated", worldPts);

                        if (worldPts.Count < 12)
                        {
                            TaskDialog.Show("Pipe Fitter", $"Only {worldPts.Count} points remain after depth filtering. Draw a tighter rectangle over one pipe, or clip the cloud first.");
                            log.AppendLine("Skipped due to low depth-filtered point count.");
                            continue;
                        }

                        // 3) Fit a cylinder (normal-based RANSAC — rejects walls/background itself).
                        CylinderFit.Result fit = CylinderFit.Fit(worldPts);
                        if (fit.Success)
                            CylinderFit.RefineToPointCloud(ref fit, worldPts);
                        log.AppendLine($"Fit success={fit.Success} msg='{fit.Message}'");
                        if (fit.Success)
                        {
                            log.AppendLine($"  method={fit.Method} arc={fit.ArcSpanDeg:F1}deg");
                            log.AppendLine($"  dia={fit.Radius * 24.0:F3}in rms={fit.Rms * 12.0:F4}in flatness={fit.Flatness:F3} inliers={fit.PointCount}");
                            log.AppendLine($"  axis={Fmt(fit.Axis)} start={Fmt(fit.Start)} end={Fmt(fit.End)} len={fit.Start.DistanceTo(fit.End):F3}ft");
                        }
                        
                        if (!fit.Success) 
                        { 
                            TaskDialog.Show("Pipe Fitter", "Fitting failed: " + fit.Message);
                            log.AppendLine("Skipped due to failed fit.");
                            continue; 
                        }

                        XYZ axisSnap = Biruscan.Models.FitterSettings.OrthoSnapDirection(fit.Axis);
                        bool snapped = CylinderFit.MaybeOrthoSnap(ref fit, worldPts, axisSnap);
                        log.AppendLine(snapped
                            ? "  ortho snap applied (cloud residual did not increase)"
                            : "  kept measured cloud angle (as-built, not forced to X/Y)");
                        log.AppendLine($"  final axis={Fmt(fit.Axis)} start={Fmt(fit.Start)} end={Fmt(fit.End)}");

                        double measuredInches = fit.Radius * 2.0 * 12.0;
                        double nominalInches = Biruscan.Models.FitterSettings.SnapInches(measuredInches, NominalInches);
                        double diameterFeet = nominalInches / 12.0;

                        if (!WithinLimits(fit.Start) || !WithinLimits(fit.End))
                        {
                            TaskDialog.Show("Pipe Fitter", "Fitted centreline is out of Revit design limits (background likely captured). Slice tighter / draw tighter.");
                            log.AppendLine("Skipped due to out of limits.");
                            continue;
                        }

                        // 4) Create the pipe on the fitted centreline.
                        Level level = new FilteredElementCollector(doc)
                            .OfClass(typeof(Level)).Cast<Level>()
                            .OrderBy(l => Math.Abs(l.Elevation - fit.Start.Z)).FirstOrDefault();
                        if (level == null) 
                        { 
                            TaskDialog.Show("Pipe Fitter", "No level found. Create a level first.");
                            log.AppendLine("Skipped due to no level.");
                            break; 
                        }
                        
                        log.AppendLine($"Level='{level.Name}' elev={level.Elevation:F3}");

                        FitterService fitter = new FitterService(doc);
                        var result = fitter.FitPipe(fit.Start, fit.End, diameterFeet, level);
                        log.AppendLine($"FitPipe success={result.Success} msg='{result.Message}'");

                        if (result.Success)
                        {
                            fittedAny = true;
                            pipeCount++;
                            try { Services.Ai.AutoTrainingManager.Instance.RecordFittedElement(doc, result.CreatedElementId, "Piping", "Pipe Fitter", worldPts); } catch { }
                            log.AppendLine($"PIPE CREATED. Auto-detected diameter: {measuredInches:F2}\" (snapped {nominalInches:F2}\"), Length: {fit.Start.DistanceTo(fit.End):F2}ft");
                        }
                        else
                        {
                            TaskDialog.Show("Pipe Fitter", "Pipe creation failed: " + result.Message);
                        }
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        // User hit ESC to end the loop
                        log.AppendLine("User exited pipe fitter loop.");
                        break;
                    }
                }

                Finish(log, fittedAny ? $"PIPE FITTER COMPLETED. Created {pipeCount} pipe(s)." : "PIPE FITTER CANCELLED or no pipes created.", false);
                return fittedAny ? Result.Succeeded : Result.Cancelled;
            }
            catch (Exception ex)
            {
                log.AppendLine("EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
                log.AppendLine(ex.StackTrace);
                message = ex.Message;
                Finish(log, "ERROR: " + ex.Message, true);
                return Result.Failed;
            }
        }

        private static string Fmt(XYZ p) => p == null ? "null" : $"({p.X:F2},{p.Y:F2},{p.Z:F2})";

        private static void LogBounds(StringBuilder log, string label, List<XYZ> pts)
        {
            if (pts == null || pts.Count == 0) { log.AppendLine($"  {label} bounds: (none)"); return; }
            double mnx = double.MaxValue, mny = double.MaxValue, mnz = double.MaxValue;
            double mxx = double.MinValue, mxy = double.MinValue, mxz = double.MinValue;
            foreach (var p in pts)
            {
                if (p.X < mnx) mnx = p.X; if (p.Y < mny) mny = p.Y; if (p.Z < mnz) mnz = p.Z;
                if (p.X > mxx) mxx = p.X; if (p.Y > mxy) mxy = p.Y; if (p.Z > mxz) mxz = p.Z;
            }
            log.AppendLine($"  {label} bounds: min=({mnx:F2},{mny:F2},{mnz:F2}) max=({mxx:F2},{mxy:F2},{mxz:F2})");
        }

        private static void Finish(StringBuilder log, string summary, bool isError = false)
        {
            log.AppendLine("RESULT: " + summary.Replace("\n", " | "));
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    "Biruscan_PipeFitter.log");
                File.AppendAllText(path, log.ToString() + Environment.NewLine);
            }
            catch { }

            if (isError)
            {
                TaskDialog dlg = new TaskDialog("Pipe Fitter");
                dlg.MainInstruction = summary.Length > 200 ? summary.Substring(0, 200) : summary;
                dlg.MainContent = log.ToString();
                dlg.Show();
            }
        }

        /// <summary>
        /// Shoots a ray through the rectangle centre into the scene and returns the true
        /// rendered hit point on whatever the cloud displays there (model coordinates).
        /// Returns null if nothing was hit (e.g. perspective view, or ray missed).
        /// </summary>
        private static XYZ RayHitOnCloud(View view, XYZ corner1, XYZ corner2, out string diag)
        {
            diag = "";
            View3D v3d = view as View3D;
            if (v3d == null) { diag = "not a 3D view"; return null; }
            if (v3d.IsPerspective) { diag = "perspective view — switch to parallel/orthographic"; return null; }
            try
            {
                XYZ center = (corner1 + corner2).Multiply(0.5);
                XYZ dir = view.ViewDirection.Normalize().Negate(); // into the scene
                var ri = new ReferenceIntersector(v3d) { TargetType = FindReferenceTarget.All };
                ReferenceWithContext rc = ri.FindNearest(center, dir);
                if (rc == null) { diag = "no hit"; return null; }
                XYZ hit = center + dir.Multiply(rc.Proximity);
                diag = $"hit@{rc.Proximity:F2}=({hit.X:F2},{hit.Y:F2},{hit.Z:F2})";
                return hit;
            }
            catch (Exception ex) { diag = "err " + ex.Message; return null; }
        }

        /// <summary>
        /// Drops points that lie significantly behind the front-most surface in the pick
        /// rectangle. Keeps a generous depth band so slanted / long pipes stay intact.
        /// </summary>
        private static List<XYZ> NearestSurface(List<XYZ> pts, View view,
            XYZ corner1, XYZ corner2, out string diag, XYZ precomputedHit = null)
        {
            diag = "";
            if (pts == null || pts.Count < 12) return pts ?? new List<XYZ>();

            Transform viewT = Transform.Identity;
            viewT.Origin = view.Origin;
            viewT.BasisX = view.RightDirection.Normalize();
            viewT.BasisY = view.UpDirection.Normalize();
            viewT.BasisZ = view.ViewDirection.Normalize();
            Transform invView = viewT.Inverse;

            double[] depths = new double[pts.Count];
            for (int i = 0; i < pts.Count; i++)
                depths[i] = invView.OfPoint(pts[i]).Z;

            Array.Sort(depths);

            double refZ;
            string rayDiag;
            XYZ hit = precomputedHit;
            if (hit == null)
                hit = RayHitOnCloud(view, corner1, corner2, out rayDiag);
            else
                rayDiag = "reuse";
            if (hit != null)
            {
                refZ = invView.OfPoint(hit).Z;
                diag = "ref=ray " + rayDiag;
            }
            else
            {
                int idx = Math.Min(depths.Length / 10, depths.Length - 1);
                refZ = depths[idx];
                diag = "ref=front10% " + rayDiag;
            }

            double span = depths[depths.Length - 1] - depths[0];
            double tol = Math.Max(1.0, Math.Min(4.0, span * 0.20 + 0.75));

            var kept = new List<XYZ>(pts.Count);
            foreach (var p in pts)
            {
                double z = invView.OfPoint(p).Z;
                if (z <= refZ + tol && z >= refZ - 0.5)
                    kept.Add(p);
            }

            if (kept.Count < 12)
            {
                diag += " fallback=all";
                return pts;
            }

            diag += $" tol={tol:F2}ft";
            return kept;
        }

        /// <summary>True if the point is comfortably inside Revit's design limits.</summary>
        private static bool WithinLimits(XYZ p)
        {
            return Math.Abs(p.X) < 30000.0 && Math.Abs(p.Y) < 30000.0 && Math.Abs(p.Z) < 30000.0;
        }

        /// <summary>Snaps a measured diameter (inches) using Fitter Settings.</summary>
        private static double SnapNominal(double measuredInches)
            => Biruscan.Models.FitterSettings.SnapInches(measuredInches, NominalInches);
    }
}
