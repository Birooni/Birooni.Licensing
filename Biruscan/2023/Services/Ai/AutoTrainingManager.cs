using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Biruscan.AI.DeepLearning;
using Biruscan.Models;

namespace Biruscan.Services.Ai
{
    public class TrackedFittedElement
    {
        public long ElementIdValue { get; set; }
        public string Service { get; set; }
        public string ToolName { get; set; }
        public DateTime CreatedTime { get; set; } = DateTime.Now;
        public ElementGeometrySnapshot InitialSnapshot { get; set; }
        public List<double[]> RoiPoints { get; set; } = new List<double[]>();
    }

    /// <summary>
    /// Master Active Learning & Auto-Training Manager.
    /// Listens to Revit DocumentChanged events to observe user workflows in real-time:
    ///   1. Accepted Fits (positive reinforcement).
    ///   2. User Manual Modifications / Corrections (learning from mistakes / residual learning).
    ///   3. User Deletions / Rollbacks (negative feedback to suppress false-positive fitters).
    /// Updates in-process neural network weights and synchronizes with the training engine.
    /// </summary>
    public sealed class AutoTrainingManager
    {
        private static readonly Lazy<AutoTrainingManager> _instance =
            new Lazy<AutoTrainingManager>(() => new AutoTrainingManager());

        public static AutoTrainingManager Instance => _instance.Value;

        public AutoTrainingSettings Settings => AutoTrainingSettings.Instance;

        private readonly ConcurrentDictionary<long, TrackedFittedElement> _trackedElements =
            new ConcurrentDictionary<long, TrackedFittedElement>();

        private readonly List<AutoLearningSample> _learningBuffer = new List<AutoLearningSample>();
        private readonly List<string> _activityLog = new List<string>();
        private readonly object _bufferLock = new object();
        private readonly object _logLock = new object();

        public int AcceptedFitsCount { get; private set; }
        public int CorrectionsLearnedCount { get; private set; }
        public int RejectionsCount { get; private set; }
        public DateTime? LastRetrainedTime { get; private set; }
        public bool IsMonitoring { get; private set; }

        public event Action OnStateChanged;
        public event Action<string> OnLogMessage;

        public static bool IsInternalTransaction { get; set; } = false;
        private static bool _isInternalTransaction { get => IsInternalTransaction; set => IsInternalTransaction = value; }

        private AutoTrainingManager()
        {
            LastRetrainedTime = NeuralWeightsDataset.LastTrainedTime;
            LogActivity("Auto-Training & Active Learning Engine initialized.");
        }

        public void Initialize(UIControlledApplication app)
        {
            if (app == null) return;
            try
            {
                app.ControlledApplication.DocumentChanged += OnDocumentChanged;
                IsMonitoring = true;
                LogActivity("Active Learning Document Watcher active. Monitoring Fitter and MEP operations.");
            }
            catch (Exception ex)
            {
                LogActivity($"Failed to initialize DocumentChanged hook: {ex.Message}");
            }
        }

        public void Shutdown(UIControlledApplication app)
        {
            if (app == null) return;
            try
            {
                app.ControlledApplication.DocumentChanged -= OnDocumentChanged;
                IsMonitoring = false;
            }
            catch { }
        }

        public void SetInternalTransaction(bool isInternal)
        {
            _isInternalTransaction = isInternal;
        }

        /// <summary>
        /// Registers a newly fitted Revit element into the active learning tracker.
        /// </summary>
        public void RecordFittedElement(Document doc, ElementId elemId, string service, string toolName, List<XYZ> roiPoints = null)
        {
            if (!Settings.Enabled || elemId == null || elemId == ElementId.InvalidElementId || doc == null)
                return;

            try
            {
                Element elem = doc.GetElement(elemId);
                if (elem == null) return;

                var snapshot = CaptureSnapshot(elem);
                if (snapshot == null) return;

                var tracked = new TrackedFittedElement
                {
                    ElementIdValue = elemId.IntegerValue,
                    Service = service ?? "Piping",
                    ToolName = toolName ?? "Fitter",
                    CreatedTime = DateTime.Now,
                    InitialSnapshot = snapshot,
                    RoiPoints = roiPoints != null ? roiPoints.Select(p => new double[] { p.X, p.Y, p.Z }).ToList() : new List<double[]>()
                };

                _trackedElements[elemId.IntegerValue] = tracked;

                if (Settings.LearnAcceptedFits)
                {
                    var sample = new AutoLearningSample
                    {
                        LearningType = ActiveLearningType.PositiveFit,
                        Service = service,
                        SourceTool = toolName,
                        ElementIdValue = elemId.IntegerValue,
                        ElementCategory = elem.Category?.Name ?? "MEP",
                        InitialGeometry = snapshot.Clone(),
                        RoiPoints = tracked.RoiPoints,
                        Description = $"Fitted {service} (Dia={snapshot.DiameterMm:F0}mm, Length={GetSnapshotLengthFt(snapshot):F1}ft)"
                    };

                    lock (_bufferLock)
                    {
                        _learningBuffer.Add(sample);
                        AcceptedFitsCount++;
                    }

                    LogActivity($"[Accepted Fit] {toolName} placed {service} #{elemId.IntegerValue} (Dia={snapshot.DiameterMm:F0}mm).");
                    CheckAutoRetrainThreshold();
                }

                OnStateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                LogActivity($"Error recording fitted element #{elemId.IntegerValue}: {ex.Message}");
            }
        }

        private void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            if (!Settings.Enabled || _isInternalTransaction) return;

            Document doc = e.GetDocument();
            if (doc == null) return;

            try
            {
                var transNames = e.GetTransactionNames();
                string transName = transNames != null && transNames.Count > 0 ? transNames[0] : "";

                // 1. Handle Deletions (Negative / Rejection Learning)
                if (Settings.LearnRejections)
                {
                    var deletedIds = e.GetDeletedElementIds();
                    if (deletedIds != null && deletedIds.Count > 0)
                    {
                        foreach (var delId in deletedIds)
                        {
                            if (_trackedElements.TryRemove(delId.IntegerValue, out var tracked))
                            {
                                var timeElapsed = DateTime.Now - tracked.CreatedTime;
                                // If deleted within 45 minutes of fitting, it represents a user rejection/undo
                                if (timeElapsed.TotalMinutes <= 45.0)
                                {
                                    var negSample = new AutoLearningSample
                                    {
                                        LearningType = ActiveLearningType.RejectedDeletion,
                                        Service = tracked.Service,
                                        SourceTool = tracked.ToolName,
                                        ElementIdValue = delId.IntegerValue,
                                        ElementCategory = tracked.Service,
                                        InitialGeometry = tracked.InitialSnapshot,
                                        RoiPoints = tracked.RoiPoints,
                                        Description = $"User deleted {tracked.ToolName} {tracked.Service} #{delId.IntegerValue} after {timeElapsed.TotalSeconds:F0}s (False-positive rejection)"
                                    };

                                    lock (_bufferLock)
                                    {
                                        _learningBuffer.Add(negSample);
                                        RejectionsCount++;
                                    }

                                    LogActivity($"[Rejection Learned] {tracked.ToolName} #{delId.IntegerValue} was deleted by user. False-positive feedback recorded.");
                                    CheckAutoRetrainThreshold();
                                }
                            }
                        }
                    }
                }

                // 2. Handle Modifications (Mistake & Correction Learning)
                if (Settings.LearnCorrections)
                {
                    var modifiedIds = e.GetModifiedElementIds();
                    if (modifiedIds != null && modifiedIds.Count > 0)
                    {
                        foreach (var modId in modifiedIds)
                        {
                            if (_trackedElements.TryGetValue(modId.IntegerValue, out var tracked))
                            {
                                Element elem = doc.GetElement(modId);
                                if (elem == null) continue;

                                var currentSnapshot = CaptureSnapshot(elem);
                                if (currentSnapshot == null) continue;

                                var initial = tracked.InitialSnapshot;
                                bool hasSignificantDelta = false;
                                var deltaLogs = new List<string>();

                                // Diameter change?
                                double dDia = currentSnapshot.DiameterMm - initial.DiameterMm;
                                if (Math.Abs(dDia) > 1.5)
                                {
                                    hasSignificantDelta = true;
                                    deltaLogs.Add($"Diameter {initial.DiameterMm:F0}mm -> {currentSnapshot.DiameterMm:F0}mm ({dDia:+0.0;-0.0}mm)");
                                }

                                // Dimension (Width/Height) change?
                                double dWidth = currentSnapshot.WidthMm - initial.WidthMm;
                                double dHeight = currentSnapshot.HeightMm - initial.HeightMm;
                                if (Math.Abs(dWidth) > 5.0 || Math.Abs(dHeight) > 5.0)
                                {
                                    hasSignificantDelta = true;
                                    deltaLogs.Add($"Size {initial.WidthMm:F0}x{initial.HeightMm:F0}mm -> {currentSnapshot.WidthMm:F0}x{currentSnapshot.HeightMm:F0}mm");
                                }

                                // Start or End point moved?
                                if (currentSnapshot.StartPoint != null && initial.StartPoint != null && currentSnapshot.EndPoint != null && initial.EndPoint != null)
                                {
                                    double dStart = Distance(currentSnapshot.StartPoint, initial.StartPoint);
                                    double dEnd = Distance(currentSnapshot.EndPoint, initial.EndPoint);

                                    if (dStart > 0.05 || dEnd > 0.05) // > ~15 mm
                                    {
                                        hasSignificantDelta = true;
                                        deltaLogs.Add($"Axis adjusted (Start moved {dStart * 304.8:F0}mm, End moved {dEnd * 304.8:F0}mm)");
                                    }
                                }

                                if (hasSignificantDelta)
                                {
                                    string deltaDesc = string.Join(", ", deltaLogs);
                                    var corrSample = new AutoLearningSample
                                    {
                                        LearningType = ActiveLearningType.UserCorrection,
                                        Service = tracked.Service,
                                        SourceTool = tracked.ToolName,
                                        ElementIdValue = modId.IntegerValue,
                                        ElementCategory = elem.Category?.Name ?? "MEP",
                                        InitialGeometry = initial.Clone(),
                                        CorrectedGeometry = currentSnapshot.Clone(),
                                        RoiPoints = tracked.RoiPoints,
                                        Description = $"User corrected #{modId.IntegerValue} ({tracked.ToolName}): {deltaDesc}"
                                    };

                                    lock (_bufferLock)
                                    {
                                        _learningBuffer.Add(corrSample);
                                        CorrectionsLearnedCount++;
                                    }

                                    LogActivity($"[Mistake & Correction Learned] #{modId.IntegerValue} ({tracked.ToolName}): {deltaDesc}");

                                    // Update tracked snapshot to avoid duplicate reports for the same edit
                                    tracked.InitialSnapshot = currentSnapshot;
                                    CheckAutoRetrainThreshold();
                                }
                            }
                        }
                    }
                }

                OnStateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                LogActivity($"Error processing DocumentChanged: {ex.Message}");
            }
        }

        private void CheckAutoRetrainThreshold()
        {
            int pendingCount;
            lock (_bufferLock)
            {
                pendingCount = _learningBuffer.Count(s => !s.IsTrained);
            }

            if (pendingCount >= Settings.AutoRetrainThreshold && Settings.AutoRetrainThreshold > 0)
            {
                LogActivity($"Auto-retrain threshold reached ({pendingCount} pending samples). Triggering background neural update...");
                Task.Run(() => RetrainModelNow());
            }
        }

        /// <summary>
        /// Retrains neural network weights from accumulated active learning buffer.
        /// </summary>
        public bool RetrainModelNow()
        {
            List<AutoLearningSample> samplesToTrain;
            lock (_bufferLock)
            {
                samplesToTrain = _learningBuffer.Where(s => !s.IsTrained).ToList();
            }

            if (samplesToTrain.Count == 0)
            {
                LogActivity("No new active learning samples to train.");
                return false;
            }

            try
            {
                LogActivity($"Starting active learning training on {samplesToTrain.Count} sample(s)...");

                var neuralEngine = new MepPointNetNeuralEngine();

                int posCount = 0, corrCount = 0, negCount = 0;

                foreach (var sample in samplesToTrain)
                {
                    List<XYZ> pts = sample.RoiPoints != null && sample.RoiPoints.Count >= 4
                        ? sample.RoiPoints.Select(p => new XYZ(p[0], p[1], p[2])).ToList()
                        : GenerateSyntheticPointsForSnapshot(sample.InitialGeometry);

                    MepSemanticClass targetClass = MapServiceToClass(sample.Service, sample.SourceTool);

                    switch (sample.LearningType)
                    {
                        case ActiveLearningType.PositiveFit:
                            neuralEngine.TrainPositiveSample(
                                pts,
                                targetClass,
                                (float)sample.InitialGeometry.DiameterMm,
                                XYZ.Zero);
                            posCount++;
                            break;

                        case ActiveLearningType.UserCorrection:
                            if (sample.CorrectedGeometry != null)
                            {
                                double dDia = sample.CorrectedGeometry.DiameterMm - sample.InitialGeometry.DiameterMm;
                                XYZ deltaOffset = XYZ.Zero;
                                if (sample.CorrectedGeometry.StartPoint != null && sample.InitialGeometry.StartPoint != null)
                                {
                                    deltaOffset = new XYZ(
                                        sample.CorrectedGeometry.StartPoint[0] - sample.InitialGeometry.StartPoint[0],
                                        sample.CorrectedGeometry.StartPoint[1] - sample.InitialGeometry.StartPoint[1],
                                        sample.CorrectedGeometry.StartPoint[2] - sample.InitialGeometry.StartPoint[2]);
                                }

                                neuralEngine.TrainCorrectionSample(pts, deltaOffset, (float)dDia);
                                corrCount++;
                            }
                            break;

                        case ActiveLearningType.RejectedDeletion:
                            neuralEngine.TrainNegativeSample(pts, targetClass);
                            negCount++;
                            break;
                    }

                    sample.IsTrained = true;
                }

                if (Settings.AutoSaveWeights)
                {
                    NeuralWeightsDataset.SaveCurrentWeights();
                }

                SaveLearningBufferArchive();

                LastRetrainedTime = DateTime.Now;
                LogActivity($"[Auto-Retrain Complete] Adapted weights: +{posCount} accepted, +{corrCount} corrections, +{negCount} rejections.");
                OnStateChanged?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                LogActivity($"Retraining failed: {ex.Message}");
                return false;
            }
        }

        private void SaveLearningBufferArchive()
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Birooni", "Training", "AutoLearning");

                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                string filePath = Path.Combine(dir, $"auto_learning_{DateTime.Now:yyyyMMdd}.json");

                List<AutoLearningSample> listCopy;
                lock (_bufferLock)
                {
                    listCopy = new List<AutoLearningSample>(_learningBuffer);
                }

                string json = JsonSerializer.Serialize(listCopy, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, json);
            }
            catch { }
        }

        public void ClearBuffer()
        {
            lock (_bufferLock)
            {
                _learningBuffer.Clear();
                AcceptedFitsCount = 0;
                CorrectionsLearnedCount = 0;
                RejectionsCount = 0;
            }
            LogActivity("Active learning buffer cleared.");
            OnStateChanged?.Invoke();
        }

        public int GetBufferCount()
        {
            lock (_bufferLock) return _learningBuffer.Count;
        }

        public int GetPendingCount()
        {
            lock (_bufferLock) return _learningBuffer.Count(s => !s.IsTrained);
        }

        public List<AutoLearningSample> GetAllSamples()
        {
            lock (_bufferLock) return new List<AutoLearningSample>(_learningBuffer);
        }

        public List<string> GetRecentLogs(int maxCount = 100)
        {
            lock (_logLock)
            {
                int count = Math.Min(maxCount, _activityLog.Count);
                return _activityLog.Skip(_activityLog.Count - count).ToList();
            }
        }

        public void LogActivity(string msg)
        {
            string entry = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            lock (_logLock)
            {
                _activityLog.Add(entry);
                if (_activityLog.Count > 300) _activityLog.RemoveAt(0);
            }
            OnLogMessage?.Invoke(entry);
        }

        public bool ExportAutoDataset(string targetZipPath)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Birooni", "Training", "AutoLearning");

                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                SaveLearningBufferArchive();

                if (File.Exists(targetZipPath)) File.Delete(targetZipPath);
                ZipFile.CreateFromDirectory(dir, targetZipPath, CompressionLevel.Fastest, includeBaseDirectory: false);
                LogActivity($"Exported active learning dataset to {Path.GetFileName(targetZipPath)}.");
                return true;
            }
            catch (Exception ex)
            {
                LogActivity($"Failed to export dataset: {ex.Message}");
                return false;
            }
        }

        #region Helpers

        private static ElementGeometrySnapshot CaptureSnapshot(Element elem)
        {
            if (elem == null) return null;

            var snapshot = new ElementGeometrySnapshot();

            if (elem.Location is LocationCurve lc && lc.Curve is Line line)
            {
                XYZ p0 = line.GetEndPoint(0);
                XYZ p1 = line.GetEndPoint(1);
                snapshot.StartPoint = new double[] { p0.X, p0.Y, p0.Z };
                snapshot.EndPoint = new double[] { p1.X, p1.Y, p1.Z };
                XYZ dir = (p1 - p0).Normalize();
                snapshot.DirectionVector = new double[] { dir.X, dir.Y, dir.Z };
                XYZ mid = (p0 + p1) * 0.5;
                snapshot.CenterPoint = new double[] { mid.X, mid.Y, mid.Z };
                snapshot.ElevationFeet = mid.Z;
            }
            else if (elem.Location is LocationPoint lp)
            {
                XYZ pt = lp.Point;
                snapshot.CenterPoint = new double[] { pt.X, pt.Y, pt.Z };
                snapshot.StartPoint = new double[] { pt.X, pt.Y, pt.Z };
                snapshot.EndPoint = new double[] { pt.X, pt.Y, pt.Z };
                snapshot.ElevationFeet = pt.Z;
            }

            snapshot.TypeName = elem.Name;

            // Diameter
            Parameter pDiam = elem.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)
                           ?? elem.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM)
                           ?? elem.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
            if (pDiam != null && pDiam.StorageType == StorageType.Double)
            {
                snapshot.DiameterMm = UnitUtils.ConvertFromInternalUnits(pDiam.AsDouble(), UnitTypeId.Millimeters);
            }

            // Width / Height
            Parameter pWidth = elem.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM)
                            ?? elem.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
            if (pWidth != null && pWidth.StorageType == StorageType.Double)
            {
                snapshot.WidthMm = UnitUtils.ConvertFromInternalUnits(pWidth.AsDouble(), UnitTypeId.Millimeters);
            }

            Parameter pHeight = elem.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM)
                             ?? elem.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
            if (pHeight != null && pHeight.StorageType == StorageType.Double)
            {
                snapshot.HeightMm = UnitUtils.ConvertFromInternalUnits(pHeight.AsDouble(), UnitTypeId.Millimeters);
            }

            return snapshot;
        }

        private static double GetSnapshotLengthFt(ElementGeometrySnapshot s)
        {
            if (s.StartPoint == null || s.EndPoint == null) return 0;
            double dx = s.EndPoint[0] - s.StartPoint[0];
            double dy = s.EndPoint[1] - s.StartPoint[1];
            double dz = s.EndPoint[2] - s.StartPoint[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static double Distance(double[] p1, double[] p2)
        {
            if (p1 == null || p2 == null || p1.Length < 3 || p2.Length < 3) return 0;
            double dx = p1[0] - p2[0];
            double dy = p1[1] - p2[1];
            double dz = p1[2] - p2[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static MepSemanticClass MapServiceToClass(string service, string toolName)
        {
            string s = (service + " " + toolName).ToLowerInvariant();
            if (s.Contains("elbow")) return MepSemanticClass.PipingElbow;
            if (s.Contains("tee")) return MepSemanticClass.PipingTee;
            if (s.Contains("duct")) return MepSemanticClass.Duct;
            if (s.Contains("tray") || s.Contains("cable")) return MepSemanticClass.CableTray;
            if (s.Contains("wall")) return MepSemanticClass.WallStructureClutter;
            return MepSemanticClass.PipingCylinder;
        }

        private static List<XYZ> GenerateSyntheticPointsForSnapshot(ElementGeometrySnapshot s)
        {
            var pts = new List<XYZ>();
            if (s.StartPoint == null || s.EndPoint == null)
            {
                XYZ cp = s.CenterPoint != null ? new XYZ(s.CenterPoint[0], s.CenterPoint[1], s.CenterPoint[2]) : XYZ.Zero;
                pts.Add(cp);
                return pts;
            }

            XYZ p0 = new XYZ(s.StartPoint[0], s.StartPoint[1], s.StartPoint[2]);
            XYZ p1 = new XYZ(s.EndPoint[0], s.EndPoint[1], s.EndPoint[2]);
            XYZ axis = (p1 - p0).Normalize();
            double len = p0.DistanceTo(p1);
            double r = (s.DiameterMm > 0 ? s.DiameterMm : 50.0) / (2.0 * 304.8);

            XYZ u = new XYZ(-axis.Y, axis.X, 0);
            if (u.GetLength() < 0.1) u = XYZ.BasisX.CrossProduct(axis);
            u = u.Normalize();
            XYZ v = axis.CrossProduct(u).Normalize();

            for (double t = 0; t <= len; t += Math.Max(0.2, len / 10.0))
            {
                XYZ center = p0 + axis.Multiply(t);
                for (int a = 0; a < 8; a++)
                {
                    double rad = a * (Math.PI / 4.0);
                    XYZ ringPt = center + u.Multiply(r * Math.Cos(rad)) + v.Multiply(r * Math.Sin(rad));
                    pts.Add(ringPt);
                }
            }

            return pts;
        }

        #endregion
    }
}
