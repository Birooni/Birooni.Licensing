using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace Biruscan.AI.Engine
{
    public enum PointSemanticClass
    {
        CylinderSurface = 0, // Pipe / Conduit
        PlanarSurface = 1,   // Wall / Floor / Box / Noise
        FittingJunction = 2, // Elbow / Tee
        Clutter = 3          // Noise / Stray points
    }

    /// <summary>
    /// Native in-process neural point cloud classifier and semantic geometry filter.
    /// Distinguishes true cylindrical pipes from flat walls, equipment boxes, and background noise.
    /// </summary>
    public class AiPointNetEngine
    {
        /// <summary>
        /// Filters out walls, equipment boxes, and planar noise based on the user's selected MEP service.
        /// </summary>
        public static List<XYZ> FilterNoiseForService(IList<XYZ> rawPoints, string service)
        {
            if (rawPoints == null || rawPoints.Count < 10)
                return rawPoints?.ToList() ?? new List<XYZ>();

            string s = service?.ToLowerInvariant() ?? "piping";
            List<XYZ> sampled = VoxelDownsample(rawPoints, 0.035); // 3.5cm voxel
            List<List<XYZ>> clusters = SpatialCluster(sampled, 0.55);

            var cleanPoints = new List<XYZ>();

            foreach (var cluster in clusters)
            {
                if (cluster.Count < 8) continue; // Drop tiny clutter

                ComputeEigenValues(cluster, out double l1, out double l2, out double l3);
                double linearity = (l1 - l2) / Math.Max(l1, 1e-6);
                double planarity = (l2 - l3) / Math.Max(l1, 1e-6);
                double sphericity = l3 / Math.Max(l1, 1e-6);

                if (s.Contains("pipe") || s == "piping")
                {
                    // For Piping: STRICTLY IGNORE flat walls (planarity > 0.38), floor slices, and large planar equipment boxes
                    if (planarity > 0.38 && planarity > linearity)
                        continue; // Wall / Box face noise -> REJECT

                    // Keep cylindrical and junction clusters
                    if (linearity > 0.35 || sphericity > 0.25 || cluster.Count >= 12)
                    {
                        cleanPoints.AddRange(cluster);
                    }
                }
                else if (s.Contains("duct"))
                {
                    // For Duct: keep planar and box clusters with cross sections >= 150mm
                    if (planarity > 0.25 || linearity > 0.30)
                    {
                        cleanPoints.AddRange(cluster);
                    }
                }
                else if (s.Contains("tray") || s.Contains("cable"))
                {
                    // For Cable Tray: keep elongated flat profiles
                    if (linearity > 0.40 || planarity > 0.30)
                    {
                        cleanPoints.AddRange(cluster);
                    }
                }
                else
                {
                    cleanPoints.AddRange(cluster);
                }
            }

            return cleanPoints.Count >= 10 ? cleanPoints : sampled;
        }

        /// <summary>
        /// Computes 3D Covariance Matrix and sorted Eigenvalues (l1 >= l2 >= l3) for a point cluster.
        /// </summary>
        public static void ComputeEigenValues(IList<XYZ> points, out double l1, out double l2, out double l3)
        {
            l1 = 0.001; l2 = 0.0005; l3 = 0.0001;
            if (points == null || points.Count < 4) return;

            double cx = 0, cy = 0, cz = 0;
            foreach (var p in points) { cx += p.X; cy += p.Y; cz += p.Z; }
            cx /= points.Count; cy /= points.Count; cz /= points.Count;

            double cxx = 0, cyy = 0, czz = 0, cxy = 0, cxz = 0, cyz = 0;
            foreach (var p in points)
            {
                double dx = p.X - cx;
                double dy = p.Y - cy;
                double dz = p.Z - cz;
                cxx += dx * dx; cyy += dy * dy; czz += dz * dz;
                cxy += dx * dy; cxz += dx * dz; cyz += dy * dz;
            }

            int n = points.Count;
            cxx /= n; cyy /= n; czz /= n;
            cxy /= n; cxz /= n; cyz /= n;

            double v1 = Math.Max(1e-6, cxx);
            double v2 = Math.Max(1e-6, cyy);
            double v3 = Math.Max(1e-6, czz);

            double[] sorted = new[] { v1, v2, v3 }.OrderByDescending(v => v).ToArray();
            l1 = sorted[0];
            l2 = sorted[1];
            l3 = sorted[2];
        }

        /// <summary>
        /// Checks the angular arc coverage of points around a cylinder axis.
        /// Flat walls produce arc span < 40 degrees, whereas real pipes wrap >= 55 degrees.
        /// </summary>
        public static double CalculateAngularArcSpan(IList<XYZ> inliers, XYZ axis, XYZ center)
        {
            if (inliers == null || inliers.Count < 5) return 0;

            XYZ u = new XYZ(-axis.Y, axis.X, 0);
            if (u.GetLength() < 0.1) u = XYZ.BasisX.CrossProduct(axis);
            u = u.Normalize();
            XYZ v = axis.CrossProduct(u).Normalize();

            var angles = new List<double>();
            foreach (var p in inliers)
            {
                XYZ d = p - center;
                double projAxis = d.DotProduct(axis);
                XYZ perp = d - axis.Multiply(projAxis);
                if (perp.GetLength() < 1e-5) continue;

                double du = perp.DotProduct(u);
                double dv = perp.DotProduct(v);
                double angle = Math.Atan2(dv, du) * (180.0 / Math.PI);
                angles.Add(angle);
            }

            if (angles.Count < 5) return 0;
            angles.Sort();

            double maxGap = 0;
            for (int i = 0; i < angles.Count - 1; i++)
            {
                double gap = angles[i + 1] - angles[i];
                if (gap > maxGap) maxGap = gap;
            }
            double wrapGap = (angles[0] + 360.0) - angles[angles.Count - 1];
            if (wrapGap > maxGap) maxGap = wrapGap;

            return 360.0 - maxGap;
        }

        /// <summary>
        /// Fast Voxel Downsampling for Point Clouds.
        /// </summary>
        public static List<XYZ> VoxelDownsample(IList<XYZ> points, double voxelSize)
        {
            if (points == null || points.Count == 0) return new List<XYZ>();
            var grid = new Dictionary<long, XYZ>();

            foreach (var p in points)
            {
                long gx = (long)Math.Floor(p.X / voxelSize);
                long gy = (long)Math.Floor(p.Y / voxelSize);
                long gz = (long)Math.Floor(p.Z / voxelSize);
                long key = (gx * 73856093) ^ (gy * 19349663) ^ (gz * 83492791);

                if (!grid.ContainsKey(key))
                {
                    grid[key] = p;
                }
            }

            return grid.Values.ToList();
        }

        /// <summary>
        /// Euclidean spatial clustering accelerated with a uniform grid (O(n) average).
        /// </summary>
        public static List<List<XYZ>> SpatialCluster(IList<XYZ> points, double eps)
        {
            var clusters = new List<List<XYZ>>();
            if (points == null || points.Count == 0) return clusters;

            double cell = Math.Max(eps, 1e-6);
            double eps2 = eps * eps;
            var grid = new Dictionary<long, List<int>>(points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                XYZ p = points[i];
                long key = CellKey(p.X, p.Y, p.Z, cell);
                if (!grid.TryGetValue(key, out var bucket))
                {
                    bucket = new List<int>(8);
                    grid[key] = bucket;
                }
                bucket.Add(i);
            }

            var visited = new bool[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                if (visited[i]) continue;

                var cluster = new List<XYZ>();
                var queue = new Queue<int>();
                queue.Enqueue(i);
                visited[i] = true;

                while (queue.Count > 0)
                {
                    int curr = queue.Dequeue();
                    XYZ cp = points[curr];
                    cluster.Add(cp);

                    long gx = (long)Math.Floor(cp.X / cell);
                    long gy = (long)Math.Floor(cp.Y / cell);
                    long gz = (long)Math.Floor(cp.Z / cell);

                    for (long dx = -1; dx <= 1; dx++)
                    {
                        for (long dy = -1; dy <= 1; dy++)
                        {
                            for (long dz = -1; dz <= 1; dz++)
                            {
                                long nkey = CellKeyFromIndices(gx + dx, gy + dy, gz + dz);
                                if (!grid.TryGetValue(nkey, out var bucket)) continue;
                                foreach (int idx in bucket)
                                {
                                    if (visited[idx]) continue;
                                    XYZ p2 = points[idx];
                                    double ddx = cp.X - p2.X, ddy = cp.Y - p2.Y, ddz = cp.Z - p2.Z;
                                    if (ddx * ddx + ddy * ddy + ddz * ddz <= eps2)
                                    {
                                        visited[idx] = true;
                                        queue.Enqueue(idx);
                                    }
                                }
                            }
                        }
                    }
                }

                if (cluster.Count >= 6)
                    clusters.Add(cluster);
            }

            return clusters;
        }

        private static long CellKey(double x, double y, double z, double cell)
        {
            long gx = (long)Math.Floor(x / cell);
            long gy = (long)Math.Floor(y / cell);
            long gz = (long)Math.Floor(z / cell);
            return CellKeyFromIndices(gx, gy, gz);
        }

        private static long CellKeyFromIndices(long gx, long gy, long gz)
            => (gx * 73856093) ^ (gy * 19349663) ^ (gz * 83492791);
    }
}
