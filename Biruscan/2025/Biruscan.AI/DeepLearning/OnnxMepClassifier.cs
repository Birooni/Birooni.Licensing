using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Autodesk.Revit.DB;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Biruscan.AI.DeepLearning
{
    /// <summary>
    /// Loads BiruscanMepWeights.onnx (4→16→16→4 per-point MLP) and uses it as the
    /// pretrained Standard-AI filter. Missing/unloadable ONNX is not an error.
    /// </summary>
    public static class OnnxMepClassifier
    {
        private static InferenceSession _session;
        private static readonly object _lock = new object();

        public static bool IsAvailable { get; private set; }

        public static string ModelPath
        {
            get
            {
                try
                {
                    string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                    return Path.Combine(dir, "BiruscanMepWeights.onnx");
                }
                catch
                {
                    return "BiruscanMepWeights.onnx";
                }
            }
        }

        public static void TryLoad()
        {
            lock (_lock)
            {
                if (_session != null)
                {
                    IsAvailable = true;
                    return;
                }

                string path = ModelPath;
                if (!File.Exists(path))
                {
                    IsAvailable = false;
                    return;
                }

                try
                {
                    var opts = new SessionOptions
                    {
                        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC,
                        IntraOpNumThreads = 2
                    };
                    _session = new InferenceSession(path, opts);
                    IsAvailable = true;
                }
                catch
                {
                    _session = null;
                    IsAvailable = false;
                }
            }
        }

        /// <summary>
        /// Keep points whose ONNX class matches the requested service.
        /// Returns an empty list when the model is unavailable or the keep-ratio is implausible.
        /// </summary>
        public static List<XYZ> FilterForService(IList<XYZ> points, string service, int maxPoints = 12000)
        {
            var empty = new List<XYZ>();
            if (!IsAvailable || _session == null || points == null || points.Count < 8)
                return empty;

            IList<XYZ> work = points;
            if (work.Count > maxPoints)
                work = Biruscan.AI.Engine.AiPointNetEngine.VoxelDownsample(points, 0.05);

            int n = work.Count;
            if (n < 8) return empty;

            XYZ c = Centroid(work);
            double maxR = 1e-6;
            for (int i = 0; i < n; i++)
            {
                double d = work[i].DistanceTo(c);
                if (d > maxR) maxR = d;
            }

            var tensor = new DenseTensor<float>(new[] { 1, n, 4 });
            for (int i = 0; i < n; i++)
            {
                XYZ p = work[i];
                tensor[0, i, 0] = (float)((p.X - c.X) / maxR);
                tensor[0, i, 1] = (float)((p.Y - c.Y) / maxR);
                tensor[0, i, 2] = (float)((p.Z - c.Z) / maxR);
                tensor[0, i, 3] = 1.0f;
            }

            float[] logits;
            lock (_lock)
            {
                if (_session == null) return empty;
                try
                {
                    var inputs = new List<NamedOnnxValue>
                    {
                        NamedOnnxValue.CreateFromTensor("input", tensor)
                    };
                    using var results = _session.Run(inputs);
                    var output = results[0].AsTensor<float>();
                    logits = new float[n * 4];
                    for (int i = 0; i < n; i++)
                    {
                        logits[i * 4 + 0] = output[0, i, 0];
                        logits[i * 4 + 1] = output[0, i, 1];
                        logits[i * 4 + 2] = output[0, i, 2];
                        logits[i * 4 + 3] = output[0, i, 3];
                    }
                }
                catch
                {
                    return empty;
                }
            }

            string s = service?.ToLowerInvariant() ?? "piping";
            var kept = new List<XYZ>(n);
            for (int i = 0; i < n; i++)
            {
                int cls = ArgMax4(logits, i * 4);
                if (KeepClass(cls, s))
                    kept.Add(work[i]);
            }

            double ratio = kept.Count / (double)n;
            if (ratio < 0.06 || ratio > 0.94)
                return empty;

            return kept;
        }

        private static bool KeepClass(int cls, string service)
        {
            // 0 CylinderSurface, 1 PlanarSurface, 2 FittingJunction, 3 Clutter
            if (cls == 3) return false;
            if (service.Contains("duct") || service.Contains("tray") || service.Contains("cable"))
                return cls == 0 || cls == 1 || cls == 2;
            return cls == 0 || cls == 2;
        }

        private static int ArgMax4(float[] v, int offset)
        {
            int best = 0;
            float bestV = v[offset];
            for (int k = 1; k < 4; k++)
            {
                if (v[offset + k] > bestV)
                {
                    bestV = v[offset + k];
                    best = k;
                }
            }
            return best;
        }

        private static XYZ Centroid(IList<XYZ> pts)
        {
            double x = 0, y = 0, z = 0;
            foreach (var p in pts) { x += p.X; y += p.Y; z += p.Z; }
            return new XYZ(x / pts.Count, y / pts.Count, z / pts.Count);
        }
    }
}
