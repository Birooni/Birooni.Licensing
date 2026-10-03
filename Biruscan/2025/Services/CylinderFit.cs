using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;

namespace Biruscan.Services
{
    /// <summary>
    /// Pipe (cylinder) detection from a point set.
    ///
    /// Two complementary pipelines:
    ///   A) Normal-based RANSAC — best for full cross-sections (complete pipe arcs).
    ///   B) PCA strip + 2D circle RANSAC — best for partial cross-sections (half /
    ///      quarter pipe visible) and edge-wise longitudinal scans.
    /// </summary>
    public static class CylinderFit
    {
        private static readonly Random _rng = new Random(20260601);
        private const double MaxRadius = 1.5;   // ft (36" diameter)
        private const int MaxFitPoints = 1800;
        private const int MaxRefinePoints = 2200;

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

        /// <summary>
        /// PCA-strip only (no normals / RANSAC / LM). Safe to call on many grid cells.
        /// </summary>
        public static Result FitFast(IList<XYZ> input, bool relaxed)
        {
            var fail = new Result { Success = false, PointCount = input == null ? 0 : input.Count };
            int minPts = relaxed ? 8 : 12;
            if (input == null || input.Count < minPts) return fail;
            List<XYZ> pts = Downsample(input, 900);
            Result strip = FitPcaStrip(pts, relaxed, nor: null);
            if (!strip.Success) return strip;
            RefineToPointCloud(ref strip, pts);
            return strip;
        }

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

            // Fast path: PCA strip is enough for plan/elevation ribbons and most
            // straight runs. Skip O(n²) normals + 4000-iter RANSAC + LM.
            Result strip = FitPcaStrip(pts, relaxed, nor: null);
            if (strip.Success && strip.Rms <= 0.055 && strip.ArcSpanDeg >= 16.0)
            {
                RefineToPointCloud(ref strip, pts);
                return strip;
            }

            XYZ[] nor = pts.Count >= 10 ? EstimateNormals(pts, 10) : null;
            Result ransac = pts.Count >= (relaxed ? 14 : 18) && nor != null
                ? FitRansac(pts, relaxed, nor)
                : new Result { Success = false };
            Result lm = TryLmPartialFit(pts);

            Result pick = PickBestPartial(strip, ransac, lm);
            if (pick.Success)
            {
                RefineToPointCloud(ref pick, pts);
                return pick;
            }

            return new Result
            {
                Success = false,
                PointCount = pts.Count,
                Message = "No pipe cylinder found. For partial (half/quarter) scans, draw the box along the " +
                          "visible pipe length in a 3D view. For full-round pipes, show more of the curved face.",
            };
        }

        private static Result TryLmPartialFit(List<XYZ> pts)
        {
            var r = new Result { Success = false, PointCount = pts.Count, Method = "lm-partial" };
            try
            {
                var lm = Biruscan.AI.Vision.LevenbergMarquardtCylinderFitter.FitAndValidate(pts);
                if (lm == null || !lm.Success) return r;
                if (lm.Radius < 0.015 || lm.Radius > MaxRadius) return r;
                r.Success = true;
                r.Start = lm.Start;
                r.End = lm.End;
                r.Axis = lm.Axis;
                r.Radius = lm.Radius;
                r.Rms = lm.RmsError;
                r.PointCount = lm.Inliers != null && lm.Inliers.Count > 0 ? lm.Inliers.Count : pts.Count;
                r.ArcSpanDeg = lm.ArcSpanDeg;
                r.Flatness = ArcFlatness(lm.ArcSpanDeg);
                r.Message = "ok";
                return r;
            }
            catch
            {
                return r;
            }
        }

        private static Result PickBestPartial(params Result[] cands)
        {
            Result best = new Result { Success = false };
            double bestScore = double.MaxValue;
            foreach (var c in cands)
            {
                if (c.Success != true) continue;
                // Prefer lower RMS; slight penalty for tiny arcs (but 12–72° is OK).
                double arcPen = c.ArcSpanDeg < 12.0 ? 0.05 : 0;
                double score = c.Rms + arcPen;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = c;
                }
            }
            return best;
        }

        /// <summary>Pratt algebraic 2D circle — stable on 15–25% arcs where 3-point RANSAC blows up.</summary>
        private static bool PrattCircle2d(double[] x, double[] y, out double xc, out double yc, out double r)
        {
            xc = 0; yc = 0; r = 0;
            int n = x.Length;
            if (n < 5) return false;

            double meanX = 0, meanY = 0;
            for (int i = 0; i < n; i++) { meanX += x[i]; meanY += y[i]; }
            meanX /= n; meanY /= n;

            double mxx = 0, myy = 0, mxy = 0, mxz = 0, myz = 0;
            for (int i = 0; i < n; i++)
            {
                double xi = x[i] - meanX;
                double yi = y[i] - meanY;
                double zi = xi * xi + yi * yi;
                mxx += xi * xi; myy += yi * yi; mxy += xi * yi;
                mxz += xi * zi; myz += yi * zi;
            }
            mxx /= n; myy /= n; mxy /= n; mxz /= n; myz /= n;

            double c0 = mxx * myy - mxy * mxy;
            if (Math.Abs(c0) < 1e-12) return false;

            double uc = (mxz * myy - myz * mxy) / (2.0 * c0);
            double vc = (myz * mxx - mxz * mxy) / (2.0 * c0);
            double rSq = uc * uc + vc * vc + mxx + myy;
            if (rSq <= 0) return false;

            r = Math.Sqrt(rSq);
            xc = uc + meanX;
            yc = vc + meanY;
            return r >= 0.015 && r <= MaxRadius;
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

            // Estimate the axis from the inner 80% so an elbow/tee at a box end
            // cannot rotate the run. Cross-section and length still use all points.
            List<XYZ> axisPts = InnerSpanPoints(pts, c, axis, 0.10, 0.90);
            if (axisPts.Count >= 8)
            {
                XYZ c2 = Centroid(axisPts);
                Covariance(axisPts, c2, out double[] ev2, out XYZ[] evec2);
                int ax2 = 0;
                if (ev2[1] > ev2[ax2]) ax2 = 1;
                if (ev2[2] > ev2[ax2]) ax2 = 2;
                XYZ a2 = Norm(evec2[ax2]);
                if (a2.DotProduct(axis) < 0) a2 = a2.Negate();
                if (Math.Abs(a2.DotProduct(axis)) > 0.90)
                {
                    axis = a2;
                    c = c2;
                }
                axis = RefineAxisGrid(axisPts, c, axis, 12.0, 1.0);
            }
            else
            {
                axis = RefineAxisGrid(pts, c, axis, 12.0, 1.0);
            }

            int minInliers = MinInliers(n, relaxed);
            // Partial 15–20% scans have noisy/planar-looking normals — never require them
            // for the strip pipeline (that is the whole point of a longitudinal ribbon).
            bool requireNorm = false;
            if (!FitCrossSection(pts, c, axis, requireNormals: requireNorm, relaxed, nor, out XYZ center, out double radius,
                    out double rms, out int inliers, out double arcDeg))
            {
                r.Message = "PCA strip fit failed cross-section.";
                return r;
            }

            double minArc = relaxed ? 12.0 : 18.0;
            if (radius < 0.015 || radius > 0.85 || inliers < minInliers || arcDeg < minArc)
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

            for (int it = 0; it < 900; it++)
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

            double cu, cw, r;
            bool gotCircle = PrattCircle2d(U, W, out cu, out cw, out r);
            if (!gotCircle || r < 0.015 || r > MaxRadius)
                gotCircle = Circle2dRansac(U, W, relaxed, out cu, out cw, out r, out _);
            if (!gotCircle)
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
            int iterations = 900;
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
            List<XYZ> work = pts.Count > 700 ? Downsample(pts, 700) : pts;
            XYZ best = axis;
            double bestErr = EvaluateAxis(axis, work, refPoint, out _, out _);
            Basis(axis, out XYZ t1, out XYZ t2);

            int step = coarseDeg >= 8.0 ? 2 : 1;
            int coarse = (int)coarseDeg;
            for (int u = -coarse; u <= coarse; u += step)
            {
                for (int v = -coarse; v <= coarse; v += step)
                {
                    if (u == 0 && v == 0) continue;
                    XYZ dir = Norm(axis + t1.Multiply(u * Math.PI / 180.0) + t2.Multiply(v * Math.PI / 180.0));
                    double err = EvaluateAxis(dir, work, refPoint, out _, out _);
                    if (err < bestErr) { bestErr = err; best = dir; }
                }
            }

            Basis(best, out t1, out t2);
            double fineStep = Math.Max(fineDeg / 5.0, 0.05);
            for (double u = -fineDeg; u <= fineDeg; u += fineStep)
            {
                for (double v = -fineDeg; v <= fineDeg; v += fineStep)
                {
                    if (Math.Abs(u) < 0.01 && Math.Abs(v) < 0.01) continue;
                    XYZ dir = Norm(best + t1.Multiply(u * Math.PI / 180.0) + t2.Multiply(v * Math.PI / 180.0));
                    double err = EvaluateAxis(dir, work, refPoint, out _, out _);
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
        /// Multi-pass inlier refine: median radius, PCA axis, 2D circle, then
        /// percentile endpoints. Call again on the full (not downsampled) cloud.
        /// </summary>
        public static void RefineToPointCloud(ref Result fit, IList<XYZ> points)
            => RefineToPointCloud(ref fit, points, lockAxis: false);

        public static void RefineToPointCloud(ref Result fit, IList<XYZ> points, bool lockAxis)
        {
            if (!fit.Success || points == null || points.Count < 6) return;
            List<XYZ> raw = points as List<XYZ> ?? new List<XYZ>(points);
            List<XYZ> pts = raw.Count > MaxRefinePoints ? Downsample(raw, MaxRefinePoints) : raw;

            XYZ axis = Norm(fit.Axis);
            double radius = fit.Radius;
            XYZ center = fit.Start + axis.Multiply(fit.Start.DistanceTo(fit.End) * 0.5);

            for (int pass = 0; pass < 3; pass++)
            {
                double tol = Math.Max(Tol(radius, true), radius * 0.22);
                var inliers = new List<XYZ>();
                var rads = new List<double>();
                foreach (XYZ p in pts)
                {
                    XYZ rel = p - center;
                    double tt = rel.DotProduct(axis);
                    XYZ radial = rel - axis.Multiply(tt);
                    double dist = radial.GetLength();
                    if (Math.Abs(dist - radius) > tol) continue;
                    inliers.Add(p);
                    rads.Add(dist);
                }
                if (inliers.Count < 8) break;

                rads.Sort();
                radius = rads[rads.Count / 2];

                XYZ cIn = Centroid(inliers);
                if (!lockAxis)
                {
                    Covariance(inliers, cIn, out double[] ev, out XYZ[] evec);
                    int ax = 0;
                    if (ev[1] > ev[ax]) ax = 1;
                    if (ev[2] > ev[ax]) ax = 2;
                    XYZ pca = Norm(evec[ax]);
                    if (pca.DotProduct(axis) < 0) pca = pca.Negate();
                    if (Math.Abs(pca.DotProduct(axis)) > 0.92)
                        axis = pca;

                    axis = RefineAxisGrid(inliers, cIn, axis, 4.0, 0.12);
                }

                if (FitCrossSection(inliers, cIn, axis, requireNormals: false, relaxed: true, nor: null,
                        out XYZ nC, out double nR, out double nRms, out _, out double arc))
                {
                    center = nC;
                    if (nR > 0.015 && nR < MaxRadius) radius = nR;
                    fit.Rms = nRms;
                    fit.ArcSpanDeg = arc;
                }
                else
                {
                    center = cIn;
                }
            }

            double tolF = Math.Max(Tol(radius, true), radius * 0.25);
            var tvals = new List<double>();
            foreach (XYZ p in pts)
            {
                XYZ rel = p - center;
                double t = rel.DotProduct(axis);
                XYZ radial = rel - axis.Multiply(t);
                if (Math.Abs(radial.GetLength() - radius) > tolF) continue;
                tvals.Add(t);
            }
            if (tvals.Count < 6) return;
            tvals.Sort();
            int i0 = Math.Max(0, (int)(tvals.Count * 0.01));
            int i1 = Math.Min(tvals.Count - 1, (int)(tvals.Count * 0.99));

            fit.Axis = axis;
            fit.Radius = radius;
            fit.Start = center + axis.Multiply(tvals[i0]);
            fit.End = center + axis.Multiply(tvals[i1]);
            fit.PointCount = tvals.Count;
        }

        /// <summary>
        /// Snap to world X/Y/Z only when that axis still fits the cylinder.
        /// As-built pipes a few degrees off grid keep their measured direction.
        /// </summary>
        public static bool MaybeOrthoSnap(ref Result fit, IList<XYZ> points, XYZ snappedAxis)
        {
            if (!fit.Success || points == null || points.Count < 8) return false;
            if (snappedAxis == null || snappedAxis.GetLength() < 1e-9) return false;

            XYZ orig = Norm(fit.Axis);
            XYZ snap = Norm(snappedAxis);
            if (snap.DotProduct(orig) < 0) snap = snap.Negate();
            if (snap.DotProduct(orig) >= 0.9995) return false;

            XYZ mid = fit.Start + orig.Multiply(fit.Start.DistanceTo(fit.End) * 0.5);
            double origRms = RadialRms(points, mid, orig, fit.Radius);
            double snapRms = RadialRms(points, mid, snap, fit.Radius);
            if (snapRms > origRms * 1.25 + 0.004)
                return false;

            fit.Axis = snap;
            RefineToPointCloud(ref fit, points, lockAxis: true);
            return true;
        }

        private static double RadialRms(IList<XYZ> points, XYZ origin, XYZ axis, double radius)
        {
            axis = Norm(axis);
            double acc = 0;
            int n = 0;
            foreach (XYZ p in points)
            {
                XYZ rel = p - origin;
                XYZ perp = rel - axis.Multiply(rel.DotProduct(axis));
                double e = perp.GetLength() - radius;
                acc += e * e;
                n++;
            }
            return n == 0 ? double.MaxValue : Math.Sqrt(acc / n);
        }

        private static List<XYZ> InnerSpanPoints(List<XYZ> pts, XYZ origin, XYZ axis, double lo, double hi)
        {
            var tagged = new List<(double t, XYZ p)>(pts.Count);
            foreach (XYZ p in pts)
                tagged.Add(((p - origin).DotProduct(axis), p));
            tagged.Sort((a, b) => a.t.CompareTo(b.t));
            int i0 = Math.Max(0, (int)(tagged.Count * lo));
            int i1 = Math.Min(tagged.Count, Math.Max(i0 + 8, (int)(tagged.Count * hi)));
            var inner = new List<XYZ>(Math.Max(0, i1 - i0));
            for (int i = i0; i < i1; i++) inner.Add(tagged[i].p);
            return inner;
        }

        private static double Percentile(List<double> sorted, double p)
        {
            if (sorted == null || sorted.Count == 0) return 0;
            if (p <= 0) return sorted[0];
            if (p >= 1) return sorted[sorted.Count - 1];
            double idx = p * (sorted.Count - 1);
            int i = (int)idx;
            if (i >= sorted.Count - 1) return sorted[sorted.Count - 1];
            double f = idx - i;
            return sorted[i] * (1.0 - f) + sorted[i + 1] * f;
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

            var grid = new Dictionary<long, List<int>>(n);
            double cell = 0.12;
            double minx = pts[0].X, maxx = pts[0].X, miny = pts[0].Y, maxy = pts[0].Y, minz = pts[0].Z, maxz = pts[0].Z;
            for (int i = 1; i < n; i++)
            {
                XYZ p = pts[i];
                if (p.X < minx) minx = p.X; if (p.X > maxx) maxx = p.X;
                if (p.Y < miny) miny = p.Y; if (p.Y > maxy) maxy = p.Y;
                if (p.Z < minz) minz = p.Z; if (p.Z > maxz) maxz = p.Z;
            }
            double diag = Math.Sqrt((maxx - minx) * (maxx - minx) + (maxy - miny) * (maxy - miny) + (maxz - minz) * (maxz - minz));
            if (diag > 1e-6) cell = Math.Max(0.05, Math.Min(0.35, diag / 40.0));

            long Pack(int ix, int iy, int iz) => ((long)(ix + 100000) * 73856093) ^ ((long)(iy + 100000) * 19349663) ^ ((long)(iz + 100000) * 83492791);

            for (int i = 0; i < n; i++)
            {
                int ix = (int)Math.Floor(pts[i].X / cell);
                int iy = (int)Math.Floor(pts[i].Y / cell);
                int iz = (int)Math.Floor(pts[i].Z / cell);
                long key = Pack(ix, iy, iz);
                if (!grid.TryGetValue(key, out List<int> bucket))
                {
                    bucket = new List<int>(8);
                    grid[key] = bucket;
                }
                bucket.Add(i);
            }

            var candD = new double[96];
            var candI = new int[96];
            for (int i = 0; i < n; i++)
            {
                XYZ pi = pts[i];
                int ix = (int)Math.Floor(pi.X / cell);
                int iy = (int)Math.Floor(pi.Y / cell);
                int iz = (int)Math.Floor(pi.Z / cell);
                int nc = 0;
                for (int dx = -1; dx <= 1 && nc < candD.Length; dx++)
                for (int dy = -1; dy <= 1 && nc < candD.Length; dy++)
                for (int dz = -1; dz <= 1 && nc < candD.Length; dz++)
                {
                    if (!grid.TryGetValue(Pack(ix + dx, iy + dy, iz + dz), out List<int> bucket)) continue;
                    for (int b = 0; b < bucket.Count && nc < candD.Length; b++)
                    {
                        int j = bucket[b];
                        if (j == i) continue;
                        XYZ d = pts[j] - pi;
                        candD[nc] = d.DotProduct(d);
                        candI[nc] = j;
                        nc++;
                    }
                }

                int take = Math.Min(k, nc);
                if (take < 3)
                {
                    nor[i] = XYZ.BasisZ;
                    continue;
                }
                // Partial select of `take` nearest among nc (nc is small).
                for (int a = 0; a < take; a++)
                {
                    int best = a;
                    for (int b = a + 1; b < nc; b++)
                        if (candD[b] < candD[best]) best = b;
                    double td = candD[a]; candD[a] = candD[best]; candD[best] = td;
                    int ti = candI[a]; candI[a] = candI[best]; candI[best] = ti;
                }

                var nb = new List<XYZ>(take);
                for (int a = 0; a < take; a++) nb.Add(pts[candI[a]]);
                XYZ c = Centroid(nb);
                Covariance(nb, c, out double[] ev, out XYZ[] evec);
                int smFinal = 0;
                if (ev[1] < ev[smFinal]) smFinal = 1;
                if (ev[2] < ev[smFinal]) smFinal = 2;
                nor[i] = Norm(evec[smFinal]);
            }
            return nor;
        }

        private static XYZ Centroid(List<XYZ> p)
        {
            double x = 0, y = 0, z = 0;
            foreach (var q in p) { x += q.X; y += q.Y; z += q.Z; }
            int n = p.Count;
            return new XYZ(x / n, y / n, z / n);
        }

        private static void Covariance(List<XYZ> p, XYZ c, out double[] eval, out XYZ[] evec)
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










