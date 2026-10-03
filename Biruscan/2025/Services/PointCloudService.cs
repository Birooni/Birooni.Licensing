using Autodesk.Revit.DB;
using Autodesk.Revit.DB.PointClouds;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Biruscan.Models;

namespace Biruscan.Services
{
    /// <summary>
    /// Service for loading, querying, and managing point cloud instances in Revit.
    /// </summary>
    public class PointCloudService
    {
        private readonly Document _doc;

        public PointCloudService(Document doc)
        {
            _doc = doc;
        }

        /// <summary>
        /// Loads an RCP or RCS point cloud file into the active view.
        /// </summary>
        public PointCloudInfo LoadPointCloud(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                throw new FileNotFoundException("Point cloud file not found: " + filePath);

            string extension = Path.GetExtension(filePath).ToLower();
            if (extension != ".rcp" && extension != ".rcs")
                throw new ArgumentException("Only RCP and RCS files are supported. Got: " + extension);

            PointCloudType cloudType = FindOrCreatePointCloudType(filePath);
            if (cloudType == null)
                throw new Exception("Failed to create PointCloudType for: " + filePath);

            // Place the point cloud at the origin
            // PointCloudInstance.Create(Document, typeId, transform) — 3 parameters
            PointCloudInstance cloudInstance = PointCloudInstance.Create(
                _doc,
                cloudType.Id,
                Transform.Identity
            );

            if (cloudInstance == null)
                throw new Exception("Failed to create PointCloudInstance.");

            PointCloudInfo info = new PointCloudInfo
            {
                FileName = Path.GetFileName(filePath),
                PointCloudTypeId = cloudType.Id,
                PointCloudInstanceId = cloudInstance.Id,
                BoundingBox = cloudInstance.get_BoundingBox(null),
                FileSizeMB = new FileInfo(filePath).Length / (1024.0 * 1024.0)
            };

            return info;
        }

        /// <summary>
        /// Finds an existing PointCloudType or creates a new one from the file path.
        /// </summary>
        private PointCloudType FindOrCreatePointCloudType(string filePath)
        {
            // Search for existing type with same file
            FilteredElementCollector collector = new FilteredElementCollector(_doc);
            var existingTypes = collector.OfClass(typeof(PointCloudType))
                .Cast<PointCloudType>()
                .FirstOrDefault(t =>
                {
                    ModelPath mp = t.GetExternalFileReference()?.GetPath();
                    string ext = mp != null ? ModelPathUtils.ConvertModelPathToUserVisiblePath(mp) : null;
                    return !string.IsNullOrEmpty(ext) &&
                           Path.GetFileName(ext).Equals(Path.GetFileName(filePath),
                                StringComparison.OrdinalIgnoreCase);
                });

            if (existingTypes != null)
                return existingTypes;

            // Create new PointCloudType
            PointCloudType cloudType = PointCloudType.Create(_doc, "rcp", filePath);
            return cloudType;
        }

        /// <summary>
        /// Gets all point cloud instances in the document.
        /// </summary>
        public List<PointCloudInstance> GetAllPointCloudInstances()
        {
            return new FilteredElementCollector(_doc)
                .OfClass(typeof(PointCloudInstance))
                .Cast<PointCloudInstance>()
                .ToList();
        }

        /// <summary>
        /// Gets the first point cloud instance in the document, or null.
        /// </summary>
        public PointCloudInstance GetFirstPointCloudInstance()
        {
            return GetAllPointCloudInstances().FirstOrDefault();
        }

        /// <summary>
        /// World-space axis-aligned bounding box of the first point cloud instance.
        /// Always returns Transform = Identity with Min/Max in model coordinates
        /// (all 8 corners expanded) so callers can safely use the mid-point as a plane origin.
        /// </summary>
        public BoundingBoxXYZ GetPointCloudBoundingBox()
        {
            var instance = GetFirstPointCloudInstance();
            if (instance == null) return null;
            return GetWorldBounds(instance);
        }

        /// <summary>
        /// Toggles the visibility of the point cloud in the given view.
        /// Uses category-level visibility toggle (hide/show point cloud category).
        /// </summary>
        public void ToggleVisibility(View view, ElementId instanceId)
        {
            if (view == null || instanceId == null) return;

            Category pointCloudCategory = Category.GetCategory(_doc, BuiltInCategory.OST_PointClouds);
            if (pointCloudCategory == null) return;

            bool isCurrentlyHidden = view.GetCategoryHidden(pointCloudCategory.Id);

            if (isCurrentlyHidden)
            {
                view.SetCategoryHidden(pointCloudCategory.Id, false);
            }
            else
            {
                view.SetCategoryHidden(pointCloudCategory.Id, true);
            }
        }

        /// <summary>
        /// Sets the visibility of a point cloud in a view using Category visibility.
        /// </summary>
        public void SetCategoryVisibility(View view, bool visible)
        {
            if (view == null) return;

            Category pointCloudCategory = Category.GetCategory(_doc, BuiltInCategory.OST_PointClouds);
            if (pointCloudCategory != null)
            {
                if (visible)
                    view.SetCategoryHidden(pointCloudCategory.Id, false);
                else
                    view.SetCategoryHidden(pointCloudCategory.Id, true);
            }
        }

        /// <summary>
        /// Gets point cloud points within a given WORLD/model-space bounding box.
        /// Per Revit API, the filter is in Revit model coordinates; returned CloudPoints
        /// are in the cloud engine's local frame (callers must map to world if needed).
        /// </summary>
        public List<XYZ> GetPointsInRegion(PointCloudInstance instance, BoundingBoxXYZ region, int maxPoints = 10000, double averageDistance = 0.001)
        {
            List<XYZ> points = new List<XYZ>();
            if (instance == null || region == null) return points;

            try
            {
                // Filter must be model coordinates (NOT cloud-local).
                PointCloudFilter filter = CreateBoundingBoxFilter(region);

                PointCollection pointCollection = instance.GetPoints(filter, averageDistance, maxPoints);
                foreach (CloudPoint cp in pointCollection)
                {
                    points.Add(new XYZ(cp.X, cp.Y, cp.Z));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Biruscan] Point access error: " + ex.Message);
            }

            return points;
        }

        /// <summary>
        /// Transforms a world-space bounding box to the point cloud's local coordinate system.
        /// This is critical because user picks (GlobalPoint) are in world coordinates,
        /// but IPointCloudAccess works in the cloud's local coordinate system.
        /// </summary>
        public BoundingBoxXYZ TransformToCloudLocalSpace(PointCloudInstance instance, BoundingBoxXYZ worldBox)
        {
            Transform cloudTransform = instance.GetTransform();

            if (cloudTransform == null || cloudTransform.IsIdentity)
            {
                // No transform — world and local are the same
                return worldBox;
            }

            Transform inverse = cloudTransform.Inverse;
            BoundingBoxXYZ localBox = new BoundingBoxXYZ();
            // BoundingBoxXYZ.Min and Max are relative to its Transform.
            // By multiplying the transform by the inverse of the cloud's transform,
            // we effectively map the bounding box into the cloud's local coordinate system.
            localBox.Transform = inverse.Multiply(worldBox.Transform);
            localBox.Min = worldBox.Min;
            localBox.Max = worldBox.Max;
            return localBox;
        }

        /// <summary>
        /// Creates a PointCloudFilter from a bounding box.
        /// Autodesk API: CreateMultiPlaneFilter keeps points on the POSITIVE side of
        /// every plane (dot(P - origin, normal) &gt; 0). To keep the BOX INTERIOR,
        /// all normals must point INWARD:
        ///   Min.X face → +X,  Max.X face → -X,  (and same for Y/Z).
        /// </summary>
        public PointCloudFilter CreateBoundingBoxFilter(BoundingBoxXYZ bbox)
        {
            Transform t = bbox.Transform ?? Transform.Identity;
            List<Plane> planes = new List<Plane>
            {
                // Min.X face — inward normal +X
                Plane.CreateByNormalAndOrigin(t.OfVector(new XYZ(1, 0, 0)), t.OfPoint(new XYZ(bbox.Min.X, 0, 0))),
                // Max.X face — inward normal -X
                Plane.CreateByNormalAndOrigin(t.OfVector(new XYZ(-1, 0, 0)), t.OfPoint(new XYZ(bbox.Max.X, 0, 0))),
                // Min.Y face — inward normal +Y
                Plane.CreateByNormalAndOrigin(t.OfVector(new XYZ(0, 1, 0)), t.OfPoint(new XYZ(0, bbox.Min.Y, 0))),
                // Max.Y face — inward normal -Y
                Plane.CreateByNormalAndOrigin(t.OfVector(new XYZ(0, -1, 0)), t.OfPoint(new XYZ(0, bbox.Max.Y, 0))),
                // Min.Z face — inward normal +Z
                Plane.CreateByNormalAndOrigin(t.OfVector(new XYZ(0, 0, 1)), t.OfPoint(new XYZ(0, 0, bbox.Min.Z))),
                // Max.Z face — inward normal -Z
                Plane.CreateByNormalAndOrigin(t.OfVector(new XYZ(0, 0, -1)), t.OfPoint(new XYZ(0, 0, bbox.Max.Z)))
            };
            return PointCloudFilterFactory.CreateMultiPlaneFilter(planes);
        }

        /// <summary>
        /// Moves a point cloud instance by a given offset.
        /// </summary>
        public void MovePointCloud(ElementId instanceId, XYZ offset)
        {
            Element elem = _doc.GetElement(instanceId);
            if (elem is PointCloudInstance pci)
            {
                using (Transaction t = new Transaction(_doc, "Move Point Cloud"))
                {
                    t.Start();
                    ElementTransformUtils.MoveElement(_doc, instanceId, offset);
                    t.Commit();
                }
            }
        }

        /// <summary>
        /// Rotates a point cloud instance around a given axis.
        /// </summary>
        public void RotatePointCloud(ElementId instanceId, XYZ axisOrigin, XYZ axisDirection, double angleRadians)
        {
            Element elem = _doc.GetElement(instanceId);
            if (elem is PointCloudInstance pci)
            {
                using (Transaction t = new Transaction(_doc, "Rotate Point Cloud"))
                {
                    t.Start();
                    Line axis = Line.CreateBound(axisOrigin, axisOrigin + axisDirection);
                    ElementTransformUtils.RotateElement(_doc, instanceId, axis, angleRadians);
                    t.Commit();
                }
            }
        }

        // ===================================================================
        // Screen-rectangle point reading (used by the Pipe/MEP fitters)
        // ===================================================================

        /// <summary>
        /// Maps GetPoints() native coordinates to Revit model (world) coordinates.
        /// Revit API: world = instance.GetTransform().OfPoint(local) — we verify with
        /// bbox/anchor checks because clipped re-imported clouds may already be in world space.
        /// </summary>
        public Transform GetWorldMappingTransform(PointCloudInstance pci, XYZ worldAnchor = null)
        {
            if (pci == null) return Transform.Identity;
            Transform ct = pci.GetTransform() ?? Transform.Identity;
            if (ct.IsIdentity) return Transform.Identity;

            BoundingBoxXYZ worldBounds = GetWorldBounds(pci);
            if (worldBounds == null) return ct;

            string readDiag;
            List<XYZ> raw = ReadCloudLocal(pci, 0.05, 8000, out readDiag);
            if (raw.Count == 0) return ct;

            return ResolveWorldMapping(ct, worldBounds, raw, worldAnchor).map;
        }

        /// <summary>Maps one raw GetPoints coordinate to model world space.</summary>
        public XYZ ToWorldPoint(PointCloudInstance pci, XYZ rawPoint, XYZ worldAnchor = null)
            => GetWorldMappingTransform(pci, worldAnchor).OfPoint(rawPoint);

        /// <summary>
        /// Reads cloud points inside a screen-aligned pick rectangle; returns WORLD coordinates.
        /// </summary>
        public List<XYZ> GetPointsInScreenRect(View view, XYZ corner1, XYZ corner2,
            int maxPoints, out string diagnostics, XYZ worldAnchor = null)
        {
            diagnostics = "";
            var result = new List<XYZ>();

            Transform viewT = Transform.Identity;
            viewT.Origin = view.Origin;
            viewT.BasisX = view.RightDirection.Normalize();
            viewT.BasisY = view.UpDirection.Normalize();
            viewT.BasisZ = view.ViewDirection.Normalize();
            Transform invView = viewT.Inverse;

            XYZ lc1 = invView.OfPoint(corner1);
            XYZ lc2 = invView.OfPoint(corner2);
            double minX = Math.Min(lc1.X, lc2.X), maxX = Math.Max(lc1.X, lc2.X);
            double minY = Math.Min(lc1.Y, lc2.Y), maxY = Math.Max(lc1.Y, lc2.Y);

            var instances = GetAllPointCloudInstances();
            diagnostics += $"instances={instances.Count}; ";

            int cap = Math.Min(Math.Max(maxPoints, 2000), 25000);
            int idx = 0;
            foreach (var pci in instances)
            {
                idx++;
                Transform worldMap = GetWorldMappingTransform(pci, worldAnchor);

                PointCloudFilter pickFilter = null;
                try
                {
                    if (pci.FilterAction == SelectionFilterAction.Isolate)
                        pickFilter = pci.GetSelectionFilter();
                }
                catch { }

                if (pickFilter == null)
                    pickFilter = BuildPickVolumeFilter(pci, viewT, minX, maxX, minY, maxY);

                string readDiag;
                List<XYZ> raw = ReadCloudLocal(pci, 0.04, cap, out readDiag, pickFilter);
                if (raw.Count == 0)
                {
                    raw = ReadCloudLocal(pci, 0.04, cap, out readDiag, null);
                    if (raw.Count == 0) { diagnostics += $"#{idx}:{readDiag}; "; continue; }
                }

                string mapName = worldMap.IsIdentity ? "identity" : "ct";

                int kept = 0;
                foreach (var rp in raw)
                {
                    XYZ p = worldMap.OfPoint(rp);
                    XYZ l = invView.OfPoint(p);
                    if (l.X >= minX && l.X <= maxX && l.Y >= minY && l.Y <= maxY)
                    {
                        result.Add(p);
                        kept++;
                    }
                }
                diagnostics += $"#{idx}:raw={raw.Count},kept={kept},map={mapName}; ";
            }

            diagnostics += $"total in-rect={result.Count};";
            return result;
        }

        /// <summary>
        /// Reads points in the cloud's native (local) coordinate system. Uses the active
        /// clip filter when present; otherwise builds a permissive filter from the cloud's
        /// actual bounds (safe for georeferenced / far-from-origin clouds).
        /// </summary>
        private List<XYZ> ReadCloudLocal(PointCloudInstance pci, double averageDistance, int maxPoints, out string diag,
            PointCloudFilter overrideFilter = null)
        {
            diag = "";
            var pts = new List<XYZ>();
            try
            {
                PointCloudFilter filter = overrideFilter;
                if (filter == null)
                {
                    try
                    {
                        if (pci.FilterAction == SelectionFilterAction.Isolate)
                            filter = pci.GetSelectionFilter();
                    }
                    catch { }
                }

                if (filter == null)
                    filter = BuildPermissiveLocalFilter(pci);

                if (filter == null)
                {
                    diag = "no-filter";
                    return pts;
                }

                PointCollection pc = pci.GetPoints(filter, averageDistance, maxPoints);
                foreach (CloudPoint cp in pc)
                    pts.Add(new XYZ(cp.X, cp.Y, cp.Z));
                diag = pts.Count == 0 ? "read0" : "ok";
            }
            catch (Exception ex)
            {
                diag = "err:" + ex.Message;
            }
            return pts;
        }

        /// <summary>
        /// Builds a GetPoints filter covering the full cloud in MODEL coordinates
        /// (Revit API requirement for PointCloudFilterFactory).
        /// </summary>
        private PointCloudFilter BuildPermissiveLocalFilter(PointCloudInstance pci)
        {
            BoundingBoxXYZ worldBounds = GetWorldBounds(pci);
            if (worldBounds == null) return null;

            const double pad = 5.0;
            var padded = new BoundingBoxXYZ
            {
                Transform = Transform.Identity,
                Min = worldBounds.Min - new XYZ(pad, pad, pad),
                Max = worldBounds.Max + new XYZ(pad, pad, pad)
            };
            return CreateBoundingBoxFilter(padded);
        }

        /// <summary>
        /// Tight world AABB around the pick rectangle extruded through the cloud's
        /// view-depth, so GetPoints does not scan the entire model.
        /// </summary>
        private PointCloudFilter BuildPickVolumeFilter(PointCloudInstance pci, Transform viewT,
            double minX, double maxX, double minY, double maxY)
        {
            try
            {
                BoundingBoxXYZ wb = GetWorldBounds(pci);
                double z0 = -80.0, z1 = 80.0;
                if (wb != null)
                {
                    Transform inv = viewT.Inverse;
                    z0 = double.PositiveInfinity;
                    z1 = double.NegativeInfinity;
                    XYZ mn = wb.Min, mx = wb.Max;
                    XYZ[] corners =
                    {
                        new XYZ(mn.X, mn.Y, mn.Z), new XYZ(mx.X, mn.Y, mn.Z),
                        new XYZ(mn.X, mx.Y, mn.Z), new XYZ(mx.X, mx.Y, mn.Z),
                        new XYZ(mn.X, mn.Y, mx.Z), new XYZ(mx.X, mn.Y, mx.Z),
                        new XYZ(mn.X, mx.Y, mx.Z), new XYZ(mx.X, mx.Y, mx.Z)
                    };
                    foreach (XYZ c in corners)
                    {
                        double z = inv.OfPoint(c).Z;
                        if (z < z0) z0 = z;
                        if (z > z1) z1 = z;
                    }
                    if (double.IsInfinity(z0) || double.IsInfinity(z1) || z1 - z0 < 0.5)
                    {
                        z0 = -80.0;
                        z1 = 80.0;
                    }
                    else
                    {
                        z0 -= 2.0;
                        z1 += 2.0;
                    }
                }

                double pad = 0.75;
                XYZ[] vs =
                {
                    new XYZ(minX - pad, minY - pad, z0), new XYZ(maxX + pad, minY - pad, z0),
                    new XYZ(minX - pad, maxY + pad, z0), new XYZ(maxX + pad, maxY + pad, z0),
                    new XYZ(minX - pad, minY - pad, z1), new XYZ(maxX + pad, minY - pad, z1),
                    new XYZ(minX - pad, maxY + pad, z1), new XYZ(maxX + pad, maxY + pad, z1)
                };

                double wnx = double.PositiveInfinity, wny = double.PositiveInfinity, wnz = double.PositiveInfinity;
                double wxx = double.NegativeInfinity, wxy = double.NegativeInfinity, wxz = double.NegativeInfinity;
                foreach (XYZ v in vs)
                {
                    XYZ w = viewT.OfPoint(v);
                    if (w.X < wnx) wnx = w.X; if (w.Y < wny) wny = w.Y; if (w.Z < wnz) wnz = w.Z;
                    if (w.X > wxx) wxx = w.X; if (w.Y > wxy) wxy = w.Y; if (w.Z > wxz) wxz = w.Z;
                }

                var box = new BoundingBoxXYZ
                {
                    Transform = Transform.Identity,
                    Min = new XYZ(wnx - 0.5, wny - 0.5, wnz - 0.5),
                    Max = new XYZ(wxx + 0.5, wxy + 0.5, wxz + 0.5)
                };
                return CreateBoundingBoxFilter(box);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Picks GetPoints→world mapping. Revit documents local→world via GetTransform();
        /// identity wins only when the mapped AABB clearly matches the instance world bbox
        /// better than ct (e.g. re-imported clipped clouds stored in world space).
        /// </summary>
        private static (Transform map, string name) ResolveWorldMapping(
            Transform ct, BoundingBoxXYZ worldBounds, List<XYZ> rawLocal, XYZ worldAnchor = null)
        {
            if (ct == null || ct.IsIdentity)
                return (Transform.Identity, "identity");
            if (worldBounds == null || rawLocal.Count == 0)
                return (ct, "ct");

            double sId = MappedBboxMatchScore(rawLocal, Transform.Identity, worldBounds);
            double sCt = MappedBboxMatchScore(rawLocal, ct, worldBounds);

            double anchorId = 0, anchorCt = 0;
            if (worldAnchor != null)
            {
                double diag = worldBounds.Max.DistanceTo(worldBounds.Min);
                if (diag < 1e-3) diag = 1.0;
                anchorId = 1.0 - Math.Min(1.0, AnchorProximityFt(rawLocal, Transform.Identity, worldAnchor) / diag);
                anchorCt = 1.0 - Math.Min(1.0, AnchorProximityFt(rawLocal, ct, worldAnchor) / diag);
            }

            double totalId = worldAnchor != null ? sId * 0.35 + anchorId * 0.65 : sId;
            double totalCt = worldAnchor != null ? sCt * 0.35 + anchorCt * 0.65 : sCt;

            // Prefer ct (Revit API default) unless identity is clearly better.
            if (totalId > totalCt + 0.12)
                return (Transform.Identity, "identity");
            return (ct, "ct");
        }

        private static double MappedBboxMatchScore(List<XYZ> rawLocal, Transform map, BoundingBoxXYZ wb)
        {
            var mapped = new List<XYZ>();
            int step = Math.Max(1, rawLocal.Count / 2000);
            for (int i = 0; i < rawLocal.Count; i += step)
                mapped.Add(map.OfPoint(rawLocal[i]));
            return BboxMatch(mapped, wb);
        }

        /// <summary>10th-percentile distance (ft) from mapped points to a world anchor.</summary>
        private static double AnchorProximityFt(List<XYZ> rawLocal, Transform map, XYZ anchor)
        {
            var dists = new List<double>();
            int step = Math.Max(1, rawLocal.Count / 1000);
            for (int i = 0; i < rawLocal.Count; i += step)
                dists.Add(map.OfPoint(rawLocal[i]).DistanceTo(anchor));
            if (dists.Count == 0) return double.MaxValue;
            dists.Sort();
            int idx = Math.Min(Math.Max(0, dists.Count / 10), dists.Count - 1);
            return dists[idx];
        }

        /// <summary>
        /// Reads cloud points near the segment w1→w2 (both TRUE world points obtained from
        /// cloud picks, i.e. Reference.GlobalPoint), returned in WORLD coordinates.
        ///
        /// The correct GetPoints→world mapping is calibrated EMPIRICALLY rather than
        /// assumed: the picked anchor is a real point on the cloud surface, so whichever
        /// candidate mapping places a read point essentially on top of it is the right one.
        /// This sidesteps all instance-transform / shared-coordinate ambiguity.
        /// </summary>
        public List<XYZ> GetPipePoints(PointCloudInstance pci, XYZ w1, XYZ w2,
            double margin, int maxPoints, out string diag)
        {
            diag = "";
            var result = new List<XYZ>();
            if (pci == null) { diag = "no pci"; return result; }

            Transform ct = pci.GetTransform() ?? Transform.Identity;

            string readDiag;
            List<XYZ> raw = ReadCloudLocal(pci, 0.02, maxPoints, out readDiag);
            if (raw.Count == 0) { diag = "no points read (" + readDiag + ")"; return result; }

            BoundingBoxXYZ worldBounds = GetWorldBounds(pci);
            var (bestT, bestName) = ResolveWorldMapping(ct, worldBounds, raw, w1);
            diag += $"map={bestName}; ";

            // Keep points within 'margin' of the picked segment (in true world).
            XYZ axis = w2 - w1;
            double L = axis.GetLength();
            if (L < 1e-6) { diag += "picks coincide"; return result; }
            axis = axis.Multiply(1.0 / L);
            const double pad = 0.25;
            foreach (var rp in raw)
            {
                XYZ p = bestT.OfPoint(rp);
                XYZ d = p - w1;
                double t = d.DotProduct(axis);
                if (t < -pad || t > L + pad) continue;
                double perp = (d - axis.Multiply(t)).GetLength();
                if (perp <= margin) result.Add(p);
            }
            diag += $"near-seg kept={result.Count}/{raw.Count}";
            return result;
        }

        /// <summary>
        /// Determines whether GetPoints returns points in cloud-LOCAL space (needing the
        /// instance transform to reach world) or already in WORLD space, by reading a
        /// small sample both ways and seeing which lands inside the world bounding box.
        /// </summary>
        private bool DetectUseLocal(PointCloudInstance pci, Transform ct, BoundingBoxXYZ wb,
            List<Plane> worldAabb, out string diag)
        {
            diag = "";
            if (ct == null || ct.IsIdentity) { diag = "ct=identity"; return true; }

            string tmp = "";
            int rl, rw;
            var ptsL = ReadWorld(pci, ct, worldAabb, true, 0.1, 8000, ref tmp, out rl);
            var ptsW = ReadWorld(pci, ct, worldAabb, false, 0.1, 8000, ref tmp, out rw);
            // Match the read points' bounding box against the TRUE world bbox. The correct
            // convention reproduces the cloud's world bbox; the wrong one is rotated/shifted.
            double sL = BboxMatch(ptsL, wb);
            double sW = BboxMatch(ptsW, wb);
            diag = $"L(n={ptsL.Count},match={sL:F2}) W(n={ptsW.Count},match={sW:F2})";
            return sL >= sW;
        }

        /// <summary>1.0 = the points' bbox matches the target exactly; lower = worse.</summary>
        private static double BboxMatch(List<XYZ> pts, BoundingBoxXYZ wb)
        {
            if (pts == null || pts.Count == 0) return -1;
            double mnx = 1e30, mny = 1e30, mnz = 1e30, mxx = -1e30, mxy = -1e30, mxz = -1e30;
            foreach (var p in pts)
            {
                if (p.X < mnx) mnx = p.X; if (p.Y < mny) mny = p.Y; if (p.Z < mnz) mnz = p.Z;
                if (p.X > mxx) mxx = p.X; if (p.Y > mxy) mxy = p.Y; if (p.Z > mxz) mxz = p.Z;
            }
            double diag = wb.Max.DistanceTo(wb.Min);
            if (diag < 1e-6) diag = 1;
            double dmin = Math.Sqrt(Sq(mnx - wb.Min.X) + Sq(mny - wb.Min.Y) + Sq(mnz - wb.Min.Z));
            double dmax = Math.Sqrt(Sq(mxx - wb.Max.X) + Sq(mxy - wb.Max.Y) + Sq(mxz - wb.Max.Z));
            return 1.0 - (dmin + dmax) / (2.0 * diag);
        }

        private static double Sq(double x) => x * x;

        /// <summary>
        /// Reads points selected by a world-space multi-plane filter and returns them in
        /// WORLD coordinates. When <paramref name="useLocal"/> is true the filter is first
        /// mapped into cloud-local space and the returned points are mapped back to world
        /// via the instance transform; otherwise the filter/points are used as world.
        /// </summary>
        private List<XYZ> ReadWorld(PointCloudInstance pci, Transform ct, List<Plane> worldPlanes,
            bool useLocal, double averageDistance, int maxPoints, ref string diag, out int rawCount)
        {
            var pts = new List<XYZ>();
            rawCount = 0;
            try
            {
                List<Plane> planes;
                bool mapBack = useLocal && !ct.IsIdentity;
                if (mapBack)
                {
                    Transform inv = ct.Inverse;
                    planes = new List<Plane>(worldPlanes.Count);
                    foreach (var pl in worldPlanes)
                        planes.Add(Plane.CreateByNormalAndOrigin(inv.OfVector(pl.Normal), inv.OfPoint(pl.Origin)));
                }
                else planes = worldPlanes;

                PointCloudFilter filter = PointCloudFilterFactory.CreateMultiPlaneFilter(planes);
                PointCollection pc = pci.GetPoints(filter, averageDistance, maxPoints);
                foreach (CloudPoint cp in pc)
                {
                    rawCount++;
                    XYZ p = new XYZ(cp.X, cp.Y, cp.Z);
                    pts.Add(mapBack ? ct.OfPoint(p) : p);
                }
            }
            catch (Exception ex)
            {
                diag += " GetPoints error: " + ex.Message + ";";
            }
            return pts;
        }

        /// <summary>Six inward-normal planes (interior = positive side of all) for a world AABB.</summary>
        private static List<Plane> InwardAabbPlanes(BoundingBoxXYZ wb)
        {
            return new List<Plane>
            {
                Plane.CreateByNormalAndOrigin(XYZ.BasisX,  new XYZ(wb.Min.X, 0, 0)),
                Plane.CreateByNormalAndOrigin(-XYZ.BasisX, new XYZ(wb.Max.X, 0, 0)),
                Plane.CreateByNormalAndOrigin(XYZ.BasisY,  new XYZ(0, wb.Min.Y, 0)),
                Plane.CreateByNormalAndOrigin(-XYZ.BasisY, new XYZ(0, wb.Max.Y, 0)),
                Plane.CreateByNormalAndOrigin(XYZ.BasisZ,  new XYZ(0, 0, wb.Min.Z)),
                Plane.CreateByNormalAndOrigin(-XYZ.BasisZ, new XYZ(0, 0, wb.Max.Z)),
            };
        }

        /// <summary>Keeps only points whose screen (right/up) coordinates lie in the rectangle (any depth).</summary>
        private static List<XYZ> ScreenRectFilter(List<XYZ> pts, Transform invView,
            double minX, double maxX, double minY, double maxY)
        {
            var keep = new List<XYZ>(pts.Count);
            foreach (var p in pts)
            {
                XYZ l = invView.OfPoint(p);
                if (l.X >= minX && l.X <= maxX && l.Y >= minY && l.Y <= maxY)
                    keep.Add(p);
            }
            return keep;
        }

        /// <summary>Returns the axis-aligned WORLD bounding box of the instance.</summary>
        public BoundingBoxXYZ GetWorldBounds(PointCloudInstance pci)
        {
            BoundingBoxXYZ bb = pci.get_BoundingBox(null);
            if (bb == null) return null;
            Transform t = bb.Transform;
            if (t == null || t.IsIdentity) return bb;

            XYZ[] corners = BoxCorners(bb.Min, bb.Max);
            XYZ wmn = null, wmx = null;
            foreach (var c in corners)
            {
                XYZ w = t.OfPoint(c);
                wmn = wmn == null ? w : new XYZ(Math.Min(wmn.X, w.X), Math.Min(wmn.Y, w.Y), Math.Min(wmn.Z, w.Z));
                wmx = wmx == null ? w : new XYZ(Math.Max(wmx.X, w.X), Math.Max(wmx.Y, w.Y), Math.Max(wmx.Z, w.Z));
            }
            return new BoundingBoxXYZ { Transform = Transform.Identity, Min = wmn, Max = wmx };
        }

        /// <summary>Returns the 8 corners of an axis-aligned box.</summary>
        private static XYZ[] BoxCorners(XYZ mn, XYZ mx)
        {
            return new XYZ[]
            {
                new XYZ(mn.X, mn.Y, mn.Z), new XYZ(mx.X, mn.Y, mn.Z), new XYZ(mn.X, mx.Y, mn.Z), new XYZ(mx.X, mx.Y, mn.Z),
                new XYZ(mn.X, mn.Y, mx.Z), new XYZ(mx.X, mn.Y, mx.Z), new XYZ(mn.X, mx.Y, mx.Z), new XYZ(mx.X, mx.Y, mx.Z),
            };
        }
    }
}
