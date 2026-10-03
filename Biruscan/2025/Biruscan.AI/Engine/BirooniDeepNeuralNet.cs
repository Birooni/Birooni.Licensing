using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Biruscan.AI.DeepLearning;

namespace Biruscan.AI.Engine
{
    public enum MepSemanticClass
    {
        Pipe = 0,
        Duct = 1,
        CableTray = 2,
        NoiseWall = 3
    }

    /// <summary>
    /// Deep Neural Network point cloud semantic classifier & noise filter.
    /// Uses the 500MB pre-trained NeuralWeightsDataset and MepPointNetNeuralEngine
    /// to isolate MEP piping geometry and filter architectural wall/floor/ceiling clutter.
    /// </summary>
    public static class BirooniDeepNeuralNet
    {
        private static readonly MepPointNetNeuralEngine _pointNet = new MepPointNetNeuralEngine();

        public static void LoadPreTrainedModel(string weightsFilePath)
        {
            NeuralWeightsDataset.ReloadFromDisk();
            if (!string.IsNullOrWhiteSpace(weightsFilePath) && File.Exists(weightsFilePath))
                NeuralWeightsDataset.LoadFromPath(weightsFilePath, asLive: true);
            OnnxMepClassifier.TryLoad();
        }

        public static List<XYZ> FilterNoiseAndIsolateService(IList<XYZ> rawPoints, MepSemanticClass targetService)
            => FilterNoiseAndIsolateService(rawPoints, targetService, useFineTuned: true);

        public static List<XYZ> FilterNoiseAndIsolateService(IList<XYZ> rawPoints, MepSemanticClass targetService, bool useFineTuned)
            => FilterNoiseAndIsolateService(rawPoints, targetService, useFineTuned, allowGeometricFallback: true);

        public static List<XYZ> FilterNoiseAndIsolateService(IList<XYZ> rawPoints, MepSemanticClass targetService, bool useFineTuned, bool allowGeometricFallback)
        {
            if (rawPoints == null || rawPoints.Count == 0) return new List<XYZ>();

            string service = targetService == MepSemanticClass.Duct ? "duct"
                : targetService == MepSemanticClass.CableTray ? "tray"
                : "piping";

            return MepPointNetNeuralEngine.FilterPointsForService(rawPoints, service, useFineTuned, allowGeometricFallback);
        }
    }
}

