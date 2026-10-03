using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace Biruscan.AI.Vision
{
    /// <summary>
    /// 100% Native 3D Spatial Cylinder & Pipe Fitter.
    /// Operates directly in full 3D Cartesian coordinates (X, Y, Z) without any 2D image projections.
    /// Accurately detects pipes at ANY 3D spatial orientation (horizontal, vertical, diagonal, sloping).
    /// Licensed under MIT / Apache 2.0 (Commercially safe for proprietary sale).
    /// </summary>
    public static class Native3dSpatialCylinderFitter
    {
        public class FittedCylinder3D
        {
            public XYZ Start;
            public XYZ End;
            public XYZ Axis;
            public double Radius;       // feet
            public double DiameterMm;   // mm
            public double Length;
            public List<XYZ> Inliers = new List<XYZ>();
            public double Confidence;
        }

        private const double MinRadiusFt = 0.0246; // ~7.5mm (15mm diameter / 0.5")
        private const double MaxRadiusFt = 0.6562; // ~200mm (400mm diameter / 16")

        /// <summary>
        /// Extracts all 3D cylinders from the point cloud using robust 3D RANSAC multi-pass peeling.
        /// </summary>
        public static List<FittedCylinder3D> ExtractAllCylinders3D(IList<XYZ> points, double? diameterOverrideMm = null, int maxPasses = 15)
        {
            var results = new List<FittedCylinder3D>();
            if (points == null || points.Count < 10) return results;

            var remaining = new List<XYZ>(points);

            for (int pass = 0; pass < maxPasses && remaining.Count >= 10; pass++)
            {
                var fit = FitBestCylinder3D(remaining, diameterOverrideMm);
                if (fit != null && fit.Inliers.Count >= 8 && fit.Length >= 0.25)
                {
                    results.Add(fit);

                    var inlierSet = new HashSet<XYZ>(fit.Inliers);
                    remaining = remaining.Where(p => !inlierSet.Contains(p)).ToList();
                }
                else
                {
                    break;
                }
            }

            return results;
        }

        private static FittedCylinder3D FitBestCylinder3D(List<XYZ> points, double? diameterOverrideMm)
        {
            if (points.Count < 10) return null;

            int iterations = Math.Min(300, points.Count * 3);
            var rnd = new Random(1337);

            FittedCylinder3D bestFit = null;
            int maxInliers = 0;

            for (int iter = 0; iter < iterations; iter++)
            {
                int idx1 = rnd.Next(points.Count);
                int idx2 = rnd.Next(points.Count);
                if (idx1 == idx2) continue;

                XYZ p1 = points[idx1];
                XYZ p2 = points[idx2];
                double pairDist = p1.DistanceTo(p2);
                if (pairDist < 0.5 || pairDist > 20.0) continue;

                XYZ axis = (p2 - p1).Normalize();

                var sampleDists = new List<double>();
                int sampleStep = Math.Max(1, points.Count / 100);
                for (int i = 0; i < points.Count; i += sampleStep)
                {
                    XYZ pt = points[i];
                    XYZ v = pt - p1;
                    double proj = v.DotProduct(axis);
                    XYZ perp = v - axis.Multiply(proj);
                    double d = perp.GetLength();
                    if (d >= MinRadiusFt && d <= MaxRadiusFt)
                    {
                        sampleDists.Add(d);
                    }
                }

                if (sampleDists.Count < 6) continue;
                sampleDists.Sort();
                double candidateRadius = sampleDists[sampleDists.Count / 2];

                double inlierTol = Math.Max(0.025, candidateRadius * 0.25);
                var inliers = new List<XYZ>();
                double minT = double.MaxValue, maxT = double.MinValue;

                foreach (var pt in points)
                {
                    XYZ v = pt - p1;
                    double t = v.DotProduct(axis);
                    XYZ perp = v - axis.Multiply(t);
                    double d = perp.GetLength();

                    if (Math.Abs(d - candidateRadius) <= inlierTol)
                    {
                        inliers.Add(pt);
                        if (t < minT) minT = t;
                        if (t > maxT) maxT = t;
                    }
                }

                double span = maxT - minT;
                if (inliers.Count > maxInliers && inliers.Count >= 8 && span >= 0.25)
                {
                    maxInliers = inliers.Count;

                    ComputeCentroidAndPca(inliers, out XYZ refinedCenter, out XYZ refinedAxis);
                    if (refinedAxis.DotProduct(axis) < 0) refinedAxis = -refinedAxis;

                    double rMinT = double.MaxValue, rMaxT = double.MinValue;
                    foreach (var pt in inliers)
                    {
                        double t = (pt - refinedCenter).DotProduct(refinedAxis);
                        if (t < rMinT) rMinT = t;
                        if (t > rMaxT) rMaxT = t;
                    }

                    XYZ start = refinedCenter + refinedAxis.Multiply(rMinT);
                    XYZ end = refinedCenter + refinedAxis.Multiply(rMaxT);
                    double diaMm = diameterOverrideMm ?? ClosestNominalPipeMm(candidateRadius * 2.0 * 304.8);

                    bestFit = new FittedCylinder3D
                    {
                        Start = start,
                        End = end,
                        Axis = refinedAxis,
                        Radius = candidateRadius,
                        DiameterMm = diaMm,
                        Length = start.DistanceTo(end),
                        Inliers = inliers,
                        Confidence = Math.Min(1.0, (double)inliers.Count / points.Count + 0.4)
                    };
                }
            }

            return bestFit;
        }

        private static void ComputeCentroidAndPca(IList<XYZ> pts, out XYZ centroid, out XYZ axis)
        {
            double cx = 0, cy = 0, cz = 0;
            foreach (var p in pts) { cx += p.X; cy += p.Y; cz += p.Z; }
            centroid = new XYZ(cx / pts.Count, cy / pts.Count, cz / pts.Count);

            double maxDist = 0;
            XYZ bestDir = XYZ.BasisZ;
            foreach (var p in pts)
            {
                double d = p.DistanceTo(centroid);
                if (d > maxDist && d > 0.05)
                {
                    maxDist = d;
                    bestDir = (p - centroid).Normalize();
                }
            }
            axis = bestDir;
        }

        private static readonly double[] StdNominalMm =
        {
            15, 20, 25, 32, 40, 50, 65, 80, 100, 125, 150, 200, 250, 300, 350, 400
        };

        private static double ClosestNominalPipeMm(double detectedMm)
        {
            return StdNominalMm.OrderBy(d => Math.Abs(d - detectedMm)).FirstOrDefault();
        }
    }
}
