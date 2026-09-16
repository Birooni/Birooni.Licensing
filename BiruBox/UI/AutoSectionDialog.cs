using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Autodesk.Revit.UI;
using BiruBox.Core;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;

namespace BiruBox.UI;

public sealed class AutoSectionDialog : Window
{
    private readonly AutoSectionSettings _settings;
    private readonly ComboBox _targetView;
    private readonly ComboBox _viewTemplate;
    private readonly TextBox _offsetX;
    private readonly TextBox _offsetY;
    private readonly TextBox _offsetZ;
    private readonly CheckBox _align;

    public bool Confirmed { get; private set; }
    public bool QuickRequested { get; private set; }

    public AutoSectionDialog(
        UIApplication uiapp,
        AutoSectionSettings settings,
        string unitLabel,
        IEnumerable<string> availableViews,
        IEnumerable<string> availableTemplates)
    {
        _settings = settings;

        Title = "BiruBox — Auto-Section Box";
        Icon = BiruIcons.GetImageSource(BiruIconType.AutoSectionBox, 32);
        Width = 440;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.White;
        FontFamily = new FontFamily("Segoe UI");

        var root = new StackPanel { Margin = new Thickness(16) };

        // Header Banner
        var headerBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(10, 37, 64)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 14)
        };
        var headerText = new TextBlock
        {
            Text = "Auto-Section Box Options",
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14
        };
        headerBorder.Child = headerText;
        root.Children.Add(headerBorder);

        // Group 1: Target 3D View
        root.Children.Add(CreateLabel("Target 3D View"));
        _targetView = new ComboBox
        {
            Height = 28,
            Margin = new Thickness(0, 2, 0, 10),
            IsEditable = true,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        foreach (string name in availableViews)
        {
            _targetView.Items.Add(name);
        }
        _targetView.Text = settings.TargetViewName;
        root.Children.Add(_targetView);

        // Group 2: View Template
        root.Children.Add(CreateLabel("View Template"));
        _viewTemplate = new ComboBox
        {
            Height = 28,
            Margin = new Thickness(0, 2, 0, 12),
            IsEditable = true,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _viewTemplate.Items.Add("(none)");
        foreach (string name in availableTemplates)
        {
            _viewTemplate.Items.Add(name);
        }
        _viewTemplate.Text = string.IsNullOrWhiteSpace(settings.ViewTemplateName) ? "(none)" : settings.ViewTemplateName;
        root.Children.Add(_viewTemplate);

        // Group 3: Section Box Offsets
        root.Children.Add(CreateLabel($"Section Box Offsets ({unitLabel})"));

        var gridOffsets = new Grid { Margin = new Thickness(0, 4, 0, 12) };
        gridOffsets.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        gridOffsets.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        gridOffsets.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        gridOffsets.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        gridOffsets.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _offsetX = CreateNumberInput(settings.OffsetX);
        _offsetY = CreateNumberInput(settings.OffsetY);
        _offsetZ = CreateNumberInput(settings.OffsetZ);

        var panelX = CreateInputWithLabel("Offset X", _offsetX);
        var panelY = CreateInputWithLabel("Offset Y", _offsetY);
        var panelZ = CreateInputWithLabel("Offset Z", _offsetZ);

        Grid.SetColumn(panelX, 0);
        Grid.SetColumn(panelY, 2);
        Grid.SetColumn(panelZ, 4);

        gridOffsets.Children.Add(panelX);
        gridOffsets.Children.Add(panelY);
        gridOffsets.Children.Add(panelZ);
        root.Children.Add(gridOffsets);

        // Group 3: Options / Alignment
        root.Children.Add(CreateLabel("Options"));
        _align = new CheckBox
        {
            Content = "Align section box to selected element orientation",
            IsChecked = settings.AlignToLinearElements,
            Margin = new Thickness(0, 4, 0, 16),
            ToolTip = "Rotate the section box to match walls, ducts, grids, or line-based elements."
        };
        root.Children.Add(_align);

        // Bottom Action Buttons
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var btnQuick = new Button
        {
            Content = "Quick",
            Width = 85,
            Height = 28,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "Apply settings and create the section box immediately."
        };
        btnQuick.Click += (_, _) =>
        {
            ApplyBack();
            QuickRequested = true;
            Confirmed = true;
            Close();
        };

        var btnOk = new Button
        {
            Content = "OK",
            Width = 85,
            Height = 28,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        btnOk.Click += (_, _) =>
        {
            ApplyBack();
            Confirmed = true;
            Close();
        };

        var btnCancel = new Button
        {
            Content = "Cancel",
            Width = 85,
            Height = 28,
            IsCancel = true
        };
        btnCancel.Click += (_, _) =>
        {
            Confirmed = false;
            Close();
        };

        buttons.Children.Add(btnQuick);
        buttons.Children.Add(btnOk);
        buttons.Children.Add(btnCancel);
        root.Children.Add(buttons);

        Content = root;

        try
        {
            new WindowInteropHelper(this).Owner = uiapp.MainWindowHandle;
        }
        catch
        {
            // Owner is optional
        }
    }

    private static TextBlock CreateLabel(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        FontSize = 12,
        Margin = new Thickness(0, 2, 0, 2),
        Foreground = new SolidColorBrush(Color.FromRgb(30, 41, 59))
    };

    private static TextBox CreateNumberInput(double initialValue) => new()
    {
        Text = initialValue < 0 ? "0" : initialValue.ToString("0.##", CultureInfo.InvariantCulture),
        Height = 26,
        VerticalContentAlignment = VerticalAlignment.Center,
        Padding = new Thickness(4, 0, 4, 0)
    };

    private static StackPanel CreateInputWithLabel(string label, TextBox input)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 2)
        });
        panel.Children.Add(input);
        return panel;
    }

    private void ApplyBack()
    {
        _settings.TargetViewName = _targetView.Text.Trim();

        string template = _viewTemplate.Text.Trim();
        _settings.ViewTemplateName = (string.IsNullOrWhiteSpace(template) || template.Equals("(none)", StringComparison.OrdinalIgnoreCase) || template.Equals("<None>", StringComparison.OrdinalIgnoreCase))
            ? string.Empty
            : template;

        _settings.AlignToLinearElements = _align.IsChecked == true;

        if (double.TryParse(_offsetX.Text, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double ox) ||
            double.TryParse(_offsetX.Text, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.CurrentCulture, out ox))
        {
            _settings.OffsetX = Math.Max(0, ox);
        }

        if (double.TryParse(_offsetY.Text, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double oy) ||
            double.TryParse(_offsetY.Text, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.CurrentCulture, out oy))
        {
            _settings.OffsetY = Math.Max(0, oy);
        }

        if (double.TryParse(_offsetZ.Text, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out double oz) ||
            double.TryParse(_offsetZ.Text, NumberStyles.Float | NumberStyles.AllowLeadingSign, CultureInfo.CurrentCulture, out oz))
        {
            _settings.OffsetZ = Math.Max(0, oz);
        }
    }
}
