using System;
using System.Windows;

namespace Biruscan.UI.Views
{
    public partial class LimitBoxWindow : Window
    {
        public bool UsePickPoints { get; private set; } = true;
        public double MinX { get; private set; }
        public double MaxX { get; private set; }
        public double MinY { get; private set; }
        public double MaxY { get; private set; }
        public double MinZ { get; private set; }
        public double MaxZ { get; private set; }
        public bool ApplySectionBox { get; private set; } = true;
        public bool ApplyClicked { get; private set; }

        public LimitBoxWindow()
        {
            InitializeComponent();
            ApplyClicked = false;
        }

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            UsePickPoints = rbPickPoints.IsChecked == true;
            ApplySectionBox = chkApplySectionBox.IsChecked == true;

            if (!UsePickPoints)
            {
                double.TryParse(txtMinX.Text, out double minX);
                double.TryParse(txtMaxX.Text, out double maxX);
                double.TryParse(txtMinY.Text, out double minY);
                double.TryParse(txtMaxY.Text, out double maxY);
                double.TryParse(txtMinZ.Text, out double minZ);
                double.TryParse(txtMaxZ.Text, out double maxZ);

                MinX = minX; MaxX = maxX;
                MinY = minY; MaxY = maxY;
                MinZ = minZ; MaxZ = maxZ;
            }

            ApplyClicked = true;
            this.Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
