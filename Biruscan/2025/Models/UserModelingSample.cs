using System;
using System.Collections.Generic;

namespace Biruscan.Models
{
    /// <summary>
    /// Represents a manual modeling action performed natively by the user in Revit,
    /// paired with the local point-cloud ROI extracted at that element's location.
    /// Used for Imitation Learning / Behavioral Cloning to teach AI the user's manual modeling habits.
    /// </summary>
    public class UserModelingSample
    {
        public string SampleId { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public long ElementIdValue { get; set; }
        public string CategoryName { get; set; } = "Pipes";
        public string Service { get; set; } = "Piping";
        public string TypeName { get; set; } = string.Empty;
        public string SystemTypeName { get; set; } = string.Empty;
        public string LevelName { get; set; } = string.Empty;

        public double[] StartPoint { get; set; }
        public double[] EndPoint { get; set; }
        public double[] CenterPoint { get; set; }
        public double[] DirectionVector { get; set; }

        public double DiameterMm { get; set; }
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }
        public double LengthFeet { get; set; }
        public bool HasInsulation { get; set; }

        /// <summary>Local point cloud cluster points (in feet) extracted in a cylinder/box around the manual element.</summary>
        public List<double[]> LocalRoiPoints { get; set; } = new List<double[]>();

        public bool IsTrained { get; set; }
        public string ActionDescription { get; set; }

        public string GetSummary()
        {
            if (DiameterMm > 0)
                return $"[Manual {Service}] #{ElementIdValue} '{TypeName}' (Dia={DiameterMm:F0}mm, L={LengthFeet:F1}ft) · {LocalRoiPoints.Count} pts extracted";
            else if (WidthMm > 0 && HeightMm > 0)
                return $"[Manual {Service}] #{ElementIdValue} '{TypeName}' ({WidthMm:F0}x{HeightMm:F0}mm, L={LengthFeet:F1}ft) · {LocalRoiPoints.Count} pts extracted";
            else
                return $"[Manual {Service}] #{ElementIdValue} '{TypeName}' · {LocalRoiPoints.Count} pts extracted";
        }
    }
}
