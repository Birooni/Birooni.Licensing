using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Biruscan.Services.Ai
{
    /// <summary>
    /// In-process AI placement and training sample types.
    /// </summary>
    public sealed class BirooniAiElement
    {
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = string.Empty;

        [JsonPropertyName("start")]
        public double[] Start { get; set; }

        [JsonPropertyName("end")]
        public double[] End { get; set; }

        [JsonPropertyName("center")]
        public double[] Center { get; set; }

        [JsonPropertyName("diameter_mm")]
        public double? DiameterMm { get; set; }

        [JsonPropertyName("width_mm")]
        public double? WidthMm { get; set; }

        [JsonPropertyName("height_mm")]
        public double? HeightMm { get; set; }

        [JsonPropertyName("angle_degrees")]
        public double? AngleDegrees { get; set; }

        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }

        [JsonPropertyName("direction")]
        public string Direction { get; set; }
    }

    public sealed class BirooniTrainingElement
    {
        [JsonPropertyName("kind")]
        public string Kind { get; set; } = "background";

        [JsonPropertyName("start")]
        public double[] Start { get; set; }

        [JsonPropertyName("end")]
        public double[] End { get; set; }

        [JsonPropertyName("center")]
        public double[] Center { get; set; }

        [JsonPropertyName("diameter_mm")]
        public double DiameterMm { get; set; }
    }

    public sealed class BirooniTrainSampleRequest
    {
        [JsonPropertyName("project_name")]
        public string ProjectName { get; set; } = "default";

        [JsonPropertyName("revit_doc_hash")]
        public string RevitDocHash { get; set; } = string.Empty;

        [JsonPropertyName("service")]
        public string Service { get; set; } = "Piping";

        [JsonPropertyName("units_in")]
        public string UnitsIn { get; set; } = "feet";

        [JsonPropertyName("points")]
        public List<double[]> Points { get; set; } = new();

        [JsonPropertyName("elements")]
        public List<BirooniTrainingElement> Elements { get; set; } = new();

        [JsonPropertyName("primary_type_name")]
        public string PrimaryTypeName { get; set; }

        [JsonPropertyName("system_type_name")]
        public string SystemTypeName { get; set; }

        [JsonPropertyName("level_name")]
        public string LevelName { get; set; }

        [JsonPropertyName("insulation_present")]
        public bool? InsulationPresent { get; set; }
    }
}
