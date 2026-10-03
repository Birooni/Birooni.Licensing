using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.PointClouds;
using Biruscan.AI.DeepLearning;
using Biruscan.Models;
using Biruscan.Services;

namespace Biruscan.Services.Ai
{
    /// <summary>
    /// User Modeling Watcher (Imitation Learning & Behavioral Cloning).
    /// Observes the user's manual modeling in Revit (drawing pipes, ducts, cable trays, conduits, fittings, walls),
    /// extracts local point-cloud clusters at each placed element, and trains AI to emulate the user's modeling style.
    /// </summary>
    public sealed class UserModelingWatcher
    {
        private static readonly Lazy<UserModelingWatcher> _instance =
            new Lazy<UserModelingWatcher>(() => new UserModelingWatcher());

        public static UserModelingWatcher Instance => _instance.Value;

        public UserModelingWatcherSettings Settings => UserModelingWatcherSettings.Instance;

        private readonly List<UserModelingSample> _modelingBuffer = new List<UserModelingSample>();
        private readonly List<string> _activityLog = new List<string>();
        private readonly object _bufferLock = new object();
        private bool _isInitialized = false;

        public int TotalObservedCount { get; private set; }
        public int PipesObservedCount { get; private set; }
        public int DuctsObservedCount { get; private set; }
        public int ElectricalObservedCount { get; private set; }
        public int FittingsObservedCount { get; private set; }
        public int StructuresObservedCount { get; private set; }
        public int RetrainedBatchesCount { get; private set; }
        public DateTime? LastRetrainedTime { get; private set; }

        public event Action OnStateChanged;
        public event Action<string> OnLogMessage;

        private UserModelingWatcher() { }

        public void Initialize(ControlledApplication app)
        {
            if (_isInitialized || app == null) return;
            app.DocumentChanged += OnDocumentChanged;
            _isInitialized = true;
            LogActivity("User Modeling Watcher active. Listening for manual Revit modeling.");
        }

        public void Shutdown(ControlledApplication app)
        {
            if (!_isInitialized || app == null) return;
            try { app.DocumentChanged -= OnDocumentChanged; } catch { }
            _isInitialized = false;
        }

        public void LogActivity(string message)
        {
            string formatted = $"[{DateTime.Now:HH:mm:ss}] {message}";
            lock (_bufferLock)
            {
                _activityLog.Add(formatted);
                if (_activityLog.Count > 300) _activityLog.RemoveAt(0);
            }
            OnLogMessage?.Invoke(formatted);
        }

        public List<string> GetRecentLogs(int count = 50)
        {
            lock (_bufferLock)
            {
                int skip = Math.Max(0, _activityLog.Count - count);
                return _activityLog.Skip(skip).ToList();
            }
        }

        public int GetPendingCount()
        {
            lock (_bufferLock)
            {
                return _modelingBuffer.Count(s => !s.IsTrained);
            }
        }

        private void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            if (!Settings.Enabled || AutoTrainingManager.IsInternalTransaction) return;

            Document doc = e.GetDocument();
            if (doc == null) return;

            try
            {
                var addedIds = e.GetAddedElementIds();
                if (addedIds == null || addedIds.Count == 0) return;

                // Query point clouds once per transaction
                var pcInstances = new FilteredElementCollector(doc)
                    .OfClass(typeof(PointCloudInstance))
                    .Cast<PointCloudInstance>()
                    .ToList();

                foreach (var id in addedIds)
                {
                    try
                    {
                        Element elem = doc.GetElement(id);
                        if (elem == null || elem.Category == null) continue;

                        UserModelingSample sample = ProcessAddedElement(doc, elem, pcInstances);
                        if (sample != null)
                        {
                            lock (_bufferLock)
                            {
                                _modelingBuffer.Add(sample);
                                TotalObservedCount++;
                                UpdateCategoryCount(sample.Service);
                            }

                            LogActivity(sample.GetSummary());
                            CheckAutoRetrain();
                            OnStateChanged?.Invoke();
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                LogActivity($"Watcher observation error: {ex.Message}");
            }
        }

        private UserModelingSample ProcessAddedElement(Document doc, Element elem, List<PointCloudInstance> pcInstances)
        {
            string service = string.Empty;
            string categoryName = elem.Category?.Name ?? "Unknown";
            BuiltInCategory cat = (BuiltInCategory)elem.Category.Id.IntegerValue;

            if (elem is Pipe pipe)
            {
                if (!Settings.WatchPipes) return null;
                service = "Piping";
                return ExtractCurveSample(doc, elem, pipe.Location as LocationCurve, service, cat, pcInstances, TryGetDiameterMm(pipe), 0, 0);
            }
            else if (elem is Duct duct)
            {
                if (!Settings.WatchDucts) return null;
                service = "Duct";
                double width = TryGetParamMm(duct, BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                double height = TryGetParamMm(duct, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
                double dia = TryGetParamMm(duct, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
                return ExtractCurveSample(doc, elem, duct.Location as LocationCurve, service, cat, pcInstances, dia, width, height);
            }
            else if (elem is Conduit conduit)
            {
                if (!Settings.WatchConduits) return null;
                service = "Conduit";
                return ExtractCurveSample(doc, elem, conduit.Location as LocationCurve, service, cat, pcInstances, TryGetParamMm(conduit, BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM), 0, 0);
            }
            else if (elem is CableTray tray)
            {
                if (!Settings.WatchCableTrays) return null;
                service = "Cable Tray";
                double width = TryGetParamMm(tray, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
                double height = TryGetParamMm(tray, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
                return ExtractCurveSample(doc, elem, tray.Location as LocationCurve, service, cat, pcInstances, 0, width, height);
            }
            else if (elem is FamilyInstance fi)
            {
                if (cat == BuiltInCategory.OST_PipeFitting && Settings.WatchFittings)
                {
                    service = "Pipe Fitting";
                    return ExtractPointSample(doc, elem, fi, service, pcInstances);
                }
                else if (cat == BuiltInCategory.OST_DuctFitting && Settings.WatchFittings)
                {
                    service = "Duct Fitting";
                    return ExtractPointSample(doc, elem, fi, service, pcInstances);
                }
                else if (cat == BuiltInCategory.OST_StructuralFraming && Settings.WatchWallsAndStructures)
                {
                    service = "Steel";
                    return ExtractCurveSample(doc, elem, fi.Location as LocationCurve, service, cat, pcInstances, 0, 0, 0);
                }
            }
            else if (elem is Wall wall && Settings.WatchWallsAndStructures)
            {
                service = "Wall";
                double thickness = wall.WallType?.Width * 304.8 ?? 200.0;
                return ExtractCurveSample(doc, elem, wall.Location as LocationCurve, service, cat, pcInstances, 0, thickness, 0);
            }

            return null;
        }

        private UserModelingSample ExtractCurveSample(Document doc, Element elem, LocationCurve lc, string service,
            BuiltInCategory cat, List<PointCloudInstance> pcInstances, double diaMm, double widthMm, double heightMm)
        {
            if (lc == null || !(lc.Curve is Line line)) return null;

            XYZ start = line.GetEndPoint(0);
            XYZ end = line.GetEndPoint(1);
            XYZ mid = (start + end) * 0.5;
            XYZ dir = (end - start).Normalize();
            double lengthFt = start.DistanceTo(end);

            List<double[]> roiPoints = ExtractLocalPointCloud(doc, start, end, Settings.RoiSearchRadiusFeet, pcInstances);

            var typeElem = doc.GetElement(elem.GetTypeId());
            string typeName = typeElem?.Name ?? elem.Name;

            var sysParam = elem.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)
                        ?? elem.get_Parameter(BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM);
            string sysName = "";
            if (sysParam != null && sysParam.HasValue)
            {
                var st = doc.GetElement(sysParam.AsElementId());
                if (st != null) sysName = st.Name;
            }

            var lvlParam = elem.get_Parameter(BuiltInParameter.RBS_START_LEVEL_PARAM)
                        ?? elem.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM);
            string lvlName = "";
            if (lvlParam != null && lvlParam.HasValue)
            {
                var lvl = doc.GetElement(lvlParam.AsElementId());
                if (lvl != null) lvlName = lvl.Name;
            }

            return new UserModelingSample
            {
                ElementIdValue = elem.Id.IntegerValue,
                CategoryName = elem.Category.Name,
                Service = service,
                TypeName = typeName,
                SystemTypeName = sysName,
                LevelName = lvlName,
                StartPoint = new double[] { start.X, start.Y, start.Z },
                EndPoint = new double[] { end.X, end.Y, end.Z },
                CenterPoint = new double[] { mid.X, mid.Y, mid.Z },
                DirectionVector = new double[] { dir.X, dir.Y, dir.Z },
                DiameterMm = diaMm,
                WidthMm = widthMm,
                HeightMm = heightMm,
                LengthFeet = lengthFt,
                LocalRoiPoints = roiPoints,
                ActionDescription = $"Manual {service} created in Revit"
            };
        }

        private UserModelingSample ExtractPointSample(Document doc, Element elem, FamilyInstance fi, string service, List<PointCloudInstance> pcInstances)
        {
            LocationPoint lp = fi.Location as LocationPoint;
            if (lp == null) return null;

            XYZ pt = lp.Point;
            List<double[]> roiPoints = ExtractLocalPointCloud(doc, pt - new XYZ(0.5, 0.5, 0.5), pt + new XYZ(0.5, 0.5, 0.5), Settings.RoiSearchRadiusFeet, pcInstances);

            var typeElem = doc.GetElement(elem.GetTypeId());
            string typeName = typeElem?.Name ?? elem.Name;

            return new UserModelingSample
            {
                ElementIdValue = elem.Id.IntegerValue,
                CategoryName = elem.Category.Name,
                Service = service,
                TypeName = typeName,
                CenterPoint = new double[] { pt.X, pt.Y, pt.Z },
                StartPoint = new double[] { pt.X, pt.Y, pt.Z },
                EndPoint = new double[] { pt.X, pt.Y, pt.Z },
                LocalRoiPoints = roiPoints,
                ActionDescription = $"Manual {service} fitting placed in Revit"
            };
        }

        private List<double[]> ExtractLocalPointCloud(Document doc, XYZ p1, XYZ p2, double searchRadiusFt, List<PointCloudInstance> pcInstances)
        {
            var pts = new List<double[]>();
            if (pcInstances == null || pcInstances.Count == 0) return pts;

            XYZ min = new XYZ(
                Math.Min(p1.X, p2.X) - searchRadiusFt,
                Math.Min(p1.Y, p2.Y) - searchRadiusFt,
                Math.Min(p1.Z, p2.Z) - searchRadiusFt);

            XYZ max = new XYZ(
                Math.Max(p1.X, p2.X) + searchRadiusFt,
                Math.Max(p1.Y, p2.Y) + searchRadiusFt,
                Math.Max(p1.Z, p2.Z) + searchRadiusFt);

            try
            {
                List<Plane> planes = new List<Plane>
                {
                    Plane.CreateByNormalAndOrigin(new XYZ(1, 0, 0), new XYZ(min.X, 0, 0)),
                    Plane.CreateByNormalAndOrigin(new XYZ(-1, 0, 0), new XYZ(max.X, 0, 0)),
                    Plane.CreateByNormalAndOrigin(new XYZ(0, 1, 0), new XYZ(0, min.Y, 0)),
                    Plane.CreateByNormalAndOrigin(new XYZ(0, -1, 0), new XYZ(0, max.Y, 0)),
                    Plane.CreateByNormalAndOrigin(new XYZ(0, 0, 1), new XYZ(0, 0, min.Z)),
                    Plane.CreateByNormalAndOrigin(new XYZ(0, 0, -1), new XYZ(0, 0, max.Z))
                };
                PointCloudFilter filter = PointCloudFilterFactory.CreateMultiPlaneFilter(planes);

                var pcs = new PointCloudService(doc);
                XYZ mid = (p1 + p2) * 0.5;
                foreach (var pc in pcInstances)
                {
                    try
                    {
                        Transform worldMap = pcs.GetWorldMappingTransform(pc, mid);
                        var collection = pc.GetPoints(filter, 0.03, 1500);
                        if (collection != null && collection.Count > 0)
                        {
                            foreach (var p in collection)
                            {
                                XYZ w = worldMap.OfPoint(new XYZ(p.X, p.Y, p.Z));
                                pts.Add(new double[] { w.X, w.Y, w.Z });
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return pts;
        }

        private void UpdateCategoryCount(string service)
        {
            switch (service)
            {
                case "Piping": PipesObservedCount++; break;
                case "Duct": DuctsObservedCount++; break;
                case "Conduit":
                case "Cable Tray": ElectricalObservedCount++; break;
                case "Pipe Fitting":
                case "Duct Fitting": FittingsObservedCount++; break;
                case "Wall":
                case "Steel": StructuresObservedCount++; break;
            }
        }

        private void CheckAutoRetrain()
        {
            int pending = GetPendingCount();
            if (pending >= Settings.AutoRetrainThreshold && Settings.AutoRetrainThreshold > 0)
            {
                Task.Run(() => RetrainOnManualModeling());
            }
        }

        public bool RetrainOnManualModeling()
        {
            List<UserModelingSample> samplesToTrain;
            lock (_bufferLock)
            {
                samplesToTrain = _modelingBuffer.Where(s => !s.IsTrained && s.LocalRoiPoints.Count >= 6).ToList();
            }

            if (samplesToTrain.Count == 0) return false;

            try
            {
                LogActivity($"Retraining AI on {samplesToTrain.Count} manual modeling imitation sample(s)...");
                var neuralEngine = new MepPointNetNeuralEngine();

                foreach (var sample in samplesToTrain)
                {
                    List<XYZ> xyzPoints = sample.LocalRoiPoints.Select(p => new XYZ(p[0], p[1], p[2])).ToList();

                    MepSemanticClass targetClass = MepSemanticClass.PipingCylinder;
                    if (sample.Service == "Duct") targetClass = MepSemanticClass.Duct;
                    else if (sample.Service == "Cable Tray") targetClass = MepSemanticClass.CableTray;
                    else if (sample.Service == "Conduit") targetClass = MepSemanticClass.PipingCylinder;
                    else if (sample.Service == "Pipe Fitting") targetClass = MepSemanticClass.PipingElbow;
                    else if (sample.Service == "Wall" || sample.Service == "Steel") targetClass = MepSemanticClass.WallStructureClutter;

                    float targetDia = (float)Math.Max(sample.DiameterMm, sample.WidthMm);
                    XYZ offset = XYZ.Zero;
                    if (sample.StartPoint != null && sample.EndPoint != null)
                    {
                        XYZ center = new XYZ(sample.CenterPoint[0], sample.CenterPoint[1], sample.CenterPoint[2]);
                        XYZ avgPt = new XYZ(xyzPoints.Average(p => p.X), xyzPoints.Average(p => p.Y), xyzPoints.Average(p => p.Z));
                        offset = center - avgPt;
                    }

                    neuralEngine.TrainPositiveSample(xyzPoints, targetClass, targetDia, offset);
                    sample.IsTrained = true;
                }

                if (Settings.AutoSaveWeights)
                {
                    NeuralWeightsDataset.SaveCurrentWeights();
                }

                LastRetrainedTime = DateTime.Now;
                RetrainedBatchesCount++;
                LogActivity($"Imitation training complete! Retrained on {samplesToTrain.Count} samples. Neural weights saved.");

                OnStateChanged?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                LogActivity($"Manual modeling retraining error: {ex.Message}");
                return false;
            }
        }

        public void ClearBuffer()
        {
            lock (_bufferLock)
            {
                _modelingBuffer.Clear();
                TotalObservedCount = 0;
                PipesObservedCount = 0;
                DuctsObservedCount = 0;
                ElectricalObservedCount = 0;
                FittingsObservedCount = 0;
                StructuresObservedCount = 0;
            }
            LogActivity("User Modeling Watcher buffer and metrics reset.");
            OnStateChanged?.Invoke();
        }

        public bool ExportDataset(string zipFilePath)
        {
            try
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "Biruscan_UserModeling_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);

                string jsonPath = Path.Combine(tempDir, "user_modeling_samples.json");
                string json;
                lock (_bufferLock)
                {
                    json = JsonSerializer.Serialize(_modelingBuffer, new JsonSerializerOptions { WriteIndented = true });
                }
                File.WriteAllText(jsonPath, json);

                if (File.Exists(zipFilePath)) File.Delete(zipFilePath);
                ZipFile.CreateFromDirectory(tempDir, zipFilePath, CompressionLevel.Fastest, false);
                Directory.Delete(tempDir, true);
                LogActivity($"User modeling dataset exported to: {Path.GetFileName(zipFilePath)}");
                return true;
            }
            catch (Exception ex)
            {
                LogActivity($"Export dataset error: {ex.Message}");
                return false;
            }
        }

        private static double TryGetDiameterMm(Pipe pipe)
        {
            try
            {
                var p = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                if (p != null && p.HasValue) return p.AsDouble() * 304.8;
            }
            catch { }
            return 50.0;
        }

        private static double TryGetParamMm(Element elem, BuiltInParameter bip)
        {
            try
            {
                var p = elem.get_Parameter(bip);
                if (p != null && p.HasValue) return p.AsDouble() * 304.8;
            }
            catch { }
            return 0.0;
        }
    }
}
