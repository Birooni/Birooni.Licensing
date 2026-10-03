using Autodesk.Revit.UI;
using System;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace Biruscan.UI
{
    /// <summary>
    /// Helper class for creating ribbon panels, push buttons, and pulldown dropdowns.
    /// Uses the correct Revit API for ribbon creation.
    /// </summary>
    public class RibbonHelper
    {
        private readonly UIControlledApplication _application;
        private readonly string _tabName;

        public RibbonHelper(UIControlledApplication application, string tabName)
        {
            _application = application;
            _tabName = tabName;
        }

        /// <summary>
        /// Creates a ribbon panel on the plugin tab.
        /// </summary>
        public RibbonPanel CreatePanel(string panelName)
        {
            return _application.CreateRibbonPanel(_tabName, panelName);
        }

        /// <summary>
        /// Adds a push button to the specified panel.
        /// </summary>
        public PushButton AddPushButton(RibbonPanel panel, Type commandType, string buttonText, string toolTip)
        {
            string assemblyPath = Assembly.GetExecutingAssembly().Location;

            PushButtonData pbData = new PushButtonData(
                commandType.Name,
                buttonText,
                assemblyPath,
                commandType.FullName
            );

            PushButton pushButton = panel.AddItem(pbData) as PushButton;
            if (pushButton != null)
            {
                pushButton.ToolTip = toolTip;

                // Load vector icon from RibbonIconService (high-DPI, crystal clear)
                try
                {
                    var vectorIcon = Services.RibbonIconService.GetIcon(commandType.Name);
                    if (vectorIcon != null)
                    {
                        pushButton.LargeImage = vectorIcon;
                        pushButton.Image = vectorIcon;
                    }
                    else
                    {
                        // Fallback to legacy PNG if available
                        string iconPath = Path.Combine(
                            Path.GetDirectoryName(assemblyPath),
                            "Resources", "Icons",
                            commandType.Name + ".png"
                        );
                        if (File.Exists(iconPath))
                        {
                            Uri iconUri = new Uri(iconPath);
                            BitmapImage icon = new BitmapImage(iconUri);
                            pushButton.LargeImage = icon;
                            pushButton.Image = icon;
                        }
                    }
                }
                catch
                {
                    // Continue without icon
                }
            }

            return pushButton;
        }

        /// <summary>
        /// Creates and adds a PulldownButton dropdown to the specified panel.
        /// </summary>
        public PulldownButton AddPulldownButton(RibbonPanel panel, string buttonName, string buttonText, string toolTip)
        {
            PulldownButtonData pdData = new PulldownButtonData(buttonName, buttonText);
            PulldownButton pulldown = panel.AddItem(pdData) as PulldownButton;
            if (pulldown != null)
            {
                pulldown.ToolTip = toolTip;
                try
                {
                    var vectorIcon = Services.RibbonIconService.GetIcon(buttonName);
                    if (vectorIcon != null)
                    {
                        pulldown.LargeImage = vectorIcon;
                        pulldown.Image = vectorIcon;
                    }
                }
                catch { }
            }
            return pulldown;
        }

        /// <summary>
        /// Adds a PushButton item to an existing PulldownButton dropdown.
        /// </summary>
        public PushButton AddPulldownItem(PulldownButton pulldown, Type commandType, string buttonText, string toolTip)
        {
            string assemblyPath = Assembly.GetExecutingAssembly().Location;
            PushButtonData pbData = new PushButtonData(
                commandType.Name,
                buttonText,
                assemblyPath,
                commandType.FullName
            );

            PushButton pushButton = pulldown.AddPushButton(pbData);
            if (pushButton != null)
            {
                pushButton.ToolTip = toolTip;
                try
                {
                    var vectorIcon = Services.RibbonIconService.GetIcon(commandType.Name);
                    if (vectorIcon != null)
                    {
                        pushButton.LargeImage = vectorIcon;
                        pushButton.Image = vectorIcon;
                    }
                }
                catch { }
            }
            return pushButton;
        }
    }
}