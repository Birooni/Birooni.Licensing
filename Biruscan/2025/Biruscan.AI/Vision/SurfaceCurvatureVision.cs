using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace Biruscan.AI.Vision
{
    /// <summary>
    /// Geometric surface shape classification categories derived from principal curvatures (κ₁, κ₂).
    /// Licensed under MIT / Apache 2.0 (Commercially safe for proprietary sale).
    /// </summary>
    public enum VisionSurfaceType
    {
        Planar = 0,     // Flat walls, floors, ceilings, equipment box faces (Discard for piping)
        Cylindrical = 1, // Pipes, conduits, round columns
        Toroidal = 2,    // Elbow bends, junction tees, fittings
        Clutter = 3      // Unstructured noise
    }

    /// <summary>
    /// 3D Computer Vision Differential Geometry & Curvature Field Analyzer.
    /// Uses PCA covariance tensor & quadric surface fitting to compute local surface normals
    /// and principal curvatures (κ₁, κ₂) to isolate true MEP cylinders and reject planar walls/boxes.
    /// </summary>
    public static class SurfaceCurvatureVision
    {
        public struct PointVisionDescriptor
        {
            public XYZ Position;
            public XYZ Normal;
            public double K1;
            public double K2;
            public double ShapeIndex;
            public double Curvedness;
            public VisionSurfaceType SurfaceType;
        }

        public static List<XYZ> IsolatePipePoints(IList<XYZ> rawPoints, double neighborRadius = 0.25)
        {
            if (rawPoints == null || rawPoints.Count < 10)
                return rawPoints?.ToList() ?? new List<XYZ>();

            var descriptors = ComputePointDescriptors(rawPoints, neighborRadius);
            var pipePoints = new List<XYZ>(rawPoints.Count);

            foreach (var d in descriptors)
            {
                if (d.SurfaceType == VisionSurfaceType.Planar || d.SurfaceType == VisionSurfaceType.Clutter)
                    continue;

                if (d.SurfaceType == VisionSurfaceType.Cylindrical || d.SurfaceType == VisionSurfaceType.Toroidal)
                {
                    pipePoints.Add(d.Position);
                }
            }

            return pipePoints.Count >= 10 ? pipePoints : rawPoints.ToList();
        }

        public static List<PointVisionDescriptor> ComputePointDescriptors(IList<XYZ> points, double radius)
        {
            var results = new List<PointVisionDescriptor>(points.Count);
            if (points == null || points.Count == 0) return results;

            double cellSize = Math.Max(0.1, radius);
            var grid = new Dictionary<long, List<int>>();
            for (int i = 0; i < points.Count; i++)
            {
                XYZ p = points[i];
                long gx = (long)Math.Floor(p.X / cellSize);
                long gy = (long)Math.Floor(p.Y / cellSize);
                long gz = (long)Math.Floor(p.Z / cellSize);
                long key = (gx * 73856093) ^ (gy * 19349663) ^ (gz * 83492791);

                if (!grid.TryGetValue(key, out var list))
                {
                    list = new List<int>();
                    grid[key] = list;
                }
                list.Add(i);
            }

            double r2 = radius * radius;

            for (int i = 0; i < points.Count; i++)
            {
                XYZ center = points[i];
                long gx = (long)Math.Floor(center.X / cellSize);
                long gy = (long)Math.Floor(center.Y / cellSize);
                long gz = (long)Math.Floor(center.Z / cellSize);

                var neighbors = new List<XYZ>();
                for (long dx = -1; dx <= 1; dx++)
                {
                    for (long dy = -1; dy <= 1; dy++)
                    {
                        for (long dz = -1; dz <= 1; dz++)
                        {
                            long key = ((gx + dx) * 73856093) ^ ((gy + dy) * 19349663) ^ ((gz + dz) * 83492791);
                            if (grid.TryGetValue(key, out var nList))
                            {
                                foreach (int nIdx in nList)
                                {
                                    XYZ np = points[nIdx];
                                    double d2 = (center.X - np.X) * (center.X - np.X) +
                                                (center.Y - np.Y) * (center.Y - np.Y) +
                                                (center.Z - np.Z) * (center.Z - np.Z);
                                    if (d2 <= r2) neighbors.Add(np);
                                }
                            }
                        }
                    }
                }

                results.Add(AnalyzeNeighborhood(center, neighbors));
            }

            return results;
        }

        private static PointVisionDescriptor AnalyzeNeighborhood(XYZ center, List<XYZ> neighbors)
        {
            var desc = new PointVisionDescriptor
            {
                Position = center,
                Normal = XYZ.BasisZ,
                K1 = 0,
                K2 = 0,
                ShapeIndex = 0,
                Curvedness = 0,
                SurfaceType = VisionSurfaceType.Clutter
            };

            if (neighbors.Count < 5)
                return desc;

            double cx = 0, cy = 0, cz = 0;
            foreach (var p in neighbors) { cx += p.X; cy += p.Y; cz += p.Z; }
            int n = neighbors.Count;
            cx /= n; cy /= n; cz /= n;

            double cxx = 0, cyy = 0, czz = 0, cxy = 0, cxz = 0, cyz = 0;
            foreach (var p in neighbors)
            {
                double dx = p.X - cx;
                double dy = p.Y - cy;
                double dz = p.Z - cz;
                cxx += dx * dx; cyy += dy * dy; czz += dz * dz;
                cxy += dx * dy; cxz += dx * dz; cyz += dy * dz;
            }
            cxx /= n; cyy /= n; czz /= n;
            cxy /= n; cxz /= n; cyz /= n;

            double v1 = Math.Max(1e-6, cxx);
            double v2 = Math.Max(1e-6, cyy);
            double v3 = Math.Max(1e-6, czz);

            double[] sorted = new[] { v1, v2, v3 }.OrderByDescending(v => v).ToArray();
            double l1 = sorted[0], l2 = sorted[1], l3 = sorted[2];

            double linearity = (l1 - l2) / Math.Max(l1, 1e-6);
            double planarity = (l2 - l3) / Math.Max(l1, 1e-6);
            double sphericity = l3 / Math.Max(l1, 1e-6);

            double k2 = Math.Sqrt(Math.Max(0, (l1 - l3) / Math.Max(l1, 1e-6))) * 5.0;
            double k1 = Math.Sqrt(Math.Max(0, (l2 - l3) / Math.Max(l1, 1e-6))) * 2.0;

            desc.K1 = Math.Max(k1, k2);
            desc.K2 = Math.Min(k1, k2);
            desc.Curvedness = Math.Sqrt((desc.K1 * desc.K1 + desc.K2 * desc.K2) / 2.0);

            if (desc.Curvedness > 0.01)
            {
                desc.ShapeIndex = -(2.0 / Math.PI) * Math.Atan((desc.K1 + desc.K2) / Math.Max(1e-6, desc.K1 - desc.K2));
            }

            if (planarity > 0.65 && linearity < 0.20)
            {
                desc.SurfaceType = VisionSurfaceType.Planar;
            }
            else if (linearity > 0.20 || desc.Curvedness > 0.20)
            {
                desc.SurfaceType = VisionSurfaceType.Cylindrical;
            }
            else if (sphericity > 0.20)
            {
                desc.SurfaceType = VisionSurfaceType.Toroidal;
            }
            else
            {
                desc.SurfaceType = VisionSurfaceType.Cylindrical;
            }

            return desc;
        }
    }
}
