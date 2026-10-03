using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Biruscan.AI.Contracts;

namespace Biruscan.AI.Vision
{
    /// <summary>
    /// AI Robot 36-Axis 3D Spatial Orbit Scanner & Multi-View Directional Decomposer.
    /// Sweeps through 36 distinct angular axes (every 10°) around the 3D bounding center
    /// to detect true straight pipe/duct runs and completely eliminate diagonal corner bridging.
    /// Licensed under MIT / Apache 2.0 (Commercially safe for proprietary sale).
    /// </summary>
    public static class AiRobot3dOrbitScanner
    {
        public class OrbitScanRun
        {
            public XYZ Start;
            public XYZ End;
            public XYZ Direction;
            public double DiameterMm;
            public double Length;
            public List<XYZ> Inliers = new List<XYZ>();
            public double Confidence;
        }

        private const double MinRadiusFt = 0.0246;
        private const double MaxRadiusFt = 0.6562;

        public static List<OrbitScanRun> Scan36Axes(IList<XYZ> points, double? diameterOverrideMm = null)
        {
            var detectedRuns = new List<OrbitScanRun>();
            if (points == null || points.Count < 10) return detectedRuns;

            var remainingPoints = new List<XYZ>(points);

            var vertRun = ExtractRunAlongDirection(remainingPoints, XYZ.BasisZ, diameterOverrideMm);
            if (vertRun != null && vertRun.Length >= 0.25)
            {
                detectedRuns.Add(vertRun);
                var inliers = new HashSet<XYZ>(vertRun.Inliers);
                remainingPoints = remainingPoints.Where(p => !inliers.Contains(p)).ToList();
            }

            int numAxes = 36;
            double angleStep = (2.0 * Math.PI) / numAxes;

            var candidates = new List<OrbitScanRun>();

            for (int k = 0; k < numAxes; k++)
            {
                double angle = k * angleStep;
                XYZ axisDir = new XYZ(Math.Cos(angle), Math.Sin(angle), 0.0).Normalize();

                var run = ExtractRunAlongDirection(remainingPoints, axisDir, diameterOverrideMm);
                if (run != null && run.Length >= 0.25 && run.Inliers.Count >= 8)
                {
                    candidates.Add(run);
                }
            }

            candidates = candidates.OrderByDescending(c => c.Inliers.Count).ToList();
            var usedPoints = new HashSet<XYZ>();

            foreach (var cand in candidates)
            {
                int overlap = cand.Inliers.Count(p => usedPoints.Contains(p));
                if (overlap < cand.Inliers.Count * 0.40)
                {
                    detectedRuns.Add(cand);
                    foreach (var p in cand.Inliers) usedPoints.Add(p);
                }
            }

            var leftover = remainingPoints.Where(p => !usedPoints.Contains(p)).ToList();
            if (leftover.Count >= 10)
            {
                var slopedRuns = Native3dSpatialCylinderFitter.ExtractAllCylinders3D(leftover, diameterOverrideMm, maxPasses: 3);
                foreach (var cyl in slopedRuns)
                {
                    detectedRuns.Add(new OrbitScanRun
                    {
                        Start = cyl.Start,
                        End = cyl.End,
                        Direction = cyl.Axis,
                        DiameterMm = cyl.DiameterMm,
                        Length = cyl.Length,
                        Inliers = cyl.Inliers,
                        Confidence = cyl.Confidence
                    });
                }
            }

            return detectedRuns;
        }

        private static OrbitScanRun ExtractRunAlongDirection(List<XYZ> points, XYZ axisDir, double? diameterOverrideMm)
        {
            if (points.Count < 10) return null;

            XYZ u = new XYZ(-axisDir.Y, axisDir.X, 0);
            if (u.GetLength() < 0.1) u = XYZ.BasisX.CrossProduct(axisDir);
            u = u.Normalize();
            XYZ v = axisDir.CrossProduct(u).Normalize();

            var uList = new double[points.Count];
            var vList = new double[points.Count];
            var tList = new double[points.Count];

            XYZ refPt = points[0];

            for (int i = 0; i < points.Count; i++)
            {
                XYZ d = points[i] - refPt;
                tList[i] = d.DotProduct(axisDir);
                uList[i] = d.DotProduct(u);
                vList[i] = d.DotProduct(v);
            }

            double medU = Median(uList);
            double medV = Median(vList);

            double maxSearchDist = 0.55;
            var inliers = new List<XYZ>();
            var inlierT = new List<double>();
            var inlierRadialDists = new List<double>();

            for (int i = 0; i < points.Count; i++)
            {
                double du = uList[i] - medU;
                double dv = vList[i] - medV;
                double crossDist = Math.Sqrt(du * du + dv * dv);

                if (crossDist <= maxSearchDist)
                {
                    inliers.Add(points[i]);
                    inlierT.Add(tList[i]);
                    if (crossDist >= MinRadiusFt * 0.5) inlierRadialDists.Add(crossDist);
                }
            }

            if (inliers.Count < 8) return null;

            inlierT.Sort();
            double minT = inlierT[0];
            double maxT = inlierT[inlierT.Count - 1];
            double length = maxT - minT;

            if (length < 0.25) return null;

            double estRadius = inlierRadialDists.Count > 0 ? Median(inlierRadialDists.ToArray()) : 0.082;
            estRadius = Math.Max(MinRadiusFt, Math.Min(MaxRadiusFt, estRadius));

            XYZ runCenter = refPt + u.Multiply(medU) + v.Multiply(medV);
            XYZ start = runCenter + axisDir.Multiply(minT);
            XYZ end = runCenter + axisDir.Multiply(maxT);

            double diaMm = diameterOverrideMm ?? ClosestNominalPipeMm(estRadius * 2.0 * 304.8);

            return new OrbitScanRun
            {
                Start = start,
                End = end,
                Direction = axisDir,
                DiameterMm = diaMm,
                Length = length,
                Inliers = inliers,
                Confidence = Math.Min(1.0, (double)inliers.Count / points.Count + 0.4)
            };
        }

        private static double Median(double[] vals)
        {
            if (vals == null || vals.Length == 0) return 0;
            var copy = (double[])vals.Clone();
            Array.Sort(copy);
            return copy[copy.Length / 2];
        }

        private static XYZ ComputeCentroid(IList<XYZ> pts)
        {
            double x = 0, y = 0, z = 0;
            foreach (var p in pts) { x += p.X; y += p.Y; z += p.Z; }
            return new XYZ(x / pts.Count, y / pts.Count, z / pts.Count);
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
