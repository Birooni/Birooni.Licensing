using Autodesk.Revit.DB;
using Biruscan.Services.Ai;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Biruscan.Services
{
    /// <summary>
    /// Native geometric MEP extraction for Fit Generate MEP.
    /// Clusters the clipped cloud, peels cylinders per cluster, then builds
    /// a connected pipe network (merge, branch split, junction detect).
    /// </summary>
    public sealed class GeometricMepFitService
    {
        private static readonly double[] NominalInches =
        {
            0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 2.5, 3.0, 3.5, 4.0, 5.0, 6.0,
            8.0, 10.0, 12.0, 14.0, 16.0, 18.0, 20.0, 24.0, 30.0, 36.0
        };

        private const int MaxPipesPerCluster = 24;
        private const double MinLengthFt = 0.35;
        private const int MinRemaining = 10;
        private const int MinRemainingRelaxed = 8;
        private const int MinClusterSize = 8;
        private const double ClusterEpsFt = 0.22;
        private const double VoxelFtSmall = 0.04;
        private const double VoxelFtLarge = 0.025;
        private const int VoxelAdaptiveThreshold = 5000;
        private const float MinConfidence = 0.46f;
        /// <summary>
        /// Unified geometric extraction for Generate MEP:
        /// - Cylindrical fit for circular services (Piping, Conduit, Round Duct)
        /// - Rectangular PCA bounding fit for rectangular services (Duct, Cable Tray)
        /// </summary>
        public List<BirooniAiElement> ExtractMepRuns(IList<XYZ> worldPoints, string service, double? diameterOverrideMm, bool skipVerticalRisers = false)
        {
            if (worldPoints == null || worldPoints.Count < MinRemaining)
                return new List<BirooniAiElement>();

            if (string.Equals(service, "Duct", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(service, "Cable Tray", StringComparison.OrdinalIgnoreCase))
            {
                return ExtractRectangularRuns(worldPoints, service, diameterOverrideMm);
            }

            // Circular elements (Piping, Conduit, Round Duct) -> Cylindrical Peel
            return ExtractPipingRuns(worldPoints, diameterOverrideMm, skipVerticalRisers);
        }

        public List<BirooniAiElement> ExtractRectangularRuns(IList<XYZ> worldPoints, string service, double? dimensionOverrideMm)
        {
            var results = new List<BirooniAiElement>();
            if (worldPoints == null || worldPoints.Count < 20) return results;

            double voxel = worldPoints.Count > VoxelAdaptiveThreshold ? VoxelFtLarge : VoxelFtSmall;
            List<XYZ> sampled = VoxelDownsample(worldPoints, voxel);
            List<List<XYZ>> clusters = SpatialCluster(sampled, 0.45);

            foreach (var cluster in clusters)
            {
                if (cluster.Count < 15) continue;
                if (IsFlatWallCluster(cluster)) continue;

                var fitElem = FitRectangularCluster(cluster, service, dimensionOverrideMm);
                if (fitElem != null)
                {
                    results.Add(fitElem);
                }
            }

            if (results.Count == 0 && sampled.Count >= 20)
            {
                var fitElem = FitRectangularCluster(sampled, service, dimensionOverrideMm);
                if (fitElem != null) results.Add(fitElem);
            }

            return results;
        }

        private static BirooniAiElement FitRectangularCluster(List<XYZ> pts, string service, double? dimensionOverrideMm)
        {
            if (pts == null || pts.Count < 12) return null;

            double cx = 0, cy = 0, cz = 0;
            foreach (var p in pts) { cx += p.X; cy += p.Y; cz += p.Z; }
            XYZ center = new XYZ(cx / pts.Count, cy / pts.Count, cz / pts.Count);

            double cxx = 0, cxy = 0, cxz = 0, cyy = 0, cyz = 0, czz = 0;
            foreach (var p in pts)
            {
                double dx = p.X - center.X;
                double dy = p.Y - center.Y;
                double dz = p.Z - center.Z;
                cxx += dx * dx; cxy += dx * dy; cxz += dx * dz;
                cyy += dy * dy; cyz += dy * dz; czz += dz * dz;
            }

            XYZ vDir = new XYZ(1, 0, 0);
            for (int i = 0; i < 20; i++)
            {
                double nx = cxx * vDir.X + cxy * vDir.Y + cxz * vDir.Z;
                double ny = cxy * vDir.X + cyy * vDir.Y + cyz * vDir.Z;
                double nz = cxz * vDir.X + cyz * vDir.Y + czz * vDir.Z;
                double mag = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (mag > 1e-8) vDir = new XYZ(nx / mag, ny / mag, nz / mag);
            }

            if (Math.Abs(vDir.Z) > 0.85)
            {
                vDir = XYZ.BasisZ;
            }
            else
            {
                XYZ vHoriz = new XYZ(vDir.X, vDir.Y, 0).Normalize();
                double ang = Math.Atan2(vHoriz.Y, vHoriz.X);
                double snapped = Math.Round(ang / (Math.PI / 2.0)) * (Math.PI / 2.0);
                if (Math.Abs(ang - snapped) < 15.0 * Math.PI / 180.0)
                    vDir = new XYZ(Math.Cos(snapped), Math.Sin(snapped), 0);
                else
                    vDir = vHoriz;
            }

            XYZ vWidth = Math.Abs(vDir.Z) > 0.85 ? XYZ.BasisX : new XYZ(-vDir.Y, vDir.X, 0).Normalize();
            XYZ vHeight = vDir.CrossProduct(vWidth).Normalize();

            List<double> tProj = new List<double>(pts.Count);
            List<double> wProj = new List<double>(pts.Count);
            List<double> hProj = new List<double>(pts.Count);

            foreach (var p in pts)
            {
                tProj.Add(p.DotProduct(vDir));
                wProj.Add(p.DotProduct(vWidth));
                hProj.Add(p.DotProduct(vHeight));
            }

            tProj.Sort();
            wProj.Sort();
            hProj.Sort();

            int trim = Math.Max(0, (int)(pts.Count * 0.03));
            double len = tProj[pts.Count - 1 - trim] - tProj[trim];
            if (len < 0.4) return null;

            double centerT = (tProj[trim] + tProj[pts.Count - 1 - trim]) * 0.5;
            double centerW = (wProj[trim] + wProj[pts.Count - 1 - trim]) * 0.5;
            double centerH = (hProj[trim] + hProj[pts.Count - 1 - trim]) * 0.5;

            double measuredW = (wProj[pts.Count - 1 - trim] - wProj[trim]) * 304.8;
            double measuredH = (hProj[pts.Count - 1 - trim] - hProj[trim]) * 304.8;

            double finalW = dimensionOverrideMm.HasValue && dimensionOverrideMm.Value > 0
                ? dimensionOverrideMm.Value
                : Math.Max(measuredW, service == "Cable Tray" ? 150.0 : 200.0);

            double finalH = service == "Cable Tray" ? Math.Min(measuredH, 100.0) : Math.Max(measuredH, 150.0);

            XYZ startPt = vDir.Multiply(centerT - len * 0.5) + vWidth.Multiply(centerW) + vHeight.Multiply(centerH);
            XYZ endPt = vDir.Multiply(centerT + len * 0.5) + vWidth.Multiply(centerW) + vHeight.Multiply(centerH);

            return new BirooniAiElement
            {
                Kind = service == "Cable Tray" ? "CableTray" : "Duct",
                Start = new double[] { startPt.X, startPt.Y, startPt.Z },
                End = new double[] { endPt.X, endPt.Y, endPt.Z },
                Center = new double[] { (startPt.X + endPt.X) * 0.5, (startPt.Y + endPt.Y) * 0.5, (startPt.Z + endPt.Z) * 0.5 },
                WidthMm = finalW,
                HeightMm = finalH,
                DiameterMm = finalW,
                Confidence = 0.92f
            };
        }

        public List<BirooniAiElement> ExtractPipingRuns(IList<XYZ> worldPoints, double? diameterOverrideMm, bool skipVerticalRisers = false)
        {
            if (worldPoints == null || worldPoints.Count < MinRemaining)
                return new List<BirooniAiElement>();

            // Adaptive voxel: finer resolution for large clouds to preserve detail
            // between nearby parallel pipes.
            double voxel = worldPoints.Count > VoxelAdaptiveThreshold ? VoxelFtLarge : VoxelFtSmall;
            List<XYZ> sampled = VoxelDownsample(worldPoints, voxel);
            List<List<XYZ>> clusters = SpatialCluster(sampled, ClusterEpsFt);

            var rawSegments = new List<PipeNetworkBuilder.Segment>();
            var usedKeys = new HashSet<string>();

            double budgetSec = worldPoints.Count < 20000 ? 8.0
                : worldPoints.Count < 50000 ? 12.0
                : 16.0;
            DateTime deadline = DateTime.UtcNow.AddSeconds(budgetSec);

            // Risers against a wall share a cluster with the wall sheet and would be
            // skipped below. Scan XY for circular columns first.
            // Fit Generate MEP whole-cloud skips this — it paints every wall/column as a riser.
            if (!skipVerticalRisers)
                AddSegments(rawSegments, usedKeys, PeelVerticalRisers(sampled, diameterOverrideMm));

            foreach (List<XYZ> cluster in clusters)
            {
                if (DateTime.UtcNow > deadline) break;
                if (cluster.Count < MinClusterSize) continue;
                if (IsFlatWallCluster(cluster)) continue;

                List<PipeNetworkBuilder.Segment> found;
                if (IsBranchedCluster(cluster) || cluster.Count >= 220)
                    found = PeelLocalGrid(cluster, diameterOverrideMm, relaxed: true);
                else
                {
                    found = PeelCluster(cluster, diameterOverrideMm, relaxed: false);
                    if (found.Count == 0)
                        found = PeelCluster(cluster, diameterOverrideMm, relaxed: true);
                }

                AddSegments(rawSegments, usedKeys, found);
            }

            if (DateTime.UtcNow <= deadline)
            {
                List<XYZ> unclaimed = PointsNotNearSegments(sampled, rawSegments);
                if (unclaimed.Count >= 12 && unclaimed.Count <= 8000)
                {
                    if (!skipVerticalRisers)
                        AddSegments(rawSegments, usedKeys, PeelVerticalRisers(unclaimed, diameterOverrideMm));
                    if (!IsFlatWallCluster(unclaimed))
                        AddSegments(rawSegments, usedKeys, PeelLocalGrid(unclaimed, diameterOverrideMm, relaxed: true));
                }
            }

            if (rawSegments.Count == 0 && sampled.Count >= MinRemaining && !IsFlatWallCluster(sampled))
                AddSegments(rawSegments, usedKeys, PeelLocalGrid(sampled, diameterOverrideMm, relaxed: true));

            if (!skipVerticalRisers)
                AttachVerticalDrops(rawSegments, worldPoints, diameterOverrideMm);
            ExtendSegmentsToCloud(rawSegments, worldPoints);

            rawSegments = FilterDuplicateSegments(rawSegments);
            rawSegments = FilterDuplicateVerticals(rawSegments);
            rawSegments = FilterCornerJunk(rawSegments);
            rawSegments = FilterTinyFragments(rawSegments);
            rawSegments = FilterPhantomSegments(rawSegments);
            rawSegments = FilterWallEmbedded(rawSegments, worldPoints);
            AlignSegmentsToPointCloud(rawSegments, worldPoints);
            HealSegmentJunctions(rawSegments);
            return PipeNetworkBuilder.BuildNetwork(rawSegments, preserveAlignment: true);
        }

        private static void AddSegments(List<PipeNetworkBuilder.Segment> target, HashSet<string> usedKeys,
            List<PipeNetworkBuilder.Segment> incoming)
        {
            foreach (PipeNetworkBuilder.Segment seg in incoming)
            {
                if (seg.Confidence < MinConfidence) continue;
                string key = RunKey(seg.Start, seg.End, seg.DiameterMm / 25.4);
                if (!usedKeys.Add(key)) continue;
                target.Add(seg);
            }
        }

                private static List<PipeNetworkBuilder.Segment> PeelCluster(List<XYZ> clusterPoints,
            double? diameterOverrideMm, bool relaxed)
        {
            var segments = new List<PipeNetworkBuilder.Segment>();
            var remaining = new List<XYZ>(clusterPoints);
            var used = new HashSet<string>();
            var rnd = new Random(42);

            int minRemaining = relaxed ? MinRemainingRelaxed : MinRemaining;
            int minFitPoints = relaxed ? 7 : 8;
            int maxPipes = Math.Max(4, Math.Min(8, clusterPoints.Count / 40));
            int rejectStreak = 0;

            for (int iter = 0; iter < maxPipes && remaining.Count >= minRemaining; iter++)
            {
                if (iter > 0 && remaining.Count >= 24 && IsFlatWallCluster(remaining)) break;

                bool branched = iter == 0 && IsBranchedCluster(remaining);
                CylinderFit.Result fit = (!branched)
                    ? CylinderFit.FitFast(remaining, relaxed)
                    : new CylinderFit.Result { Success = false };

                if (!fit.Success && remaining.Count >= 12)
                    fit = FitBestLocalCell(remaining, relaxed);

                if (!fit.Success && remaining.Count >= 14)
                {
                    for (int retry = 0; retry < 6; retry++)
                    {
                        XYZ seed = remaining[rnd.Next(remaining.Count)];
                        var localPts = remaining.Where(p => p.DistanceTo(seed) < 2.4).ToList();
                        if (localPts.Count >= 10)
                        {
                            fit = CylinderFit.FitFast(localPts, relaxed);
                            if (fit.Success) break;
                        }
                    }
                }

                if (!fit.Success || fit.PointCount < minFitPoints)
                {
                    rejectStreak++;
                    if (rejectStreak >= 4) break;
                    continue;
                }

                CylinderFit.RefineToPointCloud(ref fit, remaining);

                double lengthFt = fit.Start.DistanceTo(fit.End);
                if (!AcceptFit(fit, lengthFt, remaining, relaxed) ||
                    (iter > 0 && !AcceptSecondaryPeel(fit, lengthFt, remaining.Count)))
                {
                    remaining = RemoveCylinderInliers(remaining, fit, aggressive: true);
                    rejectStreak++;
                    if (rejectStreak >= 4) break;
                    continue;
                }
                rejectStreak = 0;

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

                segments.Add(new PipeNetworkBuilder.Segment
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

        private static bool AcceptFit(CylinderFit.Result fit, double lengthFt, IList<XYZ> cloud, bool relaxed)
        {
            if (fit.Radius < 0.02 || fit.Radius > 0.55) return false; // ~0.5"–13" dia (walls/trays are larger)
            if (lengthFt < MinLengthFt) return false;

            bool vertical = fit.Axis != null && Math.Abs(fit.Axis.Z) > 0.82;

            double slenderness = lengthFt / (fit.Radius * 2.0);
            if (vertical)
            {
                if (lengthFt < 0.50) return false;
            }
            else if (slenderness < (relaxed ? 1.4 : 1.6)) return false;

            double rmsRatio = fit.Rms / Math.Max(fit.Radius, 0.03);
            if (!vertical && rmsRatio > (relaxed ? 0.30 : 0.24)) return false;

            if (fit.PointCount < (relaxed ? 6 : 7)) return false;
            if (!vertical && fit.ArcSpanDeg < (relaxed ? 14.0 : 18.0)) return false;

            IList<XYZ> shellCloud = cloud;
            if (cloud != null && cloud.Count > 80)
            {
                List<XYZ> local = GatherNearSegment(fit.Start, fit.End, fit.Radius, cloud, 400);
                if (local.Count >= 8) shellCloud = local;
            }

            if (vertical)
            {
                if (lengthFt < 0.75) return false;
                if (IsWallPaintedVertical(fit.Start, fit.End, fit.Radius, cloud))
                    return false;
                return true;
            }

            int shell = shellCloud != null ? CountShellPoints(fit, shellCloud) : fit.PointCount;
            if (!HasPointShellSupport(fit, shell, shellCloud?.Count ?? fit.PointCount, relaxed))
                return false;

            if (shellCloud != null && !LooksLikePipeShell(fit.Start, fit.End, fit.Radius, shellCloud, lenient: relaxed))
                return false;

            return true;
        }

        private static bool AcceptSecondaryPeel(CylinderFit.Result fit, double lengthFt, int cloudCount)
        {
            if (fit.Radius < 0.015 || fit.Radius > 0.85) return false;
            if (lengthFt < MinLengthFt) return false;

            double slenderness = lengthFt / (fit.Radius * 2.0);
            if (slenderness < 1.6) return false;

            if (fit.PointCount < 7) return false;
            if (fit.ArcSpanDeg < 12.0) return false;
            if (fit.Rms / Math.Max(fit.Radius, 0.03) > 0.30) return false;
            return true;
        }

        

        private static bool HasPointShellSupport(CylinderFit.Result fit, int shellCount, int clusterCount, bool relaxed)
        {
            int minShell = relaxed ? 6 : 7;
            if (shellCount < minShell) return false;

            double lengthFt = Math.Max(0.35, fit.Start.DistanceTo(fit.End));
            double ptsPerFt = shellCount / lengthFt;
            if (ptsPerFt < (relaxed ? 3.0 : 4.0) && lengthFt > 1.5)
                return false;

            return true;
        }

        private static int CountShellPoints(CylinderFit.Result fit, IList<XYZ> points)
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
        /// EdgeWise-style: a real pipe is a hollow circular shell, not a wall, tray, or light.
        /// Interior of the tube should be empty; radial distances should hug the radius.
        /// </summary>
        public static bool LooksLikePipeShell(XYZ start, XYZ end, double radiusFt, IList<XYZ> cloud, bool lenient = false)
        {
            if (start == null || end == null || cloud == null || radiusFt < 0.02) return false;
            double len = start.DistanceTo(end);
            if (len < 0.30) return false;
            XYZ axis = end - start;
            if (axis.GetLength() < 1e-9) return false;
            axis = axis.Normalize();

            int shell = 0, interior = 0, near = 0;
            double accR = 0, accR2 = 0;
            double r = radiusFt;
            var inliers = new List<XYZ>();
            foreach (XYZ p in cloud)
            {
                XYZ rel = p - start;
                double t = rel.DotProduct(axis);
                if (t < -0.12 || t > len + 0.12) continue;
                double radial = (rel - axis.Multiply(t)).GetLength();
                if (radial > r * 2.0) continue;
                near++;
                accR += radial;
                accR2 += radial * radial;
                if (radial < r * 0.42) interior++;
                else if (Math.Abs(radial - r) <= r * 0.30)
                {
                    shell++;
                    inliers.Add(p);
                }
            }

            // Too few local points to reject — keep a geometric fit (clip-starved L-run).
            if (near < 10) return lenient;
            if (shell < (lenient ? 6 : 8)) return false;
            if (interior > shell * 0.45 && interior > 8) return false; // filled wall / fixture

            double mean = accR / near;
            double var = accR2 / near - mean * mean;
            if (var < 0) var = 0;
            double std = Math.Sqrt(var);
            if (std > r * (lenient ? 0.42 : 0.32)) return false; // rectangular tray / planar patch
            if (mean < r * 0.50 || mean > r * 1.45) return false;

            // Vertical wall sticks: points sit in a narrow azimuth wedge.
            // Do not apply this to TOP-view horizontal ribbons (small arc is normal).
            if (inliers.Count >= 8 && Math.Abs(axis.Z) > 0.82)
            {
                double span = AzimuthSpanDeg(inliers, start, axis);
                if (span < (lenient ? 22.0 : 28.0)) return false;
            }

            // TOP-view pipe ribbons are coplanar; only reject short wide sheets (trays).
            double slender = len / Math.Max(2.0 * r, 0.05);
            if (inliers.Count >= 20 && slender < 2.8 && InliersArePlanarSheet(inliers))
                return false;
            return true;
        }

        /// <summary>
        /// True when the candidate axis lies in a large planar sheet (wall/tray), not a tube.
        /// </summary>
        public static bool IsWallEmbedded(XYZ start, XYZ end, double radiusFt, IList<XYZ> cloud)
        {
            if (start == null || end == null || cloud == null || cloud.Count < 28) return false;
            double len = start.DistanceTo(end);
            if (len < 0.25) return false;
            XYZ axis = end - start;
            if (axis.GetLength() < 1e-9) return false;
            axis = axis.Normalize();
            double r = Math.Max(radiusFt, 0.04);
            // Tight band: a real pipe running *near* a wall must not be classified as the wall.
            double band = Math.Max(0.42, r * 2.6);

            var near = new List<XYZ>();
            foreach (XYZ p in cloud)
            {
                XYZ rel = p - start;
                double t = rel.DotProduct(axis);
                if (t < -0.15 || t > len + 0.15) continue;
                if ((rel - axis.Multiply(t)).GetLength() > band) continue;
                near.Add(p);
                if (near.Count >= 280) break;
            }
            if (near.Count < 28) return false;

            double cx = 0, cy = 0, cz = 0;
            foreach (var p in near) { cx += p.X; cy += p.Y; cz += p.Z; }
            double inv = 1.0 / near.Count;
            cx *= inv; cy *= inv; cz *= inv;
            double cxx = 0, cxy = 0, cxz = 0, cyy = 0, cyz = 0, czz = 0;
            foreach (var p in near)
            {
                double dx = p.X - cx, dy = p.Y - cy, dz = p.Z - cz;
                cxx += dx * dx; cxy += dx * dy; cxz += dx * dz;
                cyy += dy * dy; cyz += dy * dz; czz += dz * dz;
            }
            double[] ev = EigenValues3x3(
                cxx * inv, cyy * inv, czz * inv, cxy * inv, cxz * inv, cyz * inv);
            if (ev[0] < 1e-8) return false;
            if (ev[1] / ev[0] <= 0.28 || ev[2] / ev[0] >= 0.055)
                return false;

            // Smallest-eigenvalue direction is the plane normal (Jacobi sorted descending).
            XYZ n = PlaneNormalFromCov(cxx * inv, cyy * inv, czz * inv, cxy * inv, cxz * inv, cyz * inv);
            double d0 = Math.Abs((start - new XYZ(cx, cy, cz)).DotProduct(n));
            double d1 = Math.Abs((end - new XYZ(cx, cy, cz)).DotProduct(n));
            return d0 < r * 0.55 + 0.05 && d1 < r * 0.55 + 0.05;
        }

        private static XYZ PlaneNormalFromCov(double cxx, double cyy, double czz, double cxy, double cxz, double cyz)
        {
            double target = EigenValues3x3(cxx, cyy, czz, cxy, cxz, cyz)[2];
            // Inverse iteration on (A - λI) is overkill; power on the adjugate row.
            XYZ n = new XYZ(
                cxy * cyz - cxz * cyy,
                cxz * cxy - cxx * cyz,
                cxx * cyy - cxy * cxy);
            if (n.GetLength() < 1e-10)
                n = new XYZ(cyz * cxz - czz * cxy, czz * cxx - cxz * cxz, cxz * cxy - cyz * cxx);
            double L = n.GetLength();
            if (L < 1e-10) return XYZ.BasisZ;
            n = n.Multiply(1.0 / L);
            // Prefer the eigenvector of the smallest eigenvalue via one Rayleigh step.
            XYZ An = new XYZ(
                cxx * n.X + cxy * n.Y + cxz * n.Z,
                cxy * n.X + cyy * n.Y + cyz * n.Z,
                cxz * n.X + cyz * n.Y + czz * n.Z);
            if (An.GetLength() > 1e-10 && Math.Abs(An.DotProduct(n) - target) > 0.15)
            {
                XYZ n2 = new XYZ(-n.Y, n.X, 0);
                if (n2.GetLength() < 1e-8) n2 = XYZ.BasisX;
                n2 = n2.Normalize();
                n = n2;
            }
            return n;
        }

        private static double AzimuthSpanDeg(List<XYZ> pts, XYZ origin, XYZ axis)
        {
            XYZ e1 = Math.Abs(axis.Z) < 0.9
                ? new XYZ(-axis.Y, axis.X, 0)
                : new XYZ(1, 0, 0);
            if (e1.GetLength() < 1e-9) e1 = XYZ.BasisX;
            e1 = e1.Normalize();
            XYZ e2 = axis.CrossProduct(e1);
            if (e2.GetLength() < 1e-9) return 0;
            e2 = e2.Normalize();

            var angs = new List<double>(pts.Count);
            foreach (XYZ p in pts)
            {
                XYZ rel = p - origin;
                XYZ rad = rel - axis.Multiply(rel.DotProduct(axis));
                if (rad.GetLength() < 1e-8) continue;
                angs.Add(Math.Atan2(rad.DotProduct(e2), rad.DotProduct(e1)));
            }
            if (angs.Count < 6) return 0;
            angs.Sort();
            double maxGap = angs[0] + 2.0 * Math.PI - angs[angs.Count - 1];
            for (int i = 1; i < angs.Count; i++)
            {
                double g = angs[i] - angs[i - 1];
                if (g > maxGap) maxGap = g;
            }
            double occupied = 2.0 * Math.PI - maxGap;
            return occupied * 180.0 / Math.PI;
        }

        private static bool InliersArePlanarSheet(List<XYZ> pts)
        {
            double cx = 0, cy = 0, cz = 0;
            foreach (var p in pts) { cx += p.X; cy += p.Y; cz += p.Z; }
            double inv = 1.0 / pts.Count;
            cx *= inv; cy *= inv; cz *= inv;
            double cxx = 0, cxy = 0, cxz = 0, cyy = 0, cyz = 0, czz = 0;
            foreach (var p in pts)
            {
                double dx = p.X - cx, dy = p.Y - cy, dz = p.Z - cz;
                cxx += dx * dx; cxy += dx * dy; cxz += dx * dz;
                cyy += dy * dy; cyz += dy * dz; czz += dz * dz;
            }
            double[] ev = EigenValues3x3(cxx * inv, cyy * inv, czz * inv, cxy * inv, cxz * inv, cyz * inv);
            if (ev[0] < 1e-8) return false;
            return ev[1] / ev[0] > 0.35 && ev[2] / ev[0] < 0.06;
        }

        public static List<BirooniAiElement> KeepSupportedPipes(List<BirooniAiElement> elems, IList<XYZ> cloud, string service, double? diaOverrideMm)
        {
            if (elems == null || elems.Count == 0) return elems ?? new List<BirooniAiElement>();
            if (service == "Duct" || service == "Cable Tray") return elems;
            var kept = new List<BirooniAiElement>(elems.Count);
            double maxR = 0.55;
            if (diaOverrideMm.HasValue && diaOverrideMm.Value > 0)
                maxR = Math.Max(maxR, diaOverrideMm.Value / 304.8 * 0.5 + 0.05);

            foreach (var e in elems)
            {
                if (e.Start == null || e.End == null || e.Start.Length != 3 || e.End.Length != 3) continue;
                XYZ s = new XYZ(e.Start[0], e.Start[1], e.Start[2]);
                XYZ en = new XYZ(e.End[0], e.End[1], e.End[2]);
                double diaMm = e.DiameterMm ?? 0;
                double r = diaMm > 0 ? diaMm / 304.8 * 0.5 : 0.1;
                if (r > maxR) continue;
                if (cloud != null && cloud.Count >= 10)
                {
                    XYZ axis = en - s;
                    bool vertical = axis.GetLength() > 1e-6 && Math.Abs(axis.Normalize().Z) > 0.82;
                    List<XYZ> local = GatherNearSegment(s, en, r, cloud, 400);
                    if (vertical)
                    {
                        if (IsWallPaintedVertical(s, en, r, cloud)) continue;
                    }
                    else
                    {
                        if (IsWallEmbedded(s, en, r, cloud)) continue;
                        if (local.Count >= 8 && !LooksLikePipeShell(s, en, r, local, lenient: true))
                            continue;
                    }
                }
                kept.Add(e);
            }
            return kept;
        }

        private static List<PipeNetworkBuilder.Segment> PeelLocalGrid(
            List<XYZ> pts, double? diameterOverrideMm, bool relaxed)
        {
            var segments = new List<PipeNetworkBuilder.Segment>();
            if (pts == null || pts.Count < 10) return segments;

            var used = new HashSet<string>();

            // TOP-view vertical risers are compact circular blobs, not elongated strips.
            foreach (List<XYZ> cell in BucketCells(pts, 1.6).Values)
            {
                if (!IsCompactVerticalBlob(cell)) continue;
                CylinderFit.Result vfit = FitVerticalRiser(cell, pts);
                if (!vfit.Success) continue;
                double vlen = vfit.Start.DistanceTo(vfit.End);
                if (!AcceptFit(vfit, vlen, pts, relaxed)) continue;
                double vNom = diameterOverrideMm.HasValue && diameterOverrideMm.Value > 0
                    ? diameterOverrideMm.Value / 25.4
                    : SnapNominal(vfit.Radius * 2.0 * 12.0);
                string vkey = RunKey(vfit.Start, vfit.End, vNom);
                if (!used.Add(vkey)) continue;
                segments.Add(new PipeNetworkBuilder.Segment
                {
                    Start = vfit.Start,
                    End = vfit.End,
                    Axis = vfit.Axis,
                    DiameterMm = vNom * 25.4,
                    RadiusFt = vfit.Radius,
                    ShellPointCount = vfit.PointCount,
                    Confidence = ScoreConfidence(vfit, vlen),
                });
            }

            var candidates = new List<(List<XYZ> cell, double elong)>();
            foreach (List<XYZ> cell in BucketCells(pts, 2.2).Values)
            {
                if (cell.Count < 10 || cell.Count > 480) continue;
                if (IsSheetCell(cell) || IsBranchedCluster(cell)) continue;
                if (IsCompactVerticalBlob(cell)) continue;
                double elong = CellElongation(cell);
                if (elong < 1.8) continue;
                candidates.Add((cell, elong));
            }
            candidates.Sort((a, b) => b.elong.CompareTo(a.elong));
            if (candidates.Count > 64)
                candidates.RemoveRange(64, candidates.Count - 64);

            foreach (var (cell, _) in candidates)
            {
                if (CoveredByExisting(cell, segments)) continue;

                CylinderFit.Result fit = CylinderFit.FitFast(cell, relaxed);
                if (!fit.Success) continue;
                GrowFitAlongCloud(ref fit, pts);

                double lengthFt = fit.Start.DistanceTo(fit.End);
                if (!AcceptFit(fit, lengthFt, pts, relaxed)) continue;

                double measuredIn = fit.Radius * 2.0 * 12.0;
                double nominalIn = diameterOverrideMm.HasValue && diameterOverrideMm.Value > 0
                    ? diameterOverrideMm.Value / 25.4
                    : SnapNominal(measuredIn);
                string key = RunKey(fit.Start, fit.End, nominalIn);
                if (!used.Add(key)) continue;

                segments.Add(new PipeNetworkBuilder.Segment
                {
                    Start = fit.Start,
                    End = fit.End,
                    Axis = fit.Axis,
                    DiameterMm = nominalIn * 25.4,
                    RadiusFt = fit.Radius,
                    ShellPointCount = fit.PointCount,
                    Confidence = ScoreConfidence(fit, lengthFt),
                });
            }
            return segments;
        }

        private static List<PipeNetworkBuilder.Segment> PeelVerticalRisers(
            IList<XYZ> pts, double? diameterOverrideMm)
        {
            var segments = new List<PipeNetworkBuilder.Segment>();
            if (pts == null || pts.Count < 12) return segments;

            const double bin = 0.18;
            var grid = new Dictionary<long, List<XYZ>>();
            foreach (XYZ p in pts)
            {
                long key = (((long)Math.Floor(p.X / bin) & 0x1FFFFF))
                    | (((long)Math.Floor(p.Y / bin) & 0x1FFFFF) << 21);
                if (!grid.TryGetValue(key, out List<XYZ> list))
                {
                    list = new List<XYZ>();
                    grid[key] = list;
                }
                list.Add(p);
            }

            var used = new HashSet<string>();
            foreach (var kv in grid)
            {
                if (kv.Value.Count < 3) continue;
                double cx = 0, cy = 0;
                foreach (XYZ p in kv.Value) { cx += p.X; cy += p.Y; }
                cx /= kv.Value.Count;
                cy /= kv.Value.Count;
                var local = new List<XYZ>();
                foreach (XYZ p in pts)
                {
                    double dx = p.X - cx, dy = p.Y - cy;
                    if (dx * dx + dy * dy <= 0.30 * 0.30)
                        local.Add(p);
                }
                if (local.Count < 10) continue;
                if (XyIsLinear(local)) continue;

                CylinderFit.Result vfit = FitVerticalRiser(local, pts);
                if (!vfit.Success) continue;
                double vlen = vfit.Start.DistanceTo(vfit.End);
                if (vlen < 0.80) continue;
                if (IsWallPaintedVertical(vfit.Start, vfit.End, vfit.Radius, pts)) continue;
                if (CoveredByExisting(local, segments)) continue;

                double vNom = diameterOverrideMm.HasValue && diameterOverrideMm.Value > 0
                    ? diameterOverrideMm.Value / 25.4
                    : SnapNominal(vfit.Radius * 2.0 * 12.0);
                string vkey = RunKey(vfit.Start, vfit.End, vNom);
                if (!used.Add(vkey)) continue;
                segments.Add(new PipeNetworkBuilder.Segment
                {
                    Start = vfit.Start,
                    End = vfit.End,
                    Axis = vfit.Axis,
                    DiameterMm = vNom * 25.4,
                    RadiusFt = vfit.Radius,
                    ShellPointCount = vfit.PointCount,
                    Confidence = ScoreConfidence(vfit, vlen),
                });
            }
            return segments;
        }

        /// <summary>
        /// Wall-face cylinder: XY center sits on a long wall line, and/or the column is a filled sheet.
        /// A real riser is offset from the wall by about its radius, or stands in open space.
        /// </summary>
        public static bool IsWallPaintedVertical(XYZ start, XYZ end, double radiusFt, IList<XYZ> cloud)
        {
            if (start == null || end == null || cloud == null || radiusFt < 0.02) return false;
            double cx = (start.X + end.X) * 0.5;
            double cy = (start.Y + end.Y) * 0.5;
            double r = radiusFt;
            double z0 = Math.Min(start.Z, end.Z);
            double z1 = Math.Max(start.Z, end.Z);

            int inner = 0, all = 0;
            var near = new List<XYZ>();
            foreach (XYZ p in cloud)
            {
                if (p.Z < z0 - 0.1 || p.Z > z1 + 0.1) continue;
                double rr = Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
                if (rr <= r * 1.35)
                {
                    all++;
                    if (rr < r * 0.42) inner++;
                }
                if (rr < 1.15) near.Add(p);
            }
            if (all >= 12 && inner > all * 0.48)
                return true;

            if (near.Count < 28) return false;
            if (!XyIsLinear(near)) return false;

            double mx = 0, my = 0;
            foreach (var p in near) { mx += p.X; my += p.Y; }
            mx /= near.Count;
            my /= near.Count;
            double cxx = 0, cxy = 0, cyy = 0;
            foreach (var p in near)
            {
                double dx = p.X - mx, dy = p.Y - my;
                cxx += dx * dx; cxy += dx * dy; cyy += dy * dy;
            }
            double dirx = cxx >= cyy ? 1 : 0, diry = cxx >= cyy ? 0 : 1;
            if (cxx + cyy > 1e-8)
            {
                dirx = cxx;
                diry = cxy;
                double mag = Math.Sqrt(dirx * dirx + diry * diry);
                if (mag < 1e-8) { dirx = cxy; diry = cyy; mag = Math.Sqrt(dirx * dirx + diry * diry); }
                if (mag > 1e-8) { dirx /= mag; diry /= mag; }
            }
            double nx = -diry, ny = dirx;
            double dist = Math.Abs((cx - mx) * nx + (cy - my) * ny);
            return dist < Math.Max(0.05, r * 0.35);
        }

        private static bool XyIsLinear(IList<XYZ> pts)
        {
            if (pts == null || pts.Count < 8) return false;
            return !XyIsotropic(pts as List<XYZ> ?? new List<XYZ>(pts), 0.20);
        }

        private static bool XyLooksCircular(List<XYZ> pts)
        {
            if (pts == null || pts.Count < 14) return false;
            double cx = 0, cy = 0;
            foreach (var p in pts) { cx += p.X; cy += p.Y; }
            cx /= pts.Count;
            cy /= pts.Count;

            List<XYZ> ring = null;
            double r = 0;
            for (int iter = 0; iter < 4; iter++)
            {
                var rads = new List<double>(pts.Count);
                foreach (var p in pts)
                    rads.Add(Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy)));
                rads.Sort();
                r = rads[rads.Count / 2];
                if (r < 0.03 || r > 0.50) return false;
                ring = new List<XYZ>();
                foreach (var p in pts)
                {
                    double rr = Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
                    if (Math.Abs(rr - r) <= r * 0.35) ring.Add(p);
                }
                if (ring.Count < 12) return false;
                cx = 0; cy = 0;
                foreach (var p in ring) { cx += p.X; cy += p.Y; }
                cx /= ring.Count;
                cy /= ring.Count;
            }
            if (AzimuthSpanDeg(ring, new XYZ(cx, cy, ring[0].Z), XYZ.BasisZ) < 110.0)
                return false;
            return XyIsotropic(ring, 0.40);
        }

        private static void ExtendSegmentsToCloud(List<PipeNetworkBuilder.Segment> segs, IList<XYZ> cloud)
        {
            if (segs == null || cloud == null || cloud.Count < 8) return;
            foreach (var seg in segs)
            {
                XYZ axis = seg.Axis ?? Norm(seg.End - seg.Start);
                double len = seg.Start.DistanceTo(seg.End);
                if (len < 0.1) continue;
                axis = Norm(axis);
                double r = seg.RadiusFt > 1e-6 ? seg.RadiusFt : 0.10;
                if (!ContiguousHugSpan(seg.Start, axis, r, cloud, 0, len, out double tMin, out double tMax))
                    continue;
                if (tMax - tMin < 0.35) continue;
                XYZ origin = seg.Start;
                seg.Start = origin + axis.Multiply(tMin);
                seg.End = origin + axis.Multiply(tMax);
                seg.Axis = axis;
            }
        }

        /// <summary>
        /// Longest run of cylinder-hugging inliers that overlaps [seedT0, seedT1].
        /// Stops at empty gaps so a pipe cannot jump through air onto a wall.
        /// </summary>
        private static bool ContiguousHugSpan(XYZ origin, XYZ axis, double r, IList<XYZ> cloud,
            double seedT0, double seedT1, out double tMin, out double tMax)
        {
            tMin = seedT0;
            tMax = seedT1;
            const double gapStop = 0.28;
            var ts = new List<double>();
            foreach (XYZ p in cloud)
            {
                XYZ rel = p - origin;
                double t = rel.DotProduct(axis);
                double radial = (rel - axis.Multiply(t)).GetLength();
                if (Math.Abs(radial - r) > r * 0.38 + 0.05) continue;
                ts.Add(t);
            }
            if (ts.Count < 8) return false;
            ts.Sort();

            var runs = new List<(double a, double b)>();
            double a = ts[0], b = ts[0];
            for (int i = 1; i < ts.Count; i++)
            {
                if (ts[i] - b <= gapStop) b = ts[i];
                else
                {
                    runs.Add((a, b));
                    a = b = ts[i];
                }
            }
            runs.Add((a, b));

            double seedLo = Math.Min(seedT0, seedT1);
            double seedHi = Math.Max(seedT0, seedT1);
            double bestOv = -1;
            bool found = false;
            foreach (var run in runs)
            {
                double ov = Math.Min(seedHi, run.b) - Math.Max(seedLo, run.a);
                if (ov < 0.10) continue;
                if (ov > bestOv)
                {
                    bestOv = ov;
                    tMin = run.a;
                    tMax = run.b;
                    found = true;
                }
            }
            return found;
        }

        private static void AttachVerticalDrops(
            List<PipeNetworkBuilder.Segment> segs, IList<XYZ> cloud, double? diameterOverrideMm)
        {
            if (segs == null || segs.Count == 0 || cloud == null || cloud.Count < 12) return;
            var extra = new List<PipeNetworkBuilder.Segment>();
            foreach (var s in segs.ToList())
            {
                XYZ ax = s.Axis ?? Norm(s.End - s.Start);
                if (Math.Abs(ax.Z) > 0.82) continue;
                TryAddDropAtEndpoint(s.Start, s, cloud, diameterOverrideMm, segs, extra);
                TryAddDropAtEndpoint(s.End, s, cloud, diameterOverrideMm, segs, extra);
            }
            segs.AddRange(extra);
        }

        private static void TryAddDropAtEndpoint(
            XYZ ep, PipeNetworkBuilder.Segment host, IList<XYZ> cloud, double? diameterOverrideMm,
            List<PipeNetworkBuilder.Segment> existing, List<PipeNetworkBuilder.Segment> extra)
        {
            double r = host.RadiusFt > 1e-6 ? host.RadiusFt : 0.10;
            var col = new List<XYZ>();
            foreach (XYZ p in cloud)
            {
                double xy = Math.Sqrt((p.X - ep.X) * (p.X - ep.X) + (p.Y - ep.Y) * (p.Y - ep.Y));
                if (xy > r * 1.9 + 0.10) continue;
                if (Math.Abs(p.Z - ep.Z) < Math.Max(0.18, r * 1.6)) continue;
                col.Add(p);
            }
            if (col.Count < 6) return;

            CylinderFit.Result fit = FitVerticalRiser(col, cloud);
            if (!fit.Success)
            {
                double zMin = double.MaxValue, zMax = double.MinValue;
                foreach (var p in col)
                {
                    if (p.Z < zMin) zMin = p.Z;
                    if (p.Z > zMax) zMax = p.Z;
                }
                if (zMax - zMin < 0.45) return;
                fit = new CylinderFit.Result
                {
                    Success = true,
                    Start = new XYZ(ep.X, ep.Y, zMin),
                    End = new XYZ(ep.X, ep.Y, zMax),
                    Axis = XYZ.BasisZ,
                    Radius = r,
                    Rms = 0.02,
                    PointCount = col.Count,
                    ArcSpanDeg = 180,
                    Method = "end-drop",
                };
            }
            double vlen = fit.Start.DistanceTo(fit.End);
            if (vlen < 0.80) return;
            if (IsWallPaintedVertical(fit.Start, fit.End, fit.Radius, cloud)) return;

            foreach (var e in existing)
            {
                XYZ eax = e.Axis ?? Norm(e.End - e.Start);
                if (Math.Abs(eax.Z) < 0.82) continue;
                double ex = (e.Start.X + e.End.X) * 0.5;
                double ey = (e.Start.Y + e.End.Y) * 0.5;
                if (Math.Sqrt((ex - ep.X) * (ex - ep.X) + (ey - ep.Y) * (ey - ep.Y)) < r * 2.2 + 0.15)
                    return;
            }
            foreach (var e in extra)
            {
                double ex = (e.Start.X + e.End.X) * 0.5;
                double ey = (e.Start.Y + e.End.Y) * 0.5;
                if (Math.Sqrt((ex - ep.X) * (ex - ep.X) + (ey - ep.Y) * (ey - ep.Y)) < r * 2.2 + 0.15)
                    return;
            }

            double vNom = diameterOverrideMm.HasValue && diameterOverrideMm.Value > 0
                ? diameterOverrideMm.Value / 25.4
                : SnapNominal(fit.Radius * 2.0 * 12.0);
            extra.Add(new PipeNetworkBuilder.Segment
            {
                Start = fit.Start,
                End = fit.End,
                Axis = XYZ.BasisZ,
                DiameterMm = vNom * 25.4,
                RadiusFt = fit.Radius,
                ShellPointCount = fit.PointCount,
                Confidence = 0.72f,
            });
        }

        private static List<PipeNetworkBuilder.Segment> FilterDuplicateVerticals(
            List<PipeNetworkBuilder.Segment> segs)
        {
            if (segs == null || segs.Count <= 1) return segs;
            var verts = new List<int>();
            for (int i = 0; i < segs.Count; i++)
            {
                XYZ ax = segs[i].Axis ?? Norm(segs[i].End - segs[i].Start);
                if (Math.Abs(ax.Z) > 0.82) verts.Add(i);
            }
            if (verts.Count <= 1) return segs;

            var drop = new bool[segs.Count];
            for (int a = 0; a < verts.Count; a++)
            {
                if (drop[verts[a]]) continue;
                var sa = segs[verts[a]];
                XYZ ca = new XYZ((sa.Start.X + sa.End.X) * 0.5, (sa.Start.Y + sa.End.Y) * 0.5, 0);
                double la = sa.Start.DistanceTo(sa.End);
                for (int b = a + 1; b < verts.Count; b++)
                {
                    if (drop[verts[b]]) continue;
                    var sb = segs[verts[b]];
                    XYZ cb = new XYZ((sb.Start.X + sb.End.X) * 0.5, (sb.Start.Y + sb.End.Y) * 0.5, 0);
                    double xy = Math.Sqrt((ca.X - cb.X) * (ca.X - cb.X) + (ca.Y - cb.Y) * (ca.Y - cb.Y));
                    double lim = Math.Max(sa.RadiusFt, sb.RadiusFt) * 2.0 + 0.12;
                    if (xy > lim) continue;
                    double lb = sb.Start.DistanceTo(sb.End);
                    if (la >= lb) drop[verts[b]] = true;
                    else { drop[verts[a]] = true; break; }
                }
            }
            var kept = new List<PipeNetworkBuilder.Segment>();
            for (int i = 0; i < segs.Count; i++)
                if (!drop[i]) kept.Add(segs[i]);
            return kept.Count > 0 ? kept : segs;
        }

        private static bool IsCompactVerticalBlob(List<XYZ> cell)
        {
            if (cell == null || cell.Count < 12) return false;
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            double minZ = double.MaxValue, maxZ = double.MinValue;
            foreach (var p in cell)
            {
                if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
                if (p.Z < minZ) minZ = p.Z; if (p.Z > maxZ) maxZ = p.Z;
            }
            double dx = maxX - minX, dy = maxY - minY, dz = maxZ - minZ;
            double xy = Math.Max(dx, dy);
            double xyMin = Math.Min(dx, dy);
            if (xy < 0.12 || xy > 1.05) return false;
            if (xyMin / xy < 0.45) return false;
            // Horizontal pipe cell is long in XY; a riser blob is compact in XY.
            if (xy > 1.6) return false;
            return true;
        }

        private static CylinderFit.Result FitVerticalRiser(List<XYZ> seed, IList<XYZ> cloud)
        {
            var fail = new CylinderFit.Result { Success = false, PointCount = seed == null ? 0 : seed.Count };
            if (seed == null || seed.Count < 12 || cloud == null) return fail;

            double cx = 0, cy = 0;
            foreach (var p in seed) { cx += p.X; cy += p.Y; }
            cx /= seed.Count;
            cy /= seed.Count;

            var rads = new List<double>(seed.Count);
            foreach (var p in seed)
                rads.Add(Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy)));
            rads.Sort();
            double r = rads[rads.Count / 2];
            if (r < 0.03 || r > 0.50) return fail;

            double zMin = double.MaxValue, zMax = double.MinValue;
            int n = 0;
            double acc = 0, acc2 = 0;
            var xyIn = new List<XYZ>();
            foreach (XYZ p in cloud)
            {
                double rr = Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
                if (rr > r * 1.45 + 0.04) continue;
                if (p.Z < zMin) zMin = p.Z;
                if (p.Z > zMax) zMax = p.Z;
                acc += rr;
                acc2 += rr * rr;
                n++;
                if (Math.Abs(rr - r) <= r * 0.35) xyIn.Add(p);
            }
            if (n < 8 || zMax - zMin < 0.75) return fail;

            double mean = acc / n;
            double var = acc2 / n - mean * mean;
            if (var < 0) var = 0;
            double rms = Math.Sqrt(var);
            if (rms > r * 0.38) return fail;
            double span = AzimuthSpanDeg(xyIn.Count >= 8 ? xyIn : seed, new XYZ(cx, cy, zMin), XYZ.BasisZ);

            return new CylinderFit.Result
            {
                Success = true,
                Start = new XYZ(cx, cy, zMin),
                End = new XYZ(cx, cy, zMax),
                Axis = XYZ.BasisZ,
                Radius = r,
                Rms = rms,
                PointCount = n,
                ArcSpanDeg = span,
                Message = "ok",
                Method = "vertical-riser",
            };
        }

        private static bool HasCircularXySection(XYZ start, XYZ end, double radiusFt, IList<XYZ> cloud)
        {
            if (start == null || end == null || cloud == null || radiusFt < 0.02) return false;
            XYZ axis = end - start;
            if (axis.GetLength() < 1e-9) return false;
            axis = axis.Normalize();
            if (Math.Abs(axis.Z) < 0.82) return false;

            double cx = (start.X + end.X) * 0.5;
            double cy = (start.Y + end.Y) * 0.5;
            double r = radiusFt;
            double z0 = Math.Min(start.Z, end.Z) - 0.15;
            double z1 = Math.Max(start.Z, end.Z) + 0.15;

            int shell = 0, interior = 0, near = 0;
            var ring = new List<XYZ>();
            foreach (XYZ p in cloud)
            {
                if (p.Z < z0 || p.Z > z1) continue;
                double rr = Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
                if (rr > r * 2.0) continue;
                near++;
                if (rr < r * 0.42) interior++;
                else if (Math.Abs(rr - r) <= r * 0.35)
                {
                    shell++;
                    ring.Add(p);
                }
            }
            if (near < 10 || shell < 8) return false;
            if (ring.Count < 8) return false;
            // TOP view of a riser is a filled disk (pipe cap) — do not reject interior.
            // Wall corner is ~90° (two faces). A real column is a ring/disk.
            if (AzimuthSpanDeg(ring, new XYZ(cx, cy, start.Z), XYZ.BasisZ) < 110.0)
                return false;
            return XyIsotropic(ring, 0.40);
        }

        private static bool XyIsotropic(List<XYZ> pts, double minRatio)
        {
            if (pts == null || pts.Count < 8) return false;
            double cx = 0, cy = 0;
            foreach (var p in pts) { cx += p.X; cy += p.Y; }
            cx /= pts.Count;
            cy /= pts.Count;
            double cxx = 0, cxy = 0, cyy = 0;
            foreach (var p in pts)
            {
                double dx = p.X - cx, dy = p.Y - cy;
                cxx += dx * dx; cxy += dx * dy; cyy += dy * dy;
            }
            double inv = 1.0 / pts.Count;
            cxx *= inv; cxy *= inv; cyy *= inv;
            double tr = cxx + cyy;
            double det = cxx * cyy - cxy * cxy;
            double disc = tr * tr - 4.0 * det;
            if (disc < 0) disc = 0;
            double ev0 = 0.5 * (tr + Math.Sqrt(disc));
            double ev1 = 0.5 * (tr - Math.Sqrt(disc));
            if (ev0 < 1e-8) return false;
            return ev1 / ev0 >= minRatio;
        }

        private static double CellElongation(List<XYZ> pts)
        {
            if (pts == null || pts.Count < 8) return 0;
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            double minZ = double.MaxValue, maxZ = double.MinValue;
            foreach (var p in pts)
            {
                if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
                if (p.Z < minZ) minZ = p.Z; if (p.Z > maxZ) maxZ = p.Z;
            }
            double[] d = { maxX - minX, maxY - minY, maxZ - minZ };
            Array.Sort(d);
            return d[2] / Math.Max(d[1], 0.05);
        }

        private static bool CoveredByExisting(List<XYZ> cell, List<PipeNetworkBuilder.Segment> segs)
        {
            if (segs == null || segs.Count == 0) return false;
            int hit = 0;
            foreach (XYZ p in cell)
            {
                foreach (var s in segs)
                {
                    XYZ axis = s.Axis ?? Norm(s.End - s.Start);
                    double len = s.Start.DistanceTo(s.End);
                    XYZ rel = p - s.Start;
                    double t = rel.DotProduct(axis);
                    if (t < -0.1 || t > len + 0.1) continue;
                    double rad = s.RadiusFt > 1e-6 ? s.RadiusFt : 0.12;
                    if ((rel - axis.Multiply(t)).GetLength() <= rad * 2.2 + 0.12)
                    {
                        hit++;
                        break;
                    }
                }
            }
            return hit >= cell.Count * 0.55;
        }

        private static void GrowFitAlongCloud(ref CylinderFit.Result fit, IList<XYZ> cloud)
        {
            if (!fit.Success || cloud == null || cloud.Count < 8) return;
            CylinderFit.RefineToPointCloud(ref fit, cloud);

            XYZ axis = fit.Axis;
            if (axis == null || axis.GetLength() < 1e-9) return;
            axis = Norm(axis);
            XYZ origin = fit.Start;
            double r = Math.Max(fit.Radius, 0.03);
            double seedLen = fit.Start.DistanceTo(fit.End);
            if (!ContiguousHugSpan(origin, axis, r, cloud, 0, seedLen, out double tMin, out double tMax))
                return;
            if (tMax - tMin < 0.35) return;
            fit.Start = origin + axis.Multiply(tMin);
            fit.End = origin + axis.Multiply(tMax);
            fit.Axis = axis;
        }

        private static List<PipeNetworkBuilder.Segment> FilterCornerJunk(List<PipeNetworkBuilder.Segment> segs)
        {
            if (segs == null || segs.Count <= 1) return segs;
            var kept = new List<PipeNetworkBuilder.Segment>();
            foreach (var c in segs)
            {
                double cl = c.Start.DistanceTo(c.End);
                if (cl >= 0.95)
                {
                    kept.Add(c);
                    continue;
                }
                bool junk = false;
                foreach (var o in segs)
                {
                    if (ReferenceEquals(c, o)) continue;
                    if (o.Start.DistanceTo(o.End) < cl * 1.8) continue;
                    if (c.Start.DistanceTo(o.Start) < 0.45 || c.Start.DistanceTo(o.End) < 0.45
                        || c.End.DistanceTo(o.Start) < 0.45 || c.End.DistanceTo(o.End) < 0.45)
                    {
                        junk = true;
                        break;
                    }
                }
                if (!junk) kept.Add(c);
            }
            return kept.Count > 0 ? kept : segs;
        }

        private static List<PipeNetworkBuilder.Segment> FilterTinyFragments(
            List<PipeNetworkBuilder.Segment> segs)
        {
            if (segs == null || segs.Count == 0) return segs;
            var kept = new List<PipeNetworkBuilder.Segment>();
            foreach (var s in segs)
            {
                double len = s.Start.DistanceTo(s.End);
                if (len < 0.55) continue;
                XYZ ax = s.Axis ?? Norm(s.End - s.Start);
                if (Math.Abs(ax.Z) > 0.82 && len < 0.80) continue;
                kept.Add(s);
            }
            return kept.Count > 0 ? kept : segs;
        }

        private static CylinderFit.Result FitBestLocalCell(List<XYZ> pts, bool relaxed)
        {
            var best = new CylinderFit.Result { Success = false };
            if (pts == null || pts.Count < 10) return best;

            var cells = new List<List<XYZ>>();
            foreach (List<XYZ> cell in BucketCells(pts, 2.2).Values)
            {
                if (cell.Count < 10 || cell.Count > 480) continue;
                if (IsSheetCell(cell)) continue;
                cells.Add(cell);
            }
            cells.Sort((a, b) => b.Count.CompareTo(a.Count));
            int n = Math.Min(8, cells.Count);

            double bestScore = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                CylinderFit.Result fit = CylinderFit.FitFast(cells[i], relaxed);
                if (!fit.Success) continue;
                double len = fit.Start.DistanceTo(fit.End);
                double score = len * fit.PointCount / Math.Max(fit.Rms, 0.01);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = fit;
                }
            }
            return best;
        }

        private static bool IsSheetCell(List<XYZ> pts)
        {
            if (pts == null || pts.Count < 16) return false;
            double cx = 0, cy = 0, cz = 0;
            foreach (var p in pts) { cx += p.X; cy += p.Y; cz += p.Z; }
            double inv = 1.0 / pts.Count;
            cx *= inv; cy *= inv; cz *= inv;
            double cxx = 0, cxy = 0, cxz = 0, cyy = 0, cyz = 0, czz = 0;
            foreach (var p in pts)
            {
                double dx = p.X - cx, dy = p.Y - cy, dz = p.Z - cz;
                cxx += dx * dx; cxy += dx * dy; cxz += dx * dz;
                cyy += dy * dy; cyz += dy * dz; czz += dz * dz;
            }
            double[] ev = EigenValues3x3(
                cxx * inv, cyy * inv, czz * inv, cxy * inv, cxz * inv, cyz * inv);
            if (ev[0] < 1e-8) return false;
            // Pipe cell is long and thin (ev1/ev0 small). Tray/wall cell is a filled plate.
            return ev[1] / ev[0] > 0.40 && ev[2] / ev[0] < 0.08;
        }

        private static Dictionary<long, List<XYZ>> BucketCells(IList<XYZ> pts, double cell)
        {
            var map = new Dictionary<long, List<XYZ>>();
            foreach (XYZ p in pts)
            {
                long key = CellKey(p, cell);
                if (!map.TryGetValue(key, out List<XYZ> list))
                {
                    list = new List<XYZ>();
                    map[key] = list;
                }
                list.Add(p);
            }
            return map;
        }

        private static List<XYZ> PointsNotNearSegments(IList<XYZ> pts, List<PipeNetworkBuilder.Segment> segs)
        {
            var leftover = new List<XYZ>();
            if (pts == null) return leftover;
            if (segs == null || segs.Count == 0)
            {
                leftover.AddRange(pts);
                return leftover;
            }

            foreach (XYZ p in pts)
            {
                bool on = false;
                foreach (PipeNetworkBuilder.Segment seg in segs)
                {
                    XYZ axis = seg.Axis ?? Norm(seg.End - seg.Start);
                    double len = seg.Start.DistanceTo(seg.End);
                    XYZ rel = p - seg.Start;
                    double t = rel.DotProduct(axis);
                    if (t < -0.15 || t > len + 0.15) continue;
                    double rad = seg.RadiusFt > 1e-6 ? seg.RadiusFt : 0.12;
                    if ((rel - axis.Multiply(t)).GetLength() <= rad * 2.5 + 0.18)
                    {
                        on = true;
                        break;
                    }
                }
                if (!on) leftover.Add(p);
            }
            return leftover;
        }

        private static List<XYZ> GatherNearSegment(XYZ start, XYZ end, double radiusFt, IList<XYZ> cloud, int cap)
        {
            var local = new List<XYZ>();
            XYZ axis = end - start;
            double len = axis.GetLength();
            if (len < 1e-9 || cloud == null) return local;
            axis = axis.Normalize();
            double band = Math.Max(0.28, radiusFt * 2.2);
            foreach (XYZ p in cloud)
            {
                XYZ rel = p - start;
                double t = rel.DotProduct(axis);
                if (t < -0.15 || t > len + 0.15) continue;
                if ((rel - axis.Multiply(t)).GetLength() > band) continue;
                local.Add(p);
                if (local.Count >= cap) break;
            }
            return local;
        }

        /// <summary>
        /// Remove only obvious phantoms — keep real pipes even in sparse isometric partial scans.
        /// </summary>
                private static void AlignSegmentsToPointCloud(List<PipeNetworkBuilder.Segment> segs, IList<XYZ> points)
        {
            IList<XYZ> src = points != null && points.Count > 16000 ? VoxelDownsample(points, 0.04) : points;
            foreach (PipeNetworkBuilder.Segment seg in segs)
            {
                double len = seg.Start.DistanceTo(seg.End);
                if (len < 0.1) continue;
                XYZ axis = seg.Axis ?? Norm(seg.End - seg.Start);
                double rad = seg.RadiusFt > 1e-6 ? seg.RadiusFt : (seg.DiameterMm / 304.8 / 2.0);

                var localPoints = new List<XYZ>();
                foreach (XYZ p in src)
                {
                    XYZ rel = p - seg.Start;
                    double t = rel.DotProduct(axis);
                    if (t < -0.2 || t > len + 0.2) continue;
                    XYZ onAxis = seg.Start + axis.Multiply(t);
                    if ((p - onAxis).GetLength() <= rad * 2.2 + 0.1)
                    {
                        localPoints.Add(p);
                        if (localPoints.Count >= 2200) break;
                    }
                }

                if (localPoints.Count < 6) continue;

                CylinderFit.Result fit = new CylinderFit.Result
                {
                    Success = true,
                    Start = seg.Start,
                    End = seg.End,
                    Axis = axis,
                    Radius = rad,
                };
                CylinderFit.RefineToPointCloud(ref fit, localPoints);
                seg.Start = fit.Start;
                seg.End = fit.End;
                seg.Axis = fit.Axis;
                seg.RadiusFt = fit.Radius;
            }
        }

        private static void HealSegmentJunctions(List<PipeNetworkBuilder.Segment> segs)
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

        private static List<PipeNetworkBuilder.Segment> FilterWallEmbedded(
            List<PipeNetworkBuilder.Segment> segs, IList<XYZ> cloud)
        {
            if (segs == null || segs.Count == 0 || cloud == null || cloud.Count < 28) return segs;
            var kept = new List<PipeNetworkBuilder.Segment>(segs.Count);
            foreach (var s in segs)
            {
                double r = s.RadiusFt > 1e-6 ? s.RadiusFt : 0.10;
                XYZ ax = s.Axis ?? Norm(s.End - s.Start);
                bool vertical = Math.Abs(ax.Z) > 0.82;
                if (vertical)
                {
                    if (IsWallPaintedVertical(s.Start, s.End, r, cloud)) continue;
                    kept.Add(s);
                    continue;
                }
                if (IsWallEmbedded(s.Start, s.End, r, cloud)) continue;
                kept.Add(s);
            }
            return kept.Count > 0 ? kept : segs;
        }

        private static List<PipeNetworkBuilder.Segment> FilterPhantomSegments(List<PipeNetworkBuilder.Segment> segs)
        {
            if (segs == null || segs.Count <= 1) return segs;

            var kept = new List<PipeNetworkBuilder.Segment>();
            foreach (PipeNetworkBuilder.Segment seg in segs)
            {
                double len = seg.Start.DistanceTo(seg.End);
                bool phantom = seg.ShellPointCount < 6
                    && seg.Confidence < 0.48f
                    && len < 0.7;
                if (!phantom) kept.Add(seg);
            }

            return kept.Count > 0 ? kept : segs;
        }

                private static List<PipeNetworkBuilder.Segment> FilterDuplicateSegments(List<PipeNetworkBuilder.Segment> segs)
        {
            if (segs.Count <= 1) return segs;

            // Prioritize higher confidence and tighter, realistic pipe radii
            segs.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));
            var kept = new List<PipeNetworkBuilder.Segment>();

            foreach (PipeNetworkBuilder.Segment candidate in segs)
            {
                bool duplicate = false;
                XYZ axisC = Norm(candidate.End - candidate.Start);

                foreach (PipeNetworkBuilder.Segment existing in kept)
                {
                    XYZ axisE = Norm(existing.End - existing.Start);
                    if (Math.Abs(axisC.DotProduct(axisE)) < 0.94) continue;

                    double maxDiaFt = Math.Max(candidate.DiameterMm, existing.DiameterMm) / 304.8;
                    double perp = PerpDistance(existing.Start, existing.End, candidate.Start);
                    if (perp > Math.Max(maxDiaFt * 0.9, 0.25))
                        continue;

                    if (AxisOverlapRatio(candidate, existing) < 0.28) continue;
                    
                    // Same physical 3D pipe corridor: keep the tighter/more confident segment!
                    duplicate = true;
                    break;
                }

                if (!duplicate) kept.Add(candidate);
            }

            return kept;
        }

        private static double AxisOverlapRatio(PipeNetworkBuilder.Segment a, PipeNetworkBuilder.Segment b)
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

        private static float ScoreConfidence(CylinderFit.Result fit, double lengthFt)
        {
            double rmsIn = fit.Rms * 12.0;
            double score = 1.0 - Math.Min(0.5, rmsIn / Math.Max(fit.Radius * 12.0, 0.5));
            if (fit.ArcSpanDeg < 90) score *= 0.88;
            if (lengthFt < 0.8) score *= 0.90;
            return (float)Math.Max(0.42, Math.Min(0.99, score));
        }

        /// <summary>
        /// True when the cluster is a plane (wall/floor/ceiling), not a tube.
        /// Cylinder: one long axis, two similar small cross-section axes.
        /// Plane: two large extents, one thin.
        /// </summary>
        private static bool IsPlanarCluster(List<XYZ> cluster)
        {
            if (cluster == null || cluster.Count < 18) return false;

            double cx = 0, cy = 0, cz = 0;
            foreach (var p in cluster) { cx += p.X; cy += p.Y; cz += p.Z; }
            double inv = 1.0 / cluster.Count;
            cx *= inv; cy *= inv; cz *= inv;

            double cxx = 0, cxy = 0, cxz = 0, cyy = 0, cyz = 0, czz = 0;
            foreach (var p in cluster)
            {
                double dx = p.X - cx, dy = p.Y - cy, dz = p.Z - cz;
                cxx += dx * dx; cxy += dx * dy; cxz += dx * dz;
                cyy += dy * dy; cyz += dy * dz; czz += dz * dz;
            }

            double[] ev = EigenValues3x3(
                cxx * inv, cyy * inv, czz * inv, cxy * inv, cxz * inv, cyz * inv);
            if (ev[0] < 1e-8) return false;
            // Coplanar L/U pipe networks in TOP view also have two large extents and
            // one thin (diameter). Only treat as a sheet when the plane is filled.
            if (ev[1] / ev[0] <= 0.55 || ev[2] / ev[0] >= 0.04)
                return false;

            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            double minZ = double.MaxValue, maxZ = double.MinValue;
            foreach (var p in cluster)
            {
                if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
                if (p.Z < minZ) minZ = p.Z; if (p.Z > maxZ) maxZ = p.Z;
            }
            double[] dims = { maxX - minX, maxY - minY, maxZ - minZ };
            Array.Sort(dims);
            double area = Math.Max(0.1, dims[2] * dims[1]);
            double density = cluster.Count / area;
            return density > 90.0;
        }

        /// <summary>
        /// Cluster has two comparable extents (L / U / tee), so a single global cylinder
        /// would lock onto the bounding-box diagonal.
        /// </summary>
        private static bool IsBranchedCluster(List<XYZ> cluster)
        {
            if (cluster == null || cluster.Count < 20) return false;
            double cx = 0, cy = 0, cz = 0;
            foreach (var p in cluster) { cx += p.X; cy += p.Y; cz += p.Z; }
            double inv = 1.0 / cluster.Count;
            cx *= inv; cy *= inv; cz *= inv;
            double cxx = 0, cxy = 0, cxz = 0, cyy = 0, cyz = 0, czz = 0;
            foreach (var p in cluster)
            {
                double dx = p.X - cx, dy = p.Y - cy, dz = p.Z - cz;
                cxx += dx * dx; cxy += dx * dy; cxz += dx * dz;
                cyy += dy * dy; cyz += dy * dz; czz += dz * dz;
            }
            double[] ev = EigenValues3x3(
                cxx * inv, cyy * inv, czz * inv, cxy * inv, cxz * inv, cyz * inv);
            if (ev[0] < 1e-8) return false;
            return ev[1] / ev[0] > 0.22;
        }

        private static double[] EigenValues3x3(double cxx, double cyy, double czz, double cxy, double cxz, double cyz)
        {
            // Power iteration on A and on (trace*I - A) is heavier than needed;
            // use the same Jacobi style as the duct fitter, 3x3 symmetric.
            double[,] A = new double[3, 3]
            {
                { cxx, cxy, cxz },
                { cxy, cyy, cyz },
                { cxz, cyz, czz }
            };
            for (int iter = 0; iter < 24; iter++)
            {
                int p = 0, q = 1;
                double maxOff = Math.Abs(A[0, 1]);
                if (Math.Abs(A[0, 2]) > maxOff) { p = 0; q = 2; maxOff = Math.Abs(A[0, 2]); }
                if (Math.Abs(A[1, 2]) > maxOff) { p = 1; q = 2; maxOff = Math.Abs(A[1, 2]); }
                if (maxOff < 1e-12) break;
                double app = A[p, p], aqq = A[q, q], apq = A[p, q];
                double phi = 0.5 * Math.Atan2(2 * apq, aqq - app);
                double c = Math.Cos(phi), s = Math.Sin(phi);
                A[p, p] = c * c * app - 2 * s * c * apq + s * s * aqq;
                A[q, q] = s * s * app + 2 * s * c * apq + c * c * aqq;
                A[p, q] = 0; A[q, p] = 0;
                for (int k = 0; k < 3; k++)
                {
                    if (k == p || k == q) continue;
                    double akp = A[k, p], akq = A[k, q];
                    A[k, p] = c * akp - s * akq; A[p, k] = A[k, p];
                    A[k, q] = s * akp + c * akq; A[q, k] = A[k, q];
                }
            }
            var ev = new[] { A[0, 0], A[1, 1], A[2, 2] };
            Array.Sort(ev);
            Array.Reverse(ev);
            return ev;
        }

        private static bool IsFlatWallCluster(List<XYZ> cluster)
        {
            if (cluster == null || cluster.Count < 40) return false;

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

            // Must be large in 2 dimensions (a wall face). Thin wall-edge strips
            // are rejected later by LooksLikePipeShell (filled / non-circular).
            if (d1 < 1.8 || d2 < 1.2) return false;

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
            if (d3 < 2.2 && densitySqFt > 180.0)
            {
                return true;
            }

            return false;
        }

        private static List<XYZ> RemoveCylinderInliers(List<XYZ> pts, CylinderFit.Result fit, bool aggressive)
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
            if (measuredOdInches >= lookup[lookup.Length - 1].OD) return Math.Round(measuredOdInches);

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












