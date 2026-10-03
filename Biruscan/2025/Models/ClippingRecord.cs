using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Biruscan.Models
{
    /// <summary>
    /// Represents a saved clipping/cutting record that defines a region
    /// for isolating portions of a point cloud.
    /// </summary>
    public class ClippingRecord : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private string _name;
        public string Name 
        { 
            get => _name; 
            set { _name = value; OnPropertyChanged(); } 
        }

        private bool _isActive;
        public bool IsActive 
        { 
            get => _isActive; 
            set 
            { 
                if (_isActive != value)
                {
                    _isActive = value; 
                    OnPropertyChanged(); 
                }
            } 
        }

        public double MinX { get; set; }
        public double MaxX { get; set; }
        public double MinY { get; set; }
        public double MaxY { get; set; }
        public double MinZ { get; set; }
        public double MaxZ { get; set; }
        public SliceAxis Axis { get; set; }
        public double SlicePosition { get; set; }
        public double SliceThickness { get; set; }
        public ClipType ClipType { get; set; }

        private BoundingBoxXYZ _sliceBox;
        [JsonIgnore]
        public BoundingBoxXYZ SliceBox 
        { 
            get => _sliceBox; 
            set
            {
                _sliceBox = value;
                if (value != null)
                {
                    if (value.Transform != null)
                    {
                        TransformMatrix = new double[]
                        {
                            value.Transform.BasisX.X, value.Transform.BasisX.Y, value.Transform.BasisX.Z,
                            value.Transform.BasisY.X, value.Transform.BasisY.Y, value.Transform.BasisY.Z,
                            value.Transform.BasisZ.X, value.Transform.BasisZ.Y, value.Transform.BasisZ.Z,
                            value.Transform.Origin.X, value.Transform.Origin.Y, value.Transform.Origin.Z
                        };
                    }
                    // Persist the box extents too, so the record round-trips through save/load.
                    // (Some creators set SliceBox without also setting Min/Max — without this,
                    // RestoreGeometry rebuilt a degenerate 0,0,0 box and the cloud vanished.)
                    MinX = value.Min.X; MaxX = value.Max.X;
                    MinY = value.Min.Y; MaxY = value.Max.Y;
                    MinZ = value.Min.Z; MaxZ = value.Max.Z;
                }
            }
        }

        public double[] TransformMatrix { get; set; }

        public double StepDistance { get; set; }
        private bool _inverse;
        public bool Inverse 
        { 
            get => _inverse; 
            set 
            { 
                if (_inverse != value)
                {
                    _inverse = value; 
                    OnPropertyChanged(); 
                }
            } 
        }

        private List<Plane> _planes;
        [JsonIgnore]
        public List<Plane> Planes 
        { 
            get => _planes; 
            set 
            { 
                _planes = value; 
                if (value != null)
                {
                    PlaneArray = new double[value.Count * 6];
                    for (int i = 0; i < value.Count; i++)
                    {
                        PlaneArray[i * 6] = value[i].Origin.X;
                        PlaneArray[i * 6 + 1] = value[i].Origin.Y;
                        PlaneArray[i * 6 + 2] = value[i].Origin.Z;
                        PlaneArray[i * 6 + 3] = value[i].Normal.X;
                        PlaneArray[i * 6 + 4] = value[i].Normal.Y;
                        PlaneArray[i * 6 + 5] = value[i].Normal.Z;
                    }
                }
            } 
        }

        public double[] PlaneArray { get; set; }

        /// <summary>
        /// For concave polygonal cuts: stores multiple sets of planes, one per convex sub-polygon.
        /// Each sub-polygon's planes define a convex region; the union of all sub-polygons
        /// approximates the original concave polygon.
        /// When this is set, Planes may hold the first piece (for backward compat) or be empty.
        /// </summary>
        private List<List<Plane>> _subFilterPlanes;
        [JsonIgnore]
        public List<List<Plane>> SubFilterPlanes
        {
            get => _subFilterPlanes;
            set
            {
                _subFilterPlanes = value;
                if (value != null && value.Count > 0)
                {
                    // Serialize: [pieceCount, piece0PlaneCount, piece0Data..., piece1PlaneCount, piece1Data..., ...]
                    var flat = new List<double>();
                    flat.Add(value.Count); // number of pieces
                    foreach (var piece in value)
                    {
                        flat.Add(piece.Count); // number of planes in this piece
                        foreach (var plane in piece)
                        {
                            flat.Add(plane.Origin.X);
                            flat.Add(plane.Origin.Y);
                            flat.Add(plane.Origin.Z);
                            flat.Add(plane.Normal.X);
                            flat.Add(plane.Normal.Y);
                            flat.Add(plane.Normal.Z);
                        }
                    }
                    SubFilterPlaneArray = flat.ToArray();
                }
            }
        }

        public double[] SubFilterPlaneArray { get; set; }

        public ClippingRecord()
        {
            Name = "New Cut";
            IsActive = true;
            Inverse = false;
            SliceThickness = 5.0;
            ClipType = ClipType.Slice;
            // Leave Planes null until set — an empty list is treated as "has planes"
            // in some older paths and blocked SliceBox rebuild after multi-cut sequences.
            Planes = null;
        }

        public BoundingBoxXYZ ToBoundingBox()
        {
            var bbox = new BoundingBoxXYZ
            {
                Min = new XYZ(MinX, MinY, MinZ),
                Max = new XYZ(MaxX, MaxY, MaxZ)
            };
            
            if (TransformMatrix != null && TransformMatrix.Length == 12)
            {
                Transform t = Transform.Identity;
                t.BasisX = new XYZ(TransformMatrix[0], TransformMatrix[1], TransformMatrix[2]);
                t.BasisY = new XYZ(TransformMatrix[3], TransformMatrix[4], TransformMatrix[5]);
                t.BasisZ = new XYZ(TransformMatrix[6], TransformMatrix[7], TransformMatrix[8]);
                t.Origin = new XYZ(TransformMatrix[9], TransformMatrix[10], TransformMatrix[11]);
                bbox.Transform = t;
            }
            
            return bbox;
        }

        public void RestoreGeometry()
        {
            if (TransformMatrix != null && TransformMatrix.Length == 12)
            {
                _sliceBox = ToBoundingBox();
            }
            
            if (PlaneArray != null && PlaneArray.Length % 6 == 0)
            {
                _planes = new List<Plane>();
                for (int i = 0; i < PlaneArray.Length; i += 6)
                {
                    var origin = new XYZ(PlaneArray[i], PlaneArray[i+1], PlaneArray[i+2]);
                    var normal = new XYZ(PlaneArray[i+3], PlaneArray[i+4], PlaneArray[i+5]);
                    _planes.Add(Plane.CreateByNormalAndOrigin(normal, origin));
                }
            }

            // Restore concave polygon sub-filter planes
            if (SubFilterPlaneArray != null && SubFilterPlaneArray.Length > 1)
            {
                _subFilterPlanes = new List<List<Plane>>();
                int pos = 0;
                int pieceCount = (int)SubFilterPlaneArray[pos++];
                for (int p = 0; p < pieceCount && pos < SubFilterPlaneArray.Length; p++)
                {
                    int planeCount = (int)SubFilterPlaneArray[pos++];
                    var piece = new List<Plane>();
                    for (int j = 0; j < planeCount && pos + 5 < SubFilterPlaneArray.Length; j++)
                    {
                        var origin = new XYZ(SubFilterPlaneArray[pos], SubFilterPlaneArray[pos+1], SubFilterPlaneArray[pos+2]);
                        var normal = new XYZ(SubFilterPlaneArray[pos+3], SubFilterPlaneArray[pos+4], SubFilterPlaneArray[pos+5]);
                        piece.Add(Plane.CreateByNormalAndOrigin(normal, origin));
                        pos += 6;
                    }
                    _subFilterPlanes.Add(piece);
                }
            }
        }

        public static ClippingRecord FromBoundingBox(BoundingBoxXYZ bbox, string name)
        {
            return new ClippingRecord
            {
                Name = name,
                MinX = bbox.Min.X,
                MaxX = bbox.Max.X,
                MinY = bbox.Min.Y,
                MaxY = bbox.Max.Y,
                MinZ = bbox.Min.Z,
                MaxZ = bbox.Max.Z,
                ClipType = ClipType.Rectangular
            };
        }
    }

    public enum SliceAxis
    {
        X,
        Y,
        Z
    }

    /// <summary>
    /// Type of cutting operation applied to the point cloud.
    /// </summary>
    public enum ClipType
    {
        Slice,
        Rectangular,
        Polygonal
    }

    /// <summary>
    /// Represents a group of clippings. Only the Active group's clippings are applied.
    /// </summary>
    public class ClippingGroup
    {
        public string Name { get; set; }
        public bool IsActive { get; set; }
        public System.Collections.ObjectModel.ObservableCollection<ClippingRecord> Clippings { get; set; }

        public ClippingGroup()
        {
            Name = "New Group";
            IsActive = false;
            Clippings = new System.Collections.ObjectModel.ObservableCollection<ClippingRecord>();
        }
    }
}
