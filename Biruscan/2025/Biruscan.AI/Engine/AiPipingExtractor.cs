using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Biruscan.AI.Contracts;
using Biruscan.AI.Reasoning;

namespace Biruscan.AI.Engine
{
    public class AiPipingExtractor
    {
        public List<BirooniAiElement> ExtractPiping(IList<XYZ> rawPoints, double? diameterOverrideMm)
        {
            var elements = new List<BirooniAiElement>();
            if (rawPoints == null || rawPoints.Count < 20) return elements;

            // Neural filter is applied by BiruscanAiInferenceService before this call.
            var extractor = new AiGeometricMepFitService();
            elements = extractor.ExtractPipingRuns(rawPoints, diameterOverrideMm);

            return elements;
        }
    }
}




