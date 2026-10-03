using Autodesk.Revit.DB;

namespace Biruscan.Models
{
    /// <summary>
    /// Defines a slice plane used for cutting through point cloud data
    /// at a specific elevation or along a specific axis.
    /// </summary>
    public class SlicePlane
    {
        public SliceAxis Axis { get; set; }
        public double Position { get; set; }
        public double Thickness { get; set; }
        public string Name { get; set; }

        public SlicePlane()
        {
            Thickness = 5.0; // feet default
            Name = "Slice";
        }

        /// <summary>
        /// Creates a slice plane at the given elevation on the Z axis.
        /// </summary>
        public static SlicePlane CreateElevationSlice(double elevation, double thickness, string name = "Floor Slice")
        {
            return new SlicePlane
            {
                Axis = SliceAxis.Z,
                Position = elevation,
                Thickness = thickness,
                Name = name
            };
        }

        /// <summary>
        /// Converts this slice to a BoundingBox suitable for a Section Box.
        /// </summary>
        public BoundingBoxXYZ ToSectionBox(BoundingBoxXYZ cloudBounds)
        {
            double halfThickness = Thickness / 2.0;
            var box = new BoundingBoxXYZ();

            switch (Axis)
            {
                case SliceAxis.Z:
                    box.Min = new XYZ(cloudBounds.Min.X, cloudBounds.Min.Y, Position - halfThickness);
                    box.Max = new XYZ(cloudBounds.Max.X, cloudBounds.Max.Y, Position + halfThickness);
                    break;
                case SliceAxis.X:
                    box.Min = new XYZ(Position - halfThickness, cloudBounds.Min.Y, cloudBounds.Min.Z);
                    box.Max = new XYZ(Position + halfThickness, cloudBounds.Max.Y, cloudBounds.Max.Z);
                    break;
                case SliceAxis.Y:
                    box.Min = new XYZ(cloudBounds.Min.X, Position - halfThickness, cloudBounds.Min.Z);
                    box.Max = new XYZ(cloudBounds.Max.X, Position + halfThickness, cloudBounds.Max.Z);
                    break;
            }

            return box;
        }
    }
}
