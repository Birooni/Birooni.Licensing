using System.Reflection;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using Autodesk.Windows;
using BiruBox.UI;
using RibbonPanel = Autodesk.Revit.UI.RibbonPanel;

namespace BiruBox;

public sealed class App : IExternalApplication
{
    internal const string PanelName = "BiruBox";
    internal const string PanelNameModify = "BiruBox_Modify";

    public Result OnStartup(UIControlledApplication application)
    {
        // Initialize licensing fast-check asynchronously (non-blocking)
        _ = BiruBox.Core.LicensingManager.InitializeAsync();

        string assembly = Assembly.GetExecutingAssembly().Location;

        // 1. Create panel under Add-Ins tab
        RibbonPanel addinPanel = GetOrCreatePanel(application, PanelName);
        PopulatePanel(addinPanel, assembly, "BiruBox_AddIn");

        // 2. Create panel for Modify tab
        RibbonPanel modifyPanel = GetOrCreatePanel(application, PanelNameModify);
        PopulatePanel(modifyPanel, assembly, "BiruBox_Modify");

        // Relocate modifyPanel to Modify tab and style both panels (icon-only, 34x34)
        SetupPanelsAndStyling();

        // Ensure placement and styling persist after Revit finishes startup
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(SetupPanelsAndStyling));
        application.ControlledApplication.ApplicationInitialized += (_, _) => SetupPanelsAndStyling();

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

    static RibbonPanel GetOrCreatePanel(UIControlledApplication application, string panelName)
    {
        foreach (RibbonPanel existing in application.GetRibbonPanels())
        {
            if (existing.Name == panelName)
                return existing;
        }

        return application.CreateRibbonPanel(panelName);
    }

    static void PopulatePanel(RibbonPanel panel, string assembly, string prefix)
    {
        var auto = NewButton(
            $"{prefix}_Auto",
            "Auto-Section\nBox",
            assembly,
            "BiruBox.Commands.AutoSectionBoxCommand",
            "Create a 3D section box around the selection (elements, links, scope boxes, sections, grids, levels).",
            BiruIconType.AutoSectionBox);

        var drawBox = NewButton(
            $"{prefix}_DrawBox",
            "Draw Box",
            assembly,
            "BiruBox.Commands.DrawBoxCommand",
            "Draw a box in a 2D view to create a 3D section box matching the drawn area and view range/depth.",
            BiruIconType.DrawBox);
        drawBox.AvailabilityClassName = "BiruBox.Commands.TwoDViewAvailability";

        var quick = NewButton(
            $"{prefix}_Quick",
            "Quick",
            assembly,
            "BiruBox.Commands.QuickAutoSectionCommand",
            "Run Auto-Section Box with the last dialog settings. Overwrites the view named '3D - Quick'.",
            BiruIconType.Quick);

        var toggle = NewButton(
            $"{prefix}_Toggle",
            "Toggle",
            assembly,
            "BiruBox.Commands.ToggleSectionBoxCommand",
            "Turn the active 3D view section box on or off to see the crop in context.",
            BiruIconType.Toggle);

        var grow = NewButton(
            $"{prefix}_Grow",
            "Grow",
            assembly,
            "BiruBox.Commands.GrowCommand",
            "Expand the active section box by the last buffer (default 300 mm) on each side.",
            BiruIconType.Grow);

        var shrink = NewButton(
            $"{prefix}_Shrink",
            "Shrink",
            assembly,
            "BiruBox.Commands.ShrinkCommand",
            "Contract the active section box by the last buffer on each side.",
            BiruIconType.Shrink);

        var license = NewButton(
            $"{prefix}_License",
            "License",
            assembly,
            "BiruBox.Commands.LicenseCommand",
            "View BiruBox license status, activate key, or start a 14-day free trial.",
            BiruIconType.License);

        // Arrange push buttons in two rows:
        // Column 1: Auto-Section Box (top) & Draw Box (bottom)
        // Column 2: Quick (top) & Toggle (bottom)
        // Separator
        // Column 3: Grow (top) & Shrink (bottom)
        // Separator
        // Column 4: License
        panel.AddStackedItems(auto, drawBox);
        panel.AddStackedItems(quick, toggle);
        panel.AddSeparator();
        panel.AddStackedItems(grow, shrink);
        panel.AddSeparator();
        panel.AddItem(license);
    }

    static PushButtonData NewButton(string name, string text, string assembly, string className, string tooltip, BiruIconType iconType)
    {
        return new PushButtonData(name, text, assembly, className)
        {
            ToolTip = tooltip,
            LongDescription = tooltip + "\n\nBiruBox uses the Revit API (View3D.SetSectionBox / BoundingBoxXYZ).",
            LargeImage = BiruIcons.GetImageSource(iconType, 32),
            Image = BiruIcons.GetImageSource(iconType, 32),
        };
    }

    /// <summary>
    /// Relocates the BiruBox modify panel to the Modify tab and sets ShowText = false (34x34) for both panels.
    /// </summary>
    public static void SetupPanelsAndStyling()
    {
        try
        {
            var ribbon = ComponentManager.Ribbon;
            if (ribbon is null) return;

            // Locate Modify tab
            RibbonTab? modifyTab = ribbon.FindTab("Modify")
                ?? ribbon.Tabs.FirstOrDefault(t => t.Id == "Modify" || t.Title.Equals("Modify", StringComparison.OrdinalIgnoreCase));

            // Relocate Modify panel to Modify tab if needed
            if (modifyTab is not null)
            {
                foreach (var tab in ribbon.Tabs)
                {
                    if (tab == modifyTab) continue;

                    var modPanel = tab.Panels.FirstOrDefault(p => p.Source?.Title == PanelNameModify || p.Source?.Id == PanelNameModify);
                    if (modPanel is not null)
                    {
                        tab.Panels.Remove(modPanel);
                        modPanel.Source.Title = PanelName;
                        modifyTab.Panels.Add(modPanel);
                        break;
                    }
                }
            }

            // Apply 34x34 rounded square button styling to panels on ALL tabs (Add-Ins and Modify)
            foreach (var tab in ribbon.Tabs)
            {
                foreach (var panel in tab.Panels)
                {
                    if (panel.Source?.Title == PanelName || panel.Source?.Id == PanelName ||
                        panel.Source?.Title == PanelNameModify || panel.Source?.Id == PanelNameModify)
                    {
                        ApplyIconOnlyStyle(panel.Source.Items);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Log to debug output so ribbon manipulation failures are diagnosable
            System.Diagnostics.Debug.WriteLine($"[BiruBox] SetupPanelsAndStyling failed: {ex.Message}");
        }
    }

    static void ApplyIconOnlyStyle(IEnumerable<Autodesk.Windows.RibbonItem> items)
    {
        foreach (var item in items)
        {
            if (item is Autodesk.Windows.RibbonButton button)
            {
                button.ShowText = false;
                button.ShowImage = true;
                button.Size = RibbonItemSize.Large;
                button.Width = 34;
                button.Height = 34;
                button.MinWidth = 34;
                button.MinHeight = 34;
                button.Image ??= button.LargeImage;
                button.LargeImage ??= button.Image;
            }
            else if (item is Autodesk.Windows.RibbonRowPanel row)
            {
                row.Height = 74;
                ApplyIconOnlyStyle(row.Items);
            }
            else if (item is Autodesk.Windows.RibbonSplitButton split)
            {
                ApplyIconOnlyStyle(split.Items);
            }
        }
    }
}
