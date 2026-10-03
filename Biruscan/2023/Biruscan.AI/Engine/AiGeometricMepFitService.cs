using Autodesk.Revit.DB;
using Biruscan.AI.Contracts;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Biruscan.AI.Engine
{
    /// <summary>
    /// Native geometric MEP extraction for Fit Generate MEP.
    /// Clusters the clipped cloud, peels cylinders per cluster, then builds
    /// a connected pipe network (merge, branch split, junction detect).
    /// </summary>
    public sealed class AiGeometricMepFitService
    {
        private static readonly double[] NominalInches =
        {
            0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 2.5, 3.0, 3.5, 4.0, 5.0, 6.0,
            8.0, 10.0, 12.0, 14.0, 16.0, 18.0, 20.0, 24.0, 30.0, 36.0
        };

        private const int MaxPipesPerCluster = 16;
        private const double MinLengthFt = 0.2;
        private const int MinRemaining = 12;
        private const int MinRemainingRelaxed = 10;
        private const int MinClusterSize = 8;
        private const double ClusterEpsFt = 0.18;
        private const double VoxelFtSmall = 0.05;
        private const double VoxelFtLarge = 0.07;
        private const int VoxelAdaptiveThreshold = 8000;
        private const int MaxSampledPoints = 12000;
        private const float MinConfidence = 0.42f;

        public List<BirooniAiElement> ExtractPipingRuns(IList<XYZ> worldPoints, double? diameterOverrideMm)
        {
            if (worldPoints == null || worldPoints.Count < MinRemaining)
                return new List<BirooniAiElement>();

            // Larger clouds use a coarser voxel so a small dense clip does not explode.
            double voxel = worldPoints.Count > VoxelAdaptiveThreshold ? VoxelFtLarge : VoxelFtSmall;
            List<XYZ> sampled = VoxelDownsample(worldPoints, voxel);
            if (sampled.Count > MaxSampledPoints)
                sampled = StrideDownsample(sampled, MaxSampledPoints);
            List<List<XYZ>> clusters = SpatialCluster(sampled, ClusterEpsFt);

            var rawSegments = new List<AiPipeNetworkBuilder.Segment>();
            var usedKeys = new HashSet<string>();

            foreach (List<XYZ> cluster in clusters)
            {
                if (IsFlatWallCluster(cluster) || IsPlanarLeftover(cluster)) continue;
                AddSegments(rawSegments, usedKeys, PeelCluster(cluster, diameterOverrideMm, relaxed: false));
            }

            if (rawSegments.Count == 0)
            {
                foreach (List<XYZ> cluster in clusters)
                {
                    if (IsFlatWallCluster(cluster) || IsPlanarLeftover(cluster)) continue;
                    AddSegments(rawSegments, usedKeys, PeelCluster(cluster, diameterOverrideMm, relaxed: true));
                }
            }

            // Pass 3: if nothing found, try whole sampled cloud
            if (rawSegments.Count == 0)
            {
                AddSegments(rawSegments, usedKeys, PeelCluster(sampled, diameterOverrideMm, relaxed: true));
            }

            // Pass 4: hierarchical re-clustering
            if (rawSegments.Count == 0 && clusters.Count > 1)
            {
                // Merge distance of 0.9 ft (~11 inches) instead of 1.6+ ft to prevent
                // bridging entirely separate pipe runs.
                List<List<XYZ>> merged = MergeNearbyClusters(clusters, 0.9);
                foreach (List<XYZ> mc in merged)
                {
                    if (mc.Count < MinClusterSize) continue;
                    AddSegments(rawSegments, usedKeys, PeelCluster(mc, diameterOverrideMm, relaxed: true));
                }
            }

            rawSegments = FilterDuplicateSegments(rawSegments);
            rawSegments = FilterPhantomSegments(rawSegments);
            AlignSegmentsToPointCloud(rawSegments, sampled);
            HealSegmentJunctions(rawSegments);
            return AiPipeNetworkBuilder.BuildNetwork(rawSegments, preserveAlignment: true);
        }

        private static void AddSegments(List<AiPipeNetworkBuilder.Segment> target, HashSet<string> usedKeys,
            List<AiPipeNetworkBuilder.Segment> incoming)
        {
            foreach (AiPipeNetworkBuilder.Segment seg in incoming)
            {
                if (seg.Confidence < MinConfidence) continue;
                string key = RunKey(seg.Start, seg.End, seg.DiameterMm / 25.4);
                if (!usedKeys.Add(key)) continue;
                target.Add(seg);
            }
        }

                private static List<AiPipeNetworkBuilder.Segment> PeelCluster(List<XYZ> clusterPoints,
            double? diameterOverrideMm, bool relaxed)
        {
            var segments = new List<AiPipeNetworkBuilder.Segment>();
            var remaining = new List<XYZ>(clusterPoints);
            var used = new HashSet<string>();
            var rnd = new Random(42);

            int minRemaining = relaxed ? MinRemainingRelaxed : MinRemaining;
            int minFitPoints = relaxed ? 8 : 9;
            int maxPipes = Math.Max(1, Math.Min(MaxPipesPerCluster, Math.Max(2, clusterPoints.Count / 22)));
            int rejectStreak = 0;

            for (int iter = 0; iter < maxPipes && remaining.Count >= minRemaining; iter++)
            {
                if (iter > 0 && remaining.Count >= 24 && IsPlanarLeftover(remaining)) break;

                bool branched = iter == 0 && IsBranchedCluster(remaining);
                AiCylinderFit.Result fit = branched
                    ? new AiCylinderFit.Result { Success = false }
                    : AiCylinderFit.Fit(remaining, relaxed);

                if (!fit.Success && remaining.Count >= 14)
                {
                    int retries = branched || iter > 0 ? 10 : 5;
                    double rad2 = (branched || relaxed) ? 4.0 : 6.25;
                    for (int retry = 0; retry < retries; retry++)
                    {
                        XYZ seed = remaining[rnd.Next(remaining.Count)];
                        var localPts = new List<XYZ>();
                        for (int i = 0; i < remaining.Count; i++)
                        {
                            XYZ p = remaining[i];
                            double dx = p.X - seed.X, dy = p.Y - seed.Y, dz = p.Z - seed.Z;
                            if (dx * dx + dy * dy + dz * dz < rad2)
                                localPts.Add(p);
                        }
                        if (localPts.Count >= 16)
                        {
                            fit = AiCylinderFit.Fit(localPts, relaxed);
                            if (fit.Success) break;
                        }
                    }
                }

                if (!fit.Success || fit.PointCount < minFitPoints)
                {
                    rejectStreak++;
                    if (rejectStreak >= 3) break;
                    continue;
                }
                rejectStreak = 0;

                List<XYZ> refinePts = remaining.Count > 1200 ? StrideDownsample(remaining, 1200) : remaining;
                AiCylinderFit.RefineToPointCloud(ref fit, refinePts);

                double lengthFt = fit.Start.DistanceTo(fit.End);
                                if (!AcceptFit(fit, lengthFt, remaining.Count, relaxed))
                {
                    remaining = RemoveCylinderInliers(remaining, fit, aggressive: true);
                    continue;
                }
                if (iter > 0 && !AcceptSecondaryPeel(fit, lengthFt, remaining.Count))
                {
                    remaining = RemoveCylinderInliers(remaining, fit, aggressive: true);
                    continue;
                }

                double measuredIn = fit.Radius * 2.0 * 12.0;
                double nominalIn = diameterOverrideMm.HasValue && diameterOverrideMm.Value > 0
                    ? diameterOverrideMm.Value / 25.4
                    : SnapNominal(measuredIn);
                double diaMm = nominalIn * 25.4;

                string key = RunKey(fit.Start, fit.End, nominalIn);
                if (used.Contains(key))
                {
                    remaining = RemoveCylinderInliers(remaining, fit, aggressive: true);
                    continue;
                }
                used.Add(key);

                segments.Add(new AiPipeNetworkBuilder.Segment
                {
                    Start = fit.Start,
                    End = fit.End,
                    Axis = fit.Axis,
                    DiameterMm = diaMm,
                    RadiusFt = fit.Radius,
                    ShellPointCount = fit.PointCount,
                    Confidence = ScoreConfidence(fit, lengthFt),
                });

                int before = remaining.Count;
                remaining = RemoveCylinderInliers(remaining, fit, aggressive: false);
                if (remaining.Count >= before) break;
            }
            return segments;
        }

                        private static bool AcceptFit(AiCylinderFit.Result fit, double lengthFt, int cloudCount, bool relaxed)
        {
            if (fit.Radius < 0.015 || fit.Radius > 0.85) return false;
            if (lengthFt < MinLengthFt) return false;

            // Strict slenderness: real pipes are elongated tubes, not short fat barrels
            double slenderness = lengthFt / (fit.Radius * 2.0);
            if (slenderness < 1.3) return false;

            double rmsRatio = fit.Rms / Math.Max(fit.Radius, 0.03);
            if (rmsRatio > (relaxed ? 0.36 : 0.30)) return false;

            if (fit.PointCount < (relaxed ? 6 : 8)) return false;
            if (fit.ArcSpanDeg < (relaxed ? 18.0 : 22.0)) return false;

            return true;
        }

        private static bool AcceptSecondaryPeel(AiCylinderFit.Result fit, double lengthFt, int cloudCount)
        {
            if (fit.Radius < 0.015 || fit.Radius > 0.85) return false;
            if (lengthFt < MinLengthFt) return false;

            double slenderness = lengthFt / (fit.Radius * 2.0);
            if (slenderness < 1.3) return false;

            if (fit.PointCount < 7) return false;
            if (fit.ArcSpanDeg < 18.0) return false;
            if (fit.Rms / Math.Max(fit.Radius, 0.03) > 0.32) return false;
            return true;
        }

        

        private static bool HasPointShellSupport(AiCylinderFit.Result fit, int shellCount, int clusterCount, bool relaxed)
        {
            // Shell density check, clamped to 120 degrees min
            double effectiveArc = Math.Max(fit.ArcSpanDeg, 120.0);
            double expectedAreaSqFt = (effectiveArc / 180.0 * Math.PI) * fit.Radius * 1.0; // Per 1 ft length
            double expectedShellPts = expectedAreaSqFt / 0.0016;
            double shellFillRatio = shellCount / Math.Max(1.0, expectedShellPts);

            double minShellFill = relaxed ? 0.010 : 0.020;
            if (fit.Radius > 0.2) minShellFill += (fit.Radius - 0.2) * 0.15;
            if (shellFillRatio < minShellFill) return false;

            int minShell = relaxed ? 5 : 7;
            if (shellCount < minShell) return false;

            if (shellCount >= minShell * 2) return true;
            if (shellCount >= minShell && fit.ArcSpanDeg < 130) return true;

            double ratio = shellCount / (double)Math.Max(clusterCount, 1);
            return shellCount >= minShell && ratio >= (relaxed ? 0.05 : 0.07);
        }

        private static int CountShellPoints(AiCylinderFit.Result fit, IList<XYZ> points)
        {
            XYZ axis = fit.Axis;
            double len = fit.Start.DistanceTo(fit.End);
            double tol = Tol(fit.Radius) * (fit.ArcSpanDeg < 120 ? 1.35 : 1.15);
            int count = 0;

            foreach (XYZ p in points)
            {
                XYZ rel = p - fit.Start;
                double t = rel.DotProduct(axis);
                if (t < -0.2 || t > len + 0.2) continue;

                XYZ onAxis = fit.Start + axis.Multiply(t);
                if (Math.Abs((p - onAxis).GetLength() - fit.Radius) <= tol) count++;
            }

            return count;
        }

        /// <summary>
        /// Remove only obvious phantoms — keep real pipes even in sparse isometric partial scans.
        /// </summary>
                private static void AlignSegmentsToPointCloud(List<AiPipeNetworkBuilder.Segment> segs, IList<XYZ> points)
        {
            foreach (AiPipeNetworkBuilder.Segment seg in segs)
            {
                double len = seg.Start.DistanceTo(seg.End);
                if (len < 0.1) continue;
                XYZ axis = seg.Axis ?? Norm(seg.End - seg.Start);
                double rad = seg.RadiusFt > 1e-6 ? seg.RadiusFt : (seg.DiameterMm / 304.8 / 2.0);

                var localPoints = new List<XYZ>();
                foreach (XYZ p in points)
                {
                    XYZ rel = p - seg.Start;
                    double t = rel.DotProduct(axis);
                    if (t < -0.2 || t > len + 0.2) continue;
                    XYZ onAxis = seg.Start + axis.Multiply(t);
                    if ((p - onAxis).GetLength() <= rad * 2.2 + 0.1)
                        localPoints.Add(p);
                }

                if (localPoints.Count < 6) continue;

                AiCylinderFit.Result fit = new AiCylinderFit.Result
                {
                    Success = true,
                    Start = seg.Start,
                    End = seg.End,
                    Axis = axis,
                    Radius = rad,
                };
                AiCylinderFit.RefineToPointCloud(ref fit, localPoints);
                seg.Start = fit.Start;
                seg.End = fit.End;
                seg.Axis = fit.Axis;
                seg.RadiusFt = fit.Radius;
            }
        }

        private static void HealSegmentJunctions(List<AiPipeNetworkBuilder.Segment> segs)
        {
            const double tol = 0.28;
            if (segs.Count < 2) return;

            var endpoints = new List<(int segIdx, bool isStart, XYZ pt)>();
            for (int i = 0; i < segs.Count; i++)
            {
                endpoints.Add((i, true, segs[i].Start));
                endpoints.Add((i, false, segs[i].End));
            }

            var used = new bool[endpoints.Count];
            for (int i = 0; i < endpoints.Count; i++)
            {
                if (used[i]) continue;
                var cluster = new List<int> { i };
                used[i] = true;
                var segIds = new HashSet<int> { endpoints[i].segIdx };

                for (int j = i + 1; j < endpoints.Count; j++)
                {
                    if (used[j]) continue;
                    if (endpoints[i].pt.DistanceTo(endpoints[j].pt) > tol) continue;
                    cluster.Add(j);
                    used[j] = true;
                    segIds.Add(endpoints[j].segIdx);
                }

                if (segIds.Count < 2) continue;

                double x = 0, y = 0, z = 0;
                foreach (int idx in cluster)
                {
                    x += endpoints[idx].pt.X;
                    y += endpoints[idx].pt.Y;
                    z += endpoints[idx].pt.Z;
                }
                XYZ c = new XYZ(x / cluster.Count, y / cluster.Count, z / cluster.Count);
                foreach (int idx in cluster)
                {
                    if (endpoints[idx].isStart) segs[endpoints[idx].segIdx].Start = c;
                    else segs[endpoints[idx].segIdx].End = c;
                }
            }
        }

        private static List<AiPipeNetworkBuilder.Segment> FilterPhantomSegments(List<AiPipeNetworkBuilder.Segment> segs)
        {
            if (segs.Count <= 1) return segs;

            var kept = new List<AiPipeNetworkBuilder.Segment>();
            foreach (AiPipeNetworkBuilder.Segment seg in segs)
            {
                bool phantom = seg.ShellPointCount < 5
                    && seg.Confidence < 0.48f
                    && seg.Start.DistanceTo(seg.End) < 0.6;
                if (!phantom) kept.Add(seg);
            }

            return kept.Count > 0 ? kept : segs;
        }

                private static List<AiPipeNetworkBuilder.Segment> FilterDuplicateSegments(List<AiPipeNetworkBuilder.Segment> segs)
        {
            if (segs.Count <= 1) return segs;

            // Prioritize higher confidence and tighter, realistic pipe radii
            segs.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));
            var kept = new List<AiPipeNetworkBuilder.Segment>();

            foreach (AiPipeNetworkBuilder.Segment candidate in segs)
            {
                bool duplicate = false;
                XYZ axisC = Norm(candidate.End - candidate.Start);

                foreach (AiPipeNetworkBuilder.Segment existing in kept)
                {
                    XYZ axisE = Norm(existing.End - existing.Start);
                    if (Math.Abs(axisC.DotProduct(axisE)) < 0.94) continue;

                    double maxDiaFt = Math.Max(candidate.DiameterMm, existing.DiameterMm) / 304.8;
                    double perp = PerpDistance(existing.Start, existing.End, candidate.Start);
                    if (perp > Math.Max(maxDiaFt * 0.9, 0.25))
                        continue;

                    if (AxisOverlapRatio(candidate, existing) < 0.15) continue;
                    
                    // Same physical 3D pipe corridor: keep the tighter/more confident segment!
                    duplicate = true;
                    break;
                }

                if (!duplicate) kept.Add(candidate);
            }

            return kept;
        }

        private static double AxisOverlapRatio(AiPipeNetworkBuilder.Segment a, AiPipeNetworkBuilder.Segment b)
        {
            XYZ axis = Norm(a.End - a.Start);
            Project(a.Start, axis, b.Start, out double tb0, out _);
            Project(a.Start, axis, b.End, out double tb1, out _);
            double bMin = Math.Min(tb0, tb1);
            double bMax = Math.Max(tb0, tb1);
            double lenA = a.Start.DistanceTo(a.End);
            double overlap = Math.Max(0, Math.Min(lenA, bMax) - Math.Max(0, bMin));
            return overlap / Math.Max(lenA, 1e-6);
        }

        private static double PerpDistance(XYZ a0, XYZ a1, XYZ p)
        {
            XYZ axis = Norm(a1 - a0);
            XYZ rel = p - a0;
            return (rel - axis.Multiply(rel.DotProduct(axis))).GetLength();
        }

        private static void Project(XYZ origin, XYZ axis, XYZ p, out double t, out double perp)
        {
            XYZ rel = p - origin;
            t = rel.DotProduct(axis);
            perp = (rel - axis.Multiply(t)).GetLength();
        }

        private static XYZ Norm(XYZ v)
        {
            double l = v.GetLength();
            return l < 1e-12 ? XYZ.BasisZ : v.Multiply(1.0 / l);
        }

        private static List<XYZ> StrideDownsample(List<XYZ> pts, int target)
        {
            if (pts == null || pts.Count <= target) return pts ?? new List<XYZ>();
            var result = new List<XYZ>(target);
            double step = (double)pts.Count / target;
            for (double i = 0; i < pts.Count; i += step)
                result.Add(pts[(int)i]);
            return result;
        }

        private static List<XYZ> VoxelDownsample(IList<XYZ> pts, double voxel)
        {
            var map = new Dictionary<long, XYZ>();
            foreach (XYZ p in pts)
            {
                long key = CellKey(p, voxel);
                if (!map.ContainsKey(key)) map[key] = p;
            }
            return new List<XYZ>(map.Values);
        }

        private static List<List<XYZ>> SpatialCluster(List<XYZ> pts, double eps)
        {
            int n = pts.Count;
            var visited = new bool[n];
            var clusters = new List<List<XYZ>>();
            var grid = BuildGrid(pts, eps);

            for (int i = 0; i < n; i++)
            {
                if (visited[i]) continue;
                var cluster = new List<XYZ>();
                var queue = new Queue<int>();
                queue.Enqueue(i);
                visited[i] = true;

                while (queue.Count > 0)
                {
                    int u = queue.Dequeue();
                    cluster.Add(pts[u]);
                    foreach (int v in Neighbors(pts[u], grid, eps))
                    {
                        if (visited[v]) continue;
                        if (pts[u].DistanceTo(pts[v]) > eps) continue;
                        visited[v] = true;
                        queue.Enqueue(v);
                    }
                }

                if (cluster.Count >= MinClusterSize)
                    clusters.Add(cluster);
            }

            return clusters;
        }

        /// <summary>
        /// Merges clusters whose bounding spheres are within <paramref name="mergeDist"/>.
        /// Used as a fallback when initial clustering splits a pipe network into
        /// fragments too small to peel individually.
        /// </summary>
        private static List<List<XYZ>> MergeNearbyClusters(List<List<XYZ>> clusters, double mergeDist)
        {
            if (clusters == null || clusters.Count <= 1) return clusters;

            // Compute centroid of each cluster.
            var centroids = new XYZ[clusters.Count];
            for (int i = 0; i < clusters.Count; i++)
            {
                double x = 0, y = 0, z = 0;
                foreach (XYZ p in clusters[i]) { x += p.X; y += p.Y; z += p.Z; }
                int n = clusters[i].Count;
                centroids[i] = new XYZ(x / n, y / n, z / n);
            }

            // Union-Find merge.
            int[] parent = new int[clusters.Count];
            for (int i = 0; i < parent.Length; i++) parent[i] = i;

            for (int i = 0; i < clusters.Count; i++)
                for (int j = i + 1; j < clusters.Count; j++)
                    if (centroids[i].DistanceTo(centroids[j]) <= mergeDist)
                        Union(parent, i, j);

            var groups = new Dictionary<int, List<XYZ>>();
            for (int i = 0; i < clusters.Count; i++)
            {
                int root = Find(parent, i);
                if (!groups.ContainsKey(root)) groups[root] = new List<XYZ>();
                groups[root].AddRange(clusters[i]);
            }

            return new List<List<XYZ>>(groups.Values);
        }

        private static int Find(int[] parent, int i)
        {
            while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
            return i;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int ra = Find(parent, a), rb = Find(parent, b);
            if (ra != rb) parent[ra] = rb;
        }

        private static Dictionary<long, List<int>> BuildGrid(List<XYZ> pts, double cell)
        {
            var grid = new Dictionary<long, List<int>>();
            for (int i = 0; i < pts.Count; i++)
            {
                long key = CellKey(pts[i], cell);
                if (!grid.ContainsKey(key)) grid[key] = new List<int>();
                grid[key].Add(i);
            }
            return grid;
        }

        private static IEnumerable<int> Neighbors(XYZ p, Dictionary<long, List<int>> grid, double cell)
        {
            int ix = (int)Math.Floor(p.X / cell);
            int iy = (int)Math.Floor(p.Y / cell);
            int iz = (int)Math.Floor(p.Z / cell);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        long key = PackCell(ix + dx, iy + dy, iz + dz);
                        if (grid.TryGetValue(key, out List<int> list))
                            foreach (int idx in list) yield return idx;
                    }
        }

        private static long CellKey(XYZ p, double cell)
        {
            return PackCell(
                (int)Math.Floor(p.X / cell),
                (int)Math.Floor(p.Y / cell),
                (int)Math.Floor(p.Z / cell));
        }

        private static long PackCell(int x, int y, int z)
        {
            return ((long)x & 0x1FFFFF) | (((long)y & 0x1FFFFF) << 21) | (((long)z & 0x1FFFFF) << 42);
        }

        private static string RunKey(XYZ a, XYZ b, double diaIn)
        {
            string p1 = $"{a.X:F1},{a.Y:F1},{a.Z:F1}";
            string p2 = $"{b.X:F1},{b.Y:F1},{b.Z:F1}";
            if (string.CompareOrdinal(p1, p2) > 0) { string t = p1; p1 = p2; p2 = t; }
            return p1 + "|" + p2 + "|" + diaIn.ToString("F2");
        }

        private static float ScoreConfidence(AiCylinderFit.Result fit, double lengthFt)
        {
            double rmsIn = fit.Rms * 12.0;
            double score = 1.0 - Math.Min(0.5, rmsIn / Math.Max(fit.Radius * 12.0, 0.5));
            if (fit.ArcSpanDeg < 120) score *= 0.92;
            if (lengthFt < 1.0) score *= 0.88;
            return (float)Math.Max(0.38, Math.Min(0.99, score));
        }

        private static bool IsPlanarLeftover(List<XYZ> pts)
        {
            if (pts == null || pts.Count < 16) return false;
            double cx = 0, cy = 0, cz = 0;
            foreach (XYZ p in pts) { cx += p.X; cy += p.Y; cz += p.Z; }
            double inv = 1.0 / pts.Count;
            cx *= inv; cy *= inv; cz *= inv;
            double cxx = 0, cxy = 0, cxz = 0, cyy = 0, cyz = 0, czz = 0;
            foreach (XYZ p in pts)
            {
                double dx = p.X - cx, dy = p.Y - cy, dz = p.Z - cz;
                cxx += dx * dx; cxy += dx * dy; cxz += dx * dz;
                cyy += dy * dy; cyz += dy * dz; czz += dz * dz;
            }
            double[,] A = { { cxx * inv, cxy * inv, cxz * inv }, { cxy * inv, cyy * inv, cyz * inv }, { cxz * inv, cyz * inv, czz * inv } };
            AiCylinderFit.Jacobi(A, out double[] ev, out _);
            double[] s = ev.Select(v => Math.Max(1e-12, Math.Abs(v))).OrderByDescending(v => v).ToArray();
            double planarity = (s[1] - s[2]) / s[0];
            double linearity = (s[0] - s[1]) / s[0];
            return planarity > 0.42 && planarity > linearity * 1.15;
        }

        /// <summary>
        /// L / U / tee clouds have two comparable extents. A single global cylinder
        /// locks onto the bounding-box diagonal and misses the real branch.
        /// </summary>
        private static bool IsBranchedCluster(List<XYZ> cluster)
        {
            if (cluster == null || cluster.Count < 20) return false;
            double cx = 0, cy = 0, cz = 0;
            foreach (XYZ p in cluster) { cx += p.X; cy += p.Y; cz += p.Z; }
            double inv = 1.0 / cluster.Count;
            cx *= inv; cy *= inv; cz *= inv;
            double cxx = 0, cxy = 0, cxz = 0, cyy = 0, cyz = 0, czz = 0;
            foreach (XYZ p in cluster)
            {
                double dx = p.X - cx, dy = p.Y - cy, dz = p.Z - cz;
                cxx += dx * dx; cxy += dx * dy; cxz += dx * dz;
                cyy += dy * dy; cyz += dy * dz; czz += dz * dz;
            }
            double[,] A = { { cxx * inv, cxy * inv, cxz * inv }, { cxy * inv, cyy * inv, cyz * inv }, { cxz * inv, cyz * inv, czz * inv } };
            AiCylinderFit.Jacobi(A, out double[] ev, out _);
            double[] sorted = ev.Select(v => Math.Max(1e-12, Math.Abs(v))).OrderByDescending(v => v).ToArray();
            return sorted[1] / sorted[0] > 0.22;
        }

        private static bool IsFlatWallCluster(List<XYZ> cluster)
        {
            if (cluster == null || cluster.Count < 100) return false;

            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            double minZ = double.MaxValue, maxZ = double.MinValue;

            foreach (var p in cluster)
            {
                if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
                if (p.Z < minZ) minZ = p.Z; if (p.Z > maxZ) maxZ = p.Z;
            }

            double dx = maxX - minX;
            double dy = maxY - minY;
            double dz = maxZ - minZ;

            double[] dims = new[] { dx, dy, dz }.OrderByDescending(d => d).ToArray();
            double d1 = dims[0], d2 = dims[1], d3 = dims[2];

            // Must be large in 2 dimensions
            if (d1 < 2.0 || d2 < 2.0) return false;

            // Density check (Fill Ratio). 
            // A wall is a solid sheet, so it fills its 2D bounding box.
            // A pipe network is mostly empty space in its 2D bounding box.
            // At VoxelFtSmall = 0.04 ft, 1 sq ft of solid wall has roughly (1/0.04)^2 = 625 points.
            // For a corrugated wall, maybe 400-600 points per sq ft.
            // Let's compute actual points per sq ft of the 2D bounding box area.
            double areaSqFt = d1 * d2;
            double densitySqFt = cluster.Count / areaSqFt;

            // A 4-inch pipe (0.33 ft wide) running diagonally across a 10x10 area (length 14 ft)
            // Area = 100 sq ft. Pipe area = 14 * 0.33 = 4.6 sq ft.
            // Pipe points = 4.6 * 625 = 2875. Density = 2875 / 100 = 28 pts / sq ft.
            // A solid wall of 10x10 has Area = 100. Points = 62500. Density = 625 pts / sq ft.
            
            // If the 3rd dimension is thin (< 1.5 ft) AND density is high (> 120 pts / sq ft)
            // it is a wall or ceiling.
            if (d3 < 1.8 && densitySqFt > 350.0)
            {
                return true;
            }

            return false;
        }

        private static List<XYZ> RemoveCylinderInliers(List<XYZ> pts, AiCylinderFit.Result fit, bool aggressive)
        {
            double tol = aggressive ? Tol(fit.Radius) * 1.25 : Tol(fit.Radius) * 0.95;
            double pad = aggressive ? 0.10 : 0.04; // Tight padding to protect connected branches, elbows, and tees!
            double axisLen = fit.Start.DistanceTo(fit.End);

            var kept = new List<XYZ>(pts.Count);
            foreach (XYZ p in pts)
            {
                XYZ rel = p - fit.Start;
                double t = rel.DotProduct(fit.Axis);
                if (t < -pad || t > axisLen + pad) { kept.Add(p); continue; }

                XYZ onAxis = fit.Start + fit.Axis.Multiply(t);
                if (Math.Abs((p - onAxis).GetLength() - fit.Radius) < tol) continue;
                kept.Add(p);
            }
            return kept;
        }

        private static double Tol(double r) => Math.Min(Math.Max(0.05, 0.15 * r), 0.20);

                private static double SnapNominal(double measuredOdInches)
        {
            // Standard ASME B36.10 Pipe Dimensions (OD -> Nominal)
            var lookup = new (double Nominal, double OD)[]
            {
                (0.5, 0.840), (0.75, 1.050), (1.0, 1.315), (1.25, 1.660),
                (1.5, 1.900), (2.0, 2.375), (2.5, 2.875), (3.0, 3.500),
                (3.5, 4.000), (4.0, 4.500), (5.0, 5.563), (6.0, 6.625),
                (8.0, 8.625), (10.0, 10.750), (12.0, 12.750), (14.0, 14.000),
                (16.0, 16.000), (18.0, 18.000), (20.0, 20.000), (24.0, 24.000),
                (30.0, 30.000), (36.0, 36.000)
            };

            if (measuredOdInches <= lookup[0].OD) return lookup[0].Nominal;
            if (measuredOdInches >= lookup[lookup.Length - 1].OD) return Math.Round(measuredOdInches); // Above 14", OD == Nominal

            double bestDiff = double.MaxValue;
            double bestNominal = lookup[0].Nominal;

            foreach (var pipe in lookup)
            {
                double diff = Math.Abs(measuredOdInches - pipe.OD);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    bestNominal = pipe.Nominal;
                }
            }
            return bestNominal;
        }
    }
}














