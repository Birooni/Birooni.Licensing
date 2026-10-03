using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Biruscan.AI.Contracts;

namespace Biruscan.AI.Engine
{
    public class AiDuctExtractor
    {
        public List<BirooniAiElement> ExtractDucts(IList<XYZ> points, double? widthOverrideMm, double? heightOverrideMm)
        {
            var elements = new List<BirooniAiElement>();
            if (points == null || points.Count < 15) return elements;

            List<XYZ> sampled = AiPointNetEngine.VoxelDownsample(points, 0.05);
            List<List<XYZ>> clusters = AiPointNetEngine.SpatialCluster(sampled, 0.8);

            foreach (var cluster in clusters)
            {
                if (cluster.Count < 12) continue;

                XYZ center = Centroid(cluster);
                XYZ u, v, w;
                EstimatePcaAxes(cluster, center, out u, out v, out w);

                double minU = double.MaxValue, maxU = double.MinValue;
                double minV = double.MaxValue, maxV = double.MinValue;
                double minW = double.MaxValue, maxW = double.MinValue;

                foreach (var pt in cluster)
                {
                    XYZ d = pt - center;
                    double du = d.DotProduct(u);
                    double dv = d.DotProduct(v);
                    double dw = d.DotProduct(w);

                    if (du < minU) minU = du; if (du > maxU) maxU = du;
                    if (dv < minV) minV = dv; if (dv > maxV) maxV = dv;
                    if (dw < minW) minW = dw; if (dw > maxW) maxW = dw;
                }

                double lenU = maxU - minU;
                double lenV = maxV - minV;
                double lenW = maxW - minW;

                XYZ start = center + u.Multiply(minU);
                XYZ end = center + u.Multiply(maxU);

                double detectedWidthMm = lenV * 304.8;
                double detectedHeightMm = lenW * 304.8;

                if (lenU >= 0.5 && detectedWidthMm >= 100 && detectedHeightMm >= 100)
                {
                    elements.Add(new BirooniAiElement
                    {
                        Type = "duct",
                        Start = new[] { start.X, start.Y, start.Z },
                        End = new[] { end.X, end.Y, end.Z },
                        WidthMm = widthOverrideMm ?? RoundToStandardDuct(detectedWidthMm),
                        HeightMm = heightOverrideMm ?? RoundToStandardDuct(detectedHeightMm),
                        Confidence = 0.88
                    });
                }
            }

            return elements;
        }

        private static double RoundToStandardDuct(double valMm)
        {
            double[] std = { 150, 200, 250, 300, 350, 400, 450, 500, 600, 700, 800, 900, 1000, 1200 };
            return std.OrderBy(s => Math.Abs(s - valMm)).FirstOrDefault();
        }

        private static XYZ Centroid(IList<XYZ> pts)
        {
            double x = 0, y = 0, z = 0;
            foreach (var p in pts) { x += p.X; y += p.Y; z += p.Z; }
            return new XYZ(x / pts.Count, y / pts.Count, z / pts.Count);
        }

                        private static void EstimatePcaAxes(List<XYZ> pts, XYZ center, out XYZ u, out XYZ v, out XYZ w)
        {
            AiCylinderFit.Covariance(pts, center, out double[] ev, out XYZ[] evec);
            
            int maxIdx = 0;
            if (ev[1] > ev[maxIdx]) maxIdx = 1;
            if (ev[2] > ev[maxIdx]) maxIdx = 2;
            u = evec[maxIdx].Normalize();

            v = new XYZ(-u.Y, u.X, 0).Normalize();
            if (v.GetLength() < 0.1) v = XYZ.BasisY;
            w = u.CrossProduct(v).Normalize();
        }
    }
}

