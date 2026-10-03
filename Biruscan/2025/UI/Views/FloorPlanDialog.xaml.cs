using System;
using System.Windows;

namespace Biruscan.UI.Views
{
    public partial class FloorPlanDialog : Window
    {
        public string LevelName { get; private set; }
        public double Elevation { get; private set; }
        public double SliceThickness { get; private set; }
        public bool CreateClicked { get; private set; }

        public FloorPlanDialog(string defaultName, double defaultElevation)
        {
            InitializeComponent();
            txtLevelName.Text = defaultName;
            txtElevation.Text = defaultElevation.ToString("F2");
            CreateClicked = false;
        }

        private void BtnCreate_Click(object sender, RoutedEventArgs e)
        {
            LevelName = txtLevelName.Text;
            double.TryParse(txtElevation.Text, out double elev);
            double.TryParse(txtThickness.Text, out double thick);

            Elevation = elev;
            SliceThickness = thick > 0 ? thick : 5.0;
            CreateClicked = true;
            this.Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}
