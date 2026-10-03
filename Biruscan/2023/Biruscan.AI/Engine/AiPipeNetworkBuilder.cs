using Autodesk.Revit.DB;
using Biruscan.AI.Contracts;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Biruscan.AI.Engine
{
    /// <summary>
    /// Post-processes raw cylinder fits into a connected pipe network:
    /// merge collinear fragments, snap endpoints, split at branches, detect tees/elbows.
    /// </summary>
    internal static class AiPipeNetworkBuilder
    {
        private const double SnapTolFt = 0.35;      // ~105 mm — wider for network junctions
        private const double BranchTolFt = 0.40;    // ~120 mm
        private const double ParallelDot = 0.985;
        private const double PerpDotMax = 0.26;     // ~75° minimum for elbow

        internal sealed class Segment
        {
            public XYZ Start;
            public XYZ End;
            public XYZ Axis;
            public double DiameterMm;
            public double RadiusFt;
            public int ShellPointCount;
            public float Confidence;
        }

        internal static List<BirooniAiElement> BuildNetwork(List<Segment> raw, bool preserveAlignment = false)
            => BuildNetwork(raw, preserveAlignment, keepSeparateForOperations: false);

        /// <param name="keepSeparateForOperations">
        /// Emit one pipe per peeled run (only merge overlapping duplicates).
        /// Junctions are left for MepOperationService (Elbow / Tee / Union / Reducer).
        /// </param>
        internal static List<BirooniAiElement> BuildNetwork(List<Segment> raw, bool preserveAlignment, bool keepSeparateForOperations)
        {
            if (raw == null || raw.Count == 0) return new List<BirooniAiElement>();

            var segs = raw.Select(Clone).ToList();

            if (keepSeparateForOperations)
            {
                // Same physical cylinder only — do not snap corners or drop T-stems.
                segs = MergeCollinear(segs, 0.07, 0.12);
                return ToElements(segs, new List<Junction>());
            }

            // Keep merge tight for alignment, but use a looser junction snap so tees/elbows connect.
            double junctionSnapTol = preserveAlignment ? 0.24 : SnapTolFt;
            double mergePerpTol = preserveAlignment ? 0.085 : SnapTolFt * 2.0;
            double mergeGapTol = preserveAlignment ? 0.16 : SnapTolFt * 2.5;

            segs = MergeCollinear(segs, mergePerpTol, mergeGapTol);
            SnapTeeBranches(segs);
            segs = SplitAtBranches(segs);
            segs = MergeCollinear(segs, mergePerpTol, mergeGapTol);
            ExtendToCorners(segs, preserveAlignment);
            SnapEndpoints(segs, junctionSnapTol);
            HealJunctionEndpoints(segs, junctionSnapTol);
            segs = PruneOrphanSegments(segs, junctionSnapTol);

            var junctions = DetectJunctions(segs, junctionSnapTol);
            return ToElements(segs, junctions);
        }

        private static Segment Clone(Segment s) => new Segment
        {
            Start = s.Start, End = s.End, Axis = s.Axis,
            DiameterMm = s.DiameterMm, RadiusFt = s.RadiusFt,
            ShellPointCount = s.ShellPointCount, Confidence = s.Confidence
        };

        private static void UnifyDiameter(List<Segment> segs)
        {
            if (segs.Count == 0) return;
            var groups = segs.GroupBy(s => Math.Round(s.DiameterMm / 25.0)).OrderByDescending(g => g.Count());
            double mode = groups.First().Key * 25.0;
            foreach (var s in segs)
                if (Math.Abs(s.DiameterMm - mode) < mode * 0.25) s.DiameterMm = mode;
        }

        private static List<Segment> MergeCollinear(List<Segment> input, double mergePerpTol, double mergeGapTol)
        {
            var segs = input.ToList();
            bool merged;
            do
            {
                merged = false;
                for (int i = 0; i < segs.Count; i++)
                {
                    for (int j = i + 1; j < segs.Count; j++)
                    {
                        if (!TryMerge(segs[i], segs[j], mergePerpTol, mergeGapTol, out Segment m)) continue;
                        segs[i] = m;
                        segs.RemoveAt(j);
                        merged = true;
                        break;
                    }
                    if (merged) break;
                }
            } while (merged);
            return segs;
        }

        

        private static List<Segment> SplitAtBranches(List<Segment> input)
        {
            var result = new List<Segment>();
            foreach (var seg in input)
            {
                var splits = new List<double> { 0.0, seg.Start.DistanceTo(seg.End) };
                XYZ axis = AxisOf(seg);

                foreach (var other in input)
                {
                    if (ReferenceEquals(seg, other)) continue;
                    XYZ otherAxis = AxisOf(other);
                    if (Math.Abs(axis.DotProduct(otherAxis)) > 0.92) continue;

                    foreach (XYZ ep in new[] { other.Start, other.End })
                    {
                        ProjectOntoAxis(seg.Start, seg.End, ep, out double t, out _);
                        if (t < 0.15 || t > seg.Start.DistanceTo(seg.End) - 0.15) continue;
                        XYZ onAxis = seg.Start + axis.Multiply(t);
                        if (onAxis.DistanceTo(ep) < BranchTolFt && seg.Confidence >= 0.42f && other.Confidence >= 0.42f)
                            splits.Add(t);
                    }

                    if (LineClosestApproach(seg.Start, axis, other.Start, otherAxis,
                        out double ta, out double tb, out double gap, out _, out _))
                    {
                        double len = seg.Start.DistanceTo(seg.End);
                        double olen = other.Start.DistanceTo(other.End);
                        bool midHeader = ta > 0.15 && ta < len - 0.15;
                        bool nearBranchEnd = tb < 0.40 || tb > olen - 0.40;
                        if (midHeader && nearBranchEnd && gap < BranchTolFt)
                            splits.Add(ta);
                    }
                }

                splits = splits.Distinct().OrderBy(x => x).ToList();
                if (splits.Count <= 2)
                {
                    result.Add(seg);
                    continue;
                }

                for (int i = 0; i < splits.Count - 1; i++)
                {
                    double len = splits[i + 1] - splits[i];
                    if (len < 0.15) continue;
                    result.Add(new Segment
                    {
                        Start = seg.Start + axis.Multiply(splits[i]),
                        End = seg.Start + axis.Multiply(splits[i + 1]),
                        Axis = axis,
                        DiameterMm = seg.DiameterMm,
                        RadiusFt = seg.RadiusFt,
                        ShellPointCount = seg.ShellPointCount,
                        Confidence = seg.Confidence,
                    });
                }
            }
            return result.Count > 0 ? result : input;
        }

        private static void SnapEndpoints(List<Segment> segs, double snapTol)
        {
            var points = new List<XYZ>();
            foreach (var s in segs) { points.Add(s.Start); points.Add(s.End); }

            var groups = new List<List<int>>();
            var used = new bool[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                if (used[i]) continue;
                var g = new List<int> { i };
                used[i] = true;
                for (int j = i + 1; j < points.Count; j++)
                {
                    if (used[j]) continue;
                    if (points[i].DistanceTo(points[j]) <= snapTol)
                    {
                        g.Add(j);
                        used[j] = true;
                    }
                }
                if (g.Count > 1) groups.Add(g);
            }

            foreach (var g in groups)
            {
                double x = 0, y = 0, z = 0;
                foreach (int idx in g) { x += points[idx].X; y += points[idx].Y; z += points[idx].Z; }
                XYZ c = new XYZ(x / g.Count, y / g.Count, z / g.Count);
                for (int k = 0; k < segs.Count; k++)
                {
                    for (int gi = 0; gi < g.Count; gi++)
                    {
                        int ptIdx = g[gi];
                        if (ptIdx == k * 2) segs[k].Start = c;
                        if (ptIdx == k * 2 + 1) segs[k].End = c;
                    }
                }
            }
        }

        private static void ExtendToCorners(List<Segment> segs, bool preserveAlignment)
        {
            double maxExt = preserveAlignment ? 0.38 : 0.55;
            for (int i = 0; i < segs.Count; i++)
            {
                for (int j = i + 1; j < segs.Count; j++)
                {
                    XYZ ai = AxisOf(segs[i]);
                    XYZ aj = AxisOf(segs[j]);
                    if (Math.Abs(ai.DotProduct(aj)) > 0.95) continue;
                    TryExtendPair(segs[i], segs[j], maxExt);
                }
            }
        }

        private static void TryExtendPair(Segment a, Segment b, double maxExt)
        {
            XYZ da = AxisOf(a);
            XYZ db = AxisOf(b);
            if (!LineClosestApproach(a.Start, da, b.Start, db,
                out double ta, out double tb, out double gap, out XYZ pa, out XYZ pb))
                return;

            double maxGap = Math.Max(a.DiameterMm, b.DiameterMm) / 304.8 * 5.0;
            if (gap > maxGap) return;

            double la = a.Start.DistanceTo(a.End);
            double lb = b.Start.DistanceTo(b.End);
            if (ta >= 0 && ta <= la && tb >= 0 && tb <= lb) return;

            double extA = ta < 0 ? -ta : ta - la;
            double extB = tb < 0 ? -tb : tb - lb;
            if (extA > maxExt || extB > maxExt) return;

            if (ta < 0) a.Start = a.Start + da.Multiply(ta);
            else if (ta > la) a.End = a.Start + da.Multiply(ta);
            if (tb < 0) b.Start = b.Start + db.Multiply(tb);
            else if (tb > lb) b.End = b.Start + db.Multiply(tb);
        }

        private static void HealJunctionEndpoints(List<Segment> segs, double tol)
        {
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

                private static bool TryMerge(Segment a, Segment b, double mergePerpTol, double mergeGapTol, out Segment merged)
        {
            merged = null;
            // Allow up to 40% diameter variance for collinear segments to merge duplicate parallel slices
            if (Math.Abs(a.DiameterMm - b.DiameterMm) > Math.Max(a.DiameterMm, b.DiameterMm) * 0.40)
                return false;

            XYZ axA = AxisOf(a);
            XYZ axB = AxisOf(b);
            if (Math.Abs(axA.DotProduct(axB)) < 0.95) return false;

            XYZ axis = axA;
            if (axis.DotProduct(axB) < 0) { var t = a.Start; a.Start = a.End; a.End = t; axA = AxisOf(a); axis = axA; }

            double perp = PerpDistance(a.Start, a.End, b.Start);
            if (perp > mergePerpTol * 1.5) return false;

            ProjectOntoAxis(a.Start, a.End, b.Start, out double ta0, out double ta1);
            ProjectOntoAxis(a.Start, a.End, b.End, out double tb0, out double tb1);
            double amin = Math.Min(Math.Min(ta0, ta1), Math.Min(tb0, tb1));
            double amax = Math.Max(Math.Max(ta0, ta1), Math.Max(tb0, tb1));
            double gap = Math.Max(0, Math.Max(Math.Min(ta0, ta1) - Math.Max(tb0, tb1), Math.Min(tb0, tb1) - Math.Max(ta0, ta1)));
            if (gap > mergeGapTol * 1.5) return false;

            Segment best = a.Confidence >= b.Confidence ? a : b;
            double avgDia = (a.DiameterMm * a.ShellPointCount + b.DiameterMm * b.ShellPointCount) / 
                            Math.Max(1, a.ShellPointCount + b.ShellPointCount);

            merged = new Segment
            {
                Start = a.Start + axis.Multiply(amin),
                End = a.Start + axis.Multiply(amax),
                Axis = axis,
                DiameterMm = avgDia,
                RadiusFt = best.RadiusFt,
                ShellPointCount = a.ShellPointCount + b.ShellPointCount,
                Confidence = Math.Max(a.Confidence, b.Confidence),
            };
            return merged.Start.DistanceTo(merged.End) > 0.1;
        }

                private static List<Segment> PruneOrphanSegments(List<Segment> segs, double snapTol)
        {
            if (segs == null || segs.Count <= 1) return segs;

            // 1. Build Connected Components via Union-Find on segment connectivity
            int n = segs.Count;
            int[] parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;

            int FindRoot(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(int a, int b) { int ra = FindRoot(a), rb = FindRoot(b); if (ra != rb) parent[ra] = rb; }

            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    if (IsSegmentsConnected(segs[i], segs[j], snapTol * 1.5))
                    {
                        Union(i, j);
                    }
                }
            }

            var components = new Dictionary<int, List<Segment>>();
            for (int i = 0; i < n; i++)
            {
                int root = FindRoot(i);
                if (!components.ContainsKey(root)) components[root] = new List<Segment>();
                components[root].Add(segs[i]);
            }

            // 2. Identify True MEP Service Networks by Total Connected Length & Node Count
            var validNetworks = new List<Segment>();
            double maxComponentLength = 0;
            List<Segment> bestComponent = null;

            foreach (var comp in components.Values)
            {
                double totalLen = comp.Sum(s => s.Start.DistanceTo(s.End));
                if (totalLen > maxComponentLength)
                {
                    maxComponentLength = totalLen;
                    bestComponent = comp;
                }

                // A valid MEP service network has multiple connected runs and total length >= 6.0 ft
                if (comp.Count >= 2 && totalLen >= 6.0)
                {
                    validNetworks.AddRange(comp);
                }
            }

            if (validNetworks.Count > 0)
            {
                return validNetworks;
            }

            if (bestComponent != null && bestComponent.Count >= 1)
            {
                return bestComponent;
            }

            return segs;
        }

        /// <summary>
        /// Pull a T-stem onto the header when it is close and perpendicular but
        /// the peel stopped short of the junction (inliers were eaten by the header).
        /// </summary>
        private static void SnapTeeBranches(List<Segment> segs)
        {
            if (segs == null || segs.Count < 2) return;
            const double maxGap = 0.70;
            const double perpDot = 0.40;

            for (int i = 0; i < segs.Count; i++)
            {
                for (int j = 0; j < segs.Count; j++)
                {
                    if (i == j) continue;
                    Segment header = segs[i];
                    Segment branch = segs[j];
                    XYZ ah = AxisOf(header);
                    XYZ ab = AxisOf(branch);
                    if (Math.Abs(ah.DotProduct(ab)) > perpDot) continue;

                    if (!LineClosestApproach(header.Start, ah, branch.Start, ab,
                        out double th, out double tb, out double gap, out XYZ onH, out _))
                        continue;

                    double lh = header.Start.DistanceTo(header.End);
                    double lb = branch.Start.DistanceTo(branch.End);
                    if (th < 0.20 || th > lh - 0.20) continue;
                    if (gap > maxGap) continue;

                    bool nearStart = Math.Abs(tb) <= Math.Abs(tb - lb);
                    if (nearStart && Math.Abs(tb) > maxGap) continue;
                    if (!nearStart && Math.Abs(tb - lb) > maxGap) continue;

                    if (nearStart) branch.Start = onH;
                    else branch.End = onH;
                }
            }
        }

        private static bool IsSegmentsConnected(Segment a, Segment b, double snapTol)
        {
            if (a.Start.DistanceTo(b.Start) <= snapTol) return true;
            if (a.Start.DistanceTo(b.End) <= snapTol) return true;
            if (a.End.DistanceTo(b.Start) <= snapTol) return true;
            if (a.End.DistanceTo(b.End) <= snapTol) return true;
            if (PointToSegment(a.Start, b) <= snapTol) return true;
            if (PointToSegment(a.End, b) <= snapTol) return true;
            if (PointToSegment(b.Start, a) <= snapTol) return true;
            if (PointToSegment(b.End, a) <= snapTol) return true;
            return false;
        }

        private static double PointToSegment(XYZ p, Segment s)
        {
            XYZ axis = AxisOf(s);
            double len = s.Start.DistanceTo(s.End);
            XYZ rel = p - s.Start;
            double t = rel.DotProduct(axis);
            if (t < 0) t = 0;
            else if (t > len) t = len;
            return p.DistanceTo(s.Start + axis.Multiply(t));
        }

        private static List<Junction> DetectJunctions(List<Segment> segs, double snapTol)
        {
            var endpoints = new List<(XYZ pt, int segIdx, bool isStart)>();
            for (int i = 0; i < segs.Count; i++)
            {
                endpoints.Add((segs[i].Start, i, true));
                endpoints.Add((segs[i].End, i, false));
            }

            var junctions = new List<Junction>();
            var used = new bool[endpoints.Count];

            for (int i = 0; i < endpoints.Count; i++)
            {
                if (used[i]) continue;
                var cluster = new List<int> { i };
                used[i] = true;
                for (int j = i + 1; j < endpoints.Count; j++)
                {
                    if (used[j]) continue;
                    if (endpoints[i].pt.DistanceTo(endpoints[j].pt) <= snapTol * 1.5)
                    {
                        cluster.Add(j);
                        used[j] = true;
                    }
                }

                var segIds = cluster.Select(c => endpoints[c].segIdx).Distinct().ToList();
                if (segIds.Count < 2) continue;

                double x = 0, y = 0, z = 0;
                foreach (int c in cluster) { x += endpoints[c].pt.X; y += endpoints[c].pt.Y; z += endpoints[c].pt.Z; }
                XYZ center = new XYZ(x / cluster.Count, y / cluster.Count, z / cluster.Count);

                string kind = ClassifyJunction(segIds, segs, center);
                if (kind != null)
                {
                    double dia = segIds.Average(id => segs[id].DiameterMm);
                    junctions.Add(new Junction { Kind = kind, Center = center, DiameterMm = dia });
                }
            }
            return junctions;
        }

        private static string ClassifyJunction(List<int> segIds, List<Segment> segs, XYZ center)
        {
            var dirs = new List<XYZ>();
            foreach (int id in segIds)
            {
                XYZ axis = AxisOf(segs[id]);
                XYZ toStart = center - segs[id].Start;
                if (toStart.GetLength() < toStart.DotProduct(axis) * axis.GetLength() + 0.01)
                    dirs.Add(axis);
                else
                    dirs.Add(axis.Negate());
            }

            int n = dirs.Count;
            if (n == 2)
            {
                double d = Math.Abs(dirs[0].DotProduct(dirs[1]));
                if (d < 0.92 && d > PerpDotMax) return "elbow";
            }
            if (n == 3) return "tee";
            if (n >= 4) return "cross";
            return null;
        }

        private static List<BirooniAiElement> ToElements(List<Segment> segs, List<Junction> junctions)
        {
            var outList = new List<BirooniAiElement>();
            foreach (var s in segs)
            {
                if (s.Start.DistanceTo(s.End) < 0.1) continue;
                if (s.Confidence < 0.42f) continue;
                outList.Add(new BirooniAiElement
                {
                    Type = "pipe",
                    Start = new[] { s.Start.X, s.Start.Y, s.Start.Z },
                    End = new[] { s.End.X, s.End.Y, s.End.Z },
                    DiameterMm = s.DiameterMm,
                    Confidence = s.Confidence,
                });
            }
            foreach (var j in junctions)
            {
                outList.Add(new BirooniAiElement
                {
                    Type = j.Kind,
                    Center = new[] { j.Center.X, j.Center.Y, j.Center.Z },
                    DiameterMm = j.DiameterMm,
                    
                    Confidence = 0.75f,
                });
            }
            return outList;
        }

        private sealed class Junction
        {
            public string Kind;
            public XYZ Center;
            public double DiameterMm;
        }

        private static XYZ AxisOf(Segment s)
        {
            XYZ v = s.End - s.Start;
            double l = v.GetLength();
            return l < 1e-9 ? (s.Axis ?? XYZ.BasisZ) : v.Multiply(1.0 / l);
        }

        private static double PerpDistance(XYZ a0, XYZ a1, XYZ p)
        {
            XYZ axis = AxisOf(new Segment { Start = a0, End = a1 });
            XYZ rel = p - a0;
            XYZ perp = rel - axis.Multiply(rel.DotProduct(axis));
            return perp.GetLength();
        }

        private static void ProjectOntoAxis(XYZ a0, XYZ a1, XYZ p, out double t, out double perp)
        {
            XYZ axis = AxisOf(new Segment { Start = a0, End = a1 });
            XYZ rel = p - a0;
            t = rel.DotProduct(axis);
            XYZ on = axis.Multiply(t);
            perp = (rel - on).GetLength();
        }

        private static bool LineClosestApproach(XYZ p1, XYZ d1, XYZ p2, XYZ d2,
            out double t, out double s, out double gap, out XYZ on1, out XYZ on2)
        {
            t = s = 0;
            gap = double.MaxValue;
            on1 = p1; on2 = p2;
            XYZ cross = d1.CrossProduct(d2);
            double den = cross.DotProduct(cross);
            if (den < 1e-12) return false;
            XYZ diff = p2 - p1;
            t = diff.CrossProduct(d2).DotProduct(cross) / den;
            s = diff.CrossProduct(d1).DotProduct(cross) / den;
            on1 = p1 + d1.Multiply(t);
            on2 = p2 + d2.Multiply(s);
            gap = on1.DistanceTo(on2);
            return true;
        }
    }
}





