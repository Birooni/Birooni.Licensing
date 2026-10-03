using Autodesk.Revit.DB;
using System;

namespace Biruscan.Models
{
    /// <summary>
    /// Metadata about a loaded point cloud instance.
    /// </summary>
    public class PointCloudInfo
    {
        public string FileName { get; set; }
        public ElementId PointCloudTypeId { get; set; }
        public ElementId PointCloudInstanceId { get; set; }
        public long PointCount { get; set; }
        public BoundingBoxXYZ BoundingBox { get; set; }
        public double FileSizeMB { get; set; }
        public DateTime LoadTime { get; set; }
        public string RenderingMode { get; set; }
        public bool IsVisible { get; set; }

        public PointCloudInfo()
        {
            LoadTime = DateTime.Now;
            RenderingMode = "True Color";
            IsVisible = true;
        }

        /// <summary>
        /// Returns the approximate center of the point cloud bounding box.
        /// </summary>
        public XYZ GetCenter()
        {
            if (BoundingBox == null) return XYZ.Zero;
            return (BoundingBox.Min + BoundingBox.Max) / 2.0;
        }

        /// <summary>
        /// Returns the dimensions of the point cloud bounding box in feet.
        /// </summary>
        public (double LengthX, double LengthY, double LengthZ) GetDimensions()
        {
            if (BoundingBox == null) return (0, 0, 0);
            return (
                Math.Abs(BoundingBox.Max.X - BoundingBox.Min.X),
                Math.Abs(BoundingBox.Max.Y - BoundingBox.Min.Y),
                Math.Abs(BoundingBox.Max.Z - BoundingBox.Min.Z)
            );
        }
    }
}
