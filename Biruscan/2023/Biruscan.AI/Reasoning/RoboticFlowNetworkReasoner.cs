using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Biruscan.AI.Contracts;

namespace Biruscan.AI.Reasoning
{
    /// <summary>
    /// Cognitive Flow Network Topology Reasoner for Autonomous Robotic Modeling.
    /// Performs 3D graph reasoning, resolves laser scan occlusions/shadows, snaps standard 90°/45° elbows,
    /// and identifies manifold headers, branch connections, reducers, and crosses.
    /// Licensed under MIT / Apache 2.0 (Commercially safe for proprietary sale).
    /// </summary>
    public static class RoboticFlowNetworkReasoner
    {
        public class PipeEdge
        {
            public XYZ Start;
            public XYZ End;
            public XYZ Axis;
            public double DiameterMm;
            public double Length;
            public double Confidence;
        }

        public static List<BirooniAiElement> SolveTopologyAndInpaint(List<BirooniAiElement> rawElements)
        {
            if (rawElements == null || rawElements.Count == 0)
                return rawElements ?? new List<BirooniAiElement>();

            var pipes = new List<PipeEdge>();
            var otherElements = new List<BirooniAiElement>();

            foreach (var el in rawElements)
            {
                if (el.Type == "pipe" && el.Start != null && el.End != null)
                {
                    XYZ p1 = new XYZ(el.Start[0], el.Start[1], el.Start[2]);
                    XYZ p2 = new XYZ(el.End[0], el.End[1], el.End[2]);
                    double len = p1.DistanceTo(p2);
                    if (len >= 0.2)
                    {
                        pipes.Add(new PipeEdge
                        {
                            Start = p1,
                            End = p2,
                            Axis = (p2 - p1).Normalize(),
                            DiameterMm = el.DiameterMm > 0 ? el.DiameterMm : 50.0,
                            Length = len,
                            Confidence = el.Confidence
                        });
                    }
                }
                else
                {
                    otherElements.Add(el);
                }
            }

            if (pipes.Count == 0) return rawElements;

            pipes = InpaintOcclusionsAndMerge(pipes);
            AlignToBuildingAxes(pipes);
            var solvedElements = BuildSolvedElementsWithFittings(pipes);
            solvedElements.AddRange(otherElements);

            return solvedElements;
        }

        private static List<PipeEdge> InpaintOcclusionsAndMerge(List<PipeEdge> input)
        {
            var result = new List<PipeEdge>(input);
            bool mergedAny = true;
            int maxPasses = 5;
            int pass = 0;

            while (mergedAny && pass++ < maxPasses)
            {
                mergedAny = false;
                for (int i = 0; i < result.Count; i++)
                {
                    for (int j = i + 1; j < result.Count; j++)
                    {
                        var a = result[i];
                        var b = result[j];

                        double dot = Math.Abs(a.Axis.DotProduct(b.Axis));
                        if (dot > 0.96 && Math.Abs(a.DiameterMm - b.DiameterMm) <= 15.0)
                        {
                            XYZ d = b.Start - a.Start;
                            XYZ perp = d - a.Axis.Multiply(d.DotProduct(a.Axis));
                            if (perp.GetLength() <= 0.15)
                            {
                                double t1 = 0;
                                double t2 = (a.End - a.Start).DotProduct(a.Axis);
                                double t3 = (b.Start - a.Start).DotProduct(a.Axis);
                                double t4 = (b.End - a.Start).DotProduct(a.Axis);

                                double minT = Math.Min(Math.Min(t1, t2), Math.Min(t3, t4));
                                double maxT = Math.Max(Math.Max(t1, t2), Math.Max(t3, t4));
                                double span = maxT - minT;
                                double sumLen = a.Length + b.Length;

                                if (span <= sumLen + 5.0)
                                {
                                    XYZ newStart = a.Start + a.Axis.Multiply(minT);
                                    XYZ newEnd = a.Start + a.Axis.Multiply(maxT);

                                    result[i] = new PipeEdge
                                    {
                                        Start = newStart,
                                        End = newEnd,
                                        Axis = (newEnd - newStart).Normalize(),
                                        DiameterMm = (a.DiameterMm + b.DiameterMm) * 0.5,
                                        Length = newStart.DistanceTo(newEnd),
                                        Confidence = Math.Max(a.Confidence, b.Confidence)
                                    };

                                    result.RemoveAt(j);
                                    mergedAny = true;
                                    break;
                                }
                            }
                        }
                    }
                    if (mergedAny) break;
                }
            }

            return result;
        }

        private static void AlignToBuildingAxes(List<PipeEdge> pipes)
        {
            foreach (var p in pipes)
            {
                XYZ dir = (p.End - p.Start).Normalize();
                double absX = Math.Abs(dir.X);
                double absY = Math.Abs(dir.Y);
                double absZ = Math.Abs(dir.Z);

                if (absZ > 0.92)
                {
                    double avgX = (p.Start.X + p.End.X) * 0.5;
                    double avgY = (p.Start.Y + p.End.Y) * 0.5;
                    p.Start = new XYZ(avgX, avgY, p.Start.Z);
                    p.End = new XYZ(avgX, avgY, p.End.Z);
                    p.Axis = XYZ.BasisZ;
                }
                else if (absX > 0.92 && absZ < 0.15)
                {
                    double avgY = (p.Start.Y + p.End.Y) * 0.5;
                    double avgZ = (p.Start.Z + p.End.Z) * 0.5;
                    p.Start = new XYZ(p.Start.X, avgY, avgZ);
                    p.End = new XYZ(p.End.X, avgY, avgZ);
                    p.Axis = XYZ.BasisX;
                }
                else if (absY > 0.92 && absZ < 0.15)
                {
                    double avgX = (p.Start.X + p.End.X) * 0.5;
                    double avgZ = (p.Start.Z + p.End.Z) * 0.5;
                    p.Start = new XYZ(avgX, p.Start.Y, avgZ);
                    p.End = new XYZ(avgX, p.End.Y, avgZ);
                    p.Axis = XYZ.BasisY;
                }
            }
        }

        private static List<BirooniAiElement> BuildSolvedElementsWithFittings(List<PipeEdge> pipes)
        {
            var elements = new List<BirooniAiElement>();
            var fittings = new List<BirooniAiElement>();
            double snapTol = 1.2;

            foreach (var p in pipes)
            {
                elements.Add(new BirooniAiElement
                {
                    Type = "pipe",
                    Start = new[] { p.Start.X, p.Start.Y, p.Start.Z },
                    End = new[] { p.End.X, p.End.Y, p.End.Z },
                    DiameterMm = p.DiameterMm,
                    Confidence = p.Confidence
                });
            }

            for (int i = 0; i < pipes.Count; i++)
            {
                for (int j = i + 1; j < pipes.Count; j++)
                {
                    var pA = pipes[i];
                    var pB = pipes[j];

                    if (SnapCorners(pA, pB, snapTol, out XYZ juncPt))
                    {
                        var ports = new List<(XYZ dir, double diaMm, double? widthMm, double? heightMm)>
                        {
                            (pA.Axis, pA.DiameterMm, null, null),
                            (pB.Axis, pB.DiameterMm, null, null)
                        };

                        var classified = NeuralFittingClassifier.ClassifyJunction("piping", juncPt, ports);
                        string fitStr = classified?.Kind.ToString().ToLowerInvariant() ?? "elbow";

                        fittings.Add(new BirooniAiElement
                        {
                            Type = "fitting",
                            FittingType = fitStr,
                            Center = new[] { juncPt.X, juncPt.Y, juncPt.Z },
                            DiameterMm = Math.Max(pA.DiameterMm, pB.DiameterMm)
                        });
                    }
                    else if (SnapTeeBranch(pA, pB, snapTol, out XYZ teePt))
                    {
                        var ports = new List<(XYZ dir, double diaMm, double? widthMm, double? heightMm)>
                        {
                            (pA.Axis, pA.DiameterMm, null, null),
                            (-pA.Axis, pA.DiameterMm, null, null),
                            (pB.Axis, pB.DiameterMm, null, null)
                        };

                        var classified = NeuralFittingClassifier.ClassifyJunction("piping", teePt, ports);
                        string fitStr = classified?.Kind.ToString().ToLowerInvariant() ?? "tee";

                        fittings.Add(new BirooniAiElement
                        {
                            Type = "fitting",
                            FittingType = fitStr,
                            Center = new[] { teePt.X, teePt.Y, teePt.Z },
                            DiameterMm = Math.Max(pA.DiameterMm, pB.DiameterMm)
                        });
                    }
                }
            }

            for (int i = 0; i < pipes.Count; i++)
            {
                elements[i].Start = new[] { pipes[i].Start.X, pipes[i].Start.Y, pipes[i].Start.Z };
                elements[i].End = new[] { pipes[i].End.X, pipes[i].End.Y, pipes[i].End.Z };
            }

            elements.AddRange(fittings);
            return elements;
        }

        private static bool SnapCorners(PipeEdge a, PipeEdge b, double tol, out XYZ junc)
        {
            junc = null;
            double d1 = a.Start.DistanceTo(b.Start);
            double d2 = a.Start.DistanceTo(b.End);
            double d3 = a.End.DistanceTo(b.Start);
            double d4 = a.End.DistanceTo(b.End);

            double minD = Math.Min(Math.Min(d1, d2), Math.Min(d3, d4));
            if (minD > tol) return false;

            XYZ intPt = RayRayIntersection(a.Start, a.Axis, b.Start, b.Axis);
            if (intPt == null)
            {
                if (minD == d1) intPt = (a.Start + b.Start) * 0.5;
                else if (minD == d2) intPt = (a.Start + b.End) * 0.5;
                else if (minD == d3) intPt = (a.End + b.Start) * 0.5;
                else intPt = (a.End + b.End) * 0.5;
            }

            if (minD == d1) { a.Start = intPt; b.Start = intPt; }
            else if (minD == d2) { a.Start = intPt; b.End = intPt; }
            else if (minD == d3) { a.End = intPt; b.Start = intPt; }
            else { a.End = intPt; b.End = intPt; }

            junc = intPt;
            return true;
        }

        private static bool SnapTeeBranch(PipeEdge header, PipeEdge branch, double tol, out XYZ teePt)
        {
            teePt = null;
            XYZ projStart = ProjectPointOnSegment(branch.Start, header.Start, header.End);
            XYZ projEnd = ProjectPointOnSegment(branch.End, header.Start, header.End);

            double dStart = branch.Start.DistanceTo(projStart);
            double dEnd = branch.End.DistanceTo(projEnd);

            if (dStart <= tol && dStart < dEnd)
            {
                branch.Start = projStart;
                teePt = projStart;
                return true;
            }
            if (dEnd <= tol && dEnd < dStart)
            {
                branch.End = projEnd;
                teePt = projEnd;
                return true;
            }

            return false;
        }

        private static XYZ RayRayIntersection(XYZ p1, XYZ v1, XYZ p2, XYZ v2)
        {
            XYZ w0 = p1 - p2;
            double a = v1.DotProduct(v1);
            double b = v1.DotProduct(v2);
            double c = v2.DotProduct(v2);
            double d = v1.DotProduct(w0);
            double e = v2.DotProduct(w0);

            double denom = a * c - b * b;
            if (Math.Abs(denom) < 1e-5) return null;

            double sc = (b * e - c * d) / denom;
            double tc = (a * e - b * d) / denom;

            XYZ pA = p1 + v1.Multiply(sc);
            XYZ pB = p2 + v2.Multiply(tc);

            if (pA.DistanceTo(pB) < 1.0)
                return (pA + pB) * 0.5;

            return null;
        }

        private static XYZ ProjectPointOnSegment(XYZ pt, XYZ a, XYZ b)
        {
            XYZ ab = b - a;
            double lenSq = ab.DotProduct(ab);
            if (lenSq < 1e-6) return a;
            double t = Math.Max(0.0, Math.Min(1.0, (pt - a).DotProduct(ab) / lenSq));
            return a + ab.Multiply(t);
        }
    }
}
