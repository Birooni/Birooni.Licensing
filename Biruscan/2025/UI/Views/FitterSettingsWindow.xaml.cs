using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Electrical;
using Biruscan.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Biruscan.UI.Views
{
    public partial class FitterSettingsWindow : Window
    {
        private readonly Document _doc;
        private readonly FitterSettings _settings;

        public FitterSettingsWindow(Document doc)
        {
            InitializeComponent();
            _doc = doc;
            _settings = FitterSettings.Instance;

            LoadSettingsIntoUI();
        }

        private void LoadSettingsIntoUI()
        {
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
                    
                // Populate Piping System Types
                var pipeSystemTypes = new FilteredElementCollector(_doc)
                    .OfClass(typeof(PipingSystemType))
                    .Cast<PipingSystemType>()
                    .Select(pt => pt.Name)
                    .OrderBy(n => n)
                    .ToList();

                cmbPipingSystemType.Items.Clear();
                cmbPipingSystemType.Items.Add("(Auto / Document Default)");
                foreach (var pt in pipeSystemTypes) cmbPipingSystemType.Items.Add(pt);

                if (!string.IsNullOrEmpty(_settings.PreferredPipingSystemTypeName) && cmbPipingSystemType.Items.Contains(_settings.PreferredPipingSystemTypeName))
                    cmbPipingSystemType.SelectedItem = _settings.PreferredPipingSystemTypeName;
                else
                    cmbPipingSystemType.SelectedIndex = 0;

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

                cmbDuctType.Items.Clear();
                cmbDuctType.Items.Add("(Auto / Document Default)");
                foreach (var rd in rectDucts) cmbDuctType.Items.Add(rd);

                if (!string.IsNullOrEmpty(_settings.PreferredDuctTypeName) && cmbDuctType.Items.Contains(_settings.PreferredDuctTypeName))
                    cmbDuctType.SelectedItem = _settings.PreferredDuctTypeName;
                else
                    cmbDuctType.SelectedIndex = 0;
                    
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
                    
                // Populate Duct System Types
                var ductSystemTypes = new FilteredElementCollector(_doc)
                    .OfClass(typeof(MechanicalSystemType))
                    .Cast<MechanicalSystemType>()
                    .Select(dt => dt.Name)
                    .OrderBy(n => n)
                    .ToList();

                cmbDuctSystemType.Items.Clear();
                cmbDuctSystemType.Items.Add("(Auto / Document Default)");
                foreach (var dt in ductSystemTypes) cmbDuctSystemType.Items.Add(dt);

                if (!string.IsNullOrEmpty(_settings.PreferredDuctSystemTypeName) && cmbDuctSystemType.Items.Contains(_settings.PreferredDuctSystemTypeName))
                    cmbDuctSystemType.SelectedItem = _settings.PreferredDuctSystemTypeName;
                else
                    cmbDuctSystemType.SelectedIndex = 0;

                // Populate Cable Tray Types
                var cableTrayTypes = new FilteredElementCollector(_doc)
                    .OfClass(typeof(CableTrayType))
                    .Cast<CableTrayType>()
                    .Select(dt => dt.Name)
                    .OrderBy(n => n)
                    .ToList();
                    
                cmbCableTrayType.Items.Clear();
                cmbCableTrayType.Items.Add("(Auto / Document Default)");
                foreach (var ct in cableTrayTypes) cmbCableTrayType.Items.Add(ct);
                
                if (!string.IsNullOrEmpty(_settings.PreferredCableTrayTypeName) && cmbCableTrayType.Items.Contains(_settings.PreferredCableTrayTypeName))
                    cmbCableTrayType.SelectedItem = _settings.PreferredCableTrayTypeName;
                else
                    cmbCableTrayType.SelectedIndex = 0;
            }

            chkSnapNominal.IsChecked = _settings.SnapToNominal;
            txtSnapPercent.Text = _settings.NominalSnapPercent.ToString("0.#");
            txtOrthoDeg.Text = _settings.OrthoSnapDegrees.ToString("0.#");
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            _settings.PreferredPipeTypeName = cmbPipeType.SelectedIndex > 0 ? cmbPipeType.SelectedItem?.ToString() ?? "" : "";
            _settings.PreferredPipingSystemTypeName = cmbPipingSystemType.SelectedIndex > 0 ? cmbPipingSystemType.SelectedItem?.ToString() ?? "" : "";
            _settings.PreferredDuctTypeName = cmbDuctType.SelectedIndex > 0 ? cmbDuctType.SelectedItem?.ToString() ?? "" : "";
            _settings.PreferredRoundDuctTypeName = cmbRoundDuctType.SelectedIndex > 0 ? cmbRoundDuctType.SelectedItem?.ToString() ?? "" : "";
            _settings.PreferredDuctSystemTypeName = cmbDuctSystemType.SelectedIndex > 0 ? cmbDuctSystemType.SelectedItem?.ToString() ?? "" : "";
            _settings.PreferredCableTrayTypeName = cmbCableTrayType.SelectedIndex > 0 ? cmbCableTrayType.SelectedItem?.ToString() ?? "" : "";
            _settings.SnapToNominal = chkSnapNominal.IsChecked == true;
            if (double.TryParse(txtSnapPercent.Text, out double snapPct) && snapPct > 0)
                _settings.NominalSnapPercent = snapPct;
            if (double.TryParse(txtOrthoDeg.Text, out double orthoDeg) && orthoDeg >= 0)
                _settings.OrthoSnapDegrees = orthoDeg;

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
