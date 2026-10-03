using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace Biruscan.UI.Views
{
    public partial class MepIncrementWindow : Window
    {
        private readonly Action<double, double> _onChanged;
        private bool _isUpdating;

        public double End1Value { get; private set; }
        public double End2Value { get; private set; }
        public double MaxIncrement { get; private set; }

        public MepIncrementWindow(Action<double, double> onChanged, double maxDisplay, string unitSymbol, string title = null, string hint = null)
        {
            InitializeComponent();
            _onChanged = onChanged;
            if (!string.IsNullOrWhiteSpace(title)) Title = title;
            if (!string.IsNullOrWhiteSpace(hint)) HintLabel.Text = hint;
            MaxIncrement = maxDisplay > 0 ? maxDisplay : 1;
            UnitLabel.Text = string.IsNullOrWhiteSpace(unitSymbol) ? "" : unitSymbol;
            _isUpdating = true;
            ApplyMax(MaxIncrement);
            End1Slider.Value = 0;
            End2Slider.Value = 0;
            End1Value = 0;
            End2Value = 0;
            _isUpdating = false;
        }

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdating || End1Slider == null || End2Slider == null) return;
            End1Value = End1Slider.Value;
            End2Value = End2Slider.Value;
            _onChanged?.Invoke(End1Value, End2Value);
        }

        private void MaxTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            CommitMax();
        }

        private void MaxTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitMax();
                e.Handled = true;
            }
        }

        private void CommitMax()
        {
            if (!TryParse(MaxTextBox.Text, out double max) || max <= 0)
            {
                MaxTextBox.Text = Format(MaxIncrement);
                return;
            }
            ApplyMax(max);
        }

        private void ApplyMax(double max)
        {
            MaxIncrement = max;
            double tick = SuggestTick(max);
            _isUpdating = true;
            End1Slider.Maximum = max;
            End2Slider.Maximum = max;
            End1Slider.TickFrequency = tick;
            End2Slider.TickFrequency = tick;
            End1Slider.SmallChange = tick;
            End2Slider.SmallChange = tick;
            End1Slider.LargeChange = tick * 10;
            End2Slider.LargeChange = tick * 10;
            if (End1Slider.Value > max) End1Slider.Value = max;
            if (End2Slider.Value > max) End2Slider.Value = max;
            MaxTextBox.Text = Format(max);
            End1Value = End1Slider.Value;
            End2Value = End2Slider.Value;
            _isUpdating = false;
            _onChanged?.Invoke(End1Value, End2Value);
        }

        private static double SuggestTick(double max)
        {
            if (max >= 1000) return 1;
            if (max >= 100) return 0.5;
            if (max >= 10) return 0.05;
            return 0.01;
        }

        private static bool TryParse(string text, out double value)
        {
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)) return true;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static string Format(double value)
        {
            if (value >= 100) return value.ToString("0", CultureInfo.CurrentCulture);
            if (value >= 10) return value.ToString("0.0", CultureInfo.CurrentCulture);
            return value.ToString("0.##", CultureInfo.CurrentCulture);
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void BtnOK_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}
