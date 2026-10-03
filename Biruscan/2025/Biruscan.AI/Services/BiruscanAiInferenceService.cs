using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Biruscan.AI.Contracts;
using Biruscan.AI.DeepLearning;
using Biruscan.AI.Engine;

namespace Biruscan.AI.Services
{
    /// <summary>
    /// In-process AI inference. Pipelines actually differ:
    ///   standard     — pretrained ONNX (if present) + base-weight MLP + extract
    ///   trained      — fine-tuned live weights + geometric fallback + extract
    ///   trained_pure — fine-tuned live weights only (no geometric keep)
    ///   combined     — trained extract ∪ standard extract
    /// </summary>
    public class BiruscanAiInferenceService
    {
        static BiruscanAiInferenceService()
        {
            try
            {
                NeuralWeightsDataset.ReloadFromDisk();
                string dllDir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                string weightsPath = System.IO.Path.Combine(dllDir ?? "", "BiruscanMepWeights.dat");
                if (!System.IO.File.Exists(weightsPath))
                    NeuralWeightsDataset.GenerateAndSaveDataset(weightsPath);
                BirooniDeepNeuralNet.LoadPreTrainedModel(weightsPath);
            }
            catch { }
        }

        public static BirooniAiResponse InferMep(BirooniAiRequest request)
        {
            if (request == null || request.Points == null || request.Points.Count == 0)
            {
                return new BirooniAiResponse
                {
                    Status = "error",
                    Message = "No points provided in request."
                };
            }

            List<XYZ> worldPts = request.Points
                .Select(p => new XYZ(p[0], p[1], p[2]))
                .ToList();

            string serviceKey = request.Service?.ToLowerInvariant() ?? "piping";
            string pipeline = (request.Pipeline ?? "standard").Trim().ToLowerInvariant();
            bool useFineTuned = pipeline.Contains("trained") || pipeline.Contains("combined") || pipeline.Contains("pure");
            bool pureDl = pipeline.Contains("pure");
            bool combined = pipeline.Contains("combined");

            Engine.MepSemanticClass target =
                serviceKey.Contains("duct") ? Engine.MepSemanticClass.Duct
                : (serviceKey.Contains("tray") || serviceKey.Contains("cable")) ? Engine.MepSemanticClass.CableTray
                : Engine.MepSemanticClass.Pipe;

            if (worldPts.Count > 16000)
            {
                var slim = new List<XYZ>(16000);
                double step = (double)worldPts.Count / 16000.0;
                for (double i = 0; i < worldPts.Count; i += step)
                    slim.Add(worldPts[(int)i]);
                worldPts = slim;
            }

            List<XYZ> filtered;
            List<BirooniAiElement> elements;
            try
            {
                filtered = FilterForPipeline(worldPts, serviceKey, target, useFineTuned, pureDl);
                if (combined)
                {
                    List<XYZ> stdPts = FilterForPipeline(worldPts, serviceKey, target, useFineTuned: false, pureDl: false);
                    if (stdPts != null && stdPts.Count >= 8)
                        filtered = UnionPoints(filtered ?? new List<XYZ>(), stdPts);
                }

                if (filtered == null || filtered.Count < 12)
                {
                    return new BirooniAiResponse
                    {
                        Status = "ok",
                        Service = request.Service,
                        Message = "No points matched the selected service after noise/wall/other-element filtering.",
                        Elements = new List<BirooniAiElement>()
                    };
                }

                elements = Extract(filtered, serviceKey, request.DiameterOverrideMm) ?? new List<BirooniAiElement>();
            }
            catch (Exception ex)
            {
                return new BirooniAiResponse
                {
                    Status = "error",
                    Service = request.Service,
                    Message = ex.Message,
                    Elements = new List<BirooniAiElement>()
                };
            }

            string modeNote = combined ? "combined"
                : pureDl ? "trained-pure"
                : useFineTuned ? "trained"
                : (OnnxMepClassifier.IsAvailable ? "standard-onnx+pretrained" : "standard-pretrained");

            return new BirooniAiResponse
            {
                Status = "ok",
                Service = request.Service,
                Message = $"AI ({modeNote}) extracted {elements.Count} element(s) from {filtered.Count} filtered / {worldPts.Count} raw points.",
                Elements = elements
            };
        }

        private static List<XYZ> FilterForPipeline(List<XYZ> worldPts, string serviceKey, Engine.MepSemanticClass target, bool useFineTuned, bool pureDl)
        {
            return BirooniDeepNeuralNet.FilterNoiseAndIsolateService(worldPts, target, useFineTuned, allowGeometricFallback: !pureDl);
        }

        private static List<BirooniAiElement> Extract(List<XYZ> filtered, string serviceKey, double? diameterOverrideMm)
        {
            if (filtered == null || filtered.Count < 8)
                return new List<BirooniAiElement>();

            if (serviceKey.Contains("duct"))
                return new AiDuctExtractor().ExtractDucts(filtered, null, null);
            if (serviceKey.Contains("tray") || serviceKey.Contains("cable"))
                return new AiCableTrayExtractor().ExtractCableTrays(filtered, null, null);
            if (serviceKey.Contains("conduit"))
            {
                var elements = new AiPipingExtractor().ExtractPiping(filtered, diameterOverrideMm);
                foreach (var e in elements) e.Type = "conduit";
                return elements;
            }
            return new AiPipingExtractor().ExtractPiping(filtered, diameterOverrideMm);
        }

        private static List<XYZ> UnionPoints(List<XYZ> a, List<XYZ> b)
        {
            if (a == null || a.Count == 0) return b ?? new List<XYZ>();
            if (b == null || b.Count == 0) return a;
            const double cell = 0.05;
            var seen = new HashSet<long>(a.Count + b.Count);
            var union = new List<XYZ>(a.Count + b.Count);
            foreach (XYZ p in a)
            {
                long key = ((long)Math.Floor(p.X / cell) * 73856093) ^ ((long)Math.Floor(p.Y / cell) * 19349663) ^ ((long)Math.Floor(p.Z / cell) * 83492791);
                if (seen.Add(key)) union.Add(p);
            }
            foreach (XYZ p in b)
            {
                long key = ((long)Math.Floor(p.X / cell) * 73856093) ^ ((long)Math.Floor(p.Y / cell) * 19349663) ^ ((long)Math.Floor(p.Z / cell) * 83492791);
                if (seen.Add(key)) union.Add(p);
            }
            return union;
        }

        private static void MergeElements(List<BirooniAiElement> dest, List<BirooniAiElement> extra)
        {
            if (dest == null || extra == null || extra.Count == 0) return;
            foreach (var geom in extra)
            {
                if (geom.Start == null || geom.End == null || geom.Start.Length != 3 || geom.End.Length != 3) continue;
                XYZ gMid = new XYZ(
                    (geom.Start[0] + geom.End[0]) * 0.5,
                    (geom.Start[1] + geom.End[1]) * 0.5,
                    (geom.Start[2] + geom.End[2]) * 0.5);

                bool dup = false;
                foreach (var baseEl in dest)
                {
                    if (baseEl.Start == null || baseEl.End == null || baseEl.Start.Length != 3 || baseEl.End.Length != 3) continue;
                    XYZ bMid = new XYZ(
                        (baseEl.Start[0] + baseEl.End[0]) * 0.5,
                        (baseEl.Start[1] + baseEl.End[1]) * 0.5,
                        (baseEl.Start[2] + baseEl.End[2]) * 0.5);
                    if (gMid.DistanceTo(bMid) < 0.35) { dup = true; break; }
                }
                if (!dup) dest.Add(geom);
            }
        }
    }
}
