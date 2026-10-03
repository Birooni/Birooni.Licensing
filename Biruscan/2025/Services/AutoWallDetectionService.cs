using Autodesk.Revit.DB;
using Autodesk.Revit.DB.PointClouds;
using System;
using System.Collections.Generic;
using System.Linq;
using Biruscan.Models;
using Biruscan.Services;

namespace Biruscan.Services
{
    /// <summary>
    /// Configuration options for automatic wall extraction.
    /// </summary>
    public class AutoWallOptions
    {
        public Level TargetLevel { get; set; }
        public double WallHeight { get; set; } = 10.0;
        public double MinWallLengthFt { get; set; } = 2.0;
        public bool OrthoSnap { get; set; } = true;
        public double OrthoSnapToleranceDeg { get; set; } = 4.5;
        public bool UseActiveRoiOnly { get; set; } = true;
        public WallType OverrideWallType { get; set; } = null;
    }

    /// <summary>
    /// Represents an automatically detected wall segment prior to or after Revit element placement.
    /// </summary>
    public class DetectedWallSegment
    {
        public XYZ Start { get; set; }
        public XYZ End { get; set; }
        public double ThicknessFt { get; set; } = 0.667; // default 8" (0.2m)
        public double LengthFt => Start != null && End != null ? Start.DistanceTo(End) : 0.0;
        public WallType MatchedWallType { get; set; }
        public int InlierCount { get; set; }
    }

    /// <summary>
    /// Result of the automatic wall extraction operation.
    /// </summary>
    public class WallExtractionResult
    {
        public bool Success { get; set; }
        public int WallsCreated { get; set; }
        public List<ElementId> CreatedWallIds { get; set; } = new List<ElementId>();
        public string Message { get; set; }
        public List<DetectedWallSegment> DetectedSegments { get; set; } = new List<DetectedWallSegment>();

        public static WallExtractionResult OK(int count, List<ElementId> ids, string msg, List<DetectedWallSegment> segs)
        {
            return new WallExtractionResult
            {
                Success = true,
                WallsCreated = count,
                CreatedWallIds = ids ?? new List<ElementId>(),
                Message = msg,
                DetectedSegments = segs ?? new List<DetectedWallSegment>()
            };
        }

        public static WallExtractionResult Fail(string msg)
        {
            return new WallExtractionResult
            {
                Success = false,
                WallsCreated = 0,
                Message = msg
            };
        }
    }

    /// <summary>
    /// Engine for automatically extracting 2D wall lines and wall thicknesses from point cloud data,
    /// rectifying orthogonal angles, and generating native parametric Revit walls in a single pass.
    /// </summary>
    public class AutoWallDetectionService
    {
        private readonly Document _doc;

        public AutoWallDetectionService(Document doc)
        {
            _doc = doc;
        }

        /// <summary>
        /// Automatically extracts walls from the point cloud and creates Revit wall elements.
        /// </summary>
        public WallExtractionResult AutoExtractAndCreateWalls(AutoWallOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            Level level = options.TargetLevel ?? FindDefaultLevel();
            if (level == null)
                return WallExtractionResult.Fail("No target level specified and no level found in the Revit project.");

            // 1. Gather point cloud points
            List<XYZ> worldPoints = GatherPoints(options, level);
            if (worldPoints == null || worldPoints.Count < 30)
            {
                return WallExtractionResult.Fail(
                    "Insufficient points found in the selected region (minimum 30 points required).\n" +
                    "Tip: Sice or crop the point cloud around the walls of interest, or ensure the cloud is visible.");
            }

            // 2. Project points to 2D at level elevation and voxel downsample
            double levelZ = level.Elevation;
            List<XYZ> points2D = worldPoints.Select(p => new XYZ(p.X, p.Y, levelZ)).ToList();
            List<XYZ> sampled2D = VoxelGrid2D(points2D, 0.08); // ~2.5cm voxel cell

            if (sampled2D.Count < 20)
                return WallExtractionResult.Fail("Point density too sparse after filtering.");

            // 3. Multi-pass 2D RANSAC line detection
            List<DetectedWallSegment> rawLines = ExtractRansacLines2D(sampled2D, options.MinWallLengthFt);
            if (rawLines.Count == 0)
                return WallExtractionResult.Fail("No distinct wall linear segments detected in the point cloud slice.");

            // 4. Pair parallel faces to estimate exact wall thickness and generate centerlines
            List<DetectedWallSegment> wallSegments = SynthesizeWallCenterlines(rawLines, options.MinWallLengthFt);

            // 5. Orthogonal snapping and corner intersection snapping
            if (options.OrthoSnap && wallSegments.Count > 0)
            {
                RectifyOrthogonal(wallSegments, options.OrthoSnapToleranceDeg);
                SnapWallCorners(wallSegments, 1.5); // Snap corners within 1.5ft
            }

            // 6. Filter out short noise segments
            wallSegments = wallSegments.Where(w => w.LengthFt >= options.MinWallLengthFt).ToList();
            if (wallSegments.Count == 0)
                return WallExtractionResult.Fail($"No walls met the minimum length threshold of {options.MinWallLengthFt:F1} ft.");

            // 7. Match Revit WallTypes
            List<WallType> availableWallTypes = new FilteredElementCollector(_doc)
                .OfClass(typeof(WallType))
                .Cast<WallType>()
                .Where(wt => wt.Kind == WallKind.Basic)
                .ToList();

            if (availableWallTypes.Count == 0)
                return WallExtractionResult.Fail("No Basic Wall types exist in the active Revit project.");

            WallType defaultType = options.OverrideWallType ?? availableWallTypes.FirstOrDefault();

            foreach (var seg in wallSegments)
            {
                if (options.OverrideWallType != null)
                {
                    seg.MatchedWallType = options.OverrideWallType;
                }
                else
                {
                    // Find closest matching wall type by width
                    seg.MatchedWallType = availableWallTypes
                        .OrderBy(wt => Math.Abs(wt.Width - seg.ThicknessFt))
                        .FirstOrDefault() ?? defaultType;
                }
            }

            // 8. Place Revit Wall elements inside a transaction
            List<ElementId> createdIds = new List<ElementId>();
            double wallHeight = options.WallHeight > 0.5 ? options.WallHeight : 10.0;

            using (Transaction t = new Transaction(_doc, "Auto Generate Walls from Point Cloud"))
            {
                t.Start();

                foreach (var seg in wallSegments)
                {
                    try
                    {
                        Line line = Line.CreateBound(seg.Start, seg.End);
                        Wall wall = Wall.Create(_doc, line, seg.MatchedWallType.Id, level.Id, wallHeight, 0.0, false, false);
                        if (wall != null)
                        {
                            createdIds.Add(wall.Id);
                        }
                    }
                    catch
                    {
                        // Skip individual invalid geometry segment
                    }
                }

                if (createdIds.Count > 0)
                {
                    t.Commit();
                    return WallExtractionResult.OK(
                        createdIds.Count,
                        createdIds,
                        $"Successfully generated {createdIds.Count} wall(s) automatically from point cloud data.",
                        wallSegments
                    );
                }
                else
                {
                    t.RollBack();
                    return WallExtractionResult.Fail("Failed to place Revit wall elements from detected geometry.");
                }
            }
        }

        #region Point Gathering

        private List<XYZ> GatherPoints(AutoWallOptions options, Level level)
        {
            var result = new List<XYZ>();

            try
            {
                var pcs = new PointCloudService(_doc);
                var pci = pcs.GetFirstPointCloudInstance();
                if (pci != null)
                {
                    BoundingBoxXYZ bbox = pci.get_BoundingBox(null);
                    if (bbox != null)
                    {
                        double minZ = level.Elevation + 2.5;
                        double maxZ = level.Elevation + 5.0;

                        BoundingBoxXYZ sliceBox = new BoundingBoxXYZ
                        {
                            Transform = Transform.Identity,
                            Min = new XYZ(bbox.Min.X, bbox.Min.Y, minZ),
                            Max = new XYZ(bbox.Max.X, bbox.Max.Y, maxZ)
                        };

                        Transform worldMap = pci.GetTransform() ?? Transform.Identity;
                        List<XYZ> localPts = pcs.GetPointsInRegion(pci, sliceBox, 100000);

                        foreach (XYZ lp in localPts)
                        {
                            XYZ w = worldMap.OfPoint(lp);
                            result.Add(w);
                        }
                    }
                }
            }
            catch { }

            return result;
        }

        private Level FindDefaultLevel()
        {
            return new FilteredElementCollector(_doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .FirstOrDefault();
        }

        #endregion

        #region Geometric Algorithms (2D RANSAC & Synthesis)

        private static List<XYZ> VoxelGrid2D(List<XYZ> points, double cellSize)
        {
            var grid = new Dictionary<long, XYZ>();
            foreach (var p in points)
            {
                long gx = (long)Math.Floor(p.X / cellSize);
                long gy = (long)Math.Floor(p.Y / cellSize);
                long key = (gx * 397) ^ gy;
                if (!grid.ContainsKey(key))
                {
                    grid[key] = p;
                }
            }
            return grid.Values.ToList();
        }

        private static List<DetectedWallSegment> ExtractRansacLines2D(List<XYZ> points, double minLengthFt)
        {
            var segments = new List<DetectedWallSegment>();
            var remaining = new List<XYZ>(points);
            var rand = new Random(42);
            double inlierDist = 0.12; // 3.6cm tolerance

            int maxLines = 40;
            int passes = 0;

            while (remaining.Count >= 15 && passes < maxLines)
            {
                passes++;
                int bestInlierCount = 0;
                XYZ bestP1 = null, bestP2 = null;
                List<int> bestInlierIndices = null;

                int iterations = Math.Min(800, remaining.Count * 8);

                for (int i = 0; i < iterations; i++)
                {
                    int idx1 = rand.Next(remaining.Count);
                    int idx2 = rand.Next(remaining.Count);
                    if (idx1 == idx2) continue;

                    XYZ p1 = remaining[idx1];
                    XYZ p2 = remaining[idx2];
                    if (p1.DistanceTo(p2) < 0.5) continue;

                    XYZ dir = (p2 - p1).Normalize();
                    XYZ normal = new XYZ(-dir.Y, dir.X, 0);

                    var currentInliers = new List<int>();
                    for (int k = 0; k < remaining.Count; k++)
                    {
                        XYZ pt = remaining[k];
                        double d = Math.Abs((pt.X - p1.X) * normal.X + (pt.Y - p1.Y) * normal.Y);
                        if (d <= inlierDist)
                        {
                            currentInliers.Add(k);
                        }
                    }

                    if (currentInliers.Count > bestInlierCount)
                    {
                        bestInlierCount = currentInliers.Count;
                        bestP1 = p1;
                        bestP2 = p2;
                        bestInlierIndices = currentInliers;
                    }
                }

                if (bestInlierCount < 12 || bestP1 == null || bestP2 == null)
                    break;

                // Project inliers along direction vector to find contiguous spans
                XYZ lineDir = (bestP2 - bestP1).Normalize();
                var projList = new List<(double t, XYZ orig)>();
                foreach (int idx in bestInlierIndices)
                {
                    XYZ pt = remaining[idx];
                    double t = (pt.X - bestP1.X) * lineDir.X + (pt.Y - bestP1.Y) * lineDir.Y;
                    projList.Add((t, pt));
                }

                projList.Sort((a, b) => a.t.CompareTo(b.t));

                // Find largest continuous interval (break on gaps > 1.5ft)
                int startIdx = 0;
                for (int j = 0; j < projList.Count; j++)
                {
                    bool isLast = (j == projList.Count - 1);
                    bool isGap = !isLast && ((projList[j + 1].t - projList[j].t) > 1.5);

                    if (isGap || isLast)
                    {
                        double span = projList[j].t - projList[startIdx].t;
                        if (span >= minLengthFt * 0.7)
                        {
                            XYZ segStart = bestP1 + lineDir.Multiply(projList[startIdx].t);
                            XYZ segEnd = bestP1 + lineDir.Multiply(projList[j].t);

                            segments.Add(new DetectedWallSegment
                            {
                                Start = segStart,
                                End = segEnd,
                                InlierCount = j - startIdx + 1,
                                ThicknessFt = 0.667 // default 8"
                            });
                        }
                        startIdx = j + 1;
                    }
                }

                // Remove inliers from remaining pool
                var inlierSet = new HashSet<int>(bestInlierIndices);
                var nextRemaining = new List<XYZ>();
                for (int k = 0; k < remaining.Count; k++)
                {
                    if (!inlierSet.Contains(k))
                        nextRemaining.Add(remaining[k]);
                }
                remaining = nextRemaining;
            }

            return segments;
        }

        private static List<DetectedWallSegment> SynthesizeWallCenterlines(List<DetectedWallSegment> faceLines, double minLengthFt)
        {
            var paired = new HashSet<int>();
            var walls = new List<DetectedWallSegment>();

            // 1. Search for parallel face pairs
            for (int i = 0; i < faceLines.Count; i++)
            {
                if (paired.Contains(i)) continue;
                var lA = faceLines[i];
                XYZ dirA = (lA.End - lA.Start).Normalize();

                int bestMatch = -1;
                double bestDist = 0.0;

                for (int j = i + 1; j < faceLines.Count; j++)
                {
                    if (paired.Contains(j)) continue;
                    var lB = faceLines[j];
                    XYZ dirB = (lB.End - lB.Start).Normalize();

                    double dot = Math.Abs(dirA.DotProduct(dirB));
                    if (dot < 0.96) continue; // Must be parallel within ~15 deg

                    // Calculate perpendicular distance
                    XYZ midA = (lA.Start + lA.End).Multiply(0.5);
                    XYZ midB = (lB.Start + lB.End).Multiply(0.5);
                    XYZ normalA = new XYZ(-dirA.Y, dirA.X, 0);

                    double perpDist = Math.Abs((midB.X - midA.X) * normalA.X + (midB.Y - midA.Y) * normalA.Y);

                    // Check if within architectural wall thickness range (3" to 28" -> 0.25ft to 2.3ft)
                    if (perpDist >= 0.25 && perpDist <= 2.3)
                    {
                        bestMatch = j;
                        bestDist = perpDist;
                        break;
                    }
                }

                if (bestMatch >= 0)
                {
                    paired.Add(i);
                    paired.Add(bestMatch);

                    var lB = faceLines[bestMatch];
                    XYZ midA = (lA.Start + lA.End).Multiply(0.5);
                    XYZ midB = (lB.Start + lB.End).Multiply(0.5);
                    XYZ centerMid = (midA + midB).Multiply(0.5);

                    XYZ dir = (lA.End - lA.Start).Normalize();
                    double halfLen = Math.Max(lA.LengthFt, lB.LengthFt) * 0.5;

                    walls.Add(new DetectedWallSegment
                    {
                        Start = centerMid - dir.Multiply(halfLen),
                        End = centerMid + dir.Multiply(halfLen),
                        ThicknessFt = bestDist,
                        InlierCount = lA.InlierCount + lB.InlierCount
                    });
                }
            }

            // 2. Add remaining strong unpaired lines as single-face detected walls
            for (int i = 0; i < faceLines.Count; i++)
            {
                if (!paired.Contains(i) && faceLines[i].LengthFt >= minLengthFt)
                {
                    walls.Add(faceLines[i]);
                }
            }

            return walls;
        }

        private static void RectifyOrthogonal(List<DetectedWallSegment> walls, double toleranceDeg)
        {
            if (walls.Count == 0) return;

            // Find dominant angle from longest wall
            var longest = walls.OrderByDescending(w => w.LengthFt).First();
            XYZ domDir = (longest.End - longest.Start).Normalize();
            double baseAngle = Math.Atan2(domDir.Y, domDir.X);

            double tolRad = toleranceDeg * Math.PI / 180.0;

            foreach (var wall in walls)
            {
                XYZ dir = (wall.End - wall.Start).Normalize();
                double angle = Math.Atan2(dir.Y, dir.X);
                double diff = NormalizeAngle(angle - baseAngle);

                // Check 0, 90, 180, 270 deg
                for (int k = 0; k < 4; k++)
                {
                    double target = k * (Math.PI / 2.0);
                    if (Math.Abs(NormalizeAngle(diff - target)) <= tolRad)
                    {
                        double snappedAngle = baseAngle + target;
                        XYZ snappedDir = new XYZ(Math.Cos(snappedAngle), Math.Sin(snappedAngle), 0);

                        XYZ mid = (wall.Start + wall.End).Multiply(0.5);
                        double halfLen = wall.LengthFt * 0.5;

                        wall.Start = mid - snappedDir.Multiply(halfLen);
                        wall.End = mid + snappedDir.Multiply(halfLen);
                        break;
                    }
                }
            }
        }

        private static void SnapWallCorners(List<DetectedWallSegment> walls, double maxSnapDist)
        {
            for (int i = 0; i < walls.Count; i++)
            {
                for (int j = i + 1; j < walls.Count; j++)
                {
                    var wA = walls[i];
                    var wB = walls[j];

                    XYZ dirA = (wA.End - wA.Start).Normalize();
                    XYZ dirB = (wB.End - wB.Start).Normalize();

                    double dot = Math.Abs(dirA.DotProduct(dirB));
                    if (dot > 0.25) continue; // Must be roughly perpendicular (75 to 105 deg)

                    // Check 4 endpoint combinations
                    XYZ[] ptsA = { wA.Start, wA.End };
                    XYZ[] ptsB = { wB.Start, wB.End };

                    for (int a = 0; a < 2; a++)
                    {
                        for (int b = 0; b < 2; b++)
                        {
                            if (ptsA[a].DistanceTo(ptsB[b]) <= maxSnapDist)
                            {
                                XYZ inter = LineIntersection2D(wA.Start, dirA, wB.Start, dirB);
                                if (inter != null && inter.DistanceTo(ptsA[a]) < maxSnapDist * 1.5)
                                {
                                    if (a == 0) wA.Start = inter; else wA.End = inter;
                                    if (b == 0) wB.Start = inter; else wB.End = inter;
                                }
                            }
                        }
                    }
                }
            }
        }

        private static XYZ LineIntersection2D(XYZ p1, XYZ d1, XYZ p2, XYZ d2)
        {
            double det = d1.X * d2.Y - d1.Y * d2.X;
            if (Math.Abs(det) < 1e-5) return null;

            double dx = p2.X - p1.X;
            double dy = p2.Y - p1.Y;
            double t = (dx * d2.Y - dy * d2.X) / det;

            return new XYZ(p1.X + t * d1.X, p1.Y + t * d1.Y, p1.Z);
        }

        private static double NormalizeAngle(double angle)
        {
            while (angle > Math.PI) angle -= 2 * Math.PI;
            while (angle < -Math.PI) angle += 2 * Math.PI;
            return angle;
        }

        #endregion
    }
}
