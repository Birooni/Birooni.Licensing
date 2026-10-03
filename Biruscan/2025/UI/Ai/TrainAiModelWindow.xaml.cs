using System;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Microsoft.Win32;
using Biruscan.Models;
using Biruscan.Services.Ai;
using Biruscan.AI.DeepLearning;

namespace Biruscan.UI.Ai
{
    /// <summary>
    /// Train AI & Active Learning dialog with 3 Dedicated Tabs:
    ///   Tab 1: 🎯 Manual Capture & Dataset (Ground truth element ROI capture)
    ///   Tab 2: ⚡ Auto-Training (Fitter & MEP Operations continuous active learning)
    ///   Tab 3: 👁️ User Modeling Watcher (Live imitation learning from manual Revit modeling)
    /// </summary>
    public partial class TrainAiModelWindow : Window
    {
        private readonly Document _doc;
        private readonly AutoTrainingSettings _autoSettings = AutoTrainingSettings.Instance;
        private readonly AutoTrainingManager _autoManager = AutoTrainingManager.Instance;
        private readonly UserModelingWatcherSettings _watcherSettings = UserModelingWatcherSettings.Instance;
        private readonly UserModelingWatcher _watcher = UserModelingWatcher.Instance;

        public string SelectedService { get; private set; } = "Piping";
        public bool TrainHybrid { get; private set; } = true;
        public bool TrainPureDl { get; private set; } = true;
        public bool TriggerTrainingAfterCapture { get; private set; } = true;
        public bool CaptureRequested { get; private set; }

        public TrainAiModelWindow(Document doc)
        {
            _doc = doc;
            InitializeComponent();

            LoadAutoTrainingSettingsUI();
            RefreshAutoTrainingTelemetryUI();

            LoadWatcherSettingsUI();
            RefreshWatcherTelemetryUI();

            _autoManager.OnStateChanged += AutoManager_OnStateChanged;
            _autoManager.OnLogMessage += AutoManager_OnLogMessage;

            _watcher.OnStateChanged += Watcher_OnStateChanged;
            _watcher.OnLogMessage += Watcher_OnLogMessage;

            Loaded += (_, __) =>
            {
                RefreshStatus();
                RefreshAutoTrainingTelemetryUI();
                PopulateInitialLogFeed();
                RefreshWatcherTelemetryUI();
                PopulateInitialWatcherLogFeed();
                if (!MepSkillCurriculum.IsTaught())
                {
                    try { MepSkillCurriculum.TeachAll(epochs: 2); RefreshStatus(); }
                    catch { }
                }
            };

            Closed += (_, __) =>
            {
                _autoManager.OnStateChanged -= AutoManager_OnStateChanged;
                _autoManager.OnLogMessage -= AutoManager_OnLogMessage;
                _watcher.OnStateChanged -= Watcher_OnStateChanged;
                _watcher.OnLogMessage -= Watcher_OnLogMessage;
            };
        }

        #region Tab 1: Manual Capture Workflow

        private void CaptureButton_Click(object sender, RoutedEventArgs e)
        {
            // If the user is on Tab 2 (Auto-Training), trigger a quick auto-retrain
            if (TrainAiTabControl.SelectedIndex == 1)
            {
                RetrainBufferButton_Click(sender, e);
                return;
            }

            // If the user is on Tab 3 (User Modeling Watcher), trigger manual watcher retrain
            if (TrainAiTabControl.SelectedIndex == 2)
            {
                WatcherRetrainNowButton_Click(sender, e);
                return;
            }

            if (ServiceComboBox.SelectedItem is ComboBoxItem item)
            {
                SelectedService = item.Content?.ToString() ?? "Piping";
            }
            else
            {
                SelectedService = "Piping";
            }

            TrainHybrid = HybridCheckBox.IsChecked == true;
            TrainPureDl = PureDlCheckBox.IsChecked == true;
            TriggerTrainingAfterCapture = TriggerTrainingCheckBox.IsChecked == true;

            if (!TrainHybrid && !TrainPureDl && TriggerTrainingAfterCapture)
            {
                var res = MessageBox.Show(
                    "No training method selected. The sample will be captured but no model will be retrained.\n\nContinue?",
                    "Train AI", MessageBoxButton.OKCancel, MessageBoxImage.Information);
                if (res != MessageBoxResult.OK) return;
            }

            CaptureRequested = true;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void RefreshStatus()
        {
            int localSamples = InProcessAiTrainer.CountSamples();
            SamplesCountText.Text = $"Captured Samples: {localSamples}";
            bool fineTuned = Biruscan.AI.DeepLearning.NeuralWeightsDataset.FineTunedDiffersFromBase();
            HybridStatusText.Text = fineTuned
                ? "Hybrid model: in-process fine-tuned (Trained AI uses these weights)"
                : "Hybrid model: pretrained (capture a sample to fine-tune)";
            PureDlStatusText.Text = fineTuned
                ? "Pure DL model: in-process fine-tuned"
                : "Pure DL model: pretrained";
            string onnx = OnnxMepClassifier.IsAvailable ? "ONNX loaded" : "ONNX not loaded";
            InProcStatusText.Text = NeuralWeightsDataset.StatusSummary() + " · " + onnx;
            StatusHeaderText.Text = "In-process C# engine active";
            if (CoreSkillsStatusText != null)
            {
                if (MepSkillCurriculum.IsTaught())
                {
                    CoreSkillsStatusText.Text = "Core skills: taught (pipe, elbow, tee, cross, reducer, union, duct/tray, wall, insulation, as-built slope).";
                    CoreSkillsStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80));
                }
                else
                {
                    CoreSkillsStatusText.Text = "Core skills: not taught yet. Click the button once to load the skill book.";
                    CoreSkillsStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 204, 102));
                }
            }
        }

        private void TeachCoreSkillsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                TeachCoreSkillsButton.IsEnabled = false;
                string msg = MepSkillCurriculum.TeachAll(epochs: 3);
                RefreshStatus();
                MessageBox.Show(msg + "\n\nThen capture a real plant (pipes + walls/trays) so the net sees your scan density.",
                    "Core skills", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Teach failed:\n" + ex.Message, "Core skills", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                TeachCoreSkillsButton.IsEnabled = true;
            }
        }

        private static string TrainingRoot() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Birooni", "Training");

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            string trainingRoot = TrainingRoot();
            if (!Directory.Exists(trainingRoot)) { MessageBox.Show("No training samples have been collected yet.", "Export Dataset"); return; }

            var dlg = new SaveFileDialog
            {
                FileName = $"biruscan_training_{DateTime.Now:yyyyMMdd_HHmm}.zip",
                Filter = "Zip archives (*.zip)|*.zip",
                Title = "Export training dataset",
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                if (File.Exists(dlg.FileName)) File.Delete(dlg.FileName);
                ZipFile.CreateFromDirectory(trainingRoot, dlg.FileName, CompressionLevel.Fastest, includeBaseDirectory: false);
                MessageBox.Show($"Exported {Path.GetFileName(dlg.FileName)} OK.", "Export Dataset");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Export failed:\n" + ex.Message, "Export Dataset", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "Zip archives (*.zip)|*.zip", Title = "Import training dataset" };
            if (dlg.ShowDialog(this) != true) return;

            string trainingRoot = TrainingRoot();
            Directory.CreateDirectory(trainingRoot);
            try
            {
                using (var archive = ZipFile.OpenRead(dlg.FileName))
                {
                    int copied = 0, skipped = 0;
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;
                        string outPath = Path.Combine(trainingRoot, entry.FullName);
                        Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? trainingRoot);
                        if (File.Exists(outPath)) { skipped++; continue; }
                        entry.ExtractToFile(outPath);
                        copied++;
                    }
                    MessageBox.Show($"Imported {copied} file(s), skipped {skipped} existing.", "Import Dataset");
                }
                RefreshStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Import failed:\n" + ex.Message, "Import Dataset", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Tab 2: Auto-Training & Active Learning (Fitter & MEP Tools)

        private void LoadAutoTrainingSettingsUI()
        {
            AutoTrainMasterCheckBox.IsChecked = _autoSettings.Enabled;
            ObserveFitterCheckBox.IsChecked = _autoSettings.ObserveFitterTools;
            ObserveMepOpsCheckBox.IsChecked = _autoSettings.ObserveMepOperations;
            LearnAcceptedCheckBox.IsChecked = _autoSettings.LearnAcceptedFits;
            LearnCorrectionsCheckBox.IsChecked = _autoSettings.LearnCorrections;
            LearnRejectionsCheckBox.IsChecked = _autoSettings.LearnRejections;
            AutoSaveWeightsCheckBox.IsChecked = _autoSettings.AutoSaveWeights;

            switch (_autoSettings.AutoRetrainThreshold)
            {
                case 5: ThresholdComboBox.SelectedIndex = 0; break;
                case 10: ThresholdComboBox.SelectedIndex = 1; break;
                case 20: ThresholdComboBox.SelectedIndex = 2; break;
                case 50: ThresholdComboBox.SelectedIndex = 3; break;
                default: ThresholdComboBox.SelectedIndex = 1; break;
            }
        }

        private void AutoTrainMasterCheckBox_Click(object sender, RoutedEventArgs e)
        {
            _autoSettings.Enabled = AutoTrainMasterCheckBox.IsChecked == true;
            _autoSettings.Save();
            RefreshAutoTrainingTelemetryUI();
            _autoManager.LogActivity($"Auto-Training Master State set to: {(_autoSettings.Enabled ? "ENABLED" : "DISABLED")}");
        }

        private void AutoTrainSetting_Changed(object sender, RoutedEventArgs e)
        {
            _autoSettings.ObserveFitterTools = ObserveFitterCheckBox.IsChecked == true;
            _autoSettings.ObserveMepOperations = ObserveMepOpsCheckBox.IsChecked == true;
            _autoSettings.LearnAcceptedFits = LearnAcceptedCheckBox.IsChecked == true;
            _autoSettings.LearnCorrections = LearnCorrectionsCheckBox.IsChecked == true;
            _autoSettings.LearnRejections = LearnRejectionsCheckBox.IsChecked == true;
            _autoSettings.AutoSaveWeights = AutoSaveWeightsCheckBox.IsChecked == true;
            _autoSettings.Save();
        }

        private void ThresholdComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            switch (ThresholdComboBox.SelectedIndex)
            {
                case 0: _autoSettings.AutoRetrainThreshold = 5; break;
                case 1: _autoSettings.AutoRetrainThreshold = 10; break;
                case 2: _autoSettings.AutoRetrainThreshold = 20; break;
                case 3: _autoSettings.AutoRetrainThreshold = 50; break;
            }
            _autoSettings.Save();
        }

        private void RefreshAutoTrainingTelemetryUI()
        {
            AcceptedCountText.Text = _autoManager.AcceptedFitsCount.ToString();
            CorrectionsCountText.Text = _autoManager.CorrectionsLearnedCount.ToString();
            RejectionsCountText.Text = _autoManager.RejectionsCount.ToString();

            int pending = _autoManager.GetPendingCount();
            PendingBufferText.Text = $"Buffer: {pending} sample(s) pending";

            if (_autoSettings.Enabled && _autoManager.IsMonitoring)
            {
                StatusLed.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80)); // Green
                MonitoringStatusText.Text = "Status: Monitoring Active";
                MonitoringStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80));
            }
            else
            {
                StatusLed.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(158, 158, 158)); // Gray
                MonitoringStatusText.Text = "Status: Idle / Disabled";
                MonitoringStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(158, 158, 158));
            }

            if (_autoManager.LastRetrainedTime.HasValue)
            {
                LastRetrainText.Text = $"Last Retrain: {_autoManager.LastRetrainedTime.Value:HH:mm:ss}";
            }
            else
            {
                LastRetrainText.Text = "Last Retrain: Ready (Default)";
            }
        }

        private void PopulateInitialLogFeed()
        {
            var logs = _autoManager.GetRecentLogs(50);
            ActivityLogTextBox.Text = string.Join(Environment.NewLine, logs);
            LogScrollViewer.ScrollToEnd();
        }

        private void AutoManager_OnStateChanged()
        {
            Dispatcher.Invoke(() =>
            {
                RefreshAutoTrainingTelemetryUI();
            });
        }

        private void AutoManager_OnLogMessage(string msg)
        {
            Dispatcher.Invoke(() =>
            {
                if (string.IsNullOrEmpty(ActivityLogTextBox.Text))
                    ActivityLogTextBox.Text = msg;
                else
                    ActivityLogTextBox.AppendText(Environment.NewLine + msg);

                LogScrollViewer.ScrollToEnd();
            });
        }

        private void RetrainBufferButton_Click(object sender, RoutedEventArgs e)
        {
            int pending = _autoManager.GetPendingCount();
            if (pending == 0)
            {
                MessageBox.Show("No pending active learning samples in the buffer.\nUse Fitter or MEP tools in Revit to generate real-time samples.", "Auto-Training", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            bool success = _autoManager.RetrainModelNow();
            RefreshAutoTrainingTelemetryUI();

            if (success)
            {
                MessageBox.Show($"Neural model successfully retrained on {pending} active learning sample(s)!\n\nWeights updated in memory and saved to BiruscanMepWeights.dat.", "Auto-Training Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void ClearBufferButton_Click(object sender, RoutedEventArgs e)
        {
            var res = MessageBox.Show("Are you sure you want to clear the active learning buffer and reset counters?", "Clear Buffer", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes)
            {
                _autoManager.ClearBuffer();
                RefreshAutoTrainingTelemetryUI();
                ActivityLogTextBox.Clear();
            }
        }

        private void ExportAutoDatasetButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                FileName = $"biruscan_autotrained_{DateTime.Now:yyyyMMdd_HHmm}.zip",
                Filter = "Zip archives (*.zip)|*.zip",
                Title = "Export Auto-Trained Active Learning Dataset"
            };
            if (dlg.ShowDialog(this) != true) return;

            bool ok = _autoManager.ExportAutoDataset(dlg.FileName);
            if (ok)
            {
                MessageBox.Show($"Active learning dataset exported successfully to {Path.GetFileName(dlg.FileName)}.", "Export Auto Dataset");
            }
            else
            {
                MessageBox.Show("Failed to export active learning dataset.", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Tab 3: User Modeling Watcher Workflow (Learning from Manual Modeling)

        private void LoadWatcherSettingsUI()
        {
            WatcherMasterCheckBox.IsChecked = _watcherSettings.Enabled;
            WatchPipesCheckBox.IsChecked = _watcherSettings.WatchPipes;
            WatchDuctsCheckBox.IsChecked = _watcherSettings.WatchDucts;
            WatchConduitsCheckBox.IsChecked = _watcherSettings.WatchConduits;
            WatchFittingsCheckBox.IsChecked = _watcherSettings.WatchFittings;
            WatchStructuresCheckBox.IsChecked = _watcherSettings.WatchWallsAndStructures;

            switch (_watcherSettings.AutoRetrainThreshold)
            {
                case 5: WatcherThresholdComboBox.SelectedIndex = 0; break;
                case 10: WatcherThresholdComboBox.SelectedIndex = 1; break;
                case 20: WatcherThresholdComboBox.SelectedIndex = 2; break;
                case 50: WatcherThresholdComboBox.SelectedIndex = 3; break;
                default: WatcherThresholdComboBox.SelectedIndex = 1; break;
            }
        }

        private void WatcherMasterCheckBox_Click(object sender, RoutedEventArgs e)
        {
            _watcherSettings.Enabled = WatcherMasterCheckBox.IsChecked == true;
            _watcherSettings.Save();
            RefreshWatcherTelemetryUI();
            _watcher.LogActivity($"User Modeling Watcher state set to: {(_watcherSettings.Enabled ? "ENABLED" : "DISABLED")}");
        }

        private void WatcherSetting_Changed(object sender, RoutedEventArgs e)
        {
            _watcherSettings.WatchPipes = WatchPipesCheckBox.IsChecked == true;
            _watcherSettings.WatchDucts = WatchDuctsCheckBox.IsChecked == true;
            _watcherSettings.WatchConduits = WatchConduitsCheckBox.IsChecked == true;
            _watcherSettings.WatchFittings = WatchFittingsCheckBox.IsChecked == true;
            _watcherSettings.WatchWallsAndStructures = WatchStructuresCheckBox.IsChecked == true;
            _watcherSettings.Save();
        }

        private void WatcherThresholdComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            switch (WatcherThresholdComboBox.SelectedIndex)
            {
                case 0: _watcherSettings.AutoRetrainThreshold = 5; break;
                case 1: _watcherSettings.AutoRetrainThreshold = 10; break;
                case 2: _watcherSettings.AutoRetrainThreshold = 20; break;
                case 3: _watcherSettings.AutoRetrainThreshold = 50; break;
            }
            _watcherSettings.Save();
        }

        private void RefreshWatcherTelemetryUI()
        {
            WatcherTotalObservedText.Text = _watcher.TotalObservedCount.ToString();
            WatcherPipesText.Text = _watcher.PipesObservedCount.ToString();
            WatcherDuctsText.Text = _watcher.DuctsObservedCount.ToString();
            WatcherFittingsText.Text = (_watcher.ElectricalObservedCount + _watcher.FittingsObservedCount + _watcher.StructuresObservedCount).ToString();

            int pending = _watcher.GetPendingCount();
            WatcherPendingText.Text = $"Buffer: {pending} sample(s) pending";

            if (_watcherSettings.Enabled)
            {
                WatcherStatusLed.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80)); // Green
                WatcherStatusText.Text = "Status: Watching Active";
                WatcherStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80));
            }
            else
            {
                WatcherStatusLed.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(158, 158, 158)); // Gray
                WatcherStatusText.Text = "Status: Inactive / Disabled";
                WatcherStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(158, 158, 158));
            }

            if (_watcher.LastRetrainedTime.HasValue)
            {
                WatcherLastRetrainText.Text = $"Last Retrain: {_watcher.LastRetrainedTime.Value:HH:mm:ss}";
            }
            else
            {
                WatcherLastRetrainText.Text = "Last Retrain: Ready";
            }
        }

        private void PopulateInitialWatcherLogFeed()
        {
            var logs = _watcher.GetRecentLogs(50);
            WatcherLogTextBox.Text = string.Join(Environment.NewLine, logs);
            WatcherLogScrollViewer.ScrollToEnd();
        }

        private void Watcher_OnStateChanged()
        {
            Dispatcher.Invoke(() =>
            {
                RefreshWatcherTelemetryUI();
            });
        }

        private void Watcher_OnLogMessage(string msg)
        {
            Dispatcher.Invoke(() =>
            {
                if (string.IsNullOrEmpty(WatcherLogTextBox.Text))
                    WatcherLogTextBox.Text = msg;
                else
                    WatcherLogTextBox.AppendText(Environment.NewLine + msg);

                WatcherLogScrollViewer.ScrollToEnd();
            });
        }

        private void WatcherRetrainNowButton_Click(object sender, RoutedEventArgs e)
        {
            int pending = _watcher.GetPendingCount();
            if (pending == 0)
            {
                MessageBox.Show("No pending manual modeling samples in the buffer.\nDraw elements (pipes, ducts, fittings) manually in Revit to collect real-time imitation samples.", "User Modeling Watcher", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            bool success = _watcher.RetrainOnManualModeling();
            RefreshWatcherTelemetryUI();

            if (success)
            {
                MessageBox.Show($"Neural model successfully retrained on {pending} manual modeling imitation sample(s)!\n\nWeights updated in memory and saved to BiruscanMepWeights.dat.", "Imitation Training Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void WatcherClearBufferButton_Click(object sender, RoutedEventArgs e)
        {
            var res = MessageBox.Show("Are you sure you want to clear the manual modeling buffer and reset watcher counters?", "Clear Watcher Buffer", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes)
            {
                _watcher.ClearBuffer();
                RefreshWatcherTelemetryUI();
                WatcherLogTextBox.Clear();
            }
        }

        private void WatcherExportDatasetButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                FileName = $"biruscan_usermodeling_{DateTime.Now:yyyyMMdd_HHmm}.zip",
                Filter = "Zip archives (*.zip)|*.zip",
                Title = "Export Manual User Modeling Dataset"
            };
            if (dlg.ShowDialog(this) != true) return;

            bool ok = _watcher.ExportDataset(dlg.FileName);
            if (ok)
            {
                MessageBox.Show($"User manual modeling dataset exported successfully to {Path.GetFileName(dlg.FileName)}.", "Export User Modeling Dataset");
            }
            else
            {
                MessageBox.Show("Failed to export manual user modeling dataset.", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion
    }
}
