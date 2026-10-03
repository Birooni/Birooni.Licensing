using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;

namespace Biruscan.UI.Views
{
    public enum WallFittingMode
    {
        Auto,
        ThreePoint,
        TwoPoint
    }

    public partial class WallFitterWindow : Window
    {
        private readonly Document _doc;
        private readonly List<Level> _levels;
        private readonly List<WallType> _wallTypes;

        public WallFittingMode SelectedFittingMode { get; private set; } = WallFittingMode.Auto;
        public Level SelectedLevel { get; private set; }
        public WallType SelectedWallType { get; private set; }
        public double WallHeight { get; private set; } = 10.0;
        public double MinWallLength { get; private set; } = 2.0;
        public bool OrthoSnap => chkOrthoSnap.IsChecked == true;

        public WallFitterWindow(Document doc)
        {
            _doc = doc;
            InitializeComponent();

            // Populate Levels
            _levels = new FilteredElementCollector(_doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            foreach (var lvl in _levels)
            {
                cmbLevels.Items.Add($"{lvl.Name} (Elev: {lvl.Elevation:F2} ft)");
            }

            if (_levels.Count > 0)
            {
                // Select active view level if available
                var activeViewLevel = _doc.ActiveView?.GenLevel;
                int defaultIdx = activeViewLevel != null ? _levels.FindIndex(l => l.Id == activeViewLevel.Id) : 0;
                cmbLevels.SelectedIndex = defaultIdx >= 0 ? defaultIdx : 0;
            }

            // Populate Wall Types
            _wallTypes = new FilteredElementCollector(_doc)
                .OfClass(typeof(WallType))
                .Cast<WallType>()
                .Where(wt => wt.Kind == WallKind.Basic)
                .OrderBy(wt => wt.Name)
                .ToList();

            cmbWallTypes.Items.Add("Auto-Detect by Measured Thickness (Recommended)");
            foreach (var wt in _wallTypes)
            {
                double widthMm = wt.Width * 304.8;
                cmbWallTypes.Items.Add($"{wt.Name} ({widthMm:F0}mm / {wt.Width * 12:F1}\")");
            }
            cmbWallTypes.SelectedIndex = 0;

            UpdateUI();
        }

        private void OnModeChanged(object sender, RoutedEventArgs e)
        {
            UpdateUI();
        }

        private void UpdateUI()
        {
            if (btnAction == null) return;

            if (rbAuto.IsChecked == true)
            {
                SelectedFittingMode = WallFittingMode.Auto;
                btnAction.Content = "⚡ Generate Walls";
                txtMinLength.IsEnabled = true;
                txtInfoNote.Text = "Auto Mode automatically extracts 2D line clusters, pairs parallel wall faces for thickness, rectifies 90° corners, and places all walls in one pass.";
            }
            else if (rbThreePoint.IsChecked == true)
            {
                SelectedFittingMode = WallFittingMode.ThreePoint;
                btnAction.Content = "Pick 3 Points...";
                txtMinLength.IsEnabled = false;
                txtInfoNote.Text = "3-Point Mode: Click Point 1 and Point 2 on the primary wall face, then click Point 3 on the opposite wall face. Thickness will be automatically computed and matched.";
            }
            else
            {
                SelectedFittingMode = WallFittingMode.TwoPoint;
                btnAction.Content = "Pick 2 Points...";
                txtMinLength.IsEnabled = false;
                txtInfoNote.Text = "2-Point Mode: Click Start point and End point on the point cloud to draw a single wall.";
            }
        }

        private void BtnAction_Click(object sender, RoutedEventArgs e)
        {
            // Parse Level
            if (cmbLevels.SelectedIndex >= 0 && cmbLevels.SelectedIndex < _levels.Count)
            {
                SelectedLevel = _levels[cmbLevels.SelectedIndex];
            }
            else if (_levels.Count > 0)
            {
                SelectedLevel = _levels[0];
            }

            // Parse Wall Type
            if (cmbWallTypes.SelectedIndex > 0 && cmbWallTypes.SelectedIndex - 1 < _wallTypes.Count)
            {
                SelectedWallType = _wallTypes[cmbWallTypes.SelectedIndex - 1];
            }
            else
            {
                SelectedWallType = null; // Auto-detect
            }

            // Parse Wall Height
            if (double.TryParse(txtWallHeight.Text, out double h) && h > 0.5)
            {
                WallHeight = h;
            }
            else
            {
                WallHeight = 10.0;
            }

            // Parse Min Length
            if (double.TryParse(txtMinLength.Text, out double minL) && minL > 0.2)
            {
                MinWallLength = minL;
            }
            else
            {
                MinWallLength = 2.0;
            }

            this.DialogResult = true;
            this.Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }
    }
}
