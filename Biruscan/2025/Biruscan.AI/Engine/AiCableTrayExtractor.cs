using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Biruscan.AI.Contracts;

namespace Biruscan.AI.Engine
{
    public class AiCableTrayExtractor
    {
        public List<BirooniAiElement> ExtractCableTrays(IList<XYZ> points, double? widthOverrideMm, double? heightOverrideMm)
        {
            var elements = new List<BirooniAiElement>();
            if (points == null || points.Count < 15) return elements;

            List<XYZ> sampled = AiPointNetEngine.VoxelDownsample(points, 0.05);
            List<List<XYZ>> clusters = AiPointNetEngine.SpatialCluster(sampled, 0.7);

            foreach (var cluster in clusters)
            {
                if (cluster.Count < 10) continue;

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

                double detectedWidthMm = Math.Max(lenV, lenW) * 304.8;
                double detectedHeightMm = Math.Min(lenV, lenW) * 304.8;

                if (lenU >= 0.4)
                {
                    elements.Add(new BirooniAiElement
                    {
                        Type = "cable_tray",
                        Start = new[] { start.X, start.Y, start.Z },
                        End = new[] { end.X, end.Y, end.Z },
                        WidthMm = widthOverrideMm ?? RoundToStandardTray(detectedWidthMm),
                        HeightMm = heightOverrideMm ?? 50.0,
                        Confidence = 0.85
                    });
                }
            }

            return elements;
        }

        private static double RoundToStandardTray(double valMm)
        {
            double[] std = { 100, 150, 200, 300, 400, 500, 600, 750, 900 };
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
            double maxDist = 0;
            XYZ bestVec = XYZ.BasisX;
            foreach (var p in pts)
            {
                double dist = p.DistanceTo(center);
                if (dist > maxDist)
                {
                    maxDist = dist;
                    bestVec = (p - center).Normalize();
                }
            }

            u = bestVec;
            v = new XYZ(-u.Y, u.X, 0).Normalize();
            if (v.GetLength() < 0.1) v = XYZ.BasisY;
            w = u.CrossProduct(v).Normalize();
        }
    }
}
