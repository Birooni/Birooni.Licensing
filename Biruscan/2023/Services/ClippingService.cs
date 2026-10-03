using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.DB.PointClouds;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.ObjectModel;
using Biruscan.Models;

namespace Biruscan.Services
{
    /// <summary>
    /// Service for REAL point cloud cutting operations.
    ///
    /// CUTTING STRATEGY (2 tiers — no SectionBox fallback):
    ///
    ///   Tier 1 — PointCloudInstance.CropBox (Revit 2024.2+)
    ///     Sets the crop box directly on the point cloud ELEMENT.
    ///     The cut persists across ALL views — identical to CloudWorx behavior.
    ///     No section box is visible in any view.
    ///
    ///   Tier 2 — Filtered Point Cloud (fallback for older Revit)
    ///     Reads points from the clip region, writes them to a temporary PTS file,
    ///     creates a new PointCloudInstance that contains ONLY the clipped points.
    ///     The original cloud is hidden. This IS real cutting — the new cloud
    ///     only contains points within the cut region, just like CloudWorx.
    ///
    ///   The service will NEVER silently fall back to View3D.SectionBox.
    /// </summary>
    public class ClippingService
    {
        private readonly Document _doc;

        // Per-document clipping groups
        private static readonly Dictionary<string, ObservableCollection<ClippingGroup>> _clippingGroupsByDoc
            = new Dictionary<string, ObservableCollection<ClippingGroup>>();

        // Reflection cache
        private static PropertyInfo _cropBoxProperty = null;
        private static bool _cropBoxReflectionChecked = false;
        private static string _cropBoxStrategyUsed = "None";
        private static bool _diagnosticsPrinted = false;

        // Track original point cloud for reset
        // Track original point cloud for reset per document
        private static readonly Dictionary<string, string> _originalFilePath = new Dictionary<string, string>();
        private static readonly Dictionary<string, ElementId> _originalTypeId = new Dictionary<string, ElementId>();
        private static readonly Dictionary<string, string> _lastTempFilePath = new Dictionary<string, string>();
        // Track clones for multi-instance concave point cloud cuts
        private static readonly Dictionary<string, List<ElementId>> _cloneInstancesByDoc = new Dictionary<string, List<ElementId>>();
        // Unfiltered world AABB — after Isolate, get_BoundingBox often shrinks and breaks
        // ScreenCutAnchor / depth clamp on the 2nd+ sub-cut.
        private static readonly Dictionary<string, BoundingBoxXYZ> _originalCloudBoundsByDoc =
            new Dictionary<string, BoundingBoxXYZ>();

        public ClippingService(Document doc)
        {
            _doc = doc;
        }

        private string DocKey()
            => string.IsNullOrEmpty(_doc.PathName) ? _doc.Title : _doc.PathName;

        private static BoundingBoxXYZ CloneBox(BoundingBoxXYZ b)
        {
            if (b == null) return null;
            return new BoundingBoxXYZ
            {
                Transform = Transform.Identity,
                Min = b.Min,
                Max = b.Max
            };
        }

        public ObservableCollection<ClippingGroup> GetClippingGroups()
        {
            string key = string.IsNullOrEmpty(_doc.PathName) ? _doc.Title : _doc.PathName;
            if (!_clippingGroupsByDoc.ContainsKey(key))
            {
                var defaultGroup = new ClippingGroup { Name = "Active Group", IsActive = true };
                _clippingGroupsByDoc[key] = new ObservableCollection<ClippingGroup> { defaultGroup };
            }
            return _clippingGroupsByDoc[key];
        }

        public ClippingGroup GetActiveGroup()
        {
            var groups = GetClippingGroups();
            var active = groups.FirstOrDefault(g => g.IsActive);
            if (active == null)
            {
                // Create a default group if none exists
                active = new ClippingGroup { Name = "Group 1", IsActive = true };
                groups.Add(active);
            }
            return active;
        }

        private System.Collections.ObjectModel.ObservableCollection<ClippingRecord> ClippingRecords
        {
            get { return GetActiveGroup().Clippings; }
        }

        public static string GetClippingStrategy() => _cropBoxStrategyUsed;

        // ===================================================================
        // PointCloudInstance helpers
        // ===================================================================

        /// <summary>
        /// Primary (non-clone) point cloud instance. After concave polygonal cuts the
        /// document may contain temporary clone instances — those must NOT be treated
        /// as the main cloud or the next cut silently applies to the wrong element.
        /// </summary>
        public PointCloudInstance GetPointCloudInstance()
        {
            return GetPrimaryPointCloudInstances().FirstOrDefault();
        }

        /// <summary>
        /// All primary (non-clone) point cloud instances in the document.
        /// Excludes both in-memory clone IDs and instances tagged as Biruscan clones
        /// (so undo / stale static lists cannot leave orphans treated as primaries).
        /// </summary>
        public List<PointCloudInstance> GetPrimaryPointCloudInstances()
        {
            string key = string.IsNullOrEmpty(_doc.PathName) ? _doc.Title : _doc.PathName;
            HashSet<ElementId> cloneIds = null;
            if (_cloneInstancesByDoc.TryGetValue(key, out List<ElementId> clones) && clones != null && clones.Count > 0)
                cloneIds = new HashSet<ElementId>(clones);

            var all = new FilteredElementCollector(_doc)
                .OfClass(typeof(PointCloudInstance))
                .Cast<PointCloudInstance>()
                .ToList();

            var primaries = all.Where(p =>
            {
                if (cloneIds != null && cloneIds.Contains(p.Id)) return false;
                if (IsBiruscanClone(p)) return false;
                return true;
            }).ToList();

            // Fallback: if every instance was flagged as a clone, still return something.
            return primaries.Count > 0 ? primaries : all;
        }

        // ---- Concave-cut clone tagging (survives static-list loss / partial undo) ----
        private static readonly Guid CloneSchemaGuid = new Guid("C1A5B008-4E2D-4F91-9B3A-7D6E5F4C3B2A");

        private static Schema GetCloneSchema()
        {
            Schema schema = Schema.Lookup(CloneSchemaGuid);
            if (schema != null) return schema;
            var builder = new SchemaBuilder(CloneSchemaGuid);
            builder.SetSchemaName("BiruscanPcClone");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddSimpleField("IsClone", typeof(int));
            return builder.Finish();
        }

        private static void MarkAsBiruscanClone(PointCloudInstance pci)
        {
            if (pci == null) return;
            try
            {
                Schema schema = GetCloneSchema();
                Entity entity = new Entity(schema);
                entity.Set("IsClone", 1);
                pci.SetEntity(entity);
            }
            catch { }
        }

        private static bool IsBiruscanClone(PointCloudInstance pci)
        {
            if (pci == null) return false;
            try
            {
                Schema schema = Schema.Lookup(CloneSchemaGuid);
                if (schema == null) return false;
                Entity entity = pci.GetEntity(schema);
                if (entity == null || !entity.IsValid()) return false;
                return entity.Get<int>("IsClone") == 1;
            }
            catch { return false; }
        }

        /// <summary>
        /// Deletes only Biruscan clip clones (tagged or tracked). Does NOT remove
        /// previous cuts (clipping records) and does NOT delete same-type instances
        /// on every rebuild — that would surprise users mid-session.
        /// Call inside an open transaction.
        /// </summary>
        private void DeleteTaggedClonesInTransaction()
        {
            string key = string.IsNullOrEmpty(_doc.PathName) ? _doc.Title : _doc.PathName;
            if (!_cloneInstancesByDoc.ContainsKey(key))
                _cloneInstancesByDoc[key] = new List<ElementId>();
            var cloneIds = _cloneInstancesByDoc[key];

            var all = new FilteredElementCollector(_doc)
                .OfClass(typeof(PointCloudInstance))
                .Cast<PointCloudInstance>()
                .ToList();

            foreach (PointCloudInstance inst in all)
            {
                bool tracked = cloneIds.Contains(inst.Id);
                bool tagged = IsBiruscanClone(inst);
                if (!tracked && !tagged) continue;
                try { _doc.Delete(inst.Id); } catch { }
            }
            cloneIds.Clear();
        }

        /// <summary>
        /// Deletes extra PointCloudInstances that share the same type (old untagged
        /// polygon clones). Keeps the oldest Id per type. Only for Clear Cuts / purge.
        /// </summary>
        private void PurgeSameTypeDuplicatesInTransaction()
        {
            var all = new FilteredElementCollector(_doc)
                .OfClass(typeof(PointCloudInstance))
                .Cast<PointCloudInstance>()
                .ToList();

            var byType = new Dictionary<ElementId, List<PointCloudInstance>>();
            foreach (PointCloudInstance inst in all)
            {
                ElementId typeId = inst.GetTypeId();
                if (!byType.ContainsKey(typeId))
                    byType[typeId] = new List<PointCloudInstance>();
                byType[typeId].Add(inst);
            }

            foreach (var kv in byType)
            {
                if (kv.Value.Count <= 1) continue;
                kv.Value.Sort((a, b) => a.Id.IntegerValue.CompareTo(b.Id.IntegerValue));
                for (int i = 1; i < kv.Value.Count; i++)
                {
                    try
                    {
                        Debug.WriteLine($"[Biruscan] Purging duplicate PC instance {kv.Value[i].Id.IntegerValue}");
                        _doc.Delete(kv.Value[i].Id);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Biruscan] Purge duplicate failed: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Public: purge clone / duplicate point-cloud instances (not clipping records).
        /// Use Clear Cuts or call this when old polygon clones inflated the instance count.
        /// </summary>
        public int PurgeDuplicatePointCloudInstances()
        {
            int before = new FilteredElementCollector(_doc)
                .OfClass(typeof(PointCloudInstance)).GetElementCount();

            using (Transaction t = new Transaction(_doc, "Purge Duplicate Point Clouds"))
            {
                t.Start();
                DeleteTaggedClonesInTransaction();
                PurgeSameTypeDuplicatesInTransaction();
                t.Commit();
            }

            int after = new FilteredElementCollector(_doc)
                .OfClass(typeof(PointCloudInstance)).GetElementCount();
            return Math.Max(0, before - after);
        }

        // Previous cuts are KEPT and combined (AND) on a single PointCloudInstance.
        // We used to call RemovePreviousScreenCrops() which deleted prior rect/poly
        // records on every new cut — users lost previous cuts. That replace mode is gone.

        /// <summary>
        /// Live world AABB from instance bounding boxes (may shrink after Isolate).
        /// </summary>
        private BoundingBoxXYZ ComputeLiveWorldBounds()
        {
            var instances = GetPrimaryPointCloudInstances();
            if (instances == null || instances.Count == 0) return null;

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            bool any = false;

            foreach (PointCloudInstance pci in instances)
            {
                BoundingBoxXYZ bbox = pci.get_BoundingBox(null);
                if (bbox == null) continue;

                Transform bt = bbox.Transform ?? Transform.Identity;
                XYZ[] corners =
                {
                    new XYZ(bbox.Min.X, bbox.Min.Y, bbox.Min.Z),
                    new XYZ(bbox.Max.X, bbox.Min.Y, bbox.Min.Z),
                    new XYZ(bbox.Min.X, bbox.Max.Y, bbox.Min.Z),
                    new XYZ(bbox.Max.X, bbox.Max.Y, bbox.Min.Z),
                    new XYZ(bbox.Min.X, bbox.Min.Y, bbox.Max.Z),
                    new XYZ(bbox.Max.X, bbox.Min.Y, bbox.Max.Z),
                    new XYZ(bbox.Min.X, bbox.Max.Y, bbox.Max.Z),
                    new XYZ(bbox.Max.X, bbox.Max.Y, bbox.Max.Z)
                };
                foreach (XYZ c in corners)
                {
                    XYZ w = bt.OfPoint(c);
                    if (w.X < minX) minX = w.X; if (w.X > maxX) maxX = w.X;
                    if (w.Y < minY) minY = w.Y; if (w.Y > maxY) maxY = w.Y;
                    if (w.Z < minZ) minZ = w.Z; if (w.Z > maxZ) maxZ = w.Z;
                    any = true;
                }
            }

            if (!any || minX >= maxX || minY >= maxY || minZ >= maxZ) return null;
            return new BoundingBoxXYZ
            {
                Transform = Transform.Identity,
                Min = new XYZ(minX, minY, minZ),
                Max = new XYZ(maxX, maxY, maxZ)
            };
        }

        /// <summary>
        /// World-space AABB for anchors / depth clamps. Prefers the cached unfiltered
        /// bounds so successive sub-cuts still see the full cloud extent after Isolate.
        /// </summary>
        private BoundingBoxXYZ GetFullCloudBounds()
        {
            string key = DocKey();
            BoundingBoxXYZ live = ComputeLiveWorldBounds();

            // Refresh cache when unfiltered or first time.
            PointCloudInstance pci = GetPointCloudInstance();
            bool unfiltered = false;
            try { unfiltered = pci != null && pci.FilterAction == SelectionFilterAction.None; }
            catch { unfiltered = true; }

            if (live != null && (unfiltered || !_originalCloudBoundsByDoc.ContainsKey(key)))
                _originalCloudBoundsByDoc[key] = CloneBox(live);

            if (_originalCloudBoundsByDoc.TryGetValue(key, out BoundingBoxXYZ cached) && cached != null)
            {
                // If live is larger (rare), expand cache so we never under-size.
                if (live != null)
                {
                    XYZ cMin = new XYZ(
                        Math.Min(cached.Min.X, live.Min.X),
                        Math.Min(cached.Min.Y, live.Min.Y),
                        Math.Min(cached.Min.Z, live.Min.Z));
                    XYZ cMax = new XYZ(
                        Math.Max(cached.Max.X, live.Max.X),
                        Math.Max(cached.Max.Y, live.Max.Y),
                        Math.Max(cached.Max.Z, live.Max.Z));
                    cached = new BoundingBoxXYZ { Transform = Transform.Identity, Min = cMin, Max = cMax };
                    _originalCloudBoundsByDoc[key] = CloneBox(cached);
                }
                return CloneBox(_originalCloudBoundsByDoc[key]);
            }

            return live;
        }

        /// <summary>
        /// Gets the current effective crop (CropBox if set, otherwise full bounds).
        /// </summary>
        private BoundingBoxXYZ GetCurrentCropBox()
        {
            BoundingBoxXYZ fullBounds = GetFullCloudBounds();
            if (fullBounds == null)
                throw new Exception("No point cloud found in the document.");

            BoundingBoxXYZ currentCrop = TryReadCropBox();
            return currentCrop ?? fullBounds;
        }

        /// <summary>
        /// Tries to read the CropBox from PointCloudInstance.
        /// Returns null if CropBox is not available or not set.
        /// </summary>
        private BoundingBoxXYZ TryReadCropBox()
        {
            var pci = GetPointCloudInstance();
            if (pci == null) return null;

            DiscoverCropBoxProperty();

            if (_cropBoxProperty != null && _cropBoxProperty.CanRead)
            {
                try
                {
                    var box = _cropBoxProperty.GetValue(pci) as BoundingBoxXYZ;
                    if (box == null) return null;

                    // Convert from local to world space
                    Transform cloudTransform = pci.GetTransform();
                    if (cloudTransform != null && !cloudTransform.IsIdentity)
                    {
                        return new BoundingBoxXYZ
                        {
                            Transform = Transform.Identity,
                            Min = cloudTransform.OfPoint(box.Min),
                            Max = cloudTransform.OfPoint(box.Max)
                        };
                    }
                    return new BoundingBoxXYZ
                    {
                        Transform = Transform.Identity,
                        Min = box.Min,
                        Max = box.Max
                    };
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Biruscan] TryReadCropBox read error: {ex.Message}");
                }
            }
            return null;
        }

        // ===================================================================
        // CROPBOX DISCOVERY (Reflection)
        // ===================================================================

        /// <summary>
        /// Discovers the CropBox property via reflection.
        /// Also prints diagnostic info on the first call.
        /// </summary>
        private void DiscoverCropBoxProperty()
        {
            if (_cropBoxReflectionChecked) return;
            _cropBoxReflectionChecked = true;

            // Try "CropBox"
            _cropBoxProperty = typeof(PointCloudInstance).GetProperty("CropBox",
                BindingFlags.Public | BindingFlags.Instance |
                BindingFlags.GetProperty | BindingFlags.SetProperty);

            if (_cropBoxProperty != null)
            {
                Debug.WriteLine($"[Biruscan] CropBox FOUND: CanRead={_cropBoxProperty.CanRead}, CanWrite={_cropBoxProperty.CanWrite}, Type={_cropBoxProperty.PropertyType.Name}");
            }

            // Print ALL BoundingBoxXYZ properties for diagnostics
            PrintDiagnostics();
        }

        /// <summary>
        /// Prints all properties of PointCloudInstance for debugging.
        /// Helps identify the correct property name for clipping.
        /// </summary>
        [Conditional("DEBUG")]
        private void PrintDiagnostics()
        {
            if (_diagnosticsPrinted) return;
            _diagnosticsPrinted = true;

            Debug.WriteLine("=== [Biruscan] PointCloudInstance Property Diagnostics ===");
            foreach (PropertyInfo prop in typeof(PointCloudInstance).GetProperties(
                BindingFlags.Public | BindingFlags.Instance))
            {
                string extra = "";
                if (prop.PropertyType == typeof(BoundingBoxXYZ))
                    extra = " <<< BoundingBoxXYZ!";
                if (prop.Name.ToLower().Contains("crop") ||
                    prop.Name.ToLower().Contains("clip") ||
                    prop.Name.ToLower().Contains("bound"))
                    extra += " <<< MATCH";
                Debug.WriteLine($"  {prop.Name} : {prop.PropertyType.Name} (CanRead={prop.CanRead}, CanWrite={prop.CanWrite}){extra}");
            }
            Debug.WriteLine("=== End Diagnostics ===");
        }

        // ===================================================================
        // CORE CUTTING ENGINE
        // ===================================================================

        /// <summary>
        /// Applies a crop box to the PointCloudInstance.
        /// Tier 1: CropBox property on the element.
        /// Tier 2: Create a new filtered point cloud (PTS file).
        /// NEVER falls back to SectionBox.
        /// </summary>
        private void ApplyCropToPointCloud(BoundingBoxXYZ cropBox)
        {
            if (cropBox == null)
                throw new ArgumentNullException(nameof(cropBox));

            var pci = GetPointCloudInstance();
            if (pci == null)
                throw new Exception("No point cloud found in the document.");

            // Remember original file for reset
            StoreOriginalCloudInfo(pci);

            ApplyCropToPointCloud(pci, cropBox);
        }

        /// <summary>
        /// Applies the crop by setting the SelectionFilter natively on the PointCloudInstance.
        /// This is instantaneous and does not require extracting millions of points or saving files.
        /// </summary>
        private void ApplyCropToPointCloud(PointCloudInstance pci, BoundingBoxXYZ cropBox)
        {
            Exception setFilterEx = null;
            try
            {
                PointCloudService pcService = new PointCloudService(_doc);
                // Documentation: "The filter is provided in the coordinates of the Revit model."
                // So we do NOT transform to local space! We just use the cropBox which is in World Space!
                PointCloudFilter filter = pcService.CreateBoundingBoxFilter(cropBox);

                using (Transaction t = new Transaction(_doc, "Clip Point Cloud"))
                {
                    t.Start();
                    pci.SetSelectionFilter(filter);
                    pci.FilterAction = SelectionFilterAction.Isolate;
                    t.Commit();
                }

                _cropBoxStrategyUsed = "SetSelectionFilter (Native Isolate)";
                Debug.WriteLine("[Biruscan] Successfully applied SetSelectionFilter to PointCloudInstance!");
                return;
            }
            catch (Exception ex)
            {
                setFilterEx = ex;
                Debug.WriteLine("[Biruscan] SetSelectionFilter failed: " + ex.Message);
            }

            // Fallback to CropBox if SetSelectionFilter fails (older Revit versions)
            if (TrySetCropBox(pci, cropBox))
            {
                ClearSectionBoxes();
                Debug.WriteLine($"[Biruscan] Crop applied via {_cropBoxStrategyUsed}");
                return;
            }

            throw new ClippingException(
                "Failed to visually clip the point cloud.\n\n" +
                "SetSelectionFilter Error: " + (setFilterEx != null ? setFilterEx.Message : "None") + "\n\n" +
                "Revit 2024+ SetSelectionFilter failed, and no writable CropBox property was found.",
                cropBox, pci);
        }

        /// <summary>
        /// Attempts to set CropBox using reflection.
        /// Tries "CropBox" and other possible names.
        /// Also auto-discovers any BoundingBoxXYZ property that can be written.
        /// Returns true if successful.
        /// </summary>
        private bool TrySetCropBox(PointCloudInstance pci, BoundingBoxXYZ cropBox)
        {
            // Convert world-space crop to local space
            Transform cloudTransform = pci.GetTransform();
            BoundingBoxXYZ localCrop = new BoundingBoxXYZ { Transform = Transform.Identity };

            if (cloudTransform != null && !cloudTransform.IsIdentity)
            {
                Transform inverseTransform = cloudTransform.Inverse;
                localCrop.Min = inverseTransform.OfPoint(cropBox.Min);
                localCrop.Max = inverseTransform.OfPoint(cropBox.Max);
            }
            else
            {
                localCrop.Min = cropBox.Min;
                localCrop.Max = cropBox.Max;
            }

            // --- Try known property names ---
            string[] knownNames = { "CropBox", "ClipBox", "ClippingBox", "CropBounds",
                                    "DisplayCropBox", "PointCloudCrop", "DisplayBox" };

            foreach (string name in knownNames)
            {
                PropertyInfo prop = typeof(PointCloudInstance).GetProperty(name,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.SetProperty);
                if (prop != null && prop.CanWrite && prop.PropertyType == typeof(BoundingBoxXYZ))
                {
                    try
                    {
                        using (Transaction t = new Transaction(_doc, $"Crop Point Cloud ({name})"))
                        {
                            t.Start();
                            prop.SetValue(pci, localCrop);
                            t.Commit();
                        }
                        _cropBoxStrategyUsed = $"{name} Property (Reflection)";
                        _cropBoxProperty = prop;
                        Debug.WriteLine($"[Biruscan] CropBox set via property '{name}'");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Biruscan] Property '{name}' SetValue failed: {ex.Message}");
                    }
                }
            }

            // --- Auto-discover: any writable BoundingBoxXYZ property ---
            foreach (PropertyInfo prop in typeof(PointCloudInstance).GetProperties(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.SetProperty))
            {
                if (prop.PropertyType == typeof(BoundingBoxXYZ) && prop.CanWrite)
                {
                    // Skip already-tried names
                    if (knownNames.Any(n => n.Equals(prop.Name, StringComparison.Ordinal)))
                        continue;

                    try
                    {
                        using (Transaction t = new Transaction(_doc, $"Crop Point Cloud ({prop.Name})"))
                        {
                            t.Start();
                            prop.SetValue(pci, localCrop);
                            t.Commit();
                        }
                        _cropBoxStrategyUsed = $"{prop.Name} (Auto-discovered)";
                        _cropBoxProperty = prop;
                        Debug.WriteLine($"[Biruscan] CropBox set via auto-discovered property '{prop.Name}'");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Biruscan] Auto-discovered '{prop.Name}' failed: {ex.Message}");
                    }
                }
            }

            Debug.WriteLine("[Biruscan] No writable BoundingBoxXYZ property found on PointCloudInstance");
            return false;
        }

        // ===================================================================
        // TIER 2: FILTERED POINT CLOUD (PTS file)
        // ===================================================================

        /// <summary>
        /// DEAD PATH — not called. Revit's rcp engine does not load .pts files
        /// (PointCloudType.Create with a .pts path fails). Kept only for reference;
        /// live clipping uses SetSelectionFilter isolate on the single instance.
        /// </summary>
        private void CreateFilteredPointCloud(PointCloudInstance originalPci, BoundingBoxXYZ cropBox)
        {
            throw new NotSupportedException(
                "PTS re-import is not supported by Revit (rcp engine requires .rcp/.rcs). " +
                "Use SetSelectionFilter isolate instead.");
#pragma warning disable CS0162
            string tempFilePath = null;
            try
            {
#pragma warning restore CS0162
                // Determine temp file location (next to the RVT file)
                string docPath = _doc.PathName;
                string tempDir = !string.IsNullOrEmpty(docPath)
                    ? Path.GetDirectoryName(docPath)
                    : Path.GetTempPath();

                string key = string.IsNullOrEmpty(_doc.PathName) ? _doc.Title : _doc.PathName;
                // Clean up previous temp file if any
                if (_lastTempFilePath.ContainsKey(key) && _lastTempFilePath[key] != null)
                {
                    try { if (File.Exists(_lastTempFilePath[key])) File.Delete(_lastTempFilePath[key]); }
                    catch { }
                }

                tempFilePath = Path.Combine(tempDir,
                    "Biruscan_Clipped_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".pts");

                // Attempt 1: Read points using CreateMultiPlaneFilter (fast)
                PointCloudService pcService = new PointCloudService(_doc);
                List<XYZ> clippedPoints = pcService.GetPointsInRegion(originalPci, cropBox, 500000);

                // Attempt 2: If filter returned 0, try manual spatial filtering (slower but reliable)
                if (clippedPoints.Count == 0)
                {
                    Debug.WriteLine("[Biruscan] MultiPlaneFilter returned 0 points. Trying manual spatial filter...");
                    clippedPoints = GetPointsManualFilter(originalPci, cropBox, 500000);
                }

                if (clippedPoints.Count == 0)
                    throw new ClippingException(
                        "No points found in the specified clip region.\n\n" +
                        "The region may be too small or outside the point cloud.\n" +
                        "Try picking points that are clearly on the point cloud.",
                        cropBox, originalPci);

                // Map GetPoints local coords → world using the calibrated transform
                // (not raw GetTransform alone — re-imported/clipped clouds may already be world-space).
                Transform worldMap = pcService.GetWorldMappingTransform(originalPci);
                List<XYZ> worldPoints = new List<XYZ>(clippedPoints.Count);
                foreach (XYZ pt in clippedPoints)
                    worldPoints.Add(worldMap.OfPoint(pt));

                // Write PTS file (simple ASCII format: one point per line)
                using (StreamWriter writer = new StreamWriter(tempFilePath))
                {
                    foreach (XYZ pt in worldPoints)
                    {
                        writer.WriteLine($"{pt.X:F6} {pt.Y:F6} {pt.Z:F6}");
                    }
                }

                Debug.WriteLine($"[Biruscan] Wrote {worldPoints.Count} points to {tempFilePath}");

                // Create new PointCloudType + Instance
                using (Transaction t = new Transaction(_doc, "Create Clipped Point Cloud"))
                {
                    t.Start();

                    PointCloudType newType = PointCloudType.Create(_doc, "rcp", tempFilePath);
                    // Use Identity transform since points are already in world coordinates
                    PointCloudInstance newInstance = PointCloudInstance.Create(
                        _doc, newType.Id, Transform.Identity);

                    // Hide original in all views
                    HideElementInAllViews(originalPci.Id);

                    // Remove section boxes
                    ClearSectionBoxes();

                    t.Commit();
                }

                _lastTempFilePath[key] = tempFilePath;
                _cropBoxStrategyUsed = "Filtered Point Cloud (PTS file)";

                Debug.WriteLine("[Biruscan] Filtered point cloud created successfully");
            }
            catch (ClippingException)
            {
                // Re-throw clipping exceptions as-is (they have detailed diagnostics)
                try { if (tempFilePath != null && File.Exists(tempFilePath)) File.Delete(tempFilePath); }
                catch { }
                throw;
            }
            catch (Exception ex)
            {
                try { if (tempFilePath != null && File.Exists(tempFilePath)) File.Delete(tempFilePath); }
                catch { }

                throw new Exception(
                    "Failed to create clipped point cloud.\n\n" +
                    $"Error: {ex.Message}\n\n" +
                    "Possible solutions:\n" +
                    "1. Ensure the point cloud file is accessible and not corrupted\n" +
                    "2. Try picking points that are clearly within the point cloud\n" +
                    "3. Check the Visual Studio Output window for detailed diagnostics",
                    ex);
            }
        }

        /// <summary>
        /// Manual fallback: reads ALL points from the cloud and filters them by bounding box.
        /// Slower than CreateMultiPlaneFilter but more reliable when the filter approach fails.
        /// </summary>
        private List<XYZ> GetPointsManualFilter(PointCloudInstance instance, BoundingBoxXYZ worldCropBox, int maxPoints)
        {
            List<XYZ> filteredPoints = new List<XYZ>();
            
            try
            {
                // Read ALL points using the cloud's actual bounding box mapped to local space
                BoundingBoxXYZ worldBounds = instance.get_BoundingBox(null);
                Transform cloudToWorld = instance.GetTransform();
                Transform worldToCloud = cloudToWorld.Inverse;

                XYZ min = new XYZ(double.MaxValue, double.MaxValue, double.MaxValue);
                XYZ max = new XYZ(double.MinValue, double.MinValue, double.MinValue);
                
                // Get the 8 corners of worldBounds and map them to cloud space
                for (int i = 0; i < 8; i++)
                {
                    XYZ p = new XYZ(
                        (i & 1) == 0 ? worldBounds.Min.X : worldBounds.Max.X,
                        (i & 2) == 0 ? worldBounds.Min.Y : worldBounds.Max.Y,
                        (i & 4) == 0 ? worldBounds.Min.Z : worldBounds.Max.Z
                    );
                    XYZ lp = worldToCloud.OfPoint(p);
                    min = new XYZ(Math.Min(min.X, lp.X), Math.Min(min.Y, lp.Y), Math.Min(min.Z, lp.Z));
                    max = new XYZ(Math.Max(max.X, lp.X), Math.Max(max.Y, lp.Y), Math.Max(max.Z, lp.Z));
                }

                // Add a small buffer
                min = min - new XYZ(10, 10, 10);
                max = max + new XYZ(10, 10, 10);

                List<Plane> planes = new List<Plane>
                {
                    Plane.CreateByNormalAndOrigin(new XYZ(1, 0, 0), new XYZ(min.X, 0, 0)),
                    Plane.CreateByNormalAndOrigin(new XYZ(-1, 0, 0), new XYZ(max.X, 0, 0)),
                    Plane.CreateByNormalAndOrigin(new XYZ(0, 1, 0), new XYZ(0, min.Y, 0)),
                    Plane.CreateByNormalAndOrigin(new XYZ(0, -1, 0), new XYZ(0, max.Y, 0)),
                    Plane.CreateByNormalAndOrigin(new XYZ(0, 0, 1), new XYZ(0, 0, min.Z)),
                    Plane.CreateByNormalAndOrigin(new XYZ(0, 0, -1), new XYZ(0, 0, max.Z))
                };
                PointCloudFilter fullFilter = PointCloudFilterFactory.CreateMultiPlaneFilter(planes);
                PointCollection points = instance.GetPoints(fullFilter, 0.05, maxPoints);

                Transform worldToLocal = worldCropBox.Transform.Inverse;

                foreach (CloudPoint cp in points)
                {
                    // cp is in CLOUD space. We need it in WORLD space to check against worldCropBox
                    XYZ worldPt = cloudToWorld.OfPoint(new XYZ(cp.X, cp.Y, cp.Z));
                    
                    // Transform to box local space
                    XYZ boxPt = worldToLocal.OfPoint(worldPt);

                    if (boxPt.X >= worldCropBox.Min.X && boxPt.X <= worldCropBox.Max.X &&
                        boxPt.Y >= worldCropBox.Min.Y && boxPt.Y <= worldCropBox.Max.Y &&
                        boxPt.Z >= worldCropBox.Min.Z && boxPt.Z <= worldCropBox.Max.Z)
                    {
                        filteredPoints.Add(worldPt);
                    }
                }
                
                if (points.Count == 0)
                {
                    TaskDialog.Show("Debug", $"GetPoints with fullFilter returned 0 points. Min: {min}, Max: {max}");
                }
            }
            catch(Exception ex)
            {
                TaskDialog.Show("Manual Filter Error", ex.ToString());
            }

            return filteredPoints;
        }

        /// <summary>
        /// Custom exception with diagnostic information for clipping failures.
        /// </summary>
        private class ClippingException : Exception
        {
            public BoundingBoxXYZ CropBox { get; }
            public BoundingBoxXYZ CloudBounds { get; }

            public ClippingException(string message, BoundingBoxXYZ cropBox, PointCloudInstance pci)
                : base(BuildMessage(message, cropBox, pci))
            {
                CropBox = cropBox;
            }

            private static string BuildMessage(string baseMessage, BoundingBoxXYZ cropBox, PointCloudInstance pci)
            {
                string details = baseMessage + "\n\n--- Diagnostics ---\n";

                details += $"Crop region (world): X[{cropBox.Min.X:F3} to {cropBox.Max.X:F3}] " +
                           $"Y[{cropBox.Min.Y:F3} to {cropBox.Max.Y:F3}] " +
                           $"Z[{cropBox.Min.Z:F3} to {cropBox.Max.Z:F3}]\n";

                return details;
            }
        }

        // ===================================================================
        // UTILITY METHODS
        // ===================================================================

        private void StoreOriginalCloudInfo(PointCloudInstance pci)
        {
            try
            {
                string key = string.IsNullOrEmpty(_doc.PathName) ? _doc.Title : _doc.PathName;
                if (_originalFilePath.ContainsKey(key) && _originalFilePath[key] != null) return;

                // Read-only — no transaction (starting one without commit can interfere
                // with the following clip transaction on some Revit builds).
                PointCloudType pcType = _doc.GetElement(pci.GetTypeId()) as PointCloudType;
                if (pcType != null)
                {
                    ModelPath mp = pcType.GetExternalFileReference()?.GetPath();
                    _originalFilePath[key] = mp != null ? ModelPathUtils.ConvertModelPathToUserVisiblePath(mp) : null;
                    _originalTypeId[key] = pci.GetTypeId();
                    Debug.WriteLine($"[Biruscan] Original cloud: {_originalFilePath[key]}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Biruscan] StoreOriginalCloudInfo error: {ex.Message}");
            }
        }

        private void HideElementInAllViews(ElementId elementId)
        {
            var views = new FilteredElementCollector(_doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate)
                .ToList();

            foreach (View view in views)
            {
                try { view.HideElements(new List<ElementId> { elementId }); }
                catch { }
            }
        }

        private void ShowElementInAllViews(ElementId elementId)
        {
            var views = new FilteredElementCollector(_doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate)
                .ToList();

            foreach (View view in views)
            {
                try { view.UnhideElements(new List<ElementId> { elementId }); }
                catch { }
            }
        }

        /// <summary>
        /// Disables SectionBox on ALL 3D views.
        /// Called after applying CropBox to ensure no double-clipping.
        /// </summary>
        private void ClearSectionBoxes()
        {
            var views3d = new FilteredElementCollector(_doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .Where(v => !v.IsTemplate && !v.IsPerspective)
                .ToList();

            foreach (View3D view in views3d)
            {
                try { view.IsSectionBoxActive = false; }
                catch { }
            }
        }

        /// <summary>
        /// Intersects two axis-aligned bounding boxes.
        /// Returns null if they don't overlap.
        /// </summary>
        private BoundingBoxXYZ IntersectBoxes(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            XYZ iMin = new XYZ(
                Math.Max(a.Min.X, b.Min.X),
                Math.Max(a.Min.Y, b.Min.Y),
                Math.Max(a.Min.Z, b.Min.Z));

            XYZ iMax = new XYZ(
                Math.Min(a.Max.X, b.Max.X),
                Math.Min(a.Max.Y, b.Max.Y),
                Math.Min(a.Max.Z, b.Max.Z));

            if (iMin.X >= iMax.X || iMin.Y >= iMax.Y || iMin.Z >= iMax.Z)
                return null;

            return new BoundingBoxXYZ
            {
                Transform = Transform.Identity,
                Min = iMin,
                Max = iMax
            };
        }

        // ===================================================================
        // PUBLIC CUTTING METHODS
        // ===================================================================

        /// <summary>
        /// Cuts the point cloud with a slice along the specified axis.
        /// The slice is intersected with any existing crop (cumulative cutting).
        /// </summary>
        public void ApplySlice(SliceAxis axis, double position, double thickness)
        {
            BoundingBoxXYZ currentCrop = GetCurrentCropBox();
            double half = thickness / 2.0;

            BoundingBoxXYZ sliceBox = new BoundingBoxXYZ { Transform = Transform.Identity };

            switch (axis)
            {
                case SliceAxis.Z:
                    sliceBox.Min = new XYZ(currentCrop.Min.X, currentCrop.Min.Y, position - half);
                    sliceBox.Max = new XYZ(currentCrop.Max.X, currentCrop.Max.Y, position + half);
                    break;
                case SliceAxis.X:
                    sliceBox.Min = new XYZ(position - half, currentCrop.Min.Y, currentCrop.Min.Z);
                    sliceBox.Max = new XYZ(position + half, currentCrop.Max.Y, currentCrop.Max.Z);
                    break;
                case SliceAxis.Y:
                    sliceBox.Min = new XYZ(currentCrop.Min.X, position - half, currentCrop.Min.Z);
                    sliceBox.Max = new XYZ(currentCrop.Max.X, position + half, currentCrop.Max.Z);
                    break;
            }

            var finalCrop = IntersectBoxes(currentCrop, sliceBox);
            if (finalCrop == null)
                throw new Exception("Slice region does not overlap with the current point cloud crop.");

            // Persist SliceBox + StepDistance so Clipping Manager / StepSlice / rebuild work.
            ClippingRecords.Add(new ClippingRecord
            {
                Name = "Slice_" + DateTime.Now.ToString("HHmmss"),
                ClipType = ClipType.Slice,
                Axis = axis,
                SlicePosition = position,
                SliceThickness = thickness,
                StepDistance = thickness,
                IsActive = true,
                SliceBox = finalCrop,
                MinX = finalCrop.Min.X, MaxX = finalCrop.Max.X,
                MinY = finalCrop.Min.Y, MaxY = finalCrop.Max.Y,
                MinZ = finalCrop.Min.Z, MaxZ = finalCrop.Max.Z
            });

            RebuildCropFromActiveGroup("Create Slice");
        }

        /// <summary>
        /// Creates a horizontal slice centered on the given Z elevation.
        /// </summary>
        public void ApplyHorizontalSlice(double elevation, double thickness = 0.5)
        {
            double half = thickness / 2.0;

            // Span the cloud's real XY footprint instead of an arbitrary ±10000 world box:
            // when the cloud sits far from the internal origin (survey/shared coordinates) a
            // world-centred box misses it entirely, and keeping the extents on the cloud also
            // keeps the clip-plane origins within Revit's design limits.
            double xMin = -10000, xMax = 10000, yMin = -10000, yMax = 10000;
            BoundingBoxXYZ wb = GetFullCloudBounds();
            if (wb != null)
            {
                const double margin = 1.0;
                xMin = wb.Min.X - margin; xMax = wb.Max.X + margin;
                yMin = wb.Min.Y - margin; yMax = wb.Max.Y + margin;
            }

            BoundingBoxXYZ sliceBox = new BoundingBoxXYZ
            {
                Transform = Transform.Identity,
                Min = new XYZ(xMin, yMin, elevation - half),
                Max = new XYZ(xMax, yMax, elevation + half)
            };

            // Record first, then rebuild once (same path as screen slices). Avoids a double
            // ApplyCrop+Rebuild that could leave Isolate on while the record was incomplete.
            ClippingRecords.Add(new ClippingRecord
            {
                Name = "HorizSlice_" + DateTime.Now.ToString("HHmmss"),
                ClipType = ClipType.Slice,
                Axis = SliceAxis.Z,
                IsActive = true,
                SliceBox = sliceBox,
                StepDistance = thickness,
                SliceThickness = thickness,
                MinX = sliceBox.Min.X, MaxX = sliceBox.Max.X,
                MinY = sliceBox.Min.Y, MaxY = sliceBox.Max.Y,
                MinZ = sliceBox.Min.Z, MaxZ = sliceBox.Max.Z
            });

            RebuildCropFromActiveGroup("Create Horizontal Slice");
        }

        /// <summary>
        /// Anchor point for screen-aligned cut/slice boxes. Uses the cloud's bounding-box
        /// centre when available, otherwise the view origin. Anchoring at the cloud (rather
        /// than view.Origin, which can be far from the model in 3D/section views or with a
        /// survey offset) keeps the resulting clip-plane origins near the cloud and within
        /// Revit's design limits — a far origin throws "input point lies outside of Revit
        /// design limits. Parameter name: origin".
        /// </summary>
        private XYZ ScreenCutAnchor(View view)
        {
            BoundingBoxXYZ wb = GetFullCloudBounds();
            if (wb != null) return (wb.Min + wb.Max).Multiply(0.5);
            return view.Origin;
        }

        private static double Clamp(double v, double lo, double hi)
        {
            if (lo > hi) { double t = lo; lo = hi; hi = t; }
            return v < lo ? lo : (v > hi ? hi : v);
        }

        /// <summary>
        /// Projects the full cloud bounding box into the given view-aligned frame
        /// (origin = ScreenCutAnchor) and returns the local-space axis-aligned extent.
        /// Screen cuts are clamped to this extent so every clip-plane origin stays
        /// within Revit's design limits: the cloud is already rendered, so its bounds
        /// are inside the limits, and any point within (cloudAABB + small margin) is too.
        /// Without this, an arbitrary ±depth or a far PickBox corner throws
        /// "input point lies outside of Revit design limits. Parameter name: origin".
        /// </summary>
        private bool TryGetCloudLocalExtent(Transform viewTransform, out XYZ localMin, out XYZ localMax)
        {
            localMin = null; localMax = null;
            BoundingBoxXYZ wb = GetFullCloudBounds();
            if (wb == null) return false;

            Transform inv = viewTransform.Inverse;
            XYZ[] corners =
            {
                new XYZ(wb.Min.X, wb.Min.Y, wb.Min.Z), new XYZ(wb.Max.X, wb.Min.Y, wb.Min.Z),
                new XYZ(wb.Min.X, wb.Max.Y, wb.Min.Z), new XYZ(wb.Max.X, wb.Max.Y, wb.Min.Z),
                new XYZ(wb.Min.X, wb.Min.Y, wb.Max.Z), new XYZ(wb.Max.X, wb.Min.Y, wb.Max.Z),
                new XYZ(wb.Min.X, wb.Max.Y, wb.Max.Z), new XYZ(wb.Max.X, wb.Max.Y, wb.Max.Z)
            };
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            foreach (XYZ c in corners)
            {
                XYZ l = inv.OfPoint(c);
                if (l.X < minX) minX = l.X; if (l.X > maxX) maxX = l.X;
                if (l.Y < minY) minY = l.Y; if (l.Y > maxY) maxY = l.Y;
                if (l.Z < minZ) minZ = l.Z; if (l.Z > maxZ) maxZ = l.Z;
            }
            localMin = new XYZ(minX, minY, minZ);
            localMax = new XYZ(maxX, maxY, maxZ);
            return true;
        }

        /// <summary>
        /// True if all six bounding planes of the (possibly oriented) box can be constructed
        /// within Revit's design limits. False when the cloud is georeferenced too far from
        /// the internal origin for Plane.CreateByNormalAndOrigin (survey/State-Plane clouds).
        /// </summary>
        private bool TryBuildBoxPlanes(BoundingBoxXYZ box)
        {
            try
            {
                Transform t = box.Transform;
                XYZ min = box.Min, max = box.Max;
                Plane.CreateByNormalAndOrigin(t.BasisX,  t.OfPoint(new XYZ(min.X, 0, 0)));
                Plane.CreateByNormalAndOrigin(-t.BasisX, t.OfPoint(new XYZ(max.X, 0, 0)));
                Plane.CreateByNormalAndOrigin(t.BasisY,  t.OfPoint(new XYZ(0, min.Y, 0)));
                Plane.CreateByNormalAndOrigin(-t.BasisY, t.OfPoint(new XYZ(0, max.Y, 0)));
                Plane.CreateByNormalAndOrigin(t.BasisZ,  t.OfPoint(new XYZ(0, 0, min.Z)));
                Plane.CreateByNormalAndOrigin(-t.BasisZ, t.OfPoint(new XYZ(0, 0, max.Z)));
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Creates a horizontal slice based on the screen's view direction.
        /// The thickness is determined by two picked points.
        /// </summary>
        public void ApplyScreenHorizontalSlice(XYZ p1, XYZ p2, View view)
        {
            Transform viewTransform = Transform.Identity;
            viewTransform.Origin = ScreenCutAnchor(view);
            viewTransform.BasisX = view.RightDirection.Normalize();
            viewTransform.BasisY = view.UpDirection.Normalize();
            viewTransform.BasisZ = view.ViewDirection.Normalize();

            Transform inverseTransform = viewTransform.Inverse;
            XYZ localP1 = inverseTransform.OfPoint(p1);
            XYZ localP2 = inverseTransform.OfPoint(p2);

            double minY = Math.Min(localP1.Y, localP2.Y);
            double maxY = Math.Max(localP1.Y, localP2.Y);
            double thickness = maxY - minY;
            if (thickness < 0.05)
                throw new Exception("Slice thickness is too small. Pick two points farther apart to define the slice band.");

            // Span the cloud's actual extent across the slice (not an arbitrary ±10000 ft)
            // so the clip-plane origins stay within Revit's design limits.
            double xMin = -10000, xMax = 10000, zMin = -10000, zMax = 10000;
            if (TryGetCloudLocalExtent(viewTransform, out XYZ cMin, out XYZ cMax))
            {
                const double margin = 1.0;
                xMin = cMin.X - margin; xMax = cMax.X + margin;
                zMin = cMin.Z - margin; zMax = cMax.Z + margin;
            }

            BoundingBoxXYZ sliceBox = new BoundingBoxXYZ
            {
                Transform = viewTransform,
                Min = new XYZ(xMin, minY, zMin),
                Max = new XYZ(xMax, maxY, zMax)
            };

            if (!TryBuildBoxPlanes(sliceBox))
                throw new Exception(
                    "Could not build slice planes for this point cloud (often far from project origin).\n" +
                    "Try placing the cloud closer to the origin, or use a thinner region near the model.");

            ClippingRecords.Add(new ClippingRecord
            {
                Name = "ScreenHorizSlice_" + DateTime.Now.ToString("HHmmss"),
                ClipType = ClipType.Slice,
                Axis = SliceAxis.Y, // In screen space, Up is Y
                IsActive = true,
                SliceBox = sliceBox,
                StepDistance = thickness,
                SliceThickness = thickness,
                MinX = sliceBox.Min.X, MaxX = sliceBox.Max.X,
                MinY = sliceBox.Min.Y, MaxY = sliceBox.Max.Y,
                MinZ = sliceBox.Min.Z, MaxZ = sliceBox.Max.Z
            });

            RebuildCropFromActiveGroup("Create Horizontal Slice");
        }

        /// <summary>
        /// Creates a vertical slice based on the screen's view direction.
        /// The thickness is determined by two picked points.
        /// </summary>
        public void ApplyScreenVerticalSlice(XYZ p1, XYZ p2, View view)
        {
            Transform viewTransform = Transform.Identity;
            viewTransform.Origin = ScreenCutAnchor(view);
            viewTransform.BasisX = view.RightDirection.Normalize();
            viewTransform.BasisY = view.UpDirection.Normalize();
            viewTransform.BasisZ = view.ViewDirection.Normalize();

            Transform inverseTransform = viewTransform.Inverse;
            XYZ localP1 = inverseTransform.OfPoint(p1);
            XYZ localP2 = inverseTransform.OfPoint(p2);

            double minX = Math.Min(localP1.X, localP2.X);
            double maxX = Math.Max(localP1.X, localP2.X);
            double thickness = maxX - minX;
            if (thickness < 0.05)
                throw new Exception("Slice thickness is too small. Pick two points farther apart to define the slice band.");

            // Span the cloud's actual extent across the slice (not an arbitrary ±10000 ft)
            // so the clip-plane origins stay within Revit's design limits.
            double yMin = -10000, yMax = 10000, zMin = -10000, zMax = 10000;
            if (TryGetCloudLocalExtent(viewTransform, out XYZ cMin, out XYZ cMax))
            {
                const double margin = 1.0;
                yMin = cMin.Y - margin; yMax = cMax.Y + margin;
                zMin = cMin.Z - margin; zMax = cMax.Z + margin;
            }

            BoundingBoxXYZ sliceBox = new BoundingBoxXYZ
            {
                Transform = viewTransform,
                Min = new XYZ(minX, yMin, zMin),
                Max = new XYZ(maxX, yMax, zMax)
            };

            if (!TryBuildBoxPlanes(sliceBox))
                throw new Exception(
                    "Could not build slice planes for this point cloud (often far from project origin).\n" +
                    "Try placing the cloud closer to the origin, or use a thinner region near the model.");

            ClippingRecords.Add(new ClippingRecord
            {
                Name = "ScreenVertSlice_" + DateTime.Now.ToString("HHmmss"),
                ClipType = ClipType.Slice,
                Axis = SliceAxis.X,
                IsActive = true,
                SliceBox = sliceBox,
                StepDistance = thickness,
                SliceThickness = thickness,
                MinX = sliceBox.Min.X, MaxX = sliceBox.Max.X,
                MinY = sliceBox.Min.Y, MaxY = sliceBox.Max.Y,
                MinZ = sliceBox.Min.Z, MaxZ = sliceBox.Max.Z
            });

            RebuildCropFromActiveGroup("Create Vertical Slice");
        }

        /// <summary>
        /// Steps the LAST slice (most recently created) forward or backward along its
        /// normal. Always targets that last slice even when many older slices / crops exist.
        /// Older slices are ignored by the filter (see BuildConsolidatedPlanes) so stepping
        /// is never blocked by earlier sub-slices.
        /// </summary>
        public void StepSlice(bool forward)
        {
            // Prefer last active slice; else last slice of any kind and re-activate it.
            ClippingRecord lastSlice = ClippingRecords
                .LastOrDefault(r => r.ClipType == ClipType.Slice && r.IsActive);
            if (lastSlice == null)
                lastSlice = ClippingRecords.LastOrDefault(r => r.ClipType == ClipType.Slice);

            if (lastSlice == null)
                throw new Exception(
                    "No slice found to step.\n\n" +
                    "Create a Horizontal or Vertical slice first, then use Slice Forward / Backward.");

            // Ensure SliceBox exists (restore from matrix if needed).
            if (lastSlice.SliceBox == null)
            {
                try { lastSlice.RestoreGeometry(); } catch { }
            }
            if (lastSlice.SliceBox == null)
                throw new Exception(
                    "The last slice has no geometry to step.\n\n" +
                    "Clear Cuts and create a new Horizontal / Vertical slice.");

            lastSlice.IsActive = true;

            // Step size: StepDistance → SliceThickness → box thickness along axis → 0.5 ft
            double stepDist = ResolveSliceStepDistance(lastSlice);
            double step = forward ? stepDist : -stepDist;

            Transform src = lastSlice.SliceBox.Transform ?? Transform.Identity;
            // Clone transform — never mutate a shared/cached Transform in place.
            Transform t = Transform.Identity;
            t.BasisX = src.BasisX;
            t.BasisY = src.BasisY;
            t.BasisZ = src.BasisZ;
            t.Origin = src.Origin;

            XYZ moveVector;
            if (lastSlice.Axis == SliceAxis.Z)
                moveVector = XYZ.BasisZ.Multiply(step);
            else if (lastSlice.Axis == SliceAxis.Y)
                moveVector = t.BasisY.Normalize().Multiply(step);
            else
                moveVector = t.BasisX.Normalize().Multiply(step); // Axis.X or default

            t.Origin = t.Origin + moveVector;

            var steppedBox = new BoundingBoxXYZ
            {
                Transform = t,
                Min = lastSlice.SliceBox.Min,
                Max = lastSlice.SliceBox.Max
            };
            lastSlice.SliceBox = steppedBox;
            lastSlice.StepDistance = stepDist;
            if (lastSlice.SliceThickness < 1e-6)
                lastSlice.SliceThickness = stepDist;

            RebuildCropFromActiveGroup(forward ? "Slice Forward" : "Slice Backward");
        }

        /// <summary>Resolves a non-zero step for Forward/Back even if StepDistance was never set.</summary>
        private static double ResolveSliceStepDistance(ClippingRecord slice)
        {
            if (slice.StepDistance > 1e-6)
                return slice.StepDistance;
            if (slice.SliceThickness > 1e-6)
                return slice.SliceThickness;

            if (slice.SliceBox != null)
            {
                XYZ min = slice.SliceBox.Min;
                XYZ max = slice.SliceBox.Max;
                double along;
                if (slice.Axis == SliceAxis.Z)
                    along = Math.Abs(max.Z - min.Z);
                else if (slice.Axis == SliceAxis.Y)
                    along = Math.Abs(max.Y - min.Y);
                else
                    along = Math.Abs(max.X - min.X);
                if (along > 1e-6)
                    return along;
            }

            return 0.5; // default 0.5 ft (~150 mm)
        }

        /// <summary>
        /// Gets the bounding box that represents the intersection of all active cuts.
        /// </summary>
        public BoundingBoxXYZ GetActiveBoundingBox(BoundingBoxXYZ fullBounds)
        {
            if (fullBounds == null) return null;

            double minX = fullBounds.Min.X, minY = fullBounds.Min.Y, minZ = fullBounds.Min.Z;
            double maxX = fullBounds.Max.X, maxY = fullBounds.Max.Y, maxZ = fullBounds.Max.Z;
            bool anyClip = false;

            foreach (var record in ClippingRecords.Where(r => r.IsActive))
            {
                BoundingBoxXYZ worldBox = null;
                if (record.SliceBox != null)
                {
                    worldBox = GetWorldBoundingBox(record.SliceBox);
                }
                else if (record.Planes != null && record.Planes.Count > 0)
                {
                    // Plane-only polygon cuts often have Min/Max left at 0 — use plane origins.
                    worldBox = BoundsFromPlanes(record.Planes, fullBounds);
                }
                else
                {
                    // Degenerate 0,0,0 boxes (unset Min/Max) must not wipe the section box.
                    if (Math.Abs(record.MaxX - record.MinX) < 1e-9
                        && Math.Abs(record.MaxY - record.MinY) < 1e-9
                        && Math.Abs(record.MaxZ - record.MinZ) < 1e-9)
                        continue;
                    worldBox = record.ToBoundingBox();
                }

                if (worldBox == null) continue;
                if (worldBox.Min.X >= worldBox.Max.X || worldBox.Min.Y >= worldBox.Max.Y
                    || worldBox.Min.Z >= worldBox.Max.Z)
                    continue;

                anyClip = true;
                minX = Math.Max(minX, worldBox.Min.X);
                minY = Math.Max(minY, worldBox.Min.Y);
                minZ = Math.Max(minZ, worldBox.Min.Z);
                maxX = Math.Min(maxX, worldBox.Max.X);
                maxY = Math.Min(maxY, worldBox.Max.Y);
                maxZ = Math.Min(maxZ, worldBox.Max.Z);
            }

            if (!anyClip || minX > maxX || minY > maxY || minZ > maxZ)
                return fullBounds;

            return new BoundingBoxXYZ
            {
                Min = new XYZ(minX, minY, minZ),
                Max = new XYZ(maxX, maxY, maxZ)
            };
        }

        private static BoundingBoxXYZ BoundsFromPlanes(List<Plane> planes, BoundingBoxXYZ fullBounds)
        {
            if (planes == null || planes.Count == 0) return null;
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            int n = 0;
            foreach (Plane pl in planes)
            {
                if (pl == null) continue;
                XYZ o = pl.Origin;
                if (o.X < minX) minX = o.X; if (o.X > maxX) maxX = o.X;
                if (o.Y < minY) minY = o.Y; if (o.Y > maxY) maxY = o.Y;
                if (o.Z < minZ) minZ = o.Z; if (o.Z > maxZ) maxZ = o.Z;
                n++;
            }
            if (n < 2) return null;
            // Pad slightly so section box is not zero-thickness.
            const double pad = 0.5;
            return new BoundingBoxXYZ
            {
                Min = new XYZ(minX - pad, minY - pad, minZ - pad),
                Max = new XYZ(maxX + pad, maxY + pad, maxZ + pad)
            };
        }

        private BoundingBoxXYZ GetWorldBoundingBox(BoundingBoxXYZ box)
        {
            if (box == null) return null;
            Transform tr = box.Transform ?? Transform.Identity;
            XYZ[] corners = new XYZ[8];
            corners[0] = new XYZ(box.Min.X, box.Min.Y, box.Min.Z);
            corners[1] = new XYZ(box.Max.X, box.Min.Y, box.Min.Z);
            corners[2] = new XYZ(box.Min.X, box.Max.Y, box.Min.Z);
            corners[3] = new XYZ(box.Max.X, box.Max.Y, box.Min.Z);
            corners[4] = new XYZ(box.Min.X, box.Min.Y, box.Max.Z);
            corners[5] = new XYZ(box.Max.X, box.Min.Y, box.Max.Z);
            corners[6] = new XYZ(box.Min.X, box.Max.Y, box.Max.Z);
            corners[7] = new XYZ(box.Max.X, box.Max.Y, box.Max.Z);

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

            foreach (var c in corners)
            {
                XYZ pt = tr.OfPoint(c);
                minX = Math.Min(minX, pt.X);
                minY = Math.Min(minY, pt.Y);
                minZ = Math.Min(minZ, pt.Z);
                maxX = Math.Max(maxX, pt.X);
                maxY = Math.Max(maxY, pt.Y);
                maxZ = Math.Max(maxZ, pt.Z);
            }

            return new BoundingBoxXYZ { Min = new XYZ(minX, minY, minZ), Max = new XYZ(maxX, maxY, maxZ) };
        }

        /// <summary>
        /// Cuts the point cloud with a rectangular region (two diagonal corners).
        /// Extends through the full Z height. Cumulative with existing crop.
        /// </summary>
        public void CutPointCloudRectangular(XYZ corner1, XYZ corner2)
        {
            BoundingBoxXYZ fullBounds = GetFullCloudBounds();
            BoundingBoxXYZ currentCrop = GetCurrentCropBox();

            BoundingBoxXYZ rectBox = new BoundingBoxXYZ
            {
                Transform = Transform.Identity,
                Min = new XYZ(
                    Math.Min(corner1.X, corner2.X),
                    Math.Min(corner1.Y, corner2.Y),
                    fullBounds.Min.Z),
                Max = new XYZ(
                    Math.Max(corner1.X, corner2.X),
                    Math.Max(corner1.Y, corner2.Y),
                    fullBounds.Max.Z)
            };

            var finalCrop = IntersectBoxes(currentCrop, rectBox);
            if (finalCrop == null)
                throw new Exception("Rectangular cut region does not overlap with the current point cloud crop.");

            ClippingRecords.Add(new ClippingRecord
            {
                Name = "RectCut_" + DateTime.Now.ToString("HHmmss"),
                ClipType = ClipType.Rectangular,
                IsActive = true,
                SliceBox = finalCrop
            });

            RebuildCropFromActiveGroup();
        }

        /// <summary>
        /// Cuts the point cloud with a screen-aligned rectangular region (like Cloudworx).
        /// Extends through the cloud depth. Combines with existing active cuts (AND).
        /// </summary>
        public void CutPointCloudScreenAligned(XYZ corner1, XYZ corner2, View view)
        {
            Transform viewTransform = Transform.Identity;
            viewTransform.Origin = ScreenCutAnchor(view);
            viewTransform.BasisX = view.RightDirection.Normalize();
            viewTransform.BasisY = view.UpDirection.Normalize();
            viewTransform.BasisZ = view.ViewDirection.Normalize();

            Transform inverseTransform = viewTransform.Inverse;

            XYZ localCorner1 = inverseTransform.OfPoint(corner1);
            XYZ localCorner2 = inverseTransform.OfPoint(corner2);

            double minX = Math.Min(localCorner1.X, localCorner2.X);
            double maxX = Math.Max(localCorner1.X, localCorner2.X);
            double minY = Math.Min(localCorner1.Y, localCorner2.Y);
            double maxY = Math.Max(localCorner1.Y, localCorner2.Y);

            // Clamp the screen rectangle to the cloud's own extent and use the cloud's
            // depth (instead of an arbitrary ±10000 ft) so every clip-plane origin stays
            // inside Revit's design limits. Prevents the "input point lies outside of
            // Revit design limits. Parameter name: origin" error when the cloud sits far
            // from the internal origin (survey/shared coordinates) or a PickBox corner
            // lands off-cloud in a zoomed-out/3D view.
            double minZ = -10000.0, maxZ = 10000.0;
            bool hadCloudExtent = TryGetCloudLocalExtent(viewTransform, out XYZ cloudMin, out XYZ cloudMax);
            if (hadCloudExtent)
            {
                const double margin = 1.0; // feet
                minX = Clamp(minX, cloudMin.X - margin, cloudMax.X + margin);
                maxX = Clamp(maxX, cloudMin.X - margin, cloudMax.X + margin);
                minY = Clamp(minY, cloudMin.Y - margin, cloudMax.Y + margin);
                maxY = Clamp(maxY, cloudMin.Y - margin, cloudMax.Y + margin);
                minZ = cloudMin.Z - margin;
                maxZ = cloudMax.Z + margin;
            }

            // Large / far clouds often produce PickBox corners that project outside the
            // cloud AABB. After clamp that can collapse the rectangle to a line/point —
            // RebuildCrop then silently skips the planes and the cloud looks unchanged.
            const double minExtentFt = 0.05; // ~15 mm
            if (maxX - minX < minExtentFt || maxY - minY < minExtentFt)
            {
                throw new Exception(
                    "Cut region is too small or misses the point cloud.\n\n" +
                    "On large scans, draw the rectangle while zoomed so the box sits clearly " +
                    "over the cloud (not empty space). Try an orthographic 3D view facing the region.");
            }
            if (maxZ - minZ < minExtentFt)
            {
                throw new Exception(
                    "Could not determine the point cloud depth for this cut.\n\n" +
                    "The cloud bounding box may be missing or invalid. Re-load the RCP/RCS and try again.");
            }

            BoundingBoxXYZ screenBox = new BoundingBoxXYZ
            {
                Transform = viewTransform,
                Min = new XYZ(minX, minY, minZ),
                Max = new XYZ(maxX, maxY, maxZ)
            };

            // Near cloud: build clip planes (instant per-instance isolate; integrates with the
            // Clipping Manager). Far-georeferenced cloud: Plane can't be constructed at the
            // cloud's coordinates, so fall back to an oriented 3D section box (places by box, not
            // by plane, so it bypasses the design-limit check) — same draw-a-rectangle workflow.
            if (TryBuildBoxPlanes(screenBox))
            {
                // Keep previous cuts — new crop is AND-combined with all active clips.
                ClippingRecords.Add(new ClippingRecord
                {
                    Name = "ScreenCut_" + DateTime.Now.ToString("HHmmss"),
                    ClipType = ClipType.Rectangular,
                    IsActive = true,
                    SliceBox = screenBox,
                    MinX = screenBox.Min.X, MaxX = screenBox.Max.X,
                    MinY = screenBox.Min.Y, MaxY = screenBox.Max.Y,
                    MinZ = screenBox.Min.Z, MaxZ = screenBox.Max.Z
                });
                RebuildCropFromActiveGroup("Create Rectangular Cut");
            }
            else
            {
                // Far-georeferenced cloud (survey/State-Plane coordinates): Revit cannot build
                // clip planes at the cloud's coordinates (the ~20-mile design limit), so
                // rectangular/polygon cuts are unavailable at this distance. Horizontal slices
                // still work. PARKED: the real fix is a custom point-cloud engine (the
                // CloudWorx/FARO approach), which enforces cuts in its own coordinate space.
                throw new Exception(
                    "This point cloud is georeferenced very far from the project origin, so Revit can't " +
                    "build clip planes for a rectangular cut at this location. (Horizontal slices still work.)");
            }
        }

        /// <summary>
        /// Cuts the point cloud with a screen-aligned polygonal region.
        /// Extends infinitely into the screen. Replaces existing crop box.
        /// </summary>
        public void CutPointCloudScreenAlignedPolygonal(List<XYZ> points, View view)
        {
            Transform viewTransform = Transform.Identity;
            viewTransform.Origin = ScreenCutAnchor(view);
            viewTransform.BasisX = view.RightDirection.Normalize();
            viewTransform.BasisY = view.UpDirection.Normalize();
            viewTransform.BasisZ = view.ViewDirection.Normalize();
            
            Transform inverseTransform = viewTransform.Inverse;
            List<XYZ> localPoints = new List<XYZ>();
            foreach (var pt in points) localPoints.Add(inverseTransform.OfPoint(pt));

            double signedArea = 0;
            for (int i = 0; i < localPoints.Count; i++)
            {
                XYZ p1 = localPoints[i];
                XYZ p2 = localPoints[(i + 1) % localPoints.Count];
                signedArea += (p1.X * p2.Y - p2.X * p1.Y);
            }

            bool isClockwise = signedArea < 0;
            if (isClockwise)
            {
                points.Reverse();
                localPoints.Reverse();
            }

            // Build edge half-planes for the full polygon (used for convex path + fallback)
            List<Plane> planes = new List<Plane>();
            for (int i = 0; i < localPoints.Count; i++)
            {
                XYZ p1 = localPoints[i];
                XYZ p2 = localPoints[(i + 1) % localPoints.Count];
                XYZ edge = p2 - p1;
                XYZ localNormal = new XYZ(-edge.Y, edge.X, 0).Normalize();
                XYZ worldNormal = viewTransform.OfVector(localNormal);
                XYZ worldOrigin = points[i];
                planes.Add(Plane.CreateByNormalAndOrigin(worldNormal, worldOrigin));
            }

            // Cap the through-screen depth at the cloud's own extent (not ±10000 ft) so the
            // depth-plane origins stay within Revit's design limits.
            double polyZMin = -10000.0, polyZMax = 10000.0;
            if (TryGetCloudLocalExtent(viewTransform, out XYZ pcMin, out XYZ pcMax))
            {
                const double margin = 1.0;
                polyZMin = pcMin.Z - margin;
                polyZMax = pcMax.Z + margin;
            }

            XYZ localZ = new XYZ(0, 0, 1);
            XYZ worldZ = viewTransform.OfVector(localZ);
            planes.Add(Plane.CreateByNormalAndOrigin(worldZ, viewTransform.OfPoint(new XYZ(0, 0, polyZMin))));
            planes.Add(Plane.CreateByNormalAndOrigin(-worldZ, viewTransform.OfPoint(new XYZ(0, 0, polyZMax))));

            PointCloudInstance pci = GetPointCloudInstance();
            if (pci == null)
                throw new Exception("No point cloud found in the document.");

            // Plane origins must stay inside Revit's design limits (survey / large site clouds).
            try
            {
                foreach (Plane pl in planes)
                {
                    // Touch Origin/Normal so construction failures surface early.
                    if (pl == null) throw new Exception("Invalid clip plane.");
                    _ = pl.Origin; _ = pl.Normal;
                }
            }
            catch (Exception ex)
            {
                throw new Exception(
                    "Could not build polygonal clip planes for this point cloud.\n\n" +
                    "Large or far-from-origin (survey) scans sometimes exceed Revit's plane limits.\n" +
                    "Try Rectangular Cut, a Horizontal Slice, or move the cloud closer to the project origin.\n\n" +
                    "Details: " + ex.Message);
            }

            // SINGLE PointCloudInstance only — never create helper/clone instances.
            // Revit multi-plane filters are convex (AND of half-spaces) only.
            // Concave shapes (cross / L / arrow): use convex hull so one filter still works.
            // Exact concave notches would require multiple instances (clones) or a custom
            // point-cloud engine — both are out of scope per product rule: no clones.
            if (!ConvexDecomposition.IsConvex(localPoints))
            {
                List<XYZ> hull = ConvexDecomposition.ConvexHull2D(localPoints);
                if (hull != null && hull.Count >= 3)
                {
                    planes = new List<Plane>();
                    for (int i = 0; i < hull.Count; i++)
                    {
                        XYZ p1 = hull[i];
                        XYZ p2 = hull[(i + 1) % hull.Count];
                        XYZ edge = p2 - p1;
                        if (edge.GetLength() < 1e-9) continue;
                        XYZ localNormal = new XYZ(-edge.Y, edge.X, 0).Normalize();
                        XYZ worldNormal = viewTransform.OfVector(localNormal);
                        XYZ worldOrigin = viewTransform.OfPoint(p1);
                        planes.Add(Plane.CreateByNormalAndOrigin(worldNormal, worldOrigin));
                    }
                    planes.Add(Plane.CreateByNormalAndOrigin(worldZ, viewTransform.OfPoint(new XYZ(0, 0, polyZMin))));
                    planes.Add(Plane.CreateByNormalAndOrigin(-worldZ, viewTransform.OfPoint(new XYZ(0, 0, polyZMax))));
                }
            }

            // Local AABB so Section Box / GetActiveBoundingBox do not collapse to 0,0,0.
            double bMinX = double.MaxValue, bMinY = double.MaxValue;
            double bMaxX = double.MinValue, bMaxY = double.MinValue;
            foreach (XYZ lp in localPoints)
            {
                if (lp.X < bMinX) bMinX = lp.X; if (lp.X > bMaxX) bMaxX = lp.X;
                if (lp.Y < bMinY) bMinY = lp.Y; if (lp.Y > bMaxY) bMaxY = lp.Y;
            }

            ClippingRecords.Add(new ClippingRecord
            {
                Name = "ScreenPolyCut_" + DateTime.Now.ToString("HHmmss"),
                ClipType = ClipType.Polygonal,
                IsActive = true,
                Planes = planes,
                SubFilterPlanes = null, // never multi-instance OR pieces
                SliceBox = new BoundingBoxXYZ
                {
                    Transform = viewTransform,
                    Min = new XYZ(bMinX, bMinY, polyZMin),
                    Max = new XYZ(bMaxX, bMaxY, polyZMax)
                }
            });

            RebuildCropFromActiveGroup("Create Polygonal Cut");
        }

        /// <summary>
        /// Computes the Cartesian product of plane sets for multiple clips.
        /// Used to generate combinations of filters for concave multi-instance cuts.
        /// </summary>
        private static List<List<Plane>> CombineClipPlanes(List<List<List<Plane>>> clipOptions)
        {
            var result = new List<List<Plane>>();
            result.Add(new List<Plane>()); // start with one empty combination

            foreach (var options in clipOptions)
            {
                var nextResult = new List<List<Plane>>();
                foreach (var current in result)
                {
                    foreach (var option in options)
                    {
                        var combined = new List<Plane>(current);
                        combined.AddRange(option);
                        nextResult.Add(combined);
                    }
                }
                result = nextResult;
            }
            return result;
        }

        public void RebuildCropFromActiveGroup(string transactionName = "Toggle Cut Visibility")
        {
            // Resolve primaries after cleaning known clones so we never target a clone.
            string docKey = string.IsNullOrEmpty(_doc.PathName) ? _doc.Title : _doc.PathName;
            if (!_cloneInstancesByDoc.ContainsKey(docKey))
                _cloneInstancesByDoc[docKey] = new List<ElementId>();
            var cloneIds = _cloneInstancesByDoc[docKey];

            var activeGroup = GetActiveGroup();
            // Restore SliceBox / Planes from serialized arrays if needed (after manager reload / undo sync).
            foreach (var clip in activeGroup.Clippings)
            {
                try
                {
                    if (clip.SliceBox == null && clip.TransformMatrix != null && clip.TransformMatrix.Length == 12)
                        clip.RestoreGeometry();
                    else if ((clip.Planes == null || clip.Planes.Count == 0) &&
                             clip.PlaneArray != null && clip.PlaneArray.Length >= 6)
                        clip.RestoreGeometry();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Biruscan] RestoreGeometry failed for '{clip.Name}': {ex.Message}");
                }
            }

            var activeClips = activeGroup.Clippings.Where(c => c.IsActive).ToList();
            int skippedDegenerate = 0;

            // ONE instance only: consolidate every active cut into a single multi-plane filter.
            // Never create PointCloudInstance helpers/clones (product rule).
            List<Plane> combinedPlanes = BuildConsolidatedPlanes(activeClips, ref skippedDegenerate);

            if (activeClips.Count > 0 && (combinedPlanes == null || combinedPlanes.Count == 0))
            {
                throw new Exception(
                    "The cut was recorded but could not be applied to the point cloud.\n\n" +
                    (skippedDegenerate > 0
                        ? "A cut region collapsed to zero thickness or has no overlap with previous cuts.\n" +
                          "Draw the new cut over the *remaining* cloud, or turn off an earlier cut in Clipping Manager."
                        : "No valid clip planes were generated. Use Clear Cuts, then cut again in an orthographic 3D view."));
            }

            using (Transaction t = new Transaction(_doc, transactionName))
            {
                t.Start();

                // Remove any leftover helpers from older plugin versions.
                DeleteTaggedClonesInTransaction();

                var primaries = GetPrimaryPointCloudInstances();
                if (primaries == null || primaries.Count == 0)
                {
                    t.RollBack();
                    throw new Exception("No point cloud found in the document.");
                }
                PointCloudInstance pci = primaries[0];

                if (combinedPlanes != null && combinedPlanes.Count > 0)
                {
                    // Apply only to the first/primary instance — never spawn extras.
                    ApplyFilterToInstances(new List<PointCloudInstance> { pci }, combinedPlanes);
                    // If multiple real clouds exist (user-loaded), apply same filter without cloning.
                    for (int i = 1; i < primaries.Count; i++)
                    {
                        try
                        {
                            ApplyFilterToInstances(
                                new List<PointCloudInstance> { primaries[i] }, combinedPlanes);
                        }
                        catch { }
                    }
                    _cropBoxStrategyUsed = "SetSelectionFilter (single instance, no clones)";
                }
                else
                {
                    foreach (PointCloudInstance primary in primaries)
                    {
                        try { primary.FilterAction = SelectionFilterAction.None; } catch { }
                    }
                }

                ClippingSyncService.SaveState(_doc, pci, GetClippingGroups());
                t.Commit();
            }
        }

        private void ApplyFilterToInstances(List<PointCloudInstance> instances, List<Plane> planes)
        {
            if (planes == null || planes.Count == 0)
            {
                foreach (var inst in instances)
                {
                    try { inst.FilterAction = SelectionFilterAction.None; } catch { }
                }
                return;
            }

            PointCloudFilter filter;
            try
            {
                filter = PointCloudFilterFactory.CreateMultiPlaneFilter(planes);
            }
            catch (Exception ex)
            {
                throw new Exception(
                    "Revit rejected the clip filter (too many or invalid planes).\n\n" +
                    "Use Clear Cuts and try fewer successive cuts.\n\nDetails: " + ex.Message);
            }

            foreach (var inst in instances)
            {
                try
                {
                    inst.SetSelectionFilter(filter);
                    inst.FilterAction = SelectionFilterAction.Isolate;
                }
                catch (Exception ex)
                {
                    throw new Exception(
                        "Could not apply the isolate filter.\n\nDetails: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Builds the combined multi-plane filter for all active clips without clone instances.
        /// Same-orientation SliceBoxes are intersected into one box before plane generation.
        /// </summary>
        private List<Plane> BuildConsolidatedPlanes(List<ClippingRecord> activeClips, ref int skippedDegenerate)
        {
            var planes = new List<Plane>();
            if (activeClips == null || activeClips.Count == 0) return planes;

            // --- 1a) SLICES: keep EVERY active slice (never drop older ones) ---
            // Same-orientation slices are merged by UNION along the thickness axis so
            // they do not AND to empty (which looked like "sub-slice deleted the others").
            // Slice Forward/Back still only MOVES the last slice's geometry; older slices
            // remain in the list and in the filter.
            var sliceClips = activeClips
                .Where(c => c.ClipType == ClipType.Slice && c.SliceBox != null)
                .ToList();

            var sliceGroups = new List<List<ClippingRecord>>();
            foreach (var clip in sliceClips)
            {
                bool placed = false;
                Transform t = clip.SliceBox.Transform ?? Transform.Identity;
                for (int g = 0; g < sliceGroups.Count; g++)
                {
                    Transform t0 = sliceGroups[g][0].SliceBox.Transform ?? Transform.Identity;
                    // Same orientation AND same Axis enum → one merge group
                    if (SameOrientation(t, t0) && sliceGroups[g][0].Axis == clip.Axis)
                    {
                        sliceGroups[g].Add(clip);
                        placed = true;
                        break;
                    }
                }
                if (!placed)
                    sliceGroups.Add(new List<ClippingRecord> { clip });
            }

            foreach (var group in sliceGroups)
            {
                BoundingBoxXYZ merged = MergeSlicesByUnion(group);
                if (merged == null)
                {
                    skippedDegenerate++;
                    continue;
                }
                try
                {
                    planes.AddRange(BoxToInwardPlanes(merged));
                }
                catch (Exception ex)
                {
                    throw new Exception(
                        "Could not build planes for slices.\n\nDetails: " + ex.Message);
                }
            }

            // --- 1b) RECT / POLY SliceBoxes: group by orientation, INTERSECT within group ---
            // (progressive crops — not slices)
            var boxClips = activeClips.Where(c =>
                c.ClipType != ClipType.Slice
                && c.SliceBox != null
                && (c.Planes == null || c.Planes.Count == 0)).ToList();

            var groups = new List<List<ClippingRecord>>();
            foreach (var clip in boxClips)
            {
                bool placed = false;
                Transform t = clip.SliceBox.Transform ?? Transform.Identity;
                for (int g = 0; g < groups.Count; g++)
                {
                    Transform t0 = groups[g][0].SliceBox.Transform ?? Transform.Identity;
                    if (SameOrientation(t, t0))
                    {
                        groups[g].Add(clip);
                        placed = true;
                        break;
                    }
                }
                if (!placed)
                    groups.Add(new List<ClippingRecord> { clip });
            }

            foreach (var group in groups)
            {
                BoundingBoxXYZ merged = null;
                Transform t = group[0].SliceBox.Transform ?? Transform.Identity;
                foreach (var clip in group)
                {
                    BoundingBoxXYZ box = clip.SliceBox;
                    if (box == null) continue;
                    BoundingBoxXYZ local = box;
                    if (!SameOrientation(box.Transform ?? Transform.Identity, t))
                        local = MapBoxToFrame(box, t);

                    if (merged == null)
                    {
                        merged = new BoundingBoxXYZ
                        {
                            Transform = t,
                            Min = local.Min,
                            Max = local.Max
                        };
                    }
                    else
                    {
                        XYZ iMin = new XYZ(
                            Math.Max(merged.Min.X, local.Min.X),
                            Math.Max(merged.Min.Y, local.Min.Y),
                            Math.Max(merged.Min.Z, local.Min.Z));
                        XYZ iMax = new XYZ(
                            Math.Min(merged.Max.X, local.Max.X),
                            Math.Min(merged.Max.Y, local.Max.Y),
                            Math.Min(merged.Max.Z, local.Max.Z));
                        if (iMin.X >= iMax.X - 1e-9 || iMin.Y >= iMax.Y - 1e-9 || iMin.Z >= iMax.Z - 1e-9)
                        {
                            skippedDegenerate++;
                            throw new Exception(
                                "This cut does not overlap the region kept by earlier cuts.\n\n" +
                                "Draw the new cut over the *remaining* (still visible) point cloud, " +
                                "or disable / clear an earlier cut in Clipping Manager, then try again.");
                        }
                        merged.Min = iMin;
                        merged.Max = iMax;
                    }
                }

                if (merged == null)
                {
                    skippedDegenerate++;
                    continue;
                }

                try
                {
                    planes.AddRange(BoxToInwardPlanes(merged));
                }
                catch (Exception ex)
                {
                    throw new Exception(
                        "Could not build clip planes for a cut group.\n\n" +
                        "Details: " + ex.Message);
                }
            }

            // --- 2) Explicit plane sets (polygonal) — first piece only if legacy multi stored ---
            foreach (var clip in activeClips)
            {
                if (clip.Planes != null && clip.Planes.Count > 0)
                {
                    planes.AddRange(clip.Planes);
                    continue;
                }
                // Legacy SubFilterPlanes: use first piece only (never OR-clone other pieces).
                if (clip.SubFilterPlanes != null && clip.SubFilterPlanes.Count > 0
                    && clip.SubFilterPlanes[0] != null)
                {
                    planes.AddRange(clip.SubFilterPlanes[0]);
                }
            }

            // Cap plane count — Revit MultiPlaneFilter becomes unreliable / empty past ~24–30.
            const int maxPlanes = 24;
            if (planes.Count > maxPlanes)
            {
                Debug.WriteLine($"[Biruscan] Truncating plane list from {planes.Count} to {maxPlanes} for filter stability.");
                planes = planes.Take(maxPlanes).ToList();
            }

            return planes;
        }

        private static bool SameOrientation(Transform a, Transform b)
        {
            if (a == null || b == null) return false;
            // Axes parallel (ignore origin); allow reverse direction.
            double dx = Math.Abs(a.BasisX.DotProduct(b.BasisX));
            double dy = Math.Abs(a.BasisY.DotProduct(b.BasisY));
            double dz = Math.Abs(a.BasisZ.DotProduct(b.BasisZ));
            return dx > 0.98 && dy > 0.98 && dz > 0.98;
        }

        /// <summary>
        /// Merges multiple same-orientation slices by UNION along the thickness axis
        /// and INTERSECTION of the lateral extents. Preserves every slice in the group
        /// (nothing is dropped). Single MultiPlaneFilter cannot OR separate bands, so
        /// gaps between far-apart slices are filled — that is the single-instance limit.
        /// </summary>
        private static BoundingBoxXYZ MergeSlicesByUnion(List<ClippingRecord> group)
        {
            if (group == null || group.Count == 0) return null;

            Transform t = group[0].SliceBox.Transform ?? Transform.Identity;
            SliceAxis axis = group[0].Axis;

            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            bool first = true;

            // Lateral: intersect. Thickness axis: union.
            double latMinX = double.MinValue, latMaxX = double.MaxValue;
            double latMinY = double.MinValue, latMaxY = double.MaxValue;
            double latMinZ = double.MinValue, latMaxZ = double.MaxValue;
            double thickMin = double.MaxValue, thickMax = double.MinValue;

            foreach (var clip in group)
            {
                BoundingBoxXYZ box = clip.SliceBox;
                if (box == null) continue;
                BoundingBoxXYZ local = SameOrientation(box.Transform ?? Transform.Identity, t)
                    ? box
                    : MapBoxToFrame(box, t);

                // Thickness axis depends on SliceAxis (matches StepSlice movement).
                double t0, t1;
                if (axis == SliceAxis.Z)
                {
                    t0 = local.Min.Z; t1 = local.Max.Z;
                    // Lateral X,Y
                    if (first)
                    {
                        latMinX = local.Min.X; latMaxX = local.Max.X;
                        latMinY = local.Min.Y; latMaxY = local.Max.Y;
                    }
                    else
                    {
                        latMinX = Math.Max(latMinX, local.Min.X);
                        latMaxX = Math.Min(latMaxX, local.Max.X);
                        latMinY = Math.Max(latMinY, local.Min.Y);
                        latMaxY = Math.Min(latMaxY, local.Max.Y);
                    }
                }
                else if (axis == SliceAxis.Y)
                {
                    t0 = local.Min.Y; t1 = local.Max.Y;
                    if (first)
                    {
                        latMinX = local.Min.X; latMaxX = local.Max.X;
                        latMinZ = local.Min.Z; latMaxZ = local.Max.Z;
                    }
                    else
                    {
                        latMinX = Math.Max(latMinX, local.Min.X);
                        latMaxX = Math.Min(latMaxX, local.Max.X);
                        latMinZ = Math.Max(latMinZ, local.Min.Z);
                        latMaxZ = Math.Min(latMaxZ, local.Max.Z);
                    }
                }
                else // Axis.X
                {
                    t0 = local.Min.X; t1 = local.Max.X;
                    if (first)
                    {
                        latMinY = local.Min.Y; latMaxY = local.Max.Y;
                        latMinZ = local.Min.Z; latMaxZ = local.Max.Z;
                    }
                    else
                    {
                        latMinY = Math.Max(latMinY, local.Min.Y);
                        latMaxY = Math.Min(latMaxY, local.Max.Y);
                        latMinZ = Math.Max(latMinZ, local.Min.Z);
                        latMaxZ = Math.Min(latMaxZ, local.Max.Z);
                    }
                }

                if (t0 > t1) { double tmp = t0; t0 = t1; t1 = tmp; }
                thickMin = Math.Min(thickMin, t0);
                thickMax = Math.Max(thickMax, t1);
                first = false;
            }

            if (first || thickMin >= thickMax - 1e-9)
                return null;

            if (axis == SliceAxis.Z)
            {
                if (latMinX >= latMaxX - 1e-9 || latMinY >= latMaxY - 1e-9)
                {
                    // Lateral intersect empty — fall back to first slice only (keep something).
                    return group[0].SliceBox;
                }
                minX = latMinX; maxX = latMaxX;
                minY = latMinY; maxY = latMaxY;
                minZ = thickMin; maxZ = thickMax;
            }
            else if (axis == SliceAxis.Y)
            {
                if (latMinX >= latMaxX - 1e-9 || latMinZ >= latMaxZ - 1e-9)
                    return group[0].SliceBox;
                minX = latMinX; maxX = latMaxX;
                minY = thickMin; maxY = thickMax;
                minZ = latMinZ; maxZ = latMaxZ;
            }
            else
            {
                if (latMinY >= latMaxY - 1e-9 || latMinZ >= latMaxZ - 1e-9)
                    return group[0].SliceBox;
                minX = thickMin; maxX = thickMax;
                minY = latMinY; maxY = latMaxY;
                minZ = latMinZ; maxZ = latMaxZ;
            }

            return new BoundingBoxXYZ
            {
                Transform = t,
                Min = new XYZ(minX, minY, minZ),
                Max = new XYZ(maxX, maxY, maxZ)
            };
        }

        private static BoundingBoxXYZ MapBoxToFrame(BoundingBoxXYZ box, Transform targetFrame)
        {
            Transform src = box.Transform ?? Transform.Identity;
            Transform inv = targetFrame.Inverse;
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            XYZ[] corners =
            {
                new XYZ(box.Min.X, box.Min.Y, box.Min.Z), new XYZ(box.Max.X, box.Min.Y, box.Min.Z),
                new XYZ(box.Min.X, box.Max.Y, box.Min.Z), new XYZ(box.Max.X, box.Max.Y, box.Min.Z),
                new XYZ(box.Min.X, box.Min.Y, box.Max.Z), new XYZ(box.Max.X, box.Min.Y, box.Max.Z),
                new XYZ(box.Min.X, box.Max.Y, box.Max.Z), new XYZ(box.Max.X, box.Max.Y, box.Max.Z)
            };
            foreach (XYZ c in corners)
            {
                XYZ w = src.OfPoint(c);
                XYZ l = inv.OfPoint(w);
                if (l.X < minX) minX = l.X; if (l.X > maxX) maxX = l.X;
                if (l.Y < minY) minY = l.Y; if (l.Y > maxY) maxY = l.Y;
                if (l.Z < minZ) minZ = l.Z; if (l.Z > maxZ) maxZ = l.Z;
            }
            return new BoundingBoxXYZ
            {
                Transform = targetFrame,
                Min = new XYZ(minX, minY, minZ),
                Max = new XYZ(maxX, maxY, maxZ)
            };
        }

        private static List<Plane> BoxToInwardPlanes(BoundingBoxXYZ box)
        {
            Transform t = box.Transform ?? Transform.Identity;
            XYZ min = box.Min, max = box.Max;
            if (Math.Abs(max.X - min.X) < 1e-9 || Math.Abs(max.Y - min.Y) < 1e-9 || Math.Abs(max.Z - min.Z) < 1e-9)
                throw new Exception("Degenerate cut box.");

            return new List<Plane>
            {
                Plane.CreateByNormalAndOrigin(t.BasisX, t.OfPoint(new XYZ(min.X, 0, 0))),
                Plane.CreateByNormalAndOrigin(-t.BasisX, t.OfPoint(new XYZ(max.X, 0, 0))),
                Plane.CreateByNormalAndOrigin(t.BasisY, t.OfPoint(new XYZ(0, min.Y, 0))),
                Plane.CreateByNormalAndOrigin(-t.BasisY, t.OfPoint(new XYZ(0, max.Y, 0))),
                Plane.CreateByNormalAndOrigin(t.BasisZ, t.OfPoint(new XYZ(0, 0, min.Z))),
                Plane.CreateByNormalAndOrigin(-t.BasisZ, t.OfPoint(new XYZ(0, 0, max.Z)))
            };
        }

        /// <summary>
        /// Cuts the point cloud with a polygonal region (minimum 3 points).
        /// Uses the bounding box of the polygon. Extends through full Z height.
        /// Cumulative with existing crop.
        /// </summary>
        public void CutPointCloudPolygonal(List<XYZ> polygonPoints)
        {
            if (polygonPoints == null || polygonPoints.Count < 3)
                throw new ArgumentException("At least 3 points are required for a polygonal cut.");

            BoundingBoxXYZ fullBounds = GetFullCloudBounds();
            BoundingBoxXYZ currentCrop = GetCurrentCropBox();

            double xMin = double.MaxValue, yMin = double.MaxValue;
            double xMax = double.MinValue, yMax = double.MinValue;
            foreach (XYZ pt in polygonPoints)
            {
                xMin = Math.Min(xMin, pt.X);
                yMin = Math.Min(yMin, pt.Y);
                xMax = Math.Max(xMax, pt.X);
                yMax = Math.Max(yMax, pt.Y);
            }

            BoundingBoxXYZ polyBox = new BoundingBoxXYZ
            {
                Transform = Transform.Identity,
                Min = new XYZ(xMin, yMin, fullBounds.Min.Z),
                Max = new XYZ(xMax, yMax, fullBounds.Max.Z)
            };

            var finalCrop = IntersectBoxes(currentCrop, polyBox);
            if (finalCrop == null)
                throw new Exception("Polygonal cut region does not overlap with the current point cloud crop.");

            ClippingRecords.Add(new ClippingRecord
            {
                Name = "PolyCut_" + DateTime.Now.ToString("HHmmss"),
                ClipType = ClipType.Polygonal,
                IsActive = true,
                SliceBox = finalCrop,
                MinX = finalCrop.Min.X, MaxX = finalCrop.Max.X,
                MinY = finalCrop.Min.Y, MaxY = finalCrop.Max.Y,
                MinZ = finalCrop.Min.Z, MaxZ = finalCrop.Max.Z
            });

            RebuildCropFromActiveGroup("Create Polygonal Cut");
        }

        /// <summary>
        /// Applies a 3D limit box to cut the point cloud.
        /// Cumulative with existing crop.
        /// </summary>
        public void ApplyLimitBox(BoundingBoxXYZ limitBox)
        {
            if (limitBox == null)
                throw new ArgumentNullException(nameof(limitBox));

            BoundingBoxXYZ currentCrop = GetCurrentCropBox();
            limitBox.Transform = Transform.Identity;

            var finalCrop = IntersectBoxes(currentCrop, limitBox);
            if (finalCrop == null)
                throw new Exception("Limit box does not overlap with the current point cloud crop.");

            ApplyCropToPointCloud(finalCrop);

            ClippingRecords.Add(new ClippingRecord
            {
                Name = "LimitBox_" + DateTime.Now.ToString("HHmmss"),
                ClipType = ClipType.Rectangular,
                IsActive = true,
                MinX = finalCrop.Min.X, MaxX = finalCrop.Max.X,
                MinY = finalCrop.Min.Y, MaxY = finalCrop.Max.Y,
                MinZ = finalCrop.Min.Z, MaxZ = finalCrop.Max.Z
            });
        }

        /// <summary>
        /// Resets all cuts and restores the full point cloud.
        /// If the original file path is known, it reloads the original cloud
        /// and removes any filtered point clouds created by Tier 2.
        /// </summary>
        public void ResetAllCuts()
        {
            var pci = GetPointCloudInstance();
            if (pci == null)
                throw new Exception("No point cloud found.");

            string key = string.IsNullOrEmpty(_doc.PathName) ? _doc.Title : _doc.PathName;
            if (_originalFilePath.ContainsKey(key) && _originalFilePath[key] != null && File.Exists(_originalFilePath[key]))
            {
                try
                {
                    using (Transaction t = new Transaction(_doc, "Reset Point Cloud Cuts"))
                    {
                        t.Start();

                        List<ElementId> ptsFilesToDelete = new List<ElementId>();

                        var pcTypes = new FilteredElementCollector(_doc)
                            .OfClass(typeof(PointCloudType))
                            .Cast<PointCloudType>()
                            .ToList();

                        foreach (PointCloudType pcType in pcTypes)
                        {
                            try
                            {
                                ModelPath mp = pcType.GetExternalFileReference()?.GetPath();
                                string path = mp != null ? ModelPathUtils.ConvertModelPathToUserVisiblePath(mp) : null;
                                if (!string.IsNullOrEmpty(path) && !string.Equals(path, _originalFilePath[key], StringComparison.OrdinalIgnoreCase) &&
                                    path.ToLower().Contains("biruscan_clipped_"))
                                {
                                    _doc.Delete(pcType.Id);
                                }
                            }
                            catch { }
                        }

                        var instances = new FilteredElementCollector(_doc)
                            .OfClass(typeof(PointCloudInstance))
                            .Cast<PointCloudInstance>()
                            .ToList();

                        foreach (PointCloudInstance inst in instances)
                        {
                            try
                            {
                                PointCloudType instType = _doc.GetElement(inst.GetTypeId()) as PointCloudType;
                                if (instType != null)
                                {
                                    ModelPath mp = instType.GetExternalFileReference()?.GetPath();
                                    string path = mp != null ? ModelPathUtils.ConvertModelPathToUserVisiblePath(mp) : null;
                                    if (!string.IsNullOrEmpty(path) && !string.Equals(path, _originalFilePath[key], StringComparison.OrdinalIgnoreCase))
                                    {
                                        _doc.Delete(inst.Id);
                                    }
                                }
                            }
                            catch { }
                        }

                        // Show all remaining (original) instances
                        var remainingInstances = new FilteredElementCollector(_doc)
                            .OfClass(typeof(PointCloudInstance))
                            .ToElementIds();

                        foreach (View view in new FilteredElementCollector(_doc)
                            .OfClass(typeof(View))
                            .Cast<View>()
                            .Where(v => !v.IsTemplate))
                        {
                            try { view.UnhideElements(remainingInstances.ToList()); }
                            catch { }
                        }

                        // If original instance has a CropBox, reset it
                        if (_cropBoxProperty != null && _cropBoxProperty.CanWrite)
                        {
                            BoundingBoxXYZ fullBounds = GetFullCloudBounds();
                            if (fullBounds != null)
                            {
                                var origInst = new FilteredElementCollector(_doc)
                                    .OfClass(typeof(PointCloudInstance))
                                    .Cast<PointCloudInstance>()
                                    .FirstOrDefault();

                                if (origInst != null)
                                {
                                    Transform cloudTransform = origInst.GetTransform();
                                    BoundingBoxXYZ localFull = new BoundingBoxXYZ { Transform = Transform.Identity };

                                    if (cloudTransform != null && !cloudTransform.IsIdentity)
                                    {
                                        Transform inverse = cloudTransform.Inverse;
                                        localFull.Min = inverse.OfPoint(fullBounds.Min);
                                        localFull.Max = inverse.OfPoint(fullBounds.Max);
                                    }
                                    else
                                    {
                                        localFull.Min = fullBounds.Min;
                                        localFull.Max = fullBounds.Max;
                                    }

                                    _cropBoxProperty.SetValue(origInst, localFull);
                                }
                            }
                        }

                        ClearSectionBoxes();
                        t.Commit();
                    }

                    // Clean up temp files
                    if (_lastTempFilePath.ContainsKey(key) && _lastTempFilePath[key] != null)
                    {
                        try { if (File.Exists(_lastTempFilePath[key])) File.Delete(_lastTempFilePath[key]); }
                        catch { }
                        _lastTempFilePath.Remove(key);
                    }

                    _originalFilePath.Remove(key);
                    _originalTypeId.Remove(key);
                    GetClippingGroups().Clear();

                    Debug.WriteLine("[Biruscan] Reset complete (original cloud restored)");
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Biruscan] Reset reload failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Clears ALL cuts/slices on every point cloud instance and restores the full
        /// cloud: removes the native isolate filter, resets any crop box, clears section
        /// boxes, wipes the in-memory clipping groups, and erases the persisted state
        /// (so the cuts do not come back, even though they normally survive undo).
        /// </summary>
        public int ClearAllCuts()
        {
            string docKey = string.IsNullOrEmpty(_doc.PathName) ? _doc.Title : _doc.PathName;
            if (!_cloneInstancesByDoc.ContainsKey(docKey))
                _cloneInstancesByDoc[docKey] = new List<ElementId>();
            var cloneIds = _cloneInstancesByDoc[docKey];

            var instances = new FilteredElementCollector(_doc)
                .OfClass(typeof(PointCloudInstance))
                .Cast<PointCloudInstance>()
                .ToList();

            if (instances.Count == 0)
                throw new Exception("No point cloud found.");

            int cleared = 0;
            using (Transaction t = new Transaction(_doc, "Clear All Cuts"))
            {
                t.Start();

                // Remove concave-polygon helper instances (same RCP type, tagged).
                DeleteTaggedClonesInTransaction();
                PurgeSameTypeDuplicatesInTransaction();

                DiscoverCropBoxProperty();
                BoundingBoxXYZ fullBounds = GetFullCloudBounds();

                // Re-collect after cleanup
                instances = new FilteredElementCollector(_doc)
                    .OfClass(typeof(PointCloudInstance))
                    .Cast<PointCloudInstance>()
                    .Where(p => !IsBiruscanClone(p))
                    .ToList();

                // Unhide original clouds that exact-polygon cut may have hidden.
                var allIds = instances.Select(i => i.Id).ToList();
                foreach (View view in new FilteredElementCollector(_doc)
                    .OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate))
                {
                    try { if (allIds.Count > 0) view.UnhideElements(allIds); } catch { }
                }

                foreach (var pci in instances)
                {
                    // 1) Remove the native isolate filter → full cloud visible again.
                    try { pci.FilterAction = SelectionFilterAction.None; cleared++; }
                    catch { }

                    // 2) Reset crop box to the full bounds (if CropBox is writable).
                    try
                    {
                        if (_cropBoxProperty != null && _cropBoxProperty.CanWrite && fullBounds != null)
                        {
                            Transform ctf = pci.GetTransform();
                            BoundingBoxXYZ localFull = new BoundingBoxXYZ { Transform = Transform.Identity };
                            if (ctf != null && !ctf.IsIdentity)
                            {
                                Transform inv = ctf.Inverse;
                                localFull.Min = inv.OfPoint(fullBounds.Min);
                                localFull.Max = inv.OfPoint(fullBounds.Max);
                            }
                            else { localFull.Min = fullBounds.Min; localFull.Max = fullBounds.Max; }
                            _cropBoxProperty.SetValue(pci, localFull);
                        }
                    }
                    catch { }

                    // 3) Erase the persisted clipping state on this instance.
                    ClippingSyncService.ClearState(_doc, pci);
                }

                ClearSectionBoxes();
                t.Commit();
            }

            // 4) Wipe the in-memory groups so nothing gets re-applied.
            foreach (var g in GetClippingGroups())
                g.Clippings.Clear();
            GetClippingGroups().Clear();

            // 5) Next cut can re-capture full unfiltered bounds.
            _originalCloudBoundsByDoc.Remove(DocKey());

            return cleared;
        }


        /// <summary>
        /// Applies a saved clipping record.
        /// </summary>
        public void ApplyClippingRecord(ClippingRecord record)
        {
            if (record == null) return;
            var box = record.ToBoundingBox();
            box.Transform = Transform.Identity;
            ApplyCropToPointCloud(box);
        }

        public ClippingRecord CreateClippingRecord(BoundingBoxXYZ bbox, string name)
        {
            var record = ClippingRecord.FromBoundingBox(bbox, name);
            ClippingRecords.Add(record);
            return record;
        }

        public bool DeleteClippingRecord(string name)
        {
            var record = ClippingRecords.FirstOrDefault(r => r.Name == name);
            if (record != null)
            {
                ClippingRecords.Remove(record);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Tests if a 2D point (using only X and Y) is inside a polygon using the
        /// ray-casting algorithm. Used to post-filter points extracted from convex
        /// sub-filters to enforce the actual concave polygon boundary.
        /// </summary>
        private static bool PointInPolygon2D(XYZ point, IList<XYZ> polygon)
        {
            int n = polygon.Count;
            bool inside = false;
            double px = point.X, py = point.Y;

            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double xi = polygon[i].X, yi = polygon[i].Y;
                double xj = polygon[j].X, yj = polygon[j].Y;

                if (((yi > py) != (yj > py)) &&
                    (px < (xj - xi) * (py - yi) / (yj - yi) + xi))
                {
                    inside = !inside;
                }
            }

            return inside;
        }
    }
}
