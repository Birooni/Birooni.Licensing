using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Electrical;
using Biruscan.Commands;

namespace Biruscan.UI.Ai
{
    /// <summary>
    /// Unified AI Generate MEP dialog with Three Tabs:
    ///   Tab 1: Standard AI Generate (In-process SIMD PointNet++ ensemble)
    ///   Tab 2: Trained AI Model (Fine-tuned active learning weights)
    ///   Tab 3: Combined AI (Dual-pipeline ensemble + trained fusion)
    /// </summary>
    public partial class AiGenerateMepWindow : Window
    {
        private readonly Document _doc;

        public string SelectedService { get; private set; } = "Piping";
        public ElementId SelectedPrimaryTypeId { get; private set; } = ElementId.InvalidElementId;
        public string SelectedPrimaryTypeName { get; private set; } = string.Empty;
        public ElementId SelectedSystemTypeId { get; private set; } = ElementId.InvalidElementId;
        public ElementId SelectedLevelId { get; private set; } = ElementId.InvalidElementId;
        public double? DiameterOverrideMm { get; private set; }
        public bool InsulationPresent { get; private set; }
        public bool GenerateWithoutFittings { get; private set; }
        public string TrainedMethod { get; private set; } = "hybrid";
        public AiPipelineMode SelectedPipelineMode { get; private set; } = AiPipelineMode.StandardAi;

        public AiGenerateMepWindow(Document doc) : this(doc, MepDialogMode.AiGenerate) { }

        public AiGenerateMepWindow(Document doc, bool showTrainedMethodPicker)
            : this(doc, showTrainedMethodPicker ? MepDialogMode.TrainedAi : MepDialogMode.AiGenerate) { }

        public AiGenerateMepWindow(Document doc, MepDialogMode mode)
        {
            _doc = doc;
            InitializeComponent();
            LoadLevels();
            UpdateTypeFiltering();
            ApplyInitialTab(mode);
        }

        private void ApplyInitialTab(MepDialogMode mode)
        {
            switch (mode)
            {
                case MepDialogMode.TrainedAi:
                    PipelineTabControl.SelectedIndex = 1;
                    break;
                case MepDialogMode.FitGenerate:
                    PipelineTabControl.Visibility = System.Windows.Visibility.Collapsed;
                    HeaderText.Text = "GENERATE MEP";
                    SubtitleText.Text = "No clip required. Edgewise tiles the whole point cloud and fits pipes locally (clip still works for a smaller region).";
                    Title = "Generate MEP";
                    SelectedPipelineMode = AiPipelineMode.Fit;
                    ModeIndicatorText.Text = "Mode: Parametric Geometric Fitting";
                    ModeIndicatorText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80));
                    break;
                default:
                    PipelineTabControl.SelectedIndex = 0;
                    break;
            }
            if (mode != MepDialogMode.FitGenerate)
            {
                UpdateModeIndicator();
            }
        }

        private void PipelineTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            UpdateModeIndicator();
        }

        private void UpdateModeIndicator()
        {
            if (PipelineTabControl == null || ModeIndicatorText == null) return;

            switch (PipelineTabControl.SelectedIndex)
            {
                case 0:
                    SelectedPipelineMode = AiPipelineMode.StandardAi;
                    ModeIndicatorText.Text = "Mode: Standard In-Process AI Ensemble";
                    ModeIndicatorText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80)); // Green
                    break;
                case 1:
                    SelectedPipelineMode = HybridRadio?.IsChecked == true ? AiPipelineMode.TrainedHybrid : AiPipelineMode.TrainedPureDl;
                    ModeIndicatorText.Text = "Mode: Custom Fine-Tuned Active Learning Model";
                    ModeIndicatorText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 152, 0)); // Orange
                    break;
                case 2:
                    SelectedPipelineMode = AiPipelineMode.Combined;
                    ModeIndicatorText.Text = "Mode: Dual-Engine Combined AI Fusion (Highest Precision)";
                    ModeIndicatorText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 122, 204)); // Blue
                    break;
            }
        }

        private void LoadLevels()
        {
            var levels = new FilteredElementCollector(_doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            LevelComboBox.ItemsSource = levels;
            if (levels.Count > 0) LevelComboBox.SelectedIndex = 0;
        }

        private void ServiceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (IsLoaded) UpdateTypeFiltering();
        }

        private void UpdateTypeFiltering()
        {
            if (!(ServiceComboBox.SelectedItem is ComboBoxItem item)) return;

            SelectedService = item.Content.ToString();
            SystemTypePanel.Visibility = System.Windows.Visibility.Visible;

            switch (SelectedService)
            {
                case "Piping":
                    PrimaryTypeComboBox.ItemsSource = new FilteredElementCollector(_doc).OfClass(typeof(PipeType)).Cast<ElementType>().OrderBy(x => x.Name).ToList();
                    SystemTypeComboBox.ItemsSource = new FilteredElementCollector(_doc).OfClass(typeof(PipingSystemType)).Cast<ElementType>().OrderBy(x => x.Name).ToList();
                    break;
                case "Duct":
                    PrimaryTypeComboBox.ItemsSource = new FilteredElementCollector(_doc).OfClass(typeof(DuctType)).Cast<ElementType>().OrderBy(x => x.Name).ToList();
                    SystemTypeComboBox.ItemsSource = new FilteredElementCollector(_doc).OfClass(typeof(MechanicalSystemType)).Cast<ElementType>().OrderBy(x => x.Name).ToList();
                    break;
                case "Conduit":
                    PrimaryTypeComboBox.ItemsSource = new FilteredElementCollector(_doc).OfClass(typeof(ConduitType)).Cast<ElementType>().OrderBy(x => x.Name).ToList();
                    SystemTypePanel.Visibility = System.Windows.Visibility.Collapsed;
                    break;
                case "Cable Tray":
                    PrimaryTypeComboBox.ItemsSource = new FilteredElementCollector(_doc).OfClass(typeof(CableTrayType)).Cast<ElementType>().OrderBy(x => x.Name).ToList();
                    SystemTypePanel.Visibility = System.Windows.Visibility.Collapsed;
                    break;
            }

            if (PrimaryTypeComboBox.Items.Count > 0) PrimaryTypeComboBox.SelectedIndex = 0;
            if (SystemTypeComboBox.Items.Count > 0) SystemTypeComboBox.SelectedIndex = 0;
        }

        private void GenerateButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(PrimaryTypeComboBox.SelectedItem is ElementType primaryType))
            {
                MessageBox.Show("Please select a primary type.", "AI Generate MEP");
                return;
            }

            SelectedPrimaryTypeId = primaryType.Id;
            SelectedPrimaryTypeName = primaryType.Name;

            if (SystemTypePanel.Visibility == System.Windows.Visibility.Visible && SystemTypeComboBox.SelectedItem is ElementType systemType)
                SelectedSystemTypeId = systemType.Id;
            else
                SelectedSystemTypeId = ElementId.InvalidElementId;

            if (LevelComboBox.SelectedItem is Level level)
                SelectedLevelId = level.Id;

            DiameterOverrideMm = null;
            InsulationPresent = InsulationCheckBox.IsChecked == true;
            GenerateWithoutFittings = GenerateWithoutFittingsCheckBox.IsChecked == true;

            if (PipelineTabControl.Visibility == System.Windows.Visibility.Visible)
            {
                switch (PipelineTabControl.SelectedIndex)
                {
                    case 0:
                        SelectedPipelineMode = AiPipelineMode.StandardAi;
                        break;
                    case 1:
                        TrainedMethod = PureDlRadio.IsChecked == true ? "pure_dl" : "hybrid";
                        SelectedPipelineMode = PureDlRadio.IsChecked == true ? AiPipelineMode.TrainedPureDl : AiPipelineMode.TrainedHybrid;
                        break;
                    case 2:
                        SelectedPipelineMode = AiPipelineMode.Combined;
                        break;
                }
            }

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
