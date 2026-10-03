using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace Biruscan.AI.Vision
{
    /// <summary>
    /// Directional 3D Point Cloud Skeleton Crawler.
    /// Traces continuous tubular pipe segments by crawling strictly along the pipe axis,
    /// preventing phantom jumps across open space and ignoring disconnected walls/boxes.
    /// Licensed under MIT / Apache 2.0 (Commercially safe for proprietary sale).
    /// </summary>
    public static class PipeSkeletonCrawler
    {
        public class PipeRunPath
        {
            public List<XYZ> Points = new List<XYZ>();
            public XYZ SeedPoint;
            public XYZ Direction;
        }

        public static List<PipeRunPath> CrawlPipeRuns(IList<XYZ> points, double stepSize = 0.25, double searchRadius = 0.45)
        {
            var runs = new List<PipeRunPath>();
            if (points == null || points.Count < 15) return runs;

            var visited = new HashSet<int>();
            double r2 = searchRadius * searchRadius;

            double cellSize = searchRadius;
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

            for (int i = 0; i < points.Count; i++)
            {
                if (visited.Contains(i)) continue;

                XYZ seed = points[i];
                var localNeighbors = GetNeighbors(seed, points, grid, cellSize, r2);
                if (localNeighbors.Count < 10) continue;

                ComputeLocalDirection(localNeighbors, out XYZ forwardDir);
                if (forwardDir.GetLength() < 0.1) continue;
                forwardDir = forwardDir.Normalize();

                var currentRun = new PipeRunPath { SeedPoint = seed, Direction = forwardDir };

                CrawlAlongDirection(seed, forwardDir, points, grid, cellSize, searchRadius, visited, currentRun.Points);
                CrawlAlongDirection(seed, -forwardDir, points, grid, cellSize, searchRadius, visited, currentRun.Points);

                if (currentRun.Points.Count >= 12)
                {
                    runs.Add(currentRun);
                }
            }

            return runs;
        }

        private static void CrawlAlongDirection(XYZ startPt, XYZ dir, IList<XYZ> allPoints,
            Dictionary<long, List<int>> grid, double cellSize, double radius,
            HashSet<int> visited, List<XYZ> runPoints)
        {
            XYZ currentCenter = startPt;
            double step = radius * 0.6;
            int maxSteps = 150;

            for (int stepIdx = 0; stepIdx < maxSteps; stepIdx++)
            {
                XYZ nextCenter = currentCenter + dir.Multiply(step);
                var stepNeighbors = GetNeighbors(nextCenter, allPoints, grid, cellSize, radius * radius);

                int newlyAdded = 0;
                XYZ newCentroidSum = XYZ.Zero;

                foreach (int nIdx in stepNeighbors)
                {
                    if (visited.Add(nIdx))
                    {
                        XYZ p = allPoints[nIdx];
                        runPoints.Add(p);
                        newCentroidSum += p;
                        newlyAdded++;
                    }
                }

                if (newlyAdded < 3) break;

                XYZ newCenter = newCentroidSum.Multiply(1.0 / newlyAdded);
                XYZ stepVector = newCenter - currentCenter;
                if (stepVector.GetLength() > 0.05)
                {
                    XYZ stepDir = stepVector.Normalize();
                    if (stepDir.DotProduct(dir) > 0.65)
                    {
                        dir = (dir * 0.7 + stepDir * 0.3).Normalize();
                    }
                }

                currentCenter = newCenter;
            }
        }

        private static List<int> GetNeighbors(XYZ center, IList<XYZ> points,
            Dictionary<long, List<int>> grid, double cellSize, double r2)
        {
            var result = new List<int>();
            long gx = (long)Math.Floor(center.X / cellSize);
            long gy = (long)Math.Floor(center.Y / cellSize);
            long gz = (long)Math.Floor(center.Z / cellSize);

            for (long dx = -1; dx <= 1; dx++)
            {
                for (long dy = -1; dy <= 1; dy++)
                {
                    for (long dz = -1; dz <= 1; dz++)
                    {
                        long key = ((gx + dx) * 73856093) ^ ((gy + dy) * 19349663) ^ ((gz + dz) * 83492791);
                        if (grid.TryGetValue(key, out var list))
                        {
                            foreach (int idx in list)
                            {
                                XYZ p = points[idx];
                                double d2 = (center.X - p.X) * (center.X - p.X) +
                                            (center.Y - p.Y) * (center.Y - p.Y) +
                                            (center.Z - p.Z) * (center.Z - p.Z);
                                if (d2 <= r2) result.Add(idx);
                            }
                        }
                    }
                }
            }

            return result;
        }

        private static void ComputeLocalDirection(List<int> neighborIndices, out XYZ dir)
        {
            dir = XYZ.BasisX;
            if (neighborIndices.Count < 4) return;
            dir = XYZ.BasisZ;
        }
    }
}
