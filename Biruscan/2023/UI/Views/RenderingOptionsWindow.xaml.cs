using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Biruscan.UI.Views
{
    public partial class RenderingOptionsWindow : Window
    {
        public string SelectedMode { get; private set; } = "TrueColor";

        // Default: Green (0, 255, 0) -> Yellow (255, 255, 0)
        public byte MinR { get; set; } = 0;
        public byte MinG { get; set; } = 255;
        public byte MinB { get; set; } = 0;

        public byte MaxR { get; set; } = 255;
        public byte MaxG { get; set; } = 255;
        public byte MaxB { get; set; } = 0;

        public Autodesk.Revit.DB.Color IntensityMinColor => new Autodesk.Revit.DB.Color(MinR, MinG, MinB);
        public Autodesk.Revit.DB.Color IntensityMaxColor => new Autodesk.Revit.DB.Color(MaxR, MaxG, MaxB);

        public RenderingOptionsWindow()
        {
            InitializeComponent();

            rbTrueColor.Checked += OnRenderingModeChanged;
            rbIntensity.Checked += OnRenderingModeChanged;
            rbElevation.Checked += OnRenderingModeChanged;
            rbNormals.Checked += OnRenderingModeChanged;

            // Set default Green -> Yellow
            SetGradient(0, 255, 0, 255, 255, 0);
            UpdateUI();
        }

        private void OnRenderingModeChanged(object sender, RoutedEventArgs e)
        {
            UpdateUI();
        }

        private void UpdateUI()
        {
            if (grpIntensitySettings == null) return;
            grpIntensitySettings.IsEnabled = (rbIntensity.IsChecked == true);
            grpIntensitySettings.Opacity = (rbIntensity.IsChecked == true) ? 1.0 : 0.45;
            UpdatePreview();
        }

        private void SetGradient(byte minR, byte minG, byte minB, byte maxR, byte maxG, byte maxB)
        {
            MinR = minR; MinG = minG; MinB = minB;
            MaxR = maxR; MaxG = maxG; MaxB = maxB;
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            if (rectGradientPreview == null || borderMinColor == null || borderMaxColor == null) return;

            var minColor = System.Windows.Media.Color.FromRgb(MinR, MinG, MinB);
            var maxColor = System.Windows.Media.Color.FromRgb(MaxR, MaxG, MaxB);

            borderMinColor.Background = new SolidColorBrush(minColor);
            borderMaxColor.Background = new SolidColorBrush(maxColor);

            var gradient = new LinearGradientBrush();
            gradient.StartPoint = new System.Windows.Point(0, 0.5);
            gradient.EndPoint = new System.Windows.Point(1, 0.5);
            gradient.GradientStops.Add(new GradientStop(minColor, 0.0));
            gradient.GradientStops.Add(new GradientStop(maxColor, 1.0));

            rectGradientPreview.Fill = gradient;
        }

        private void CmbIntensityPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbIntensityPreset == null) return;

            switch (cmbIntensityPreset.SelectedIndex)
            {
                case 0: // Green -> Yellow (Default)
                    SetGradient(0, 255, 0, 255, 255, 0);
                    break;
                case 1: // Yellow -> Red
                    SetGradient(255, 255, 0, 255, 0, 0);
                    break;
                case 2: // Green -> Red
                    SetGradient(0, 255, 0, 255, 0, 0);
                    break;
                case 3: // Grayscale (Black -> White)
                    SetGradient(0, 0, 0, 255, 255, 255);
                    break;
                case 4: // Custom (keep current colors)
                    break;
            }
        }

        private void BtnPickMinColor_Click(object sender, RoutedEventArgs e)
        {
            using (var dlg = new System.Windows.Forms.ColorDialog())
            {
                dlg.FullOpen = true;
                dlg.Color = System.Drawing.Color.FromArgb(MinR, MinG, MinB);
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    MinR = dlg.Color.R;
                    MinG = dlg.Color.G;
                    MinB = dlg.Color.B;
                    if (cmbIntensityPreset != null) cmbIntensityPreset.SelectedIndex = 4; // Custom
                    UpdatePreview();
                }
            }
        }

        private void BtnPickMaxColor_Click(object sender, RoutedEventArgs e)
        {
            using (var dlg = new System.Windows.Forms.ColorDialog())
            {
                dlg.FullOpen = true;
                dlg.Color = System.Drawing.Color.FromArgb(MaxR, MaxG, MaxB);
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    MaxR = dlg.Color.R;
                    MaxG = dlg.Color.G;
                    MaxB = dlg.Color.B;
                    if (cmbIntensityPreset != null) cmbIntensityPreset.SelectedIndex = 4; // Custom
                    UpdatePreview();
                }
            }
        }

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            if (rbTrueColor.IsChecked == true) SelectedMode = "TrueColor";
            else if (rbIntensity.IsChecked == true) SelectedMode = "Intensity";
            else if (rbElevation.IsChecked == true) SelectedMode = "Elevation";
            else if (rbNormals.IsChecked == true) SelectedMode = "Normals";

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
