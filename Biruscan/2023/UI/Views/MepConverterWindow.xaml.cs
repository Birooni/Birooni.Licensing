using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Biruscan.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Biruscan.UI.Views
{
    public enum ConversionScope
    {
        Selected,
        Pick,
        View
    }

    /// <summary>
    /// Interaction logic for MepConverterWindow.xaml
    /// </summary>
    public partial class MepConverterWindow : Window
    {
        private readonly Document _doc;
        private readonly ICollection<ElementId> _selectedIds;

        public MepConversionOptions Options { get; private set; } = new MepConversionOptions();
        public ConversionScope Scope { get; private set; } = ConversionScope.Selected;

        public MepConverterWindow(Document doc, ICollection<ElementId> selectedIds)
        {
            _doc = doc;
            _selectedIds = selectedIds ?? new List<ElementId>();
            InitializeComponent();
            Loaded += MepConverterWindow_Loaded;
        }

        private void MepConverterWindow_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateTypeDropdowns();
            UpdateStatusText();
        }

        private void CmbConversionMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            UpdateTypeDropdowns();
            UpdateStatusText();
        }

        private MepConversionMode GetSelectedMode()
        {
            if (cmbConversionMode.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                string tag = item.Tag.ToString();
                if (Enum.TryParse(tag, out MepConversionMode mode))
                    return mode;
            }
            return MepConversionMode.RoundDuctToPipe;
        }

        private void UpdateTypeDropdowns()
        {
            MepConversionMode mode = GetSelectedMode();

            switch (mode)
            {
                case MepConversionMode.RoundDuctToPipe:
                case MepConversionMode.ConduitToPipe:
                    pnlSystemType.Visibility = System.Windows.Visibility.Visible;
                    cmbTargetType.ItemsSource = new FilteredElementCollector(_doc)
                        .OfClass(typeof(PipeType)).Cast<ElementType>().OrderBy(x => x.Name).ToList();
                    cmbTargetSystem.ItemsSource = new FilteredElementCollector(_doc)
                        .OfClass(typeof(PipingSystemType)).Cast<ElementType>().OrderBy(x => x.Name).ToList();
                    break;

                case MepConversionMode.PipeToConduit:
                    pnlSystemType.Visibility = System.Windows.Visibility.Collapsed;
                    cmbTargetType.ItemsSource = new FilteredElementCollector(_doc)
                        .OfClass(typeof(ConduitType)).Cast<ElementType>().OrderBy(x => x.Name).ToList();
                    break;

                case MepConversionMode.PipeToRoundDuct:
                    pnlSystemType.Visibility = System.Windows.Visibility.Visible;
                    var roundDucts = new FilteredElementCollector(_doc)
                        .OfClass(typeof(DuctType)).Cast<DuctType>()
                        .Where(d => d.Shape == ConnectorProfileType.Round || d.Name.ToLowerInvariant().Contains("round"))
                        .Cast<ElementType>().OrderBy(x => x.Name).ToList();

                    if (roundDucts.Count == 0)
                    {
                        roundDucts = new FilteredElementCollector(_doc)
                            .OfClass(typeof(DuctType)).Cast<ElementType>().OrderBy(x => x.Name).ToList();
                    }

                    cmbTargetType.ItemsSource = roundDucts;
                    cmbTargetSystem.ItemsSource = new FilteredElementCollector(_doc)
                        .OfClass(typeof(MechanicalSystemType)).Cast<ElementType>().OrderBy(x => x.Name).ToList();
                    break;
            }

            if (cmbTargetType.Items.Count > 0) cmbTargetType.SelectedIndex = 0;
            if (cmbTargetSystem.Items.Count > 0) cmbTargetSystem.SelectedIndex = 0;
        }

        private void UpdateStatusText()
        {
            MepConversionMode mode = GetSelectedMode();
            var service = new MepConverterService(_doc);
            int count = 0;

            if (_selectedIds != null && _selectedIds.Count > 0)
            {
                foreach (var id in _selectedIds)
                {
                    var elem = _doc.GetElement(id);
                    if (elem is MEPCurve curve && service.IsCompatibleCurve(curve, mode))
                    {
                        count++;
                    }
                }
            }

            if (count > 0)
            {
                txtSelectionStatus.Text = $"{count} compatible element(s) selected in current Revit selection.";
                rbSelected.IsChecked = true;
            }
            else
            {
                txtSelectionStatus.Text = "No pre-selected compatible elements. Choose 'Pick Run' or select elements.";
                rbPick.IsChecked = true;
            }
        }

        private void ConvertButton_Click(object sender, RoutedEventArgs e)
        {
            MepConversionMode mode = GetSelectedMode();

            if (!(cmbTargetType.SelectedItem is ElementType targetType))
            {
                MessageBox.Show("Please select a target family type.", "MEP Converter", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ElementId systemTypeId = ElementId.InvalidElementId;
            if (pnlSystemType.Visibility == System.Windows.Visibility.Visible && cmbTargetSystem.SelectedItem is ElementType sysType)
            {
                systemTypeId = sysType.Id;
            }

            Options = new MepConversionOptions
            {
                Mode = mode,
                TargetTypeId = targetType.Id,
                TargetSystemTypeId = systemTypeId,
                RebuildFittings = chkFittings.IsChecked == true,
                TraverseConnectedNetwork = chkNetwork.IsChecked == true,
                DeleteOriginal = chkDelete.IsChecked == true,
                CopyParameters = chkParams.IsChecked == true
            };

            if (rbPick.IsChecked == true) Scope = ConversionScope.Pick;
            else if (rbView.IsChecked == true) Scope = ConversionScope.View;
            else Scope = ConversionScope.Selected;

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