using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.PointClouds;
using Biruscan.Services;

namespace Biruscan.Services.Ai
{
    /// <summary>
    /// Exports the active (clipped) point cloud ROI in true Revit model coordinates (feet).
    /// Uses PointCloudService world-mapping calibration so AI placement aligns with the
    /// visible cloud. Uses primary instances only (never Biruscan polygon-cut clones).
    /// </summary>
    public sealed class RoiPointExportService
    {
        public static bool HasActiveIsolateClip(Document doc)
        {
            try
            {
                var pcs = new PointCloudService(doc);
                PointCloudInstance pci = pcs.GetFirstPointCloudInstance();
                if (pci == null) return false;
                return pci.FilterAction == SelectionFilterAction.Isolate;
            }
            catch { return false; }
        }

        public List<double[]> ExportCurrentCutRoiPoints(Document doc)
            => ExportCurrentCutRoiPoints(doc, maxPoints: 0);

        public List<double[]> ExportCurrentCutRoiPoints(Document doc, int maxPoints)
        {
            var result = new List<double[]>();
            var pcs = new PointCloudService(doc);

            // Prefer ClippingService primaries so clone instances from older builds
            // are not double-counted into Fit/AI pipelines.
            List<PointCloudInstance> instances;
            try
            {
                instances = new ClippingService(doc).GetPrimaryPointCloudInstances();
            }
            catch
            {
                instances = pcs.GetAllPointCloudInstances();
            }

            if (instances == null || instances.Count == 0) return result;

            // One primary is enough for a normal project; avoid summing duplicate clones.
            PointCloudInstance pci = instances[0];
            if (pci != null)
                AppendFromInstance(pcs, pci, result, maxPoints);

            if (maxPoints > 0 && result.Count > maxPoints)
                return Downsample(result, maxPoints);

            return result;
        }

        private static void AppendFromInstance(PointCloudService pcs, PointCloudInstance pci, List<double[]> result, int maxPoints)
        {
            BoundingBoxXYZ wb = null;
            try { wb = pci.get_BoundingBox(null); } catch { wb = null; }

            XYZ anchor = null;
            if (wb != null)
            {
                Transform bt = wb.Transform ?? Transform.Identity;
                // Expand mid from 8 corners via identity world box when transform is set
                XYZ midLocal = (wb.Min + wb.Max).Multiply(0.5);
                anchor = bt.OfPoint(midLocal);
            }

            Transform worldMap = pcs.GetWorldMappingTransform(pci, anchor);

            // Prefer the active isolate filter (real clip ROI).
            PointCloudFilter active = null;
            try
            {
                if (pci.FilterAction == SelectionFilterAction.Isolate)
                    active = pci.GetSelectionFilter();
            }
            catch { active = null; }

            if (active != null)
            {
                int before = result.Count;
                try
                {
                    int cap = maxPoints > 0 ? maxPoints : 40000;
                    double spacing = maxPoints > 0 && maxPoints <= 20000 ? 0.03 : 0.02;
                    PointCollection pc = pci.GetPoints(active, spacing, cap);
                    foreach (CloudPoint p in pc)
                    {
                        XYZ w = worldMap.OfPoint(new XYZ(p.X, p.Y, p.Z));
                        result.Add(new[] { w.X, w.Y, w.Z });
                    }
                }
                catch { }

                if (result.Count > before)
                    return;
            }

            // Fallback: read from the instance world AABB (not infinite half-spaces).
            // Half-space fallback previously pulled arbitrary large volumes and mixed
            // unclipped points into AI/Fit pipelines when Isolate read returned 0.
            if (wb == null) return;

            try
            {
                Transform bt = wb.Transform ?? Transform.Identity;
                double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
                XYZ[] corners =
                {
                    new XYZ(wb.Min.X, wb.Min.Y, wb.Min.Z), new XYZ(wb.Max.X, wb.Min.Y, wb.Min.Z),
                    new XYZ(wb.Min.X, wb.Max.Y, wb.Min.Z), new XYZ(wb.Max.X, wb.Max.Y, wb.Min.Z),
                    new XYZ(wb.Min.X, wb.Min.Y, wb.Max.Z), new XYZ(wb.Max.X, wb.Min.Y, wb.Max.Z),
                    new XYZ(wb.Min.X, wb.Max.Y, wb.Max.Z), new XYZ(wb.Max.X, wb.Max.Y, wb.Max.Z)
                };
                foreach (XYZ c in corners)
                {
                    XYZ w = bt.OfPoint(c);
                    if (w.X < minX) minX = w.X; if (w.X > maxX) maxX = w.X;
                    if (w.Y < minY) minY = w.Y; if (w.Y > maxY) maxY = w.Y;
                    if (w.Z < minZ) minZ = w.Z; if (w.Z > maxZ) maxZ = w.Z;
                }

                var worldBox = new BoundingBoxXYZ
                {
                    Transform = Transform.Identity,
                    Min = new XYZ(minX, minY, minZ),
                    Max = new XYZ(maxX, maxY, maxZ)
                };

                int regionCap = maxPoints > 0 ? maxPoints : 40000;
                List<XYZ> localPts = pcs.GetPointsInRegion(pci, worldBox, regionCap);
                foreach (XYZ lp in localPts)
                {
                    XYZ w = worldMap.OfPoint(lp);
                    result.Add(new[] { w.X, w.Y, w.Z });
                }
            }
            catch { }
        }

        /// <summary>Uniform stride downsample to at most <paramref name="target"/> points.</summary>
        public static List<double[]> Downsample(List<double[]> pts, int target)
        {
            if (pts == null || pts.Count <= target) return pts ?? new List<double[]>();
            var result = new List<double[]>(target);
            double step = (double)pts.Count / target;
            for (double i = 0; i < pts.Count; i += step)
                result.Add(pts[(int)i]);
            return result;
        }
    }
}
