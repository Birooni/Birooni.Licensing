using System;
using System.Collections.Generic;

namespace Biruscan.Models
{
    public enum ActiveLearningType
    {
        /// <summary>Element created by Fitter/MEP tool and kept by user (positive training sample).</summary>
        PositiveFit = 0,

        /// <summary>Element manually edited/adjusted by user after fitting (learn from mistakes / residual correction).</summary>
        UserCorrection = 1,

        /// <summary>Element deleted or undone by user shortly after fitting (negative rejection sample).</summary>
        RejectedDeletion = 2
    }

    /// <summary>
    /// Geometric snapshot of an MEP or structural element at a specific point in time.
    /// </summary>
    public class ElementGeometrySnapshot
    {
        public double[] StartPoint { get; set; }
        public double[] EndPoint { get; set; }
        public double[] CenterPoint { get; set; }
        public double[] DirectionVector { get; set; }
        public double DiameterMm { get; set; }
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }
        public string TypeName { get; set; }
        public string SystemTypeName { get; set; }
        public double ElevationFeet { get; set; }

        public ElementGeometrySnapshot Clone()
        {
            return new ElementGeometrySnapshot
            {
                StartPoint = StartPoint != null ? (double[])StartPoint.Clone() : null,
                EndPoint = EndPoint != null ? (double[])EndPoint.Clone() : null,
                CenterPoint = CenterPoint != null ? (double[])CenterPoint.Clone() : null,
                DirectionVector = DirectionVector != null ? (double[])DirectionVector.Clone() : null,
                DiameterMm = DiameterMm,
                WidthMm = WidthMm,
                HeightMm = HeightMm,
                TypeName = TypeName,
                SystemTypeName = SystemTypeName,
                ElevationFeet = ElevationFeet
            };
        }
    }

    /// <summary>
    /// Represents a discrete active learning observation captured from Fitter and MEP operations.
    /// </summary>
    public class AutoLearningSample
    {
        public string SampleId { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public ActiveLearningType LearningType { get; set; }
        public string Service { get; set; } = "Piping";
        public string SourceTool { get; set; } = "Pipe Fitter";
        public long ElementIdValue { get; set; }
        public string ElementCategory { get; set; }
        public string Description { get; set; }

        /// <summary>The initial geometry fitted by Biruscan.</summary>
        public ElementGeometrySnapshot InitialGeometry { get; set; } = new ElementGeometrySnapshot();

        /// <summary>The corrected geometry if the user manually modified the element.</summary>
        public ElementGeometrySnapshot CorrectedGeometry { get; set; }

        /// <summary>Associated point cloud ROI points (in feet) if captured during the fitting operation.</summary>
        public List<double[]> RoiPoints { get; set; } = new List<double[]>();

        /// <summary>Whether this sample has been integrated into the retrained neural weights.</summary>
        public bool IsTrained { get; set; }

        public string GetSummary()
        {
            switch (LearningType)
            {
                case ActiveLearningType.PositiveFit:
                    return $"[Accepted Fit] {SourceTool} ({Service}) - Elem #{ElementIdValue}: {Description}";
                case ActiveLearningType.UserCorrection:
                    return $"[Learned Correction / Mistake] {SourceTool} - Elem #{ElementIdValue}: {Description}";
                case ActiveLearningType.RejectedDeletion:
                    return $"[Negative / Rejection] {SourceTool} - Elem #{ElementIdValue} deleted by user: {Description}";
                default:
                    return $"[Sample] {SourceTool} - Elem #{ElementIdValue}";
            }
        }
    }
}
