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
    /// Command to fit a Revit rectangular duct to point cloud data.
    ///
    /// Workflow (CloudWorx-style):
    ///   1. User draws a rectangle (pick box) over a straight length of duct in the point cloud.
    ///   2. Points inside that rectangle through view depth are read and depth-filtered.
    ///   3. Geometric fitting calculates the horizontal duct axis, centerline endpoints, and cross-section (Width x Height).
    ///   4. A rectangular Revit duct is created on the fitted centerline.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class DuctFitterCommand : IExternalCommand
    {
        // Common nominal duct sizes (inches) used to snap measured cross-section dimensions.
        private static readonly double[] NominalDuctInches =
        {
            4.0, 6.0, 8.0, 10.0, 12.0, 14.0, 16.0, 18.0, 20.0, 22.0, 24.0,
            26.0, 28.0, 30.0, 32.0, 34.0, 36.0, 38.0, 40.0, 42.0, 44.0, 48.0, 54.0, 60.0
        };

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!Biruscan.Core.LicensingManager.EnsureLicense()) return Result.Cancelled;
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;
            View view = uidoc.ActiveView;

            StringBuilder log = new StringBuilder();
            log.AppendLine("=== Biruscan Duct Fitter — " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");

            try
            {
                log.AppendLine($"View: '{view.Title}'  Type={view.ViewType}");
                if (view is View3D v3d)
                {
                    log.AppendLine($"View3D IsPerspective={v3d.IsPerspective}");
                    if (v3d.IsPerspective)
                    {
                        Finish(log, "Switch to an orthographic (parallel) 3D view before using Duct Fitter.", true);
                        return Result.Cancelled;
                    }
                }
                else
                {
                    Finish(log, "Duct Fitter requires a 3D view. Open a 3D view that shows the ductwork.", true);
                    return Result.Cancelled;
                }
                log.AppendLine($"Origin={Fmt(view.Origin)} Right={Fmt(view.RightDirection)} Up={Fmt(view.UpDirection)} ViewDir={Fmt(view.ViewDirection)}");

                PointCloudService pcs = new PointCloudService(doc);
                var pci = pcs.GetFirstPointCloudInstance();
                if (pci == null)
                {
                    Finish(log, "No point cloud found. Load a point cloud first.", true);
                    return Result.Cancelled;
                }

                // 1) Draw a rectangle over a straight length of duct.
                PickedBox box = uidoc.Selection.PickBox(PickBoxStyle.Enclosing,
                    "Draw a rectangle over a straight length of duct in the point cloud");
                log.AppendLine($"PickBox Min={Fmt(box.Min)} Max={Fmt(box.Max)}");

                // Ray-hit anchor on the visible cloud surface calibrates scan->world mapping.
                string rayDiag;
                XYZ rayHit = RayHitOnCloud(view, box.Min, box.Max, out rayDiag);
                XYZ anchor = rayHit ?? (box.Min + box.Max).Multiply(0.5);
                log.AppendLine("Ray anchor: " + rayDiag);

                // 2) Read points inside the rectangle in true model (world) coordinates.
                string diag;
                List<XYZ> worldPts = pcs.GetPointsInScreenRect(view, box.Min, box.Max, 200000, out diag, anchor);
                log.AppendLine("Capture diag: " + diag);
                log.AppendLine($"Captured (in rect): {worldPts.Count}");

                if (worldPts.Count < 8)
                {
                    Finish(log, $"Only {worldPts.Count} points found inside the rectangle. See capture diag above.", true);
                    return Result.Cancelled;
                }

                // Keep the surface nearest the camera so background surfaces don't dominate.
                int before = worldPts.Count;
                worldPts = NearestSurface(worldPts, view, box.Min, box.Max, out string gateDiag, minBandFt: 3.5);
                log.AppendLine($"After nearest-surface gate: {worldPts.Count} (was {before}) {gateDiag}");

                if (worldPts.Count < 8)
                {
                    Finish(log, $"Only {worldPts.Count} points remain after depth filtering. Draw a tighter rectangle over one duct.", true);
                    return Result.Cancelled;
                }

                // 3) Fit rectangular duct geometry.
                var fit = FitRectangularRun(worldPts, view, RectFitKind.Duct);
                log.AppendLine($"Fit success={fit.Success} msg='{fit.Message}'");
                if (!fit.Success)
                {
                    Finish(log, fit.Message + $"\nPoints used: {worldPts.Count}", true);
                    return Result.Cancelled;
                }

                log.AppendLine($"  size={fit.WidthInches:F1}x{fit.HeightInches:F1} in");
                log.AppendLine($"  start={Fmt(fit.Start)} end={Fmt(fit.End)} len={fit.Start.DistanceTo(fit.End):F3}ft");

                double widthFeet = fit.WidthInches / 12.0;
                double heightFeet = fit.HeightInches / 12.0;

                if (!WithinLimits(fit.Start) || !WithinLimits(fit.End))
                {
                    Finish(log, "Fitted centreline is out of Revit design limits.", true);
                    return Result.Cancelled;
                }

                // 4) Create the duct on the fitted centerline.
                Level level = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level)).Cast<Level>()
                    .OrderBy(l => Math.Abs(l.Elevation - fit.Start.Z)).FirstOrDefault();
                if (level == null)
                {
                    Finish(log, "No level found. Create a level first.", true);
                    return Result.Failed;
                }
                log.AppendLine($"Level='{level.Name}' elev={level.Elevation:F3}");

                FitterService fitter = new FitterService(doc);
                var result = fitter.FitDuct(fit.Start, fit.End, widthFeet, heightFeet, level);
                log.AppendLine($"FitDuct success={result.Success} msg='{result.Message}'");

                if (result.Success)
                {
                    try { Services.Ai.AutoTrainingManager.Instance.RecordFittedElement(doc, result.CreatedElementId, "Duct", "Duct Fitter", worldPts); } catch { }
                }

                Finish(log,
                    (result.Success ? "DUCT CREATED" : "DUCT FAILED") + "\n" +
                    $"Duct size: {fit.WidthInches:F0}\" x {fit.HeightInches:F0}\"\n" +
                    $"Length: {fit.Start.DistanceTo(fit.End):F2}ft\n" +
                    result.Message, !result.Success);

                return result.Success ? Result.Succeeded : Result.Failed;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
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

        public struct DuctFitResult
        {
            public bool Success;
            public XYZ Start;
            public XYZ End;
            public XYZ Direction;
            public double WidthInches;
            public double HeightInches;
            public string Message;
        }

        public enum RectFitKind { Duct, CableTray }

        public static DuctFitResult FitRectangularDuct(List<XYZ> points, View view)
            => FitRectangularRun(points, view, RectFitKind.Duct);

        /// <summary>
        /// Envelope fit: run direction from PCA, then per-slice min/max for width (lateral)
        /// and height (world Z). Sparse side walls/rails still define height, unlike a
        /// global 5–95% which collapses to the dense face sheet.
        /// </summary>
        public static DuctFitResult FitRectangularRun(List<XYZ> points, View view, RectFitKind kind)
        {
            if (points == null || points.Count < 8)
                return new DuctFitResult { Success = false, Message = "Not enough points to fit." };

            double[] widthCatalog = kind == RectFitKind.CableTray
                ? new[] { 2.0, 4.0, 6.0, 8.0, 9.0, 12.0, 16.0, 18.0, 24.0, 30.0, 36.0 }
                : NominalDuctInches;
            double[] heightCatalog = kind == RectFitKind.CableTray
                ? new[] { 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0 }
                : NominalDuctInches;

            XYZ c = XYZ.Zero;
            foreach (var p in points) c += p;
            c = c.Multiply(1.0 / points.Count);

            double cxx = 0, cyy = 0, czz = 0, cxy = 0, cxz = 0, cyz = 0;
            foreach (var p in points)
            {
                XYZ d = p - c;
                cxx += d.X * d.X; cyy += d.Y * d.Y; czz += d.Z * d.Z;
                cxy += d.X * d.Y; cxz += d.X * d.Z; cyz += d.Y * d.Z;
            }
            double invN = 1.0 / points.Count;
            cxx *= invN; cyy *= invN; czz *= invN; cxy *= invN; cxz *= invN; cyz *= invN;
            EigenDecomposition3x3(cxx, cyy, czz, cxy, cxz, cyz, out double[] ev, out XYZ[] evec);

            XYZ vDir = evec[0];
            bool verticalRiser = Math.Abs(vDir.Z) > 0.75 && ev[0] > ev[1] * 1.15;
            if (verticalRiser)
            {
                vDir = XYZ.BasisZ;
            }
            else
            {
                XYZ horiz = new XYZ(vDir.X, vDir.Y, 0);
                if (horiz.GetLength() < 1e-6)
                    horiz = new XYZ(evec[1].X, evec[1].Y, 0);
                if (horiz.GetLength() < 1e-6)
                    horiz = XYZ.BasisX;
                vDir = Biruscan.Models.FitterSettings.OrthoSnapDirection(horiz.Normalize());
                if (Math.Abs(vDir.Z) > 0.2)
                    vDir = new XYZ(vDir.X, vDir.Y, 0).Normalize();
            }

            XYZ vWidth = verticalRiser
                ? Biruscan.Models.FitterSettings.OrthoSnapDirection(XYZ.BasisX)
                : new XYZ(-vDir.Y, vDir.X, 0).Normalize();
            XYZ vHeight = verticalRiser
                ? vDir.CrossProduct(vWidth).Normalize()
                : XYZ.BasisZ;

            double tMin = double.MaxValue, tMax = double.MinValue;
            foreach (var p in points)
            {
                double t = p.DotProduct(vDir);
                if (t < tMin) tMin = t;
                if (t > tMax) tMax = t;
            }
            double length = tMax - tMin;
            if (length < 0.2)
                return new DuctFitResult { Success = false, Message = "Captured run length is too short." };

            int nBins = Math.Max(8, Math.Min(24, points.Count / 10));
            double binSize = length / nBins;
            var slicesW = new List<double>[nBins];
            var slicesH = new List<double>[nBins];
            for (int b = 0; b < nBins; b++)
            {
                slicesW[b] = new List<double>();
                slicesH[b] = new List<double>();
            }

            foreach (var p in points)
            {
                double t = p.DotProduct(vDir);
                int b = (int)((t - tMin) / binSize);
                if (b < 0) b = 0;
                if (b >= nBins) b = nBins - 1;
                slicesW[b].Add(p.DotProduct(vWidth));
                slicesH[b].Add(verticalRiser ? p.DotProduct(vHeight) : p.Z);
            }

            var binW = new List<double>();
            var binH = new List<double>();
            double wMinAll = double.MaxValue, wMaxAll = double.MinValue;
            double hMinAll = double.MaxValue, hMaxAll = double.MinValue;

            for (int b = 0; b < nBins; b++)
            {
                if (slicesW[b].Count >= 3)
                {
                    slicesW[b].Sort();
                    Envelope(slicesW[b], out double span, out _);
                    if (span > 1e-4) binW.Add(span);
                    double lo = slicesW[b][0], hi = slicesW[b][slicesW[b].Count - 1];
                    if (lo < wMinAll) wMinAll = lo;
                    if (hi > wMaxAll) wMaxAll = hi;
                }
                if (slicesH[b].Count >= 3)
                {
                    slicesH[b].Sort();
                    Envelope(slicesH[b], out double span, out _);
                    if (span > 1e-4) binH.Add(span);
                    double lo = slicesH[b][0], hi = slicesH[b][slicesH[b].Count - 1];
                    if (lo < hMinAll) hMinAll = lo;
                    if (hi > hMaxAll) hMaxAll = hi;
                }
            }

            if (binW.Count == 0 || binH.Count == 0 ||
                wMinAll == double.MaxValue || hMinAll == double.MaxValue)
                return new DuctFitResult { Success = false, Message = "Could not measure a rectangular cross-section." };

            double widthFt = Median(binW);
            double heightFt = Percentile(binH, 0.85);

            const double sheetFt = 1.25 / 12.0;
            if (heightFt < sheetFt)
                heightFt = kind == RectFitKind.CableTray ? 4.0 / 12.0 : Math.Max(widthFt * 0.5, 8.0 / 12.0);
            if (widthFt < sheetFt)
                widthFt = kind == RectFitKind.CableTray ? 12.0 / 12.0 : Math.Max(heightFt * 0.75, 8.0 / 12.0);

            double widthInches = Biruscan.Models.FitterSettings.SnapInches(widthFt * 12.0, widthCatalog);
            double heightInches = Biruscan.Models.FitterSettings.SnapInches(heightFt * 12.0, heightCatalog);

            if (kind == RectFitKind.CableTray)
            {
                if (heightInches < 2.0) heightInches = 4.0;
                if (heightInches > 8.0) heightInches = 8.0;
                if (widthInches < 2.0) widthInches = 6.0;
            }
            else
            {
                if (widthInches < 4.0) widthInches = 6.0;
                if (heightInches < 4.0) heightInches = 6.0;
            }

            double widthFtSnap = widthInches / 12.0;
            double heightFtSnap = heightInches / 12.0;

            // Stable centre: dense scanned face + half the snapped size into the body.
            // Do not average noisy per-slice midpoints — that is what made the centre jump.
            double faceTolW = Math.Max(0.03, widthFt * 0.08);
            double faceTolH = Math.Max(0.03, heightFt * 0.08);
            int nWMin = 0, nWMax = 0, nHMin = 0, nHMax = 0;
            foreach (var p in points)
            {
                double w = p.DotProduct(vWidth);
                double h = verticalRiser ? p.DotProduct(vHeight) : p.Z;
                if (Math.Abs(w - wMinAll) <= faceTolW) nWMin++;
                if (Math.Abs(w - wMaxAll) <= faceTolW) nWMax++;
                if (Math.Abs(h - hMinAll) <= faceTolH) nHMin++;
                if (Math.Abs(h - hMaxAll) <= faceTolH) nHMax++;
            }

            double centerW = PlaceBoxCenter(wMinAll, wMaxAll, widthFtSnap, nWMin, nWMax, view, vWidth);
            XYZ hAxis = verticalRiser ? vHeight : XYZ.BasisZ;
            double centerH = PlaceBoxCenter(hMinAll, hMaxAll, heightFtSnap, nHMin, nHMax, view, hAxis);
            double centerT = 0.5 * (tMin + tMax);

            // One straight centreline — constant W and H, so the run does not wander.
            XYZ centerPoint = vDir.Multiply(centerT) + vWidth.Multiply(centerW) + vHeight.Multiply(centerH);
            double halfLen = length * 0.5;
            XYZ start = centerPoint - vDir.Multiply(halfLen);
            XYZ end = centerPoint + vDir.Multiply(halfLen);

            string label = kind == RectFitKind.CableTray ? "Cable tray" : "Rectangular duct";
            return new DuctFitResult
            {
                Success = true,
                Start = start,
                End = end,
                Direction = vDir,
                WidthInches = widthInches,
                HeightInches = heightInches,
                Message = $"{label} fitted: {widthInches:F0}\" x {heightInches:F0}\" (measured {widthFt * 12.0:F1}\" x {heightFt * 12.0:F1}\")"
            };
        }

        /// <summary>
        /// Centre of a rectangular box along one axis. If both faces are visible, use the
        /// true midpoint. If only one face is scanned, offset half the catalog size into
        /// the body so the Revit centreline sits in the middle of the real section.
        /// </summary>
        private static double PlaceBoxCenter(
            double min, double max, double snappedSizeFt, int nMin, int nMax, View view, XYZ axis)
        {
            double span = max - min;
            const double sheetFt = 1.25 / 12.0;

            if (span >= snappedSizeFt * 0.40 && nMin >= 4 && nMax >= 4)
                return 0.5 * (min + max);

            if (span < sheetFt || (nMin < 4 && nMax < 4) || Math.Abs(nMin - nMax) < 3)
            {
                double face = 0.5 * (min + max);
                double into = 0;
                try { into = view.ViewDirection.Normalize().DotProduct(axis); } catch { }
                if (Math.Abs(into) < 0.15) into = 1.0;
                return face + Math.Sign(into) * 0.5 * snappedSizeFt;
            }

            if (nMin >= nMax)
                return min + 0.5 * snappedSizeFt;
            return max - 0.5 * snappedSizeFt;
        }

        private static void EigenDecomposition3x3(
            double cxx, double cyy, double czz, double cxy, double cxz, double cyz,
            out double[] eigenvalues, out XYZ[] eigenvectors)
        {
            double[,] A = new double[3, 3]
            {
                { cxx, cxy, cxz },
                { cxy, cyy, cyz },
                { cxz, cyz, czz }
            };

            double[,] V = new double[3, 3]
            {
                { 1, 0, 0 },
                { 0, 1, 0 },
                { 0, 0, 1 }
            };

            for (int iter = 0; iter < 50; iter++)
            {
                int p = 0, q = 1;
                double maxOff = Math.Abs(A[0, 1]);
                if (Math.Abs(A[0, 2]) > maxOff) { p = 0; q = 2; maxOff = Math.Abs(A[0, 2]); }
                if (Math.Abs(A[1, 2]) > maxOff) { p = 1; q = 2; maxOff = Math.Abs(A[1, 2]); }

                if (maxOff < 1e-10) break;

                double app = A[p, p], aqq = A[q, q], apq = A[p, q];
                double phi = 0.5 * Math.Atan2(2 * apq, aqq - app);
                double c = Math.Cos(phi), s = Math.Sin(phi);

                A[p, p] = c * c * app - 2 * s * c * apq + s * s * aqq;
                A[q, q] = s * s * app + 2 * s * c * apq + c * c * aqq;
                A[p, q] = 0; A[q, p] = 0;

                for (int k = 0; k < 3; k++)
                {
                    if (k != p && k != q)
                    {
                        double akp = A[k, p], akq = A[k, q];
                        A[k, p] = c * akp - s * akq; A[p, k] = A[k, p];
                        A[k, q] = s * akp + c * akq; A[q, k] = A[k, q];
                    }
                }

                for (int k = 0; k < 3; k++)
                {
                    double vkp = V[k, p], vkq = V[k, q];
                    V[k, p] = c * vkp - s * vkq;
                    V[k, q] = s * vkp + c * vkq;
                }
            }

            int[] idx = new int[] { 0, 1, 2 };
            Array.Sort(idx, (i, j) => A[j, j].CompareTo(A[i, i]));

            eigenvalues = new double[3] { A[idx[0], idx[0]], A[idx[1], idx[1]], A[idx[2], idx[2]] };
            eigenvectors = new XYZ[3]
            {
                new XYZ(V[0, idx[0]], V[1, idx[0]], V[2, idx[0]]).Normalize(),
                new XYZ(V[0, idx[1]], V[1, idx[1]], V[2, idx[1]]).Normalize(),
                new XYZ(V[0, idx[2]], V[1, idx[2]], V[2, idx[2]]).Normalize()
            };
        }

        private static string Fmt(XYZ p) => p == null ? "null" : $"({p.X:F2},{p.Y:F2},{p.Z:F2})";

        private static void Finish(StringBuilder log, string summary, bool isError = false)
        {
            log.AppendLine("RESULT: " + summary.Replace("\n", " | "));
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    "Biruscan_DuctFitter.log");
                File.AppendAllText(path, log.ToString() + Environment.NewLine);
            }
            catch { }

            if (isError)
            {
                TaskDialog dlg = new TaskDialog("Duct Fitter");
                dlg.MainInstruction = summary.Length > 200 ? summary.Substring(0, 200) : summary;
                dlg.MainContent = log.ToString();
                dlg.Show();
            }
        }

        internal static XYZ RayHitOnCloud(View view, XYZ corner1, XYZ corner2, out string diag)
        {
            diag = "";
            View3D v3d = view as View3D;
            if (v3d == null) { diag = "not a 3D view"; return null; }
            if (v3d.IsPerspective) { diag = "perspective view — switch to parallel/orthographic"; return null; }
            try
            {
                XYZ center = (corner1 + corner2).Multiply(0.5);
                XYZ dir = view.ViewDirection.Normalize().Negate();
                var ri = new ReferenceIntersector(v3d) { TargetType = FindReferenceTarget.All };
                ReferenceWithContext rc = ri.FindNearest(center, dir);
                if (rc == null) { diag = "no hit"; return null; }
                XYZ hit = center + dir.Multiply(rc.Proximity);
                diag = $"hit@{rc.Proximity:F2}=({hit.X:F2},{hit.Y:F2},{hit.Z:F2})";
                return hit;
            }
            catch (Exception ex) { diag = "err " + ex.Message; return null; }
        }

        internal static List<XYZ> NearestSurface(List<XYZ> pts, View view,
            XYZ corner1, XYZ corner2, out string diag, double minBandFt = 1.0)
        {
            diag = "";
            if (pts == null || pts.Count < 8) return pts ?? new List<XYZ>();

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
            XYZ hit = RayHitOnCloud(view, corner1, corner2, out rayDiag);
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
            double tol = Math.Max(minBandFt, Math.Min(4.0, span * 0.25 + 0.75));

            var kept = new List<XYZ>(pts.Count);
            foreach (var p in pts)
            {
                double z = invView.OfPoint(p).Z;
                if (z <= refZ + tol && z >= refZ - 0.5)
                    kept.Add(p);
            }

            if (kept.Count < 8)
            {
                diag += " fallback=all";
                return pts;
            }

            diag += $" tol={tol:F2}ft";
            return kept;
        }

        private static bool WithinLimits(XYZ p)
        {
            return Math.Abs(p.X) < 30000.0 && Math.Abs(p.Y) < 30000.0 && Math.Abs(p.Z) < 30000.0;
        }

        private static double SnapNominalDuct(double measuredInches)
            => Biruscan.Models.FitterSettings.SnapInches(measuredInches, NominalDuctInches);

        /// <summary>Outer envelope of a slice. True min/max unless the slice is dense enough to trim 2%.</summary>
        private static void Envelope(List<double> sorted, out double span, out double mid)
        {
            if (sorted == null || sorted.Count == 0) { span = 0; mid = 0; return; }
            int i0 = sorted.Count >= 20 ? Math.Max(0, (int)(sorted.Count * 0.02)) : 0;
            int i1 = sorted.Count >= 20 ? Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.98)) : sorted.Count - 1;
            span = sorted[i1] - sorted[i0];
            mid = (sorted[i0] + sorted[i1]) * 0.5;
        }

        private static double Median(List<double> values)
        {
            if (values == null || values.Count == 0) return 0;
            var s = new List<double>(values);
            s.Sort();
            int n = s.Count;
            return (n % 2 == 1) ? s[n / 2] : 0.5 * (s[n / 2 - 1] + s[n / 2]);
        }

        private static double Percentile(List<double> values, double p)
        {
            if (values == null || values.Count == 0) return 0;
            var s = new List<double>(values);
            s.Sort();
            if (s.Count == 1) return s[0];
            double idx = Math.Max(0, Math.Min(1, p)) * (s.Count - 1);
            int i = (int)idx;
            double f = idx - i;
            if (i >= s.Count - 1) return s[s.Count - 1];
            return s[i] * (1 - f) + s[i + 1] * f;
        }
    }

    /// <summary>
    /// Legacy alias for backward compatibility.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class HVACFitterCommand : DuctFitterCommand
    {
    }
}
