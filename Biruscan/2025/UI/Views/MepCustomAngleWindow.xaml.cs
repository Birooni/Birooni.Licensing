using System;
using System.Windows;

namespace Biruscan.UI.Views
{
    public partial class MepCustomAngleWindow : Window
    {
        private const double BendMin = 2.5;
        private const double BendMax = 95;
        private const double AxialMin = 0;
        private const double AxialMax = 360;
        private const double DefaultStep = 1;

        private Action<double, double> _onAngleChanged;
        private bool _isUpdating = false;

        public double SelectedBendAngle { get; private set; }
        public double SelectedAxialAngle { get; private set; }

        public MepCustomAngleWindow(Action<double, double> onAngleChanged, double defaultBend = 30, double defaultAxial = 0)
        {
            InitializeComponent();
            _onAngleChanged = onAngleChanged;
            
            _isUpdating = true;
            StepSlider.Value = DefaultStep;
            ApplyStep(DefaultStep, defaultBend, defaultAxial);
            _isUpdating = false;
        }

        private void StepSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdating || StepSlider == null || BendAngleSlider == null || AxialAngleSlider == null) return;
            ApplyStep(StepSlider.Value, BendAngleSlider.Value, AxialAngleSlider.Value);
            _onAngleChanged?.Invoke(SelectedBendAngle, SelectedAxialAngle);
        }

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdating) return;
            if (BendAngleSlider == null || AxialAngleSlider == null) return;

            SelectedBendAngle = BendAngleSlider.Value;
            SelectedAxialAngle = AxialAngleSlider.Value;
            _onAngleChanged?.Invoke(SelectedBendAngle, SelectedAxialAngle);
        }

        private void ApplyStep(double step, double bend, double axial)
        {
            if (step < 0.1) step = 0.1;

            double bMin = SnapUp(BendMin, step);
            double bMax = SnapDown(BendMax, step);
            if (bMax < bMin) bMax = bMin;

            double aMin = SnapUp(AxialMin, step);
            double aMax = SnapDown(AxialMax, step);
            if (aMax < aMin) aMax = aMin;

            bool wasUpdating = _isUpdating;
            _isUpdating = true;
            BendAngleSlider.Minimum = bMin;
            BendAngleSlider.Maximum = bMax;
            BendAngleSlider.TickFrequency = step;
            BendAngleSlider.SmallChange = step;
            BendAngleSlider.LargeChange = step * 5;
            BendAngleSlider.Value = Clamp(Snap(bend, step), bMin, bMax);

            AxialAngleSlider.Minimum = aMin;
            AxialAngleSlider.Maximum = aMax;
            AxialAngleSlider.TickFrequency = step;
            AxialAngleSlider.SmallChange = step;
            AxialAngleSlider.LargeChange = step * 5;
            AxialAngleSlider.Value = Clamp(Snap(axial, step), aMin, aMax);

            SelectedBendAngle = BendAngleSlider.Value;
            SelectedAxialAngle = AxialAngleSlider.Value;
            _isUpdating = wasUpdating;
        }

        private static double Snap(double value, double step)
        {
            return Math.Round(value / step) * step;
        }

        private static double SnapUp(double value, double step)
        {
            return Math.Ceiling(value / step - 1e-9) * step;
        }

        private static double SnapDown(double value, double step)
        {
            return Math.Floor(value / step + 1e-9) * step;
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }

        private void BtnOK_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = true;
            this.Close();
        }
    }
}
