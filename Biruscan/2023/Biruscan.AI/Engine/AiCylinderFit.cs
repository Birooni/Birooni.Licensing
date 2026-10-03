using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;

namespace Biruscan.AI.Engine
{
    /// <summary>
    /// Pipe (cylinder) detection from a point set.
    ///
    /// Two complementary pipelines:
    ///   A) Normal-based RANSAC — best for full cross-sections (complete pipe arcs).
    ///   B) PCA strip + 2D circle RANSAC — best for partial cross-sections (half /
    ///      quarter pipe visible) and edge-wise longitudinal scans.
    /// </summary>
    public static class AiCylinderFit
    {
        private static readonly Random _rng = new Random(20260601);
        private const double MaxRadius = 1.5;   // ft (36" diameter)
        private const int MaxFitPoints = 900;

        public struct Result
        {
            public bool Success;
            public XYZ Start;
            public XYZ End;
            public XYZ Axis;
            public double Radius;
            public double Rms;
            public double Flatness;
            public int PointCount;
            public string Message;
            public string Method;
            public double ArcSpanDeg;
        }

        public static Result Fit(IList<XYZ> input) => Fit(input, relaxed: false);

        /// <param name="relaxed">Lower thresholds for sparse clouds and partial (half/quarter) cross-sections.</param>
        public static Result Fit(IList<XYZ> input, bool relaxed)
        {
            var fail = new Result { Success = false, PointCount = input == null ? 0 : input.Count };
            int minPts = relaxed ? 8 : 12;
            if (input == null || input.Count < minPts)
            {
                fail.Message = relaxed
                    ? "Not enough points captured (need at least 6)."
                    : "Not enough points captured (need at least 8). Draw the box over a clear length of pipe.";
                return fail;
            }

            List<XYZ> pts = Downsample(input, MaxFitPoints);

            // PCA strip is enough for typical clips. Skip O(n²) normals + 2500-iter RANSAC unless strip fails.
            Result strip = FitPcaStrip(pts, relaxed, null);
            if (strip.Success)
            {
                RefineToPointCloud(ref strip, pts);
                return strip;
            }

            XYZ[] nor = pts.Count >= 10 ? EstimateNormals(pts, 8) : null;
            Result ransac = pts.Count >= (relaxed ? 14 : 18) && nor != null
                ? FitRansac(pts, relaxed, nor)
                : new Result { Success = false };

            if (ransac.Success)
            {
                RefineToPointCloud(ref ransac, pts);
                return ransac;
            }

            return new Result
            {
                Success = false,
                PointCount = pts.Count,
                Message = "No pipe cylinder found. For partial (half/quarter) scans, draw the box along the " +
                          "visible pipe length in a 3D view. For full-round pipes, show more of the curved face.",
            };
        }

        // ===================================================================
        // Pipeline B — PCA elongation axis + 2D partial-arc circle fit
        // ===================================================================

                private static Result FitPcaStrip(List<XYZ> pts, bool relaxed, XYZ[] nor)
        {
            var r = new Result { Success = false, PointCount = pts.Count, Method = "pca-strip" };
            int n = pts.Count;
            if (n < (relaxed ? 8 : 12)) return r;

            XYZ c = Centroid(pts);
            Covariance(pts, c, out double[] ev, out XYZ[] evec);

            int axisIdx = 0;
            if (ev[1] > ev[axisIdx]) axisIdx = 1;
            if (ev[2] > ev[axisIdx]) axisIdx = 2;
            XYZ axis = Norm(evec[axisIdx]);

            axis = RefineAxisGrid(pts, c, axis, 6.0, 0.8);

            int minInliers = MinInliers(n, relaxed);
            bool requireNorm = nor != null;
            if (!FitCrossSection(pts, c, axis, requireNormals: requireNorm, relaxed, nor, out XYZ center, out double radius,
                    out double rms, out int inliers, out double arcDeg))
            {
                r.Message = "PCA strip fit failed cross-section.";
                return r;
            }

            if (radius < 0.015 || radius > 0.85 || inliers < minInliers || arcDeg < 40.0)
            {
                r.Message = "PCA strip fit: radius or inlier count out of range.";
                return r;
            }

            if (!BuildEndpoints(pts, axis, center, radius, requireNormals: requireNorm, relaxed, nor,
                    out XYZ start, out XYZ end, out rms, out inliers))
            {
                r.Message = "PCA strip fit: could not determine pipe length.";
                return r;
        }

            r.Success = true;
            r.Start = start;
            r.End = end;
            r.Axis = axis;
            r.Radius = radius;
            r.Rms = rms;
                        r.PointCount = inliers;
            r.ArcSpanDeg = arcDeg;
            r.Flatness = ArcFlatness(arcDeg);
            r.Message = "ok";
            return r;
        }

        // ===================================================================
        // Pipeline A — normal-based RANSAC (full cross-sections)
        // ===================================================================

        private static Result FitRansac(List<XYZ> pts, bool relaxed, XYZ[] nor)
        {
            var r = new Result { Success = false, PointCount = pts.Count, Method = "ransac" };
            int n = pts.Count;
            if (n < (relaxed ? 14 : 18) || nor == null) return r;

            int bestScore = -1;
            XYZ bA = XYZ.BasisZ, bC = XYZ.Zero;
            double bR = 0;

            for (int it = 0; it < 400; it++)
            {
                int i = _rng.Next(n), j = _rng.Next(n);
                if (i == j) continue;

                XYZ a = nor[i].CrossProduct(nor[j]);
                double al = a.GetLength();
                if (al < 0.10) continue;
                a = a.Multiply(1.0 / al);
                Basis(a, out XYZ e1, out XYZ e2);

                double q1u = pts[i].DotProduct(e1), q1v = pts[i].DotProduct(e2);
                double q2u = pts[j].DotProduct(e1), q2v = pts[j].DotProduct(e2);
                double m1u = nor[i].DotProduct(e1), m1v = nor[i].DotProduct(e2);
                double m2u = nor[j].DotProduct(e1), m2v = nor[j].DotProduct(e2);
                double m1l = Math.Sqrt(m1u * m1u + m1v * m1v); if (m1l < 1e-6) continue; m1u /= m1l; m1v /= m1l;
                double m2l = Math.Sqrt(m2u * m2u + m2v * m2v); if (m2l < 1e-6) continue; m2u /= m2l; m2v /= m2l;

                double det = -m1u * m2v + m2u * m1v;
                if (Math.Abs(det) < 1e-6) continue;
                double bx = q2u - q1u, by = q2v - q1v;
                double t = (-bx * m2v + m2u * by) / det;
                double cu = q1u + t * m1u, cv = q1v + t * m1v;
                double rr = Math.Sqrt((cu - q1u) * (cu - q1u) + (cv - q1v) * (cv - q1v));
                if (rr < 0.01 || rr > MaxRadius) continue;
                double rr2 = Math.Sqrt((cu - q2u) * (cu - q2u) + (cv - q2v) * (cv - q2v));
                if (Math.Abs(rr2 - rr) > 0.25 * rr) continue;

                XYZ center = e1.Multiply(cu) + e2.Multiply(cv);
                int score = Score(pts, nor, a, center, rr, strictNormals: true);
                if (score > bestScore) { bestScore = score; bA = a; bC = center; bR = rr; }
            }

            if (bestScore <= 0) return r;

            XYZ mA = bA, mC = bC;
            double mR = bR;
            int mScore = bestScore;

            for (int pass = 0; pass < 3; pass++)
            {
                var inl = Inliers(pts, nor, mA, mC, mR, strictNormals: true);
                if (inl.Count < (relaxed ? 6 : 8)) break;

                mA = RefineAxisGrid(inl, Centroid(inl), mA, 5.0, 0.2);

                if (!FitCrossSection(inl, Centroid(inl), mA, requireNormals: true, relaxed, nor, out XYZ nC, out double nR,
                        out _, out int _, out _))
                {
                    break;
                }

                mC = nC;
                mR = nR;
                int nScore = Score(pts, nor, mA, mC, mR, strictNormals: true);
                if (nScore >= mScore) mScore = nScore;
                else break;
            }

            if (!BuildEndpoints(pts, mA, mC, mR, requireNormals: true, relaxed, nor,
                    out XYZ start, out XYZ end, out double rms, out int inlierCount))
            {
                r.Message = "RANSAC fit was not stable.";
                return r;
            }

            double arcDeg = ComputeArcSpanDegrees(pts, mA, mC, mR, requireNormals: true, nor);

            r.Success = true;
            r.Start = start;
            r.End = end;
            r.Axis = mA;
            r.Radius = mR;
            r.Rms = rms;
            r.PointCount = inlierCount;
            r.ArcSpanDeg = arcDeg;
            r.Flatness = ArcFlatness(arcDeg);
            r.Message = "ok";
            return r;
        }

        // ===================================================================
        // Shared cross-section + endpoint builders
        // ===================================================================

        private static bool FitCrossSection(List<XYZ> pts, XYZ refPoint, XYZ axis, bool requireNormals, bool relaxed,
            out XYZ center, out double radius, out double rms, out int inlierCount, out double arcSpanDeg)
        {
            return FitCrossSection(pts, refPoint, axis, requireNormals, relaxed, null,
                out center, out radius, out rms, out inlierCount, out arcSpanDeg);
        }

                private static bool FitCrossSection(List<XYZ> pts, XYZ refPoint, XYZ axis, bool requireNormals, bool relaxed,
            XYZ[] nor, out XYZ center, out double radius, out double rms, out int inlierCount, out double arcSpanDeg)
        {
            center = refPoint;
            radius = 0;
            rms = double.MaxValue;
            inlierCount = 0;
            arcSpanDeg = 0;

            Basis(axis, out XYZ e1, out XYZ e2);
            int n = pts.Count;
            var U = new double[n];
            var W = new double[n];
            double meanU = 0, meanW = 0;

            for (int i = 0; i < n; i++)
            {
                XYZ d = pts[i] - refPoint;
                U[i] = d.DotProduct(e1);
                W[i] = d.DotProduct(e2);
                meanU += U[i]; meanW += W[i];
            }
            meanU /= n; meanW /= n;

            

            if (!Circle2dRansac(U, W, relaxed, out double cu, out double cw, out double r, out _))
                return false;

            RefineCircleGeometric(U, W, ref cu, ref cw, ref r);

            center = refPoint + e1.Multiply(cu) + e2.Multiply(cw);
            radius = r;

            double tol = Tol(r, relaxed);
            double minNormalDot = relaxed ? 0.12 : 0.25;
            double sumSq = 0;
            int cnt = 0;
            for (int i = 0; i < n; i++)
            {
                XYZ d = pts[i] - center;
                XYZ dp = d - axis.Multiply(d.DotProduct(axis));
                double dl = dp.GetLength();
                if (dl < 1e-6) continue;
                if (Math.Abs(dl - r) >= tol) continue;
                if (requireNormals && nor != null &&
                    Math.Abs(nor[i].DotProduct(dp.Multiply(1.0 / dl))) <= minNormalDot)
                    continue;
                double err = dl - r;
                sumSq += err * err;
                cnt++;
            }

            if (cnt < MinInliers(n, relaxed) || r < 0.015 || r > 0.85) return false;

            rms = Math.Sqrt(sumSq / cnt);
            inlierCount = cnt;
            arcSpanDeg = ComputeArcSpanDegrees(pts, axis, center, r, requireNormals, nor);
            return true;
        }

        private static bool BuildEndpoints(List<XYZ> pts, XYZ axis, XYZ center, double radius,
            bool requireNormals, bool relaxed, out XYZ start, out XYZ end, out double rms, out int inlierCount)
        {
            return BuildEndpoints(pts, axis, center, radius, requireNormals, relaxed, null,
                out start, out end, out rms, out inlierCount);
        }

        private static bool BuildEndpoints(List<XYZ> pts, XYZ axis, XYZ center, double radius,
            bool requireNormals, bool relaxed, XYZ[] nor, out XYZ start, out XYZ end, out double rms, out int inlierCount)
        {
            start = end = XYZ.Zero;
            rms = 0;
            inlierCount = 0;

            double tol = Tol(radius, relaxed);
            double minNormalDot = relaxed ? 0.12 : 0.25;
            double minLen = relaxed ? 5e-4 : 1e-3;
            var tvals = new List<double>();
            double acc = 0;
            int cnt = 0;

            for (int i = 0; i < pts.Count; i++)
            {
                XYZ d = pts[i] - center;
                XYZ dp = d - axis.Multiply(d.DotProduct(axis));
                double dl = dp.GetLength();
                if (dl < 1e-6) continue;
                if (Math.Abs(dl - radius) >= tol) continue;
                if (requireNormals && nor != null &&
                    Math.Abs(nor[i].DotProduct(dp.Multiply(1.0 / dl))) <= minNormalDot)
                    continue;

                tvals.Add(d.DotProduct(axis));
                double err = dl - radius;
                acc += err * err;
                cnt++;
            }

            if (cnt < MinInliers(pts.Count, relaxed) || tvals.Count < 3) return false;

            tvals.Sort();
            int i0 = Math.Max(0, (int)(tvals.Count * 0.03));
            int i1 = Math.Min(tvals.Count - 1, (int)(tvals.Count * 0.97));
            double tmin = tvals[i0];
            double tmax = tvals[i1];
            if (tmax - tmin < minLen) return false;

            start = center + axis.Multiply(tmin);
            end = center + axis.Multiply(tmax);
            rms = Math.Sqrt(acc / cnt);
            inlierCount = cnt;
            return true;
        }

                private static double ComputeArcSpanDegrees(List<XYZ> pts, XYZ axis, XYZ center, double r,
            bool requireNormals, XYZ[] nor)
        {
            Basis(axis, out XYZ e1, out XYZ e2);
            double tol = Tol(r);
            var angles = new List<double>();
            bool[] bins = new bool[36];

            for (int i = 0; i < pts.Count; i++)
            {
                XYZ d = pts[i] - center;
                XYZ dp = d - axis.Multiply(d.DotProduct(axis));
                double dl = dp.GetLength();
                if (dl < 1e-6) continue;
                if (Math.Abs(dl - r) >= tol) continue;
                
                XYZ radialNorm = dp.Multiply(1.0 / dl);
                if (requireNormals && nor != null)
                {
                    if (Math.Abs(nor[i].DotProduct(radialNorm)) <= 0.25) continue;
                    
                    // Bin the normal (projected to cross-section) to verify surface curvature
                    XYZ n_proj = nor[i] - axis.Multiply(nor[i].DotProduct(axis));
                    double nl = n_proj.GetLength();
                    if (nl > 1e-6)
                    {
                        double ang = Math.Atan2(n_proj.DotProduct(e2), n_proj.DotProduct(e1));
                        int b = (int)((ang + Math.PI) / (2.0 * Math.PI) * 36);
                        if (b < 0) b = 0; else if (b > 35) b = 35;
                        bins[b] = true;
                    }
                }
                
                angles.Add(Math.Atan2(dp.DotProduct(e2), dp.DotProduct(e1)));
            }

            if (requireNormals && nor != null)
            {
                int occ = 0;
                foreach (bool b in bins) if (b) occ++;
                if (occ < 3) return 0; // Reject flat planes and 90-degree corners (which have 1 or 2 normal bins)
            }

            return ArcSpanFromAngles(angles);
        }

        private static double ArcSpanFromAngles(List<double> angles)
        {
            if (angles == null || angles.Count < 3) return 0;
            angles.Sort();
            double maxGap = 0;
            for (int i = 0; i < angles.Count; i++)
            {
                double next = angles[(i + 1) % angles.Count];
                double gap = (i + 1 < angles.Count)
                    ? next - angles[i]
                    : (next + 2.0 * Math.PI) - angles[i];
                if (gap > maxGap) maxGap = gap;
            }
            double span = 2.0 * Math.PI - maxGap;
            if (span < 0) span = 0;
            return span * 180.0 / Math.PI;
        }

        private static double ArcFlatness(double arcDeg)
            => Math.Min(1.0, arcDeg / 180.0);

        // ===================================================================
        // 2D circle fit (partial arcs)
        // ===================================================================

        private static bool Circle2dRansac(double[] U, double[] W, bool relaxed,
            out double cu, out double cw, out double r, out int bestInliers)
        {
            cu = cw = r = 0;
            bestInliers = 0;
            int n = U.Length;
            if (n < 3) return false;

            int minInliers = relaxed ? Math.Max(6, n / 12) : 6;
            int iterations = 2000;
            double bestErr = double.MaxValue;

            for (int it = 0; it < iterations; it++)
            {
                int i = _rng.Next(n), j = _rng.Next(n), k = _rng.Next(n);
                if (i == j || j == k || i == k) continue;
                if (!CircleFrom3Points(U[i], W[i], U[j], W[j], U[k], W[k], out double tcu, out double tcw, out double tr))
                    continue;
                if (tr < 0.01 || tr > MaxRadius) continue;

                double tol = Tol(tr, relaxed);
                int cnt = 0;
                double err = 0;
                for (int q = 0; q < n; q++)
                {
                    double du = U[q] - tcu, dv = W[q] - tcw;
                    double dist = Math.Sqrt(du * du + dv * dv);
                    double res = Math.Abs(dist - tr);
                    if (res < tol) { cnt++; err += res * res; }
                }

                if (cnt > bestInliers || (cnt == bestInliers && err < bestErr))
                {
                    bestInliers = cnt;
                    bestErr = err;
                    cu = tcu; cw = tcw; r = tr;
                }
            }

            return bestInliers >= minInliers;
        }

        private static bool CircleFrom3Points(double u1, double v1, double u2, double v2, double u3, double v3,
            out double cu, out double cw, out double r)
        {
            cu = cw = r = 0;
            double d = 2.0 * (u1 * (v2 - v3) + u2 * (v3 - v1) + u3 * (v1 - v2));
            if (Math.Abs(d) < 1e-12) return false;

            double s1 = u1 * u1 + v1 * v1;
            double s2 = u2 * u2 + v2 * v2;
            double s3 = u3 * u3 + v3 * v3;
            cu = (s1 * (v2 - v3) + s2 * (v3 - v1) + s3 * (v1 - v2)) / d;
            cw = (s1 * (u3 - u2) + s2 * (u1 - u3) + s3 * (u2 - u1)) / d;
            double dx = cu - u1, dy = cw - v1;
            r = Math.Sqrt(dx * dx + dy * dy);
            return r > 0.01;
        }

        private static void RefineCircleGeometric(double[] U, double[] W, ref double cu, ref double cw, ref double r)
        {
            int n = U.Length;
            for (int iter = 0; iter < 30; iter++)
            {
                double gCu = 0, gCw = 0, gR = 0;
                int cnt = 0;
                for (int i = 0; i < n; i++)
                {
                    double du = U[i] - cu, dv = W[i] - cw;
                    double dist = Math.Sqrt(du * du + dv * dv);
                    if (dist < 1e-6) continue;
                    double res = dist - r;
                    gCu += res * du / dist;
                    gCw += res * dv / dist;
                    gR += res;
                    cnt++;
                }
                if (cnt == 0) break;
                double lr = 0.35 / cnt;
                cu += lr * gCu;
                cw += lr * gCw;
                r += lr * gR;
                if (r < 0.01) r = 0.01;
            }
        }

        private static XYZ RefineAxisGrid(List<XYZ> pts, XYZ refPoint, XYZ axis, double coarseDeg, double fineDeg)
        {
            XYZ best = axis;
            double bestErr = EvaluateAxis(axis, pts, refPoint, out _, out _);
            Basis(axis, out XYZ t1, out XYZ t2);

            for (int u = (int)-coarseDeg; u <= coarseDeg; u++)
            {
                for (int v = (int)-coarseDeg; v <= coarseDeg; v++)
                {
                    if (u == 0 && v == 0) continue;
                    XYZ dir = Norm(axis + t1.Multiply(u * Math.PI / 180.0) + t2.Multiply(v * Math.PI / 180.0));
                    double err = EvaluateAxis(dir, pts, refPoint, out _, out _);
                    if (err < bestErr) { bestErr = err; best = dir; }
                }
            }

            Basis(best, out t1, out t2);
            for (double u = -fineDeg; u <= fineDeg; u += fineDeg / 5.0)
            {
                for (double v = -fineDeg; v <= fineDeg; v += fineDeg / 5.0)
                {
                    if (Math.Abs(u) < 0.01 && Math.Abs(v) < 0.01) continue;
                    XYZ dir = Norm(best + t1.Multiply(u * Math.PI / 180.0) + t2.Multiply(v * Math.PI / 180.0));
                    double err = EvaluateAxis(dir, pts, refPoint, out _, out _);
                    if (err < bestErr) { bestErr = err; best = dir; }
                }
            }

            return best;
        }

        // ===================================================================
        // RANSAC scoring / inliers
        // ===================================================================

        private static double EvaluateAxis(XYZ a, List<XYZ> pts, XYZ refPoint, out XYZ bestC, out double bestR)
        {
            Basis(a, out XYZ e1, out XYZ e2);
            int n = pts.Count;
            var U = new double[n];
            var W = new double[n];
            for (int q = 0; q < n; q++)
            {
                XYZ d = pts[q] - refPoint;
                U[q] = d.DotProduct(e1);
                W[q] = d.DotProduct(e2);
            }

            if (!KasaCircle(U, W, out double cu, out double cw, out double r) || r > MaxRadius || r < 1e-4)
            {
                bestC = refPoint;
                bestR = 0;
                return double.MaxValue;
            }

            RefineCircleGeometric(U, W, ref cu, ref cw, ref r);
            bestC = refPoint + e1.Multiply(cu) + e2.Multiply(cw);
            bestR = r;

            double sumSq = 0;
            for (int q = 0; q < n; q++)
            {
                XYZ d = pts[q] - bestC;
                XYZ dp = d - a.Multiply(d.DotProduct(a));
                double err = dp.GetLength() - r;
                sumSq += err * err;
            }
            return sumSq;
        }

        /// <summary>
        /// Snap axis origin and endpoints to the measured point-cloud shell.
        /// </summary>
        public static void RefineToPointCloud(ref Result fit, IList<XYZ> points)
        {
            if (!fit.Success || points == null || points.Count < 6) return;

            XYZ axis = Norm(fit.Axis);
            double radius = fit.Radius;
            double tol = Tol(radius, true) * 1.2;
            XYZ seed = fit.Start + axis.Multiply(fit.Start.DistanceTo(fit.End) * 0.5);

            var shellT = new List<double>();
            var perpOffsets = new List<XYZ>();
            double radAcc = 0;
            int radCnt = 0;

            foreach (XYZ p in points)
            {
                XYZ rel = p - seed;
                double t = rel.DotProduct(axis);
                XYZ onAxis = seed + axis.Multiply(t);
                XYZ radial = p - onAxis;
                double dist = radial.GetLength();
                if (Math.Abs(dist - radius) > tol) continue;

                shellT.Add(t);
                radAcc += dist;
                radCnt++;

                XYZ perp = rel - axis.Multiply(t);
                if (perp.GetLength() > 1e-6) perpOffsets.Add(perp);
            }

            if (shellT.Count < 6) return;

            shellT.Sort();
            int i0 = Math.Max(0, (int)(shellT.Count * 0.02));
            int i1 = Math.Min(shellT.Count - 1, (int)(shellT.Count * 0.98));

            XYZ origin = seed;
            if (perpOffsets.Count >= 4)
            {
                double ox = 0, oy = 0, oz = 0;
                foreach (XYZ v in perpOffsets) { ox += v.X; oy += v.Y; oz += v.Z; }
                XYZ perpMean = new XYZ(ox / perpOffsets.Count, oy / perpOffsets.Count, oz / perpOffsets.Count);
                if (perpMean.GetLength() < radius * 0.30)
                    origin = seed + perpMean;
            }

            if (radCnt > 0)
                fit.Radius = radAcc / radCnt;

            fit.Axis = axis;
            fit.Start = origin + axis.Multiply(shellT[i0]);
            fit.End = origin + axis.Multiply(shellT[i1]);
        }

        private static int MinInliers(int pointCount, bool relaxed)
            => relaxed ? Math.Max(6, pointCount / 12) : 8;

        private static double Tol(double rad, bool relaxed = false)
            => relaxed
                ? Math.Min(Math.Max(0.06, 0.18 * rad), 0.26)
                : Math.Min(Math.Max(0.05, 0.15 * rad), 0.20);

                private static int Score(List<XYZ> pts, XYZ[] nor, XYZ a, XYZ center, double r, bool strictNormals)
        {
            Basis(a, out XYZ e1, out XYZ e2);
            double tol = Tol(r);
            double minDot = strictNormals ? 0.35 : 0.15;
            int cnt = 0;
            var bins = new bool[36];

            for (int p = 0; p < pts.Count; p++)
            {
                XYZ d = pts[p] - center;
                XYZ dp = d - a.Multiply(d.DotProduct(a));
                double dl = dp.GetLength();
                if (dl < 1e-6) continue;
                if (Math.Abs(dl - r) < tol && Math.Abs(nor[p].DotProduct(dp.Multiply(1.0 / dl))) > minDot)
                {
                    cnt++;
                    double ang = Math.Atan2(dp.DotProduct(e2), dp.DotProduct(e1));
                    int b = (int)((ang + Math.PI) / (2.0 * Math.PI) * 36);
                    if (b < 0) b = 0; else if (b > 35) b = 35;
                    bins[b] = true;
                }
            }

            int occ = 0;
            foreach (bool bb in bins) if (bb) occ++;
            double density = cnt / Math.Max(0.04, r);
            return occ >= 3 ? (int)(occ * 10000 + density * 100) : 0;
        }

        private static List<XYZ> Inliers(List<XYZ> pts, XYZ[] nor, XYZ a, XYZ center, double r, bool strictNormals)
        {
            double tol = Tol(r);
            double minDot = strictNormals ? 0.40 : 0.15;
            var res = new List<XYZ>();
            for (int p = 0; p < pts.Count; p++)
            {
                XYZ d = pts[p] - center;
                XYZ dp = d - a.Multiply(d.DotProduct(a));
                double dl = dp.GetLength();
                if (dl < 1e-6) continue;
                if (Math.Abs(dl - r) < tol && Math.Abs(nor[p].DotProduct(dp.Multiply(1.0 / dl))) > minDot)
                    res.Add(pts[p]);
            }
            return res;
        }

        // ===================================================================
        // Geometry helpers
        // ===================================================================

        private static List<XYZ> Downsample(IList<XYZ> pts, int target)
        {
            if (pts.Count <= target) return new List<XYZ>(pts);
            var res = new List<XYZ>(target);
            int step = pts.Count / target;
            if (step < 1) step = 1;
            for (int i = 0; i < pts.Count; i += step) res.Add(pts[i]);
            return res;
        }

        private static XYZ[] EstimateNormals(List<XYZ> pts, int k)
        {
            int n = pts.Count;
            var nor = new XYZ[n];
            if (n == 0) return nor;

            double cell = 0.08;
            var grid = new Dictionary<long, List<int>>(n);
            for (int i = 0; i < n; i++)
            {
                long key = NormalCellKey(pts[i], cell);
                if (!grid.TryGetValue(key, out var bucket))
                {
                    bucket = new List<int>(8);
                    grid[key] = bucket;
                }
                bucket.Add(i);
            }

            var dst = new double[64];
            var idx = new int[64];
            var nb = new List<XYZ>(16);

            for (int i = 0; i < n; i++)
            {
                XYZ pi = pts[i];
                long gx = (long)Math.Floor(pi.X / cell);
                long gy = (long)Math.Floor(pi.Y / cell);
                long gz = (long)Math.Floor(pi.Z / cell);
                int nn = 0;
                for (long dx = -1; dx <= 1 && nn < 64; dx++)
                {
                    for (long dy = -1; dy <= 1 && nn < 64; dy++)
                    {
                        for (long dz = -1; dz <= 1 && nn < 64; dz++)
                        {
                            long key = (gx + dx) * 73856093 ^ (gy + dy) * 19349663 ^ (gz + dz) * 83492791;
                            if (!grid.TryGetValue(key, out var bucket)) continue;
                            foreach (int j in bucket)
                            {
                                if (nn >= 64) break;
                                double ddx = pts[j].X - pi.X, ddy = pts[j].Y - pi.Y, ddz = pts[j].Z - pi.Z;
                                dst[nn] = ddx * ddx + ddy * ddy + ddz * ddz;
                                idx[nn] = j;
                                nn++;
                            }
                        }
                    }
                }

                if (nn < 3)
                {
                    nor[i] = XYZ.BasisZ;
                    continue;
                }

                Array.Sort(dst, idx, 0, nn);
                int kk = Math.Min(k, nn);
                nb.Clear();
                for (int j = 0; j < kk; j++) nb.Add(pts[idx[j]]);
                XYZ c = Centroid(nb);
                Covariance(nb, c, out double[] ev, out XYZ[] evec);
                int smFinal = 0;
                if (ev[1] < ev[smFinal]) smFinal = 1;
                if (ev[2] < ev[smFinal]) smFinal = 2;
                nor[i] = Norm(evec[smFinal]);
            }
            return nor;
        }

        private static long NormalCellKey(XYZ p, double cell)
        {
            long gx = (long)Math.Floor(p.X / cell);
            long gy = (long)Math.Floor(p.Y / cell);
            long gz = (long)Math.Floor(p.Z / cell);
            return gx * 73856093 ^ gy * 19349663 ^ gz * 83492791;
        }

        private static XYZ Centroid(List<XYZ> p)
        {
            double x = 0, y = 0, z = 0;
            foreach (var q in p) { x += q.X; y += q.Y; z += q.Z; }
            int n = p.Count;
            return new XYZ(x / n, y / n, z / n);
        }

        internal static void Covariance(List<XYZ> p, XYZ c, out double[] eval, out XYZ[] evec)
        {
            double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (var q in p)
            {
                double dx = q.X - c.X, dy = q.Y - c.Y, dz = q.Z - c.Z;
                xx += dx * dx; xy += dx * dy; xz += dx * dz; yy += dy * dy; yz += dy * dz; zz += dz * dz;
            }
            double[,] A = { { xx, xy, xz }, { xy, yy, yz }, { xz, yz, zz } };
            Jacobi(A, out eval, out double[][] v);
            evec = new[]
            {
                new XYZ(v[0][0], v[0][1], v[0][2]),
                new XYZ(v[1][0], v[1][1], v[1][2]),
                new XYZ(v[2][0], v[2][1], v[2][2])
            };
        }

        private static XYZ Norm(XYZ v)
        {
            double l = v.GetLength();
            return l < 1e-12 ? XYZ.BasisX : v.Multiply(1.0 / l);
        }

        private static void Basis(XYZ a, out XYZ e1, out XYZ e2)
        {
            XYZ t = Math.Abs(a.Z) < 0.9 ? XYZ.BasisZ : XYZ.BasisX;
            e1 = Norm(a.CrossProduct(t));
            e2 = Norm(a.CrossProduct(e1));
        }

        public static void Jacobi(double[,] a, out double[] eval, out double[][] evec)
        {
            double[,] A = (double[,])a.Clone();
            double[,] V = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            for (int sweep = 0; sweep < 100; sweep++)
            {
                double off = Math.Abs(A[0, 1]) + Math.Abs(A[0, 2]) + Math.Abs(A[1, 2]);
                if (off < 1e-20) break;
                for (int p = 0; p < 2; p++)
                {
                    for (int q = p + 1; q < 3; q++)
                    {
                        if (Math.Abs(A[p, q]) < 1e-22) continue;
                        double app = A[p, p];
                        double aqq = A[q, q];
                        double apq = A[p, q];

                        double phi = 0.5 * Math.Atan2(2.0 * apq, aqq - app);
                        double c = Math.Cos(phi), s = Math.Sin(phi);

                        double c2 = c * c;
                        double s2 = s * s;
                        double cs = c * s;

                        A[p, p] = c2 * app - 2.0 * cs * apq + s2 * aqq;
                        A[q, q] = s2 * app + 2.0 * cs * apq + c2 * aqq;
                        A[p, q] = A[q, p] = 0.0;

                        int other = 3 - p - q;
                        double aPo = A[p, other];
                        double aQo = A[q, other];
                        A[p, other] = A[other, p] = c * aPo - s * aQo;
                        A[q, other] = A[other, q] = s * aPo + c * aQo;

                        for (int k = 0; k < 3; k++)
                        {
                            double vp = V[k, p];
                            double vq = V[k, q];
                            V[k, p] = c * vp - s * vq;
                            V[k, q] = s * vp + c * vq;
                        }
                    }
                }
            }
            eval = new[] { A[0, 0], A[1, 1], A[2, 2] };
            evec = new[]
            {
                new[] { V[0, 0], V[1, 0], V[2, 0] },
                new[] { V[0, 1], V[1, 1], V[2, 1] },
                new[] { V[0, 2], V[1, 2], V[2, 2] }
            };
        }

        private static bool KasaCircle(double[] U, double[] W, out double cu, out double cw, out double radius)
        {
            cu = cw = radius = 0;
            int n = U.Length;
            double suu = 0, svv = 0, suv = 0, su = 0, sv = 0, suz = 0, svz = 0, sz = 0;
            for (int i = 0; i < n; i++)
            {
                double u = U[i], v = W[i], z = u * u + v * v;
                suu += u * u; svv += v * v; suv += u * v; su += u; sv += v; suz += u * z; svz += v * z; sz += z;
            }
            double[,] M = { { suu, suv, su }, { suv, svv, sv }, { su, sv, n } };
            double[] rhs = { -suz, -svz, -sz };
            double det = Det(M);
            if (Math.Abs(det) < 1e-12) return false;
            double D = Det(Replace(M, 0, rhs)) / det;
            double E = Det(Replace(M, 1, rhs)) / det;
            double F = Det(Replace(M, 2, rhs)) / det;
            cu = -D / 2.0; cw = -E / 2.0;
            double r2 = cu * cu + cw * cw - F;
            if (r2 <= 0) return false;
            radius = Math.Sqrt(r2);
            return true;
        }

        private static double Det(double[,] m) =>
            m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) -
            m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0]) +
            m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);

        private static double[,] Replace(double[,] m, int col, double[] r)
        {
            var c = (double[,])m.Clone();
            for (int i = 0; i < 3; i++) c[i, col] = r[i];
            return c;
        }
    }
}














