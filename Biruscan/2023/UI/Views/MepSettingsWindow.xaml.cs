using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Biruscan.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;

namespace Biruscan.UI.Views
{
    public partial class MepSettingsWindow : Window
    {
        private readonly Document _doc;
        private readonly MepSettings _settings;

        public MepSettingsWindow(Document doc)
        {
            InitializeComponent();
            _doc = doc;
            _settings = MepSettings.Instance;

            LoadSettingsIntoUI();
        }

        private void LoadSettingsIntoUI()
        {
            txtPipeLength.Text = _settings.PipeExtensionLengthFeet.ToString("0.##", CultureInfo.InvariantCulture);
            txtDuctLength.Text = _settings.DuctExtensionLengthFeet.ToString("0.##", CultureInfo.InvariantCulture);
            txtConduitLength.Text = _settings.ConduitExtensionLengthFeet.ToString("0.##", CultureInfo.InvariantCulture);
            txtElbowLength.Text = _settings.ElbowExtensionLengthFeet.ToString("0.##", CultureInfo.InvariantCulture);
            chkAutoConnect.IsChecked = _settings.AutoConnectStubs;

            if (_doc != null)
            {
                // Populate Pipe Types
                var pipeTypes = new FilteredElementCollector(_doc)
                    .OfClass(typeof(PipeType))
                    .Cast<PipeType>()
                    .Select(pt => pt.Name)
                    .OrderBy(n => n)
                    .ToList();

                cmbPipeType.Items.Clear();
                cmbPipeType.Items.Add("(Auto / Document Default)");
                foreach (var pt in pipeTypes) cmbPipeType.Items.Add(pt);

                if (!string.IsNullOrEmpty(_settings.PreferredPipeTypeName) && cmbPipeType.Items.Contains(_settings.PreferredPipeTypeName))
                    cmbPipeType.SelectedItem = _settings.PreferredPipeTypeName;
                else
                    cmbPipeType.SelectedIndex = 0;

                // Populate Duct Types
                var allDuctTypes = new FilteredElementCollector(_doc)
                    .OfClass(typeof(DuctType))
                    .Cast<DuctType>()
                    .ToList();

                var rectDucts = allDuctTypes
                    .Where(dt => dt.Shape == ConnectorProfileType.Rectangular || dt.Name.ToLower().Contains("rect"))
                    .Select(dt => dt.Name)
                    .OrderBy(n => n)
                    .ToList();

                cmbRectDuctType.Items.Clear();
                cmbRectDuctType.Items.Add("(Auto / Document Default)");
                foreach (var rd in rectDucts) cmbRectDuctType.Items.Add(rd);

                if (!string.IsNullOrEmpty(_settings.PreferredRectDuctTypeName) && cmbRectDuctType.Items.Contains(_settings.PreferredRectDuctTypeName))
                    cmbRectDuctType.SelectedItem = _settings.PreferredRectDuctTypeName;
                else
                    cmbRectDuctType.SelectedIndex = 0;

                var roundDucts = allDuctTypes
                    .Where(dt => dt.Shape == ConnectorProfileType.Round || dt.Name.ToLower().Contains("round"))
                    .Select(dt => dt.Name)
                    .OrderBy(n => n)
                    .ToList();

                cmbRoundDuctType.Items.Clear();
                cmbRoundDuctType.Items.Add("(Auto / Document Default)");
                foreach (var rd in roundDucts) cmbRoundDuctType.Items.Add(rd);

                if (!string.IsNullOrEmpty(_settings.PreferredRoundDuctTypeName) && cmbRoundDuctType.Items.Contains(_settings.PreferredRoundDuctTypeName))
                    cmbRoundDuctType.SelectedItem = _settings.PreferredRoundDuctTypeName;
                else
                    cmbRoundDuctType.SelectedIndex = 0;
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(txtPipeLength.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double pipeLen) && pipeLen > 0.1)
                _settings.PipeExtensionLengthFeet = pipeLen;

            if (double.TryParse(txtDuctLength.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double ductLen) && ductLen > 0.1)
                _settings.DuctExtensionLengthFeet = ductLen;

            if (double.TryParse(txtConduitLength.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double conduitLen) && conduitLen > 0.1)
                _settings.ConduitExtensionLengthFeet = conduitLen;

            if (double.TryParse(txtElbowLength.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double elbowLen) && elbowLen > 0.1)
                _settings.ElbowExtensionLengthFeet = elbowLen;

            _settings.AutoConnectStubs = chkAutoConnect.IsChecked ?? true;

            _settings.PreferredPipeTypeName = cmbPipeType.SelectedIndex > 0 ? cmbPipeType.SelectedItem?.ToString() ?? "" : "";
            _settings.PreferredRectDuctTypeName = cmbRectDuctType.SelectedIndex > 0 ? cmbRectDuctType.SelectedItem?.ToString() ?? "" : "";
            _settings.PreferredRoundDuctTypeName = cmbRoundDuctType.SelectedIndex > 0 ? cmbRoundDuctType.SelectedItem?.ToString() ?? "" : "";

            _settings.Save();
            DialogResult = true;
            Close();
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            _settings.ResetDefaults();
            LoadSettingsIntoUI();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
