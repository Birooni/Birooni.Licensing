using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace Biruscan.AI.Vision
{
    /// <summary>
    /// Industrial-Grade Pipe Extraction Engine.
    /// 1. Sequential Line RANSAC: Perfectly isolates straight segments regardless of orientation (X, Y, or Z).
    /// 2. 2D Cross-Section Projection: Flattens the isolated segment into a pristine 2D arc.
    /// 3. Geometric 3-Point RANSAC: Finds the mathematical true center and radius of the arc, eliminating partial-scan offsets.
    /// </summary>
    public static class RobustRansacCylinderFitter
    {
        public class CylinderModel
        {
            public XYZ Center;
            public XYZ Axis;
            public double Radius;
            public double Length;
            public List<XYZ> Inliers = new List<XYZ>();
            public double InlierRatio;
        }

        public static List<CylinderModel> ExtractMultipleCylinders(
            IList<XYZ> points, int maxCylinders = 5, double inlierThresholdFt = 0.06)
        {
            if (points == null || points.Count < 20)
                return new List<CylinderModel>();

            var workingPoints = new List<XYZ>(points);
            var segments = SplitIntoStraightSegments(workingPoints);
            var results = new List<CylinderModel>();

            foreach (var seg in segments)
            {
                if (seg.Count < 20 || results.Count >= maxCylinders) continue;

                var cyl = FitCylinderFromSegment(seg, inlierThresholdFt);
                if (cyl != null && cyl.Length > 0.15 && cyl.Radius > 0.01 && cyl.Radius < 0.65)
                {
                    results.Add(cyl);
                }
            }

            return results;
        }

        // ─────────────────────────────────────────────────────────────
        //  1. SEQUENTIAL LINE RANSAC
        //  Isolates straight legs of a pipe network perfectly.
        // ─────────────────────────────────────────────────────────────
        private static List<List<XYZ>> SplitIntoStraightSegments(List<XYZ> points)
        {
            var segments = new List<List<XYZ>>();
            var remaining = new List<XYZ>(points);
            var rand = new Random(42);

            for (int i = 0; i < 5; i++)
            {
                if (remaining.Count < 20) break;

                int bestInliersCount = 0;
                List<XYZ> bestInliers = null;

                for (int iter = 0; iter < 100; iter++)
                {
                    XYZ seed = remaining[rand.Next(remaining.Count)];
                    
                    var local = new List<XYZ>();
                    foreach(var p in remaining) { if (p.DistanceTo(seed) < 1.0) local.Add(p); }
                    if (local.Count < 10) continue;

                    XYZ dir = ComputePCAAxis(local);

                    var inliers = new List<XYZ>();
                    foreach(var p in remaining)
                    {
                        if (DistanceToLine(p, seed, dir) < 0.65) // 200mm max radius search cylinder
                        {
                            inliers.Add(p);
                        }
                    }

                    if (inliers.Count > bestInliersCount)
                    {
                        bestInliersCount = inliers.Count;
                        bestInliers = inliers;
                    }
                }

                if (bestInliers != null && bestInliers.Count > 20)
                {
                    segments.Add(bestInliers);
                    var inlierSet = new HashSet<XYZ>(bestInliers);
                    remaining = remaining.Where(p => !inlierSet.Contains(p)).ToList();
                }
                else
                {
                    break;
                }
            }

            return segments;
        }

        // ─────────────────────────────────────────────────────────────
        //  2. 2D PROJECTION & 3-POINT RANSAC CIRCLE FIT
        // ─────────────────────────────────────────────────────────────
        private static CylinderModel FitCylinderFromSegment(List<XYZ> segment, double tolerance)
        {
            XYZ axis = ComputePCAAxis(segment);
            axis = SnapToCardinal(axis, 8.0); // Snap to architectural axes if close

            // 3D Centroid
            double sx = 0, sy = 0, sz = 0;
            foreach (var p in segment) { sx += p.X; sy += p.Y; sz += p.Z; }
            int n = segment.Count;
            XYZ centroid = new XYZ(sx / n, sy / n, sz / n);

            // 2D Frame
            XYZ u1, u2;
            ComputePerpendicularFrame(axis, out u1, out u2);

            // Project to 2D
            var proj2D = new List<(double u, double v)>();
            double minT = double.MaxValue, maxT = double.MinValue;

            foreach (var p in segment)
            {
                XYZ d = p - centroid;
                proj2D.Add((d.DotProduct(u1), d.DotProduct(u2)));
                
                double t = d.DotProduct(axis);
                if (t < minT) minT = t;
                if (t > maxT) maxT = t;
            }

            // 3-Point Geometric RANSAC for true center
            if (!Fit2DCircleRansac(proj2D, out double cu, out double cv, out double radius)) 
                return null;

            // Map 2D true center back to 3D
            XYZ pipeCenter3D = centroid + u1.Multiply(cu) + u2.Multiply(cv);
            
            // Shift to axial midpoint
            double centerT = (minT + maxT) / 2.0;
            XYZ finalCenter = pipeCenter3D + axis.Multiply(centerT);

            // Count true inliers geometrically
            int inlierCount = 0;
            foreach (var p in segment)
            {
                double dist = DistanceToLine(p, pipeCenter3D, axis);
                if (Math.Abs(dist - radius) < tolerance)
                    inlierCount++;
            }

            return new CylinderModel
            {
                Center = finalCenter,
                Axis = axis,
                Radius = radius,
                Length = maxT - minT,
                Inliers = segment,
                InlierRatio = (double)inlierCount / segment.Count
            };
        }

        private static bool Fit2DCircleRansac(List<(double u, double v)> pts, out double bestCx, out double bestCy, out double bestRadius)
        {
            bestCx = 0; bestCy = 0; bestRadius = 0;
            if (pts.Count < 3) return false;

            int maxInliers = 0;
            var rand = new Random(42);
            int iterations = Math.Min(300, pts.Count * 2);

            for (int i = 0; i < iterations; i++)
            {
                var p1 = pts[rand.Next(pts.Count)];
                var p2 = pts[rand.Next(pts.Count)];
                var p3 = pts[rand.Next(pts.Count)];

                if (!GetCircleFrom3Points(p1, p2, p3, out double cx, out double cy, out double r)) continue;

                if (r < 0.02 || r > 0.65) continue;

                int inliers = 0;
                double circleTol = 0.04; 
                foreach (var p in pts)
                {
                    double dist = Math.Sqrt((p.u - cx)*(p.u - cx) + (p.v - cy)*(p.v - cy));
                    if (Math.Abs(dist - r) < circleTol) inliers++;
                }

                if (inliers > maxInliers)
                {
                    maxInliers = inliers;
                    bestCx = cx;
                    bestCy = cy;
                    bestRadius = r;
                }
            }

            return maxInliers > pts.Count * 0.10;
        }

        private static bool GetCircleFrom3Points((double u, double v) p1, (double u, double v) p2, (double u, double v) p3, out double cx, out double cy, out double r)
        {
            cx = 0; cy = 0; r = 0;
            double temp1 = p2.u * p2.u + p2.v * p2.v;
            double bc = (p1.u * p1.u + p1.v * p1.v - temp1) / 2.0;
            double cd = (temp1 - p3.u * p3.u - p3.v * p3.v) / 2.0;
            double det = (p1.u - p2.u) * (p2.v - p3.v) - (p2.u - p3.u) * (p1.v - p2.v);

            if (Math.Abs(det) < 1e-6) return false; 

            cx = (bc * (p2.v - p3.v) - cd * (p1.v - p2.v)) / det;
            cy = ((p1.u - p2.u) * cd - (p2.u - p3.u) * bc) / det;
            r = Math.Sqrt((cx - p1.u) * (cx - p1.u) + (cy - p1.v) * (cy - p1.v));
            return true;
        }

        // ─────────────────────────────────────────────────────────────
        //  UTILITIES
        // ─────────────────────────────────────────────────────────────
        private static double DistanceToLine(XYZ pt, XYZ lineStart, XYZ lineDir)
        {
            XYZ w = pt - lineStart;
            double t = w.DotProduct(lineDir);
            XYZ proj = lineStart + lineDir.Multiply(t);
            return pt.DistanceTo(proj);
        }

        private static void ComputePerpendicularFrame(XYZ axis, out XYZ u1, out XYZ u2)
        {
            u1 = axis.CrossProduct(XYZ.BasisZ);
            if (u1.GetLength() < 0.1)
                u1 = axis.CrossProduct(XYZ.BasisX);
            u1 = u1.Normalize();
            u2 = axis.CrossProduct(u1).Normalize();
        }

        private static XYZ SnapToCardinal(XYZ dir, double thresholdDegrees)
        {
            double threshold = Math.Cos(thresholdDegrees * Math.PI / 180.0);
            XYZ[] cardinals = { XYZ.BasisX, XYZ.BasisY, XYZ.BasisZ, -XYZ.BasisX, -XYZ.BasisY, -XYZ.BasisZ };
            
            XYZ best = null;
            double bestDot = 0;
            foreach (var c in cardinals)
            {
                double dot = Math.Abs(dir.DotProduct(c));
                if (dot > bestDot) { bestDot = dot; best = c; }
            }

            if (bestDot > threshold && best != null)
                return dir.DotProduct(best) > 0 ? best : best.Negate();

            return dir;
        }

        private static XYZ ComputePCAAxis(List<XYZ> points)
        {
            if (points.Count < 3) return XYZ.BasisZ;

            double sumX = 0, sumY = 0, sumZ = 0;
            foreach (var p in points) { sumX += p.X; sumY += p.Y; sumZ += p.Z; }
            int n = points.Count;
            XYZ centroid = new XYZ(sumX / n, sumY / n, sumZ / n);

            double xx = 0, yy = 0, zz = 0, xy = 0, xz = 0, yz = 0;
            foreach (var p in points)
            {
                double dx = p.X - centroid.X; double dy = p.Y - centroid.Y; double dz = p.Z - centroid.Z;
                xx += dx * dx; yy += dy * dy; zz += dz * dz;
                xy += dx * dy; xz += dx * dz; yz += dy * dz;
            }

            XYZ vector = new XYZ(1, 1, 1).Normalize();
            for (int i = 0; i < 25; i++)
            {
                double nx = xx * vector.X + xy * vector.Y + xz * vector.Z;
                double ny = xy * vector.X + yy * vector.Y + yz * vector.Z;
                double nz = xz * vector.X + yz * vector.Y + zz * vector.Z;
                vector = new XYZ(nx, ny, nz);
                if (vector.GetLength() > 1e-6) vector = vector.Normalize();
            }
            return vector;
        }
    }
}
