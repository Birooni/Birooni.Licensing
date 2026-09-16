using System.Text.Json.Serialization;
using Autodesk.Revit.DB;

namespace BiruBox.Core;

public class AutoSectionSettings
{
    [JsonPropertyName("offsetX")]
    public double OffsetX { get; set; } = -1;

    [JsonPropertyName("offsetY")]
    public double OffsetY { get; set; } = -1;

    [JsonPropertyName("offsetZ")]
    public double OffsetZ { get; set; } = -1;

    [JsonPropertyName("alignToLinearElements")]
    public bool AlignToLinearElements { get; set; } = true;

    [JsonPropertyName("targetViewName")]
    public string TargetViewName { get; set; } = string.Empty;

    [JsonPropertyName("viewTemplateName")]
    public string ViewTemplateName { get; set; } = string.Empty;

    [JsonPropertyName("growStep")]
    public double GrowStep { get; set; } = -1;

    public string GetResolvedViewName(string default3DViewName)
    {
        return string.IsNullOrWhiteSpace(TargetViewName) ? default3DViewName : TargetViewName.Trim();
    }

    public void EnsureUnitDefaults(ForgeTypeId lengthUnitId)
    {
        if (OffsetX < 0 || OffsetY < 0 || OffsetZ < 0 || GrowStep < 0)
        {
            // Default approximately 300mm / 1ft equivalent based on unit type
            double defaultValue = 300.0; // Default for mm

            if (lengthUnitId == UnitTypeId.Meters) defaultValue = 0.3;
            else if (lengthUnitId == UnitTypeId.Centimeters) defaultValue = 30.0;
            else if (lengthUnitId == UnitTypeId.Feet) defaultValue = 1.0;
            else if (lengthUnitId == UnitTypeId.Inches) defaultValue = 12.0;
            else if (lengthUnitId == UnitTypeId.FeetFractionalInches) defaultValue = 1.0;

            if (OffsetX < 0) OffsetX = defaultValue;
            if (OffsetY < 0) OffsetY = defaultValue;
            if (OffsetZ < 0) OffsetZ = defaultValue;
            if (GrowStep < 0) GrowStep = defaultValue;
        }
    }
}
