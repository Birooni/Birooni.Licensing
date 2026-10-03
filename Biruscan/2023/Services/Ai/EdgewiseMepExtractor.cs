using Autodesk.Revit.DB;
using Autodesk.Revit.DB.PointClouds;
using System;
using System.Collections.Generic;

namespace Biruscan.Services.Ai
{
    /// <summary>
    /// EdgeWise-style automated MEP extraction over the whole point cloud.
    /// Tiles the scan, fits cylinders/rectangles locally at high density (no clip required),
    /// then merges collinear fragments. Neural Standard / Trained / Combined still differ
    /// because each tile is filtered with the selected pipeline before extract.
    /// </summary>
    public sealed class EdgewiseMepExtractor
    {
        private const double DefaultChunkFt = 12.0;
        private const double OverlapFt = 2.5;
        private const int MaxTiles = 80;
        private const int PointsPerTile = 10000;
        private const int MinTilePoints = 30;

        public sealed class Result
        {
            public List<BirooniAiElement> Elements { get; } = new List<BirooniAiElement>();
            public int TilesProcessed { get; set; }
            public int TilesWithHits { get; set; }
            public int RawPoints { get; set; }
            public bool UsedWholeCloud { get; set; }
            public string Note { get; set; } = "";
            public List<XYZ> WorldPoints { get; } = new List<XYZ>();
        }

        public Result Extract(Document doc, string service, double? diameterOverrideMm, string pipeline)
        {
            var result = new Result { UsedWholeCloud = true };
            var pcs = new PointCloudService(doc);
            PointCloudInstance pci = pcs.GetFirstPointCloudInstance();
            if (pci == null)
            {
                result.Note = "No point cloud found.";
                return result;
            }

            string pipe = (pipeline ?? "standard").Trim().ToLowerInvariant();
            List<XYZ> worldPts = LoadDenseWorldPoints(pcs, pci, out bool fromClip, tileWholeCloud: pipe == "fit");
            result.WorldPoints.AddRange(worldPts);
            result.UsedWholeCloud = !fromClip;
            result.RawPoints = worldPts.Count;
            result.TilesProcessed = 1;
            if (worldPts.Count < MinTilePoints)
            {
                result.Note = fromClip
                    ? "Clip has too few points."
                    : "Point cloud has too few points.";
                return result;
            }

            var fitter = new GeometricMepFitService();
            List<BirooniAiElement> elems = fitter.ExtractMepRuns(
                worldPts, service, diameterOverrideMm, skipVerticalRisers: pipe == "fit");
            elems = GeometricMepFitService.KeepSupportedPipes(elems, worldPts, service, diameterOverrideMm);
            // Fit Generate MEP only: drop wall-outline pipes and runs with no cloud.
            if (pipe == "fit")
                elems = FilterFitFalsePositives(elems, worldPts, service);
            if (elems != null && elems.Count > 0)
            {
                result.TilesWithHits = 1;
                result.Elements.AddRange(elems);
            }

            result.Note = fromClip
                ? $"Edgewise dense extract on clip ({pipe}, {worldPts.Count} pts)"
                : (pipe == "fit"
                    ? $"Edgewise tiled whole-cloud extract ({pipe}, {worldPts.Count} pts)"
                    : $"Edgewise dense extract on whole cloud ({pipe}, {worldPts.Count} pts)");
            return result;
        }

        public static List<XYZ> LoadDenseWorldPoints(Document doc, out bool fromClip)
        {
            fromClip = false;
            var pcs = new PointCloudService(doc);
            PointCloudInstance pci = pcs.GetFirstPointCloudInstance();
            if (pci == null) return new List<XYZ>();
            return LoadDenseWorldPoints(pcs, pci, out fromClip, tileWholeCloud: false);
        }

        private static List<XYZ> LoadDenseWorldPoints(PointCloudService pcs, PointCloudInstance pci, out bool fromClip, bool tileWholeCloud)
        {
            fromClip = false;
            var world = new List<XYZ>();
            BoundingBoxXYZ worldBox = pcs.GetWorldBounds(pci);
            XYZ anchor = worldBox != null ? (worldBox.Min + worldBox.Max).Multiply(0.5) : XYZ.Zero;
            Transform worldMap = pcs.GetWorldMappingTransform(pci, anchor);

            PointCloudFilter isolate = null;
            try
            {
                if (pci.FilterAction == SelectionFilterAction.Isolate)
                    isolate = pci.GetSelectionFilter();
            }
            catch { isolate = null; }

            try
            {
                if (isolate != null)
                {
                    fromClip = true;
                    PointCollection pc = pci.GetPoints(isolate, 0.025, 60000);
                    var voxelKeys = new HashSet<long>();
                    foreach (CloudPoint cp in pc)
                        AddUniqueWorld(world, voxelKeys, worldMap.OfPoint(new XYZ(cp.X, cp.Y, cp.Z)), 0.025);

                    // At most a few extra reads on XY tiles that the isolate sample starved.
                    AppendStarvedClipTiles(pcs, pci, worldMap, world, voxelKeys);

                    if (world.Count >= MinTilePoints)
                        return world;
                    world.Clear();
                    fromClip = false;
                }
            }
            catch { world.Clear(); fromClip = false; }

            if (tileWholeCloud)
            {
                List<XYZ> tiled = LoadTiledWorldCloud(pcs, pci, worldMap, worldBox);
                if (tiled.Count >= MinTilePoints)
                    return tiled;
            }

            if (worldBox != null)
            {
                List<XYZ> local = pcs.GetPointsInRegion(pci, worldBox, 60000, 0.04);
                foreach (XYZ lp in local)
                    world.Add(worldMap.OfPoint(lp));
            }
            return world;
        }

        /// <summary>
        /// Edgewise whole-cloud read: each XY tile gets its own GetPoints budget so thin
        /// pipes are not starved by walls/floors in a single 60k dump.
        /// </summary>
        private static List<XYZ> LoadTiledWorldCloud(
            PointCloudService pcs, PointCloudInstance pci, Transform worldMap, BoundingBoxXYZ bb)
        {
            var world = new List<XYZ>();
            if (bb == null) return world;

            double dx = bb.Max.X - bb.Min.X;
            double dy = bb.Max.Y - bb.Min.Y;
            if (dx < 0.5 || dy < 0.5) return world;

            if (dx < 18.0 && dy < 18.0)
            {
                var keysSmall = new HashSet<long>();
                List<XYZ> local = pcs.GetPointsInRegion(pci, bb, 50000, 0.03);
                foreach (XYZ lp in local)
                    AddUniqueWorld(world, keysSmall, worldMap.OfPoint(lp), 0.03);
                return world;
            }

            const int maxTiles = 16;
            const double overlap = 1.5;
            double tile = 14.0;
            int nx = Math.Max(1, (int)Math.Ceiling(dx / tile));
            int ny = Math.Max(1, (int)Math.Ceiling(dy / tile));
            if (nx * ny > maxTiles)
            {
                double scale = Math.Sqrt((nx * ny) / (double)maxTiles);
                tile *= scale;
                nx = Math.Max(1, Math.Min(4, (int)Math.Ceiling(dx / tile)));
                ny = Math.Max(1, Math.Min(4, (int)Math.Ceiling(dy / tile)));
            }

            double stepX = dx / nx;
            double stepY = dy / ny;
            var voxelKeys = new HashSet<long>();

            for (int ix = 0; ix < nx; ix++)
            {
                for (int iy = 0; iy < ny; iy++)
                {
                    if (world.Count >= 55000) return world;
                    var tileBox = new BoundingBoxXYZ
                    {
                        Transform = Transform.Identity,
                        Min = new XYZ(
                            bb.Min.X + ix * stepX - overlap,
                            bb.Min.Y + iy * stepY - overlap,
                            bb.Min.Z - 0.4),
                        Max = new XYZ(
                            bb.Min.X + (ix + 1) * stepX + overlap,
                            bb.Min.Y + (iy + 1) * stepY + overlap,
                            bb.Max.Z + 0.4)
                    };
                    List<XYZ> local = pcs.GetPointsInRegion(pci, tileBox, 7000, 0.035);
                    foreach (XYZ lp in local)
                        AddUniqueWorld(world, voxelKeys, worldMap.OfPoint(lp), 0.03);
                }
            }
            return world;
        }

        private static void AppendStarvedClipTiles(
            PointCloudService pcs, PointCloudInstance pci, Transform worldMap,
            List<XYZ> world, HashSet<long> voxelKeys)
        {
            BoundingBoxXYZ bb = pcs.GetWorldBounds(pci);
            if (bb == null) return;

            double dx = bb.Max.X - bb.Min.X;
            double dy = bb.Max.Y - bb.Min.Y;
            if (dx < 1.0 || dy < 1.0 || dx > 50.0 || dy > 50.0) return;

            const double tile = 8.0;
            int nx = Math.Max(1, Math.Min(4, (int)Math.Ceiling(dx / tile)));
            int ny = Math.Max(1, Math.Min(4, (int)Math.Ceiling(dy / tile)));
            if (nx * ny <= 1) return;

            double stepX = dx / nx;
            double stepY = dy / ny;
            var counts = new int[nx * ny];
            foreach (XYZ p in world)
            {
                int ix = (int)Math.Floor((p.X - bb.Min.X) / stepX);
                int iy = (int)Math.Floor((p.Y - bb.Min.Y) / stepY);
                if (ix < 0) ix = 0; if (iy < 0) iy = 0;
                if (ix >= nx) ix = nx - 1; if (iy >= ny) iy = ny - 1;
                counts[ix + iy * nx]++;
            }

            var starved = new List<(int ix, int iy, int n)>();
            for (int ix = 0; ix < nx; ix++)
                for (int iy = 0; iy < ny; iy++)
                    if (counts[ix + iy * nx] < 80)
                        starved.Add((ix, iy, counts[ix + iy * nx]));
            starved.Sort((a, b) => a.n.CompareTo(b.n));

            int extra = 0;
            foreach (var s in starved)
            {
                if (extra >= 4) break;
                extra++;
                var tileBox = new BoundingBoxXYZ
                {
                    Transform = Transform.Identity,
                    Min = new XYZ(
                        bb.Min.X + s.ix * stepX - 0.4,
                        bb.Min.Y + s.iy * stepY - 0.4,
                        bb.Min.Z - 0.4),
                    Max = new XYZ(
                        bb.Min.X + (s.ix + 1) * stepX + 0.4,
                        bb.Min.Y + (s.iy + 1) * stepY + 0.4,
                        bb.Max.Z + 0.4)
                };
                List<XYZ> local = pcs.GetPointsInRegion(pci, tileBox, 8000, 0.03);
                foreach (XYZ lp in local)
                    AddUniqueWorld(world, voxelKeys, worldMap.OfPoint(lp), 0.025);
            }
        }

        private static void AddUniqueWorld(List<XYZ> world, HashSet<long> keys, XYZ p, double voxel)
        {
            long key =
                ((long)Math.Floor(p.X / voxel) & 0x1FFFFF) |
                (((long)Math.Floor(p.Y / voxel) & 0x1FFFFF) << 21) |
                (((long)Math.Floor(p.Z / voxel) & 0x1FFFFF) << 42);
            if (keys.Add(key))
                world.Add(p);
        }

        /// <summary>
        /// Fit Generate MEP only. Removes wall-outline cylinders and pipes that
        /// do not sit on the point cloud (green in empty black space).
        /// </summary>
        private static List<BirooniAiElement> FilterFitFalsePositives(
            List<BirooniAiElement> elems, List<XYZ> cloud, string service)
        {
            if (elems == null || elems.Count == 0) return elems ?? new List<BirooniAiElement>();
            if (cloud == null || cloud.Count < 20) return elems;
            if (string.Equals(service, "Duct", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(service, "Cable Tray", StringComparison.OrdinalIgnoreCase))
                return elems;

            var horizontals = new List<BirooniAiElement>();
            var verticals = new List<BirooniAiElement>();
            foreach (var e in elems)
            {
                if (e.Start == null || e.End == null || e.Start.Length != 3 || e.End.Length != 3)
                    continue;
                XYZ s = new XYZ(e.Start[0], e.Start[1], e.Start[2]);
                XYZ en = new XYZ(e.End[0], e.End[1], e.End[2]);
                double len = s.DistanceTo(en);
                if (len < 0.80) continue;
                XYZ axis = en - s;
                if (axis.GetLength() < 1e-9) continue;
                axis = axis.Normalize();
                double r = 0.12;
                if (e.DiameterMm.HasValue && e.DiameterMm.Value > 0)
                    r = e.DiameterMm.Value / 304.8 * 0.5;

                bool vertical = Math.Abs(axis.Z) > 0.82;
                if (vertical)
                {
                    if (GeometricMepFitService.IsWallPaintedVertical(s, en, r, cloud)) continue;
                    verticals.Add(e);
                    continue;
                }

                if (GeometricMepFitService.IsWallEmbedded(s, en, r, cloud)) continue;
                if (!FitHasCloudShell(s, en, axis, r, len, cloud, out XYZ ns, out XYZ ne, out List<XYZ> band))
                    continue;
                if (ns.DistanceTo(ne) < 0.80) continue;
                e.Start = new[] { ns.X, ns.Y, ns.Z };
                e.End = new[] { ne.X, ne.Y, ne.Z };
                if (FitNeighborhoodIsWallSheet(band, ns, ne, r)) continue;
                horizontals.Add(e);
            }

            var kept = new List<BirooniAiElement>(horizontals);
            foreach (var v in verticals)
            {
                XYZ vs = new XYZ(v.Start[0], v.Start[1], v.Start[2]);
                XYZ ve = new XYZ(v.End[0], v.End[1], v.End[2]);
                double vx = (vs.X + ve.X) * 0.5;
                double vy = (vs.Y + ve.Y) * 0.5;
                bool connected = false;
                foreach (var h in horizontals)
                {
                    XYZ hs = new XYZ(h.Start[0], h.Start[1], h.Start[2]);
                    XYZ he = new XYZ(h.End[0], h.End[1], h.End[2]);
                    if (XyDist(vx, vy, hs.X, hs.Y) < 0.55 || XyDist(vx, vy, he.X, he.Y) < 0.55)
                    {
                        connected = true;
                        break;
                    }
                }
                if (connected) kept.Add(v);
            }
            return kept;
        }

        private static double XyDist(double x1, double y1, double x2, double y2)
        {
            double dx = x1 - x2, dy = y1 - y2;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static bool FitHasCloudShell(
            XYZ s, XYZ en, XYZ axis, double r, double len, List<XYZ> cloud,
            out XYZ ns, out XYZ ne, out List<XYZ> band)
        {
            ns = s;
            ne = en;
            band = new List<XYZ>();
            int hug = 0, near = 0;
            double tMin = double.MaxValue, tMax = double.MinValue;
            foreach (XYZ p in cloud)
            {
                XYZ rel = p - s;
                double t = rel.DotProduct(axis);
                if (t < -0.20 || t > len + 0.20) continue;
                double radial = (rel - axis.Multiply(t)).GetLength();
                if (radial > 1.15) continue;
                if (band.Count < 280) band.Add(p);
                if (radial > r * 2.2 + 0.10) continue;
                near++;
                if (Math.Abs(radial - r) <= r * 0.45 + 0.08)
                {
                    hug++;
                    if (t < tMin) tMin = t;
                    if (t > tMax) tMax = t;
                }
            }
            if (near < 8 || hug < 5) return false;
            if (tMax > tMin + 0.50)
            {
                ns = s + axis.Multiply(tMin);
                ne = s + axis.Multiply(tMax);
            }
            return true;
        }

        private static bool FitNeighborhoodIsWallSheet(List<XYZ> band, XYZ start, XYZ end, double r)
        {
            if (band == null || band.Count < 40) return false;
            double len = start.DistanceTo(end);
            if (len < 2.5) return false;

            double cx = 0, cy = 0, cz = 0;
            foreach (var p in band) { cx += p.X; cy += p.Y; cz += p.Z; }
            double inv = 1.0 / band.Count;
            cx *= inv; cy *= inv; cz *= inv;
            double cxx = 0, cxy = 0, cxz = 0, cyy = 0, cyz = 0, czz = 0;
            foreach (var p in band)
            {
                double dx = p.X - cx, dy = p.Y - cy, dz = p.Z - cz;
                cxx += dx * dx; cxy += dx * dy; cxz += dx * dz;
                cyy += dy * dy; cyz += dy * dz; czz += dz * dz;
            }
            cxx *= inv; cyy *= inv; czz *= inv;
            cxy *= inv; cxz *= inv; cyz *= inv;
            double[] ev = Eigen3(cxx, cyy, czz, cxy, cxz, cyz);
            if (ev[0] < 1e-8) return false;
            if (ev[1] / ev[0] < 0.22 || ev[2] / ev[0] > 0.07)
                return false;
            double density = band.Count / Math.Max(len * Math.Max(2.0 * r, 0.4), 0.5);
            return density > 18.0;
        }

        private static double[] Eigen3(double cxx, double cyy, double czz, double cxy, double cxz, double cyz)
        {
            double[] a = { cxx, cxy, cxz, cxy, cyy, cyz, cxz, cyz, czz };
            double[] v = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
            for (int iter = 0; iter < 12; iter++)
            {
                int p = 0, q = 1;
                double max = Math.Abs(a[1]);
                if (Math.Abs(a[2]) > max) { max = Math.Abs(a[2]); p = 0; q = 2; }
                if (Math.Abs(a[5]) > max) { p = 1; q = 2; }
                if (max < 1e-12) break;
                double app = a[p * 3 + p], aqq = a[q * 3 + q], apq = a[p * 3 + q];
                double tau = (aqq - app) / (2.0 * apq);
                double t = Math.Sign(tau) / (Math.Abs(tau) + Math.Sqrt(1.0 + tau * tau));
                double c = 1.0 / Math.Sqrt(1.0 + t * t);
                double s = t * c;
                a[p * 3 + p] = app - t * apq;
                a[q * 3 + q] = aqq + t * apq;
                a[p * 3 + q] = a[q * 3 + p] = 0;
                for (int k = 0; k < 3; k++)
                {
                    if (k == p || k == q) continue;
                    double akp = a[k * 3 + p], akq = a[k * 3 + q];
                    a[k * 3 + p] = a[p * 3 + k] = c * akp - s * akq;
                    a[k * 3 + q] = a[q * 3 + k] = s * akp + c * akq;
                }
            }
            double[] ev = { a[0], a[4], a[8] };
            Array.Sort(ev);
            Array.Reverse(ev);
            return ev;
        }

        private static void MergeInto(List<BirooniAiElement> dest, List<BirooniAiElement> extra)
        {
            foreach (var geom in extra)
            {
                if (geom.Start == null || geom.End == null || geom.Start.Length != 3 || geom.End.Length != 3)
                    continue;
                XYZ gMid = new XYZ(
                    (geom.Start[0] + geom.End[0]) * 0.5,
                    (geom.Start[1] + geom.End[1]) * 0.5,
                    (geom.Start[2] + geom.End[2]) * 0.5);

                bool dup = false;
                foreach (var baseEl in dest)
                {
                    if (baseEl.Start == null || baseEl.End == null || baseEl.Start.Length != 3 || baseEl.End.Length != 3)
                        continue;
                    XYZ bMid = new XYZ(
                        (baseEl.Start[0] + baseEl.End[0]) * 0.5,
                        (baseEl.Start[1] + baseEl.End[1]) * 0.5,
                        (baseEl.Start[2] + baseEl.End[2]) * 0.5);
                    if (gMid.DistanceTo(bMid) < 0.40) { dup = true; break; }
                }
                if (!dup) dest.Add(geom);
            }
        }
    }
}
