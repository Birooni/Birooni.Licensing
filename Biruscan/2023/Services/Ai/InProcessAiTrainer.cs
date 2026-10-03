using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Biruscan.AI.DeepLearning;

namespace Biruscan.Services.Ai
{
    public sealed class InProcessTrainResult
    {
        public bool Trained { get; set; }
        public string SampleId { get; set; } = "";
        public int NPoints { get; set; }
        public int NElements { get; set; }
        public int LabeledClusters { get; set; }
        public int Updates { get; set; }
        public string WeightsPath { get; set; } = "";
        public string Message { get; set; } = "";
    }

    /// <summary>
    /// Captures a training sample to disk and fine-tunes the in-process C# MLP.
    /// </summary>
    public static class InProcessAiTrainer
    {
        public static string TrainingRoot() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Birooni", "Training");

        public static int CountSamples()
        {
            try
            {
                string root = TrainingRoot();
                if (!Directory.Exists(root)) return 0;
                return Directory.GetFiles(root, "*.json", SearchOption.TopDirectoryOnly).Length;
            }
            catch { return 0; }
        }

        public static InProcessTrainResult TrainFromSample(BirooniTrainSampleRequest sample, bool runFineTune, int epochs = 6)
        {
            var result = new InProcessTrainResult();
            if (sample == null || sample.Points == null || sample.Points.Count < 8)
            {
                result.Message = "Sample has too few points.";
                return result;
            }

            result.NPoints = sample.Points.Count;
            result.NElements = sample.Elements?.Count ?? 0;
            result.SampleId = SaveSample(sample);

            if (!runFineTune)
            {
                result.Message = "Sample saved. Training not triggered.";
                return result;
            }

            var worldPts = sample.Points.Select(p => new XYZ(p[0], p[1], p[2])).ToList();
            var labeled = new List<(List<XYZ> Points, MepSemanticClass Class, float DiameterMm, XYZ Offset)>();
            var claimed = new bool[worldPts.Count];

            if (sample.Elements != null)
            {
                foreach (var el in sample.Elements)
                {
                    MepSemanticClass cls = MapKind(el.Kind, sample.Service);
                    List<XYZ> near;
                    XYZ targetCenter;

                    if (el.Start != null && el.End != null && el.Start.Length >= 3 && el.End.Length >= 3)
                    {
                        XYZ a = new XYZ(el.Start[0], el.Start[1], el.Start[2]);
                        XYZ b = new XYZ(el.End[0], el.End[1], el.End[2]);
                        double rad = Math.Max(0.28, (el.DiameterMm > 0 ? el.DiameterMm : 50.0) / 304.8 * 0.5 + 0.18);
                        near = PointsNearSegment(worldPts, a, b, rad, claimed);
                        targetCenter = (a + b) * 0.5;
                    }
                    else if (el.Center != null && el.Center.Length >= 3)
                    {
                        XYZ c = new XYZ(el.Center[0], el.Center[1], el.Center[2]);
                        double rad = Math.Max(0.35, (el.DiameterMm > 0 ? el.DiameterMm : 50.0) / 304.8 + 0.20);
                        near = PointsNearPoint(worldPts, c, rad, claimed);
                        targetCenter = c;
                    }
                    else continue;

                    if (near.Count < 4) continue;
                    XYZ avg = Average(near);
                    labeled.Add((near, cls, (float)(el.DiameterMm > 0 ? el.DiameterMm : 50.0), targetCenter - avg));

                    // Edgewise surface ring: teach the wall of the cylinder, not the filled tube.
                    if (el.Start != null && el.End != null && el.Start.Length >= 3 && el.End.Length >= 3)
                    {
                        XYZ a = new XYZ(el.Start[0], el.Start[1], el.Start[2]);
                        XYZ b = new XYZ(el.End[0], el.End[1], el.End[2]);
                        double rad = Math.Max(0.28, (el.DiameterMm > 0 ? el.DiameterMm : 50.0) / 304.8 * 0.5 + 0.18);
                        var shell = PointsNearSegmentShell(worldPts, a, b, rad * 0.55, rad, claimed);
                        if (shell.Count >= 6)
                        {
                            XYZ sAvg = Average(shell);
                            labeled.Add((shell, cls, (float)(el.DiameterMm > 0 ? el.DiameterMm : 50.0), targetCenter - sAvg));
                        }
                    }
                }
            }

            // Unclaimed planar leftovers → wall/clutter negatives.
            var leftovers = new List<XYZ>();
            for (int i = 0; i < worldPts.Count; i++)
                if (!claimed[i]) leftovers.Add(worldPts[i]);

            if (leftovers.Count >= 20)
            {
                var leftoverClusters = Biruscan.AI.Engine.AiPointNetEngine.SpatialCluster(
                    Biruscan.AI.Engine.AiPointNetEngine.VoxelDownsample(leftovers, 0.06), 0.45);
                int neg = 0;
                var engine = new MepPointNetNeuralEngine();
                foreach (var cluster in leftoverClusters)
                {
                    if (neg >= 8) break;
                    if (cluster.Count < 8) continue;
                    if (!LooksPlanar(cluster)) continue;
                    engine.TrainNegativeSample(cluster, MepSemanticClass.WallStructureClutter);
                    neg++;
                }
            }

            var trainer = new MepPointNetNeuralEngine();
            result.LabeledClusters = labeled.Count;
            result.Updates = trainer.TrainLabeledClusters(labeled, epochs, 0.03f);
            NeuralWeightsDataset.SaveCurrentWeights();
            result.Trained = result.Updates > 0;
            result.WeightsPath = NeuralWeightsDataset.GetDefaultWeightsPath();
            result.Message = result.Trained
                ? $"Fine-tuned {result.Updates} cluster-updates from {result.LabeledClusters} labeled run(s)."
                : "Sample saved but no labeled clusters were near the point cloud.";
            return result;
        }

        public static string SaveSample(BirooniTrainSampleRequest sample)
        {
            string root = TrainingRoot();
            Directory.CreateDirectory(root);
            string id = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string path = Path.Combine(root, id + ".json");
            try
            {
                var opts = new JsonSerializerOptions { WriteIndented = false };
                File.WriteAllText(path, JsonSerializer.Serialize(sample, opts));
            }
            catch { }
            return id;
        }

        private static MepSemanticClass MapKind(string kind, string service)
        {
            string k = (kind ?? "").ToLowerInvariant();
            string s = (service ?? "").ToLowerInvariant();
            if (k.Contains("elbow")) return MepSemanticClass.PipingElbow;
            if (k.Contains("tee") || k.Contains("cross") || k.Contains("wye")) return MepSemanticClass.PipingTee;
            if (k.Contains("reducer") || k.Contains("union") || k.Contains("coupling") || k.Contains("transition"))
                return MepSemanticClass.PipingCylinder;
            if (k.Contains("conduit") || s.Contains("conduit")) return MepSemanticClass.PipingCylinder;
            if (k.Contains("duct") || s.Contains("duct")) return MepSemanticClass.Duct;
            if (k.Contains("tray") || k.Contains("cable") || s.Contains("tray") || s.Contains("cable"))
                return MepSemanticClass.CableTray;
            if (k.Contains("wall")) return MepSemanticClass.WallStructureClutter;
            return MepSemanticClass.PipingCylinder;
        }

        private static List<XYZ> PointsNearSegmentShell(List<XYZ> pts, XYZ a, XYZ b, double rInner, double rOuter, bool[] claimed)
        {
            var near = new List<XYZ>();
            XYZ ab = b - a;
            double len2 = ab.DotProduct(ab);
            double rOut2 = rOuter * rOuter;
            double rIn2 = rInner * rInner;
            for (int i = 0; i < pts.Count; i++)
            {
                XYZ p = pts[i];
                double t = len2 < 1e-12 ? 0 : (p - a).DotProduct(ab) / len2;
                if (t < 0) t = 0; else if (t > 1) t = 1;
                XYZ proj = a + ab * t;
                double d2 = (p - proj).DotProduct(p - proj);
                if (d2 <= rOut2 && d2 >= rIn2)
                    near.Add(p);
            }
            return near;
        }

        private static List<XYZ> PointsNearSegment(List<XYZ> pts, XYZ a, XYZ b, double radius, bool[] claimed)
        {
            var near = new List<XYZ>();
            XYZ ab = b - a;
            double len2 = ab.DotProduct(ab);
            double r2 = radius * radius;
            for (int i = 0; i < pts.Count; i++)
            {
                XYZ p = pts[i];
                double t = len2 < 1e-12 ? 0 : (p - a).DotProduct(ab) / len2;
                if (t < 0) t = 0; else if (t > 1) t = 1;
                XYZ proj = a + ab * t;
                if ((p - proj).DotProduct(p - proj) <= r2)
                {
                    near.Add(p);
                    claimed[i] = true;
                }
            }
            return near;
        }

        private static List<XYZ> PointsNearPoint(List<XYZ> pts, XYZ c, double radius, bool[] claimed)
        {
            var near = new List<XYZ>();
            double r2 = radius * radius;
            for (int i = 0; i < pts.Count; i++)
            {
                XYZ p = pts[i];
                if (p.DistanceTo(c) * p.DistanceTo(c) <= r2)
                {
                    near.Add(p);
                    claimed[i] = true;
                }
            }
            return near;
        }

        private static XYZ Average(List<XYZ> pts)
        {
            double x = 0, y = 0, z = 0;
            foreach (var p in pts) { x += p.X; y += p.Y; z += p.Z; }
            return new XYZ(x / pts.Count, y / pts.Count, z / pts.Count);
        }

        private static bool LooksPlanar(List<XYZ> cluster)
        {
            XYZ c = Average(cluster);
            double cxx = 0, cyy = 0, czz = 0, cxy = 0, cxz = 0, cyz = 0;
            foreach (var p in cluster)
            {
                double dx = p.X - c.X, dy = p.Y - c.Y, dz = p.Z - c.Z;
                cxx += dx * dx; cyy += dy * dy; czz += dz * dz;
                cxy += dx * dy; cxz += dx * dz; cyz += dy * dz;
            }
            double[,] A = { { cxx, cxy, cxz }, { cxy, cyy, cyz }, { cxz, cyz, czz } };
            Biruscan.AI.Engine.AiCylinderFit.Jacobi(A, out double[] rawEv, out _);
            double[] evs = rawEv.Select(v => Math.Max(1e-9, Math.Abs(v))).OrderByDescending(v => v).ToArray();
            double l1 = evs[0], l2 = evs[1], l3 = evs[2];
            double linearity = (l1 - l2) / Math.Max(l1, 1e-6);
            double planarity = (l2 - l3) / Math.Max(l1, 1e-6);
            return planarity > 0.45 && planarity > linearity * 1.2;
        }
    }
}
