using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using AdWin = Autodesk.Windows;

namespace FamilyLoader
{
    [System.Reflection.Obfuscation(Exclude = true)]
    public class App : IExternalApplication
    {
        public class CommandMapping
        {
            public string RfaPath { get; set; } = "";
            public string TypeName { get; set; } = "";
            public bool IsMainButton { get; set; } = false;
            public bool IsLoadAllButton { get; set; } = false;
        }

        private static readonly Dictionary<int, CommandMapping> _commandMappings = new Dictionary<int, CommandMapping>();
        private static readonly HashSet<string> _iconOnlyButtonNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        // Maps RfaPath to the Last Selected TypeName
        public static readonly Dictionary<string, string> LastSelectedType = new Dictionary<string, string>();

        public static CommandMapping? GetCommandMapping(int index)
        {
            return _commandMappings.TryGetValue(index, out var mapping) ? mapping : null;
        }

        private sealed class StackableButton
        {
            public RibbonItemData Data { get; set; } = null!;
            public bool IconOnly { get; set; }
            public Action<RibbonItem>? Configure { get; set; }
        }

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                // 1. Non-blocking licensing initialization & auto-update check
                _ = FamilyLoader.Core.LicensingManager.InitializeAsync();

                // Listen for background auto-update staging notifications
                FamilyLoader.Core.LicensingManager.UpdateStaged += (_, update) =>
                {
                    Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => NotifyUpdateStaged(update)));
                };

                ConfigManager.RevitVersion = application.ControlledApplication.VersionNumber;
                var config = ConfigManager.Load();
                _commandMappings.Clear();
                _iconOnlyButtonNames.Clear();
                int commandIndex = 1;

                // Create Settings & License buttons in Add-Ins tab -> Birooni Tools panel
                string addInsPanelName = "Birooni Tools";
                RibbonPanel? settingsPanel = application.GetRibbonPanels(Tab.AddIns)
                                                .FirstOrDefault(p => p.Name.Equals(addInsPanelName, StringComparison.OrdinalIgnoreCase));
                
                if (settingsPanel == null)
                {
                    settingsPanel = application.CreateRibbonPanel(Tab.AddIns, addInsPanelName);
                }

                string assemblyPath = typeof(App).Assembly.Location;
                PushButtonData settingsBtnData = new PushButtonData(
                    "FamilyLoaderSettings",
                    "Settings",
                    assemblyPath,
                    typeof(SettingsCommand).FullName)
                {
                    LargeImage = GetSettingsImage(32),
                    Image = GetSettingsImage(16),
                    ToolTip = "Open Settings to manage your custom Tabs, Panels, and RFA files."
                };
                try
                {
                    settingsPanel.AddItem(settingsBtnData);
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException)
                {
                    // Settings button already exists on a shared Birooni Tools panel.
                }

                PushButtonData licenseBtnData = new PushButtonData(
                    "FamilyLoaderLicense",
                    "License",
                    assemblyPath,
                    typeof(LicenseCommand).FullName)
                {
                    LargeImage = GetLicenseImage(32),
                    Image = GetLicenseImage(16),
                    ToolTip = "View Family Loader license status, activate key, or start a 14-day free trial."
                };
                try
                {
                    settingsPanel.AddItem(licenseBtnData);
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException)
                {
                    // License button already exists on panel.
                }

                int familyIndex = 1;

                // Loop through configured custom Tabs
                foreach (var tabConfig in config.Tabs)
                {
                    string tabName = string.IsNullOrWhiteSpace(tabConfig.Name) ? "Family Loader" : tabConfig.Name;
                    
                    try
                    {
                        application.CreateRibbonTab(tabName);
                    }
                    catch (Autodesk.Revit.Exceptions.ArgumentException) 
                    {
                        // Tab already exists or limit reached (max 20 custom tabs in Revit)
                    }

                    // Loop through Panels in this Tab
                    foreach (var panelConfig in tabConfig.Panels)
                    {
                        var validFamilies = panelConfig.Families.Where(f => !string.IsNullOrWhiteSpace(f.RfaPath)).ToList();
                        if (validFamilies.Count == 0) continue;

                        string panelCleanName = string.IsNullOrWhiteSpace(panelConfig.Name) ? "Unnamed Panel" : panelConfig.Name;
                        
                        RibbonPanel? sessionPanel = null;
                        try
                        {
                            sessionPanel = application.GetRibbonPanels(tabName)
                                .FirstOrDefault(p => p.Name.Equals(panelCleanName, StringComparison.OrdinalIgnoreCase));
                        }
                        catch (Autodesk.Revit.Exceptions.ArgumentException)
                        {
                            continue;
                        }
                        
                        if (sessionPanel == null)
                        {
                            try { sessionPanel = application.CreateRibbonPanel(tabName, panelCleanName); }
                            catch (Autodesk.Revit.Exceptions.ArgumentException)
                            {
                                try { sessionPanel = application.CreateRibbonPanel(tabName, panelCleanName + " "); }
                                catch { continue; }
                            }
                        }

                        if (sessionPanel == null)
                            continue;

                        var pendingFamilyButtons = new List<StackableButton>();
                        int pendingStackRows = 1;

                        foreach (var family in validFamilies)
                        {
                        if (commandIndex > 1000) break;

                        try
                        {

                        ImageSource? image = null;
                        ImageSource? largeImage = null;

                        if (!string.IsNullOrWhiteSpace(family.IconPath) && File.Exists(family.IconPath))
                        {
                            image = LoadUserImage(family.IconPath, 16);
                            largeImage = LoadUserImage(family.IconPath, 32);
                        }

                        image = image ?? ThumbnailExtractor.GetTextIcon(family.FamilyName, 16);
                        largeImage = largeImage ?? ThumbnailExtractor.GetTextIcon(family.FamilyName, 32);

                        List<string> types = RfaUtils.GetFamilyTypesFast(family.RfaPath);
                        int stackRows = NormalizeStackRows(family.StackRows);

                        if (family.IsTypesAsPushButtons && types.Count > 0)
                        {
                            AddStackedButtons(sessionPanel, pendingFamilyButtons, pendingStackRows);
                            pendingFamilyButtons.Clear();

                            var typeButtons = new List<StackableButton>();

                            foreach (var typeName in types)
                            {
                                if (commandIndex > 1000) break;
                                
                                var typeConfig = family.Types.FirstOrDefault(t => t.TypeName == typeName);
                                string buttonName = typeConfig != null && !string.IsNullOrWhiteSpace(typeConfig.CustomName) 
                                    ? typeConfig.CustomName 
                                    : typeName;
                                    
                                ImageSource? typeImage = null;
                                ImageSource? typeLargeImage = null;

                                if (typeConfig != null && !string.IsNullOrWhiteSpace(typeConfig.IconPath) && File.Exists(typeConfig.IconPath))
                                {
                                    typeImage = LoadUserImage(typeConfig.IconPath, 16);
                                    typeLargeImage = LoadUserImage(typeConfig.IconPath, 32);
                                }

                                typeImage = typeImage ?? ThumbnailExtractor.GetTextIcon(buttonName, 16);
                                typeLargeImage = typeLargeImage ?? ThumbnailExtractor.GetTextIcon(buttonName, 32);

                                bool iconOnly = family.IsIconOnly || (typeConfig != null && typeConfig.IsIconOnly);
                                // Stacked small buttons (Rows 2/3) must use a real caption like RevitToolkit.
                                // A blank glyph reserves an empty text column. Revit 2025 shows the stack as icons
                                // and puts this caption in the tooltip title.
                                bool stacked = stackRows > 1;
                                string buttonText = GetTypePushButtonText(
                                    family.FamilyName,
                                    buttonName,
                                    types.Count,
                                    iconOnly: false,
                                    stacked);

                                _commandMappings[commandIndex] = new CommandMapping { RfaPath = family.RfaPath, TypeName = typeName, IsMainButton = false };

                                PushButtonData pbData = new PushButtonData(
                                    $"Cmd_{commandIndex}",
                                    buttonText,
                                    assemblyPath,
                                    $"FamilyLoader.LoadFamilyCommand{commandIndex}")
                                {
                                    Image = typeImage,
                                    LargeImage = typeLargeImage,
                                    ToolTip = GetTypePlaceToolTip(family.FamilyName, typeName)
                                };

                                typeButtons.Add(new StackableButton { Data = pbData, IconOnly = iconOnly });
                                commandIndex++;
                            }

                            AddStackedButtons(sessionPanel, typeButtons, stackRows);
                        }
                        else
                        {
                            StackableButton familyButton = types.Count > 1
                                ? CreateSplitFamilyButton(family, types, image, largeImage, assemblyPath, ref commandIndex)
                                : CreateSingleFamilyButton(
                                    family,
                                    types,
                                    image,
                                    largeImage,
                                    assemblyPath,
                                    ref commandIndex);

                            if (stackRows <= 1)
                            {
                                AddStackedButtons(sessionPanel, pendingFamilyButtons, pendingStackRows);
                                pendingFamilyButtons.Clear();
                                AddStackedButtons(sessionPanel, new List<StackableButton> { familyButton }, 1);
                            }
                            else
                            {
                                if (pendingFamilyButtons.Count > 0 && pendingStackRows != stackRows)
                                {
                                    AddStackedButtons(sessionPanel, pendingFamilyButtons, pendingStackRows);
                                    pendingFamilyButtons.Clear();
                                }

                                pendingStackRows = stackRows;
                                pendingFamilyButtons.Add(familyButton);
                                if (pendingFamilyButtons.Count >= stackRows)
                                {
                                    AddStackedButtons(sessionPanel, pendingFamilyButtons, pendingStackRows);
                                    pendingFamilyButtons.Clear();
                                }
                            }
                        }
                        }
                        catch (Exception familyEx)
                        {
                            TaskDialog.Show(
                                "Family Loader",
                                $"Could not add ribbon button for '{family.FamilyName}'.\n\n{familyEx.Message}");
                        }

                        familyIndex++;
                    }

                        AddStackedButtons(sessionPanel, pendingFamilyButtons, pendingStackRows);
                }
            }

            ApplyRibbonItemDisplay();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ApplyRibbonItemDisplay));
            application.ControlledApplication.ApplicationInitialized += (_, _) => ApplyRibbonItemDisplay();
            return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Family Loader Startup Error", $"An error occurred during plugin initialization:\n\n{ex.Message}\n\n{ex.StackTrace}");
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }

        // Constructor requires a non-empty, non-whitespace caption. Braille blank is accepted but is wide,
        // which leaves a gap beside stacked (small) buttons. After the item exists, try a zero-width caption.
        private const string IconOnlyRibbonText = "\u2800";
        private static readonly string[] IconOnlyCaptionsAfterAdd = { "\u200B", "\u2060", "\uFEFF", "\u2800" };

        private static string GetRibbonText(string text, bool iconOnly)
        {
            if (iconOnly)
                return IconOnlyRibbonText;

            string formatted = FormatRibbonText(text ?? "");
            return string.IsNullOrWhiteSpace(formatted) ? IconOnlyRibbonText : formatted;
        }

        private static string GetTypePushButtonText(string familyName, string typeName, int typeCount, bool iconOnly, bool stacked)
        {
            if (iconOnly)
                return IconOnlyRibbonText;

            if (typeCount <= 1)
                return GetRibbonText(familyName, false);

            string familyPart = (familyName ?? "").Trim();
            string typePart = (typeName ?? "").Trim();

            if (string.IsNullOrEmpty(typePart) || typePart.Equals(familyPart, StringComparison.OrdinalIgnoreCase))
                return GetRibbonText(familyPart, false);
            if (string.IsNullOrEmpty(familyPart))
                return GetRibbonText(typePart, false);

            // Stacked (small) buttons only show one text line; large buttons get two lines.
            string combined = stacked ? familyPart + " " + typePart : familyPart + "\n" + typePart;
            return string.IsNullOrWhiteSpace(combined) ? IconOnlyRibbonText : combined;
        }

        private static string GetTypePlaceToolTip(string familyName, string typeName)
        {
            string familyPart = (familyName ?? "").Trim();
            string typePart = (typeName ?? "").Trim();

            if (string.IsNullOrEmpty(familyPart))
                return $"Load and place type: {typePart}";
            if (string.IsNullOrEmpty(typePart) || typePart.Equals(familyPart, StringComparison.OrdinalIgnoreCase))
                return $"Load and place type: {familyPart}";

            return $"Load and place type:\n{familyPart}\n{typePart}";
        }

        private static void ApplyIconOnly(RibbonItem item, bool iconOnly)
        {
            if (!iconOnly)
                return;

            try
            {
                if (!string.IsNullOrEmpty(item.Name))
                    _iconOnlyButtonNames.Add(item.Name);
            }
            catch
            {
            }
        }

        private static void ApplyRibbonItemDisplay()
        {
            if (_iconOnlyButtonNames.Count == 0)
                return;

            try
            {
                AdWin.RibbonControl? ribbon = AdWin.ComponentManager.Ribbon;
                if (ribbon == null)
                    return;

                foreach (AdWin.RibbonTab tab in ribbon.Tabs)
                {
                    foreach (AdWin.RibbonPanel panel in tab.Panels)
                    {
                        if (panel.Source?.Items == null)
                            continue;
                        ApplyRibbonItemDisplay(panel.Source.Items);
                    }
                }
            }
            catch
            {
            }
        }

        private static void ApplyRibbonItemDisplay(System.Collections.IEnumerable items)
        {
            foreach (object raw in items)
            {
                if (raw is AdWin.RibbonRowPanel rowPanel)
                {
                    ApplyRibbonItemDisplay(rowPanel.Items);
                    continue;
                }

                if (raw is AdWin.RibbonSplitButton split)
                {
                    if (MatchesIconOnly(split))
                    {
                        split.ShowText = false;
                        split.ShowImage = true;
                    }
                    continue;
                }

                if (raw is AdWin.RibbonItem item && MatchesIconOnly(item))
                {
                    item.ShowText = false;
                    item.ShowImage = true;
                }
            }
        }

        private static bool MatchesIconOnly(AdWin.RibbonItem item)
        {
            foreach (string name in _iconOnlyButtonNames)
            {
                if (IdMatches(item.Id, name))
                    return true;
                if (!string.IsNullOrEmpty(item.AutomationName) &&
                    (item.AutomationName.Equals(name, StringComparison.OrdinalIgnoreCase) || IdMatches(item.AutomationName, name)))
                    return true;
            }
            return false;
        }

        private static bool IdMatches(string? id, string name)
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
                return false;
            if (id.Equals(name, StringComparison.OrdinalIgnoreCase))
                return true;
            return id.EndsWith("%" + name, StringComparison.OrdinalIgnoreCase)
                || id.EndsWith("." + name, StringComparison.OrdinalIgnoreCase);
        }

        private static int NormalizeStackRows(int stackRows)
        {
            return stackRows == 1 || stackRows == 2 || stackRows == 3 ? stackRows : 1;
        }

        private static StackableButton CreateSingleFamilyButton(
            FamilyConfig family,
            List<string> types,
            ImageSource? image,
            ImageSource? largeImage,
            string assemblyPath,
            ref int commandIndex,
            bool isMainButton = false)
        {
            string buttonText = GetRibbonText(family.FamilyName, false);
            _commandMappings[commandIndex] = new CommandMapping
            {
                RfaPath = family.RfaPath,
                TypeName = types.FirstOrDefault() ?? "",
                IsMainButton = isMainButton
            };

            PushButtonData pbData = new PushButtonData(
                $"FamilyButton_{commandIndex}",
                buttonText,
                assemblyPath,
                $"FamilyLoader.LoadFamilyCommand{commandIndex}")
            {
                Image = image,
                LargeImage = largeImage,
                ToolTip = $"Click to load and place:\n{family.FamilyName}\n\nPath: {family.RfaPath}"
            };

            commandIndex++;
            return new StackableButton { Data = pbData, IconOnly = family.IsIconOnly };
        }

        private static StackableButton CreateSplitFamilyButton(
            FamilyConfig family,
            List<string> types,
            ImageSource? image,
            ImageSource? largeImage,
            string assemblyPath,
            ref int commandIndex)
        {
            string buttonText = GetRibbonText(family.FamilyName, false);

            SplitButtonData sbData = new SplitButtonData(
                $"FamilyButton_{commandIndex}",
                buttonText)
            {
                Image = image,
                LargeImage = largeImage,
                ToolTip = $"Click icon to load last selected type.\nClick arrow to choose a type for:\n{family.FamilyName}\n\nPath: {family.RfaPath}"
            };

            _commandMappings[commandIndex] = new CommandMapping { RfaPath = family.RfaPath, TypeName = types.FirstOrDefault() ?? "", IsMainButton = true };
            PushButtonData pbMainData = new PushButtonData(
                $"CmdMain_{commandIndex}",
                buttonText,
                assemblyPath,
                $"FamilyLoader.LoadFamilyCommand{commandIndex}")
            {
                Image = image,
                LargeImage = largeImage,
                ToolTip = $"Loads the previously selected type for {family.FamilyName}."
            };
            commandIndex++;

            _commandMappings[commandIndex] = new CommandMapping { RfaPath = family.RfaPath, TypeName = "", IsLoadAllButton = true };
            PushButtonData pbLoadAllData = new PushButtonData(
                $"CmdLoadAll_{commandIndex}",
                "Load All Types",
                assemblyPath,
                $"FamilyLoader.LoadFamilyCommand{commandIndex}")
            {
                ToolTip = $"Loads all types for {family.FamilyName}."
            };
            commandIndex++;

            var typeButtons = new List<PushButtonData>();
            foreach (var typeName in types)
            {
                if (commandIndex > 1000) break;
                var typeConfig = family.Types.FirstOrDefault(t => t.TypeName == typeName);
                string buttonName = typeConfig != null && !string.IsNullOrWhiteSpace(typeConfig.CustomName)
                    ? typeConfig.CustomName
                    : typeName;

                ImageSource? typeImage = null;
                ImageSource? typeLargeImage = null;

                if (typeConfig != null && !string.IsNullOrWhiteSpace(typeConfig.IconPath) && File.Exists(typeConfig.IconPath))
                {
                    typeImage = LoadUserImage(typeConfig.IconPath, 16);
                    typeLargeImage = LoadUserImage(typeConfig.IconPath, 32);
                }

                typeImage = typeImage ?? ThumbnailExtractor.GetTextIcon(buttonName, 16);
                typeLargeImage = typeLargeImage ?? ThumbnailExtractor.GetTextIcon(buttonName, 32);

                _commandMappings[commandIndex] = new CommandMapping { RfaPath = family.RfaPath, TypeName = typeName, IsMainButton = false };

                PushButtonData pbData = new PushButtonData(
                    $"Cmd_{commandIndex}",
                    GetRibbonText(buttonName, false),
                    assemblyPath,
                    $"FamilyLoader.LoadFamilyCommand{commandIndex}")
                {
                    Image = typeImage,
                    LargeImage = typeLargeImage,
                    ToolTip = GetTypePlaceToolTip(family.FamilyName, typeName)
                };

                typeButtons.Add(pbData);
                commandIndex++;
            }

            return new StackableButton
            {
                Data = sbData,
                IconOnly = family.IsIconOnly,
                Configure = item =>
                {
                    if (item is not SplitButton sbBtn) return;
                    sbBtn.IsSynchronizedWithCurrentItem = false;
                    sbBtn.AddPushButton(pbMainData);
                    sbBtn.AddPushButton(pbLoadAllData);
                    foreach (var typeButton in typeButtons)
                    {
                        sbBtn.AddPushButton(typeButton);
                    }
                    // Adding children can copy the first item's caption onto the ribbon face.
                    sbBtn.IsSynchronizedWithCurrentItem = false;
                    ApplyIconOnly(sbBtn, family.IsIconOnly);
                }
            };
        }

        private static void AddStackedButtons(RibbonPanel panel, List<StackableButton> buttons, int stackRows)
        {
            if (buttons.Count == 0) return;

            if (stackRows <= 1)
            {
                foreach (var button in buttons)
                {
                    FinishStackedButton(panel.AddItem(button.Data), button);
                }
                return;
            }

            int i = 0;
            while (i < buttons.Count)
            {
                int remaining = buttons.Count - i;
                int take = remaining >= stackRows ? stackRows : remaining;

                if (take >= 2)
                {
                    try
                    {
                        IList<RibbonItem> stacked = take == 3
                            ? panel.AddStackedItems(buttons[i].Data, buttons[i + 1].Data, buttons[i + 2].Data)
                            : panel.AddStackedItems(buttons[i].Data, buttons[i + 1].Data);
                        FinishStackedButtons(stacked, buttons, i);
                    }
                    catch
                    {
                        for (int k = 0; k < take; k++)
                        {
                            FinishStackedButton(panel.AddItem(buttons[i + k].Data), buttons[i + k]);
                        }
                    }
                    i += take;
                }
                else
                {
                    FinishStackedButton(panel.AddItem(buttons[i].Data), buttons[i]);
                    i += 1;
                }
            }
        }

        private static void FinishStackedButtons(IList<RibbonItem> stacked, List<StackableButton> buttons, int startIndex)
        {
            for (int j = 0; j < stacked.Count; j++)
            {
                FinishStackedButton(stacked[j], buttons[startIndex + j]);
            }
        }

        private static void FinishStackedButton(RibbonItem item, StackableButton button)
        {
            button.Configure?.Invoke(item);
            if (button.IconOnly)
            {
                if (!string.IsNullOrEmpty(button.Data.Name))
                    _iconOnlyButtonNames.Add(button.Data.Name);
                ApplyIconOnly(item, true);
            }
        }

        private static string FormatRibbonText(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= 12) return text;
            
            int middle = text.Length / 2;
            int bestSplitIndex = -1;
            
            for (int i = 0; i < middle; i++)
            {
                if (IsSplitChar(text[middle + i])) { bestSplitIndex = middle + i; break; }
                if (IsSplitChar(text[middle - i])) { bestSplitIndex = middle - i; break; }
            }

            if (bestSplitIndex != -1)
            {
                char c = text[bestSplitIndex];
                if (c == ' ') return text.Substring(0, bestSplitIndex) + "\n" + text.Substring(bestSplitIndex + 1);
                if (c == '-' || c == '_') return text.Substring(0, bestSplitIndex + 1) + "\n" + text.Substring(bestSplitIndex + 1);
            }
            return text;
        }

        private static bool IsSplitChar(char c) => c == ' ' || c == '-' || c == '_';

        private static ImageSource? LoadUserImage(string path, int size)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
                return null;

            try
            {
                BitmapImage bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path);
                bitmap.DecodePixelWidth = size;
                bitmap.DecodePixelHeight = size;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private static ImageSource GetSettingsImage(int size)
        {
            DrawingVisual drawingVisual = new DrawingVisual();
            using (DrawingContext drawingContext = drawingVisual.RenderOpen())
            {
                // Dark indigo rounded box background
                LinearGradientBrush brush = new LinearGradientBrush(
                    Color.FromRgb(40, 44, 52),
                    Color.FromRgb(20, 22, 26),
                    45.0);
                
                drawingContext.DrawRoundedRectangle(
                    brush,
                    new Pen(new SolidColorBrush(Color.FromRgb(70, 75, 96)), 1),
                    new Rect(1, 1, size - 2, size - 2),
                    size / 6.0,
                    size / 6.0);

                // Draw outer gear teeth
                double center = size / 2.0;
                double radius = size * 0.22;
                
                Pen gearPen = new Pen(new SolidColorBrush(Color.FromRgb(142, 45, 226)), size * 0.08); // purple gear outline
                drawingContext.DrawEllipse(null, gearPen, new Point(center, center), radius, radius);

                int teethCount = 8;
                Pen teethPen = new Pen(new SolidColorBrush(Color.FromRgb(142, 45, 226)), size * 0.07);
                for (int i = 0; i < teethCount; i++)
                {
                    double angle = i * (2 * Math.PI / teethCount);
                    double x1 = center + Math.Cos(angle) * (radius - 1);
                    double y1 = center + Math.Sin(angle) * (radius - 1);
                    double x2 = center + Math.Cos(angle) * (radius + size * 0.1);
                    double y2 = center + Math.Sin(angle) * (radius + size * 0.1);
                    drawingContext.DrawLine(teethPen, new Point(x1, y1), new Point(x2, y2));
                }
                
                // Central hole
                drawingContext.DrawEllipse(new SolidColorBrush(Color.FromRgb(40, 44, 52)), null, new Point(center, center), radius * 0.4, radius * 0.4);
            }

            RenderTargetBitmap rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(drawingVisual);
            rtb.Freeze();
            return rtb;
        }

        private static ImageSource GetLicenseImage(int size)
        {
            DrawingVisual drawingVisual = new DrawingVisual();
            using (DrawingContext dc = drawingVisual.RenderOpen())
            {
                double padding = size * 0.15;
                Rect rect = new Rect(padding, padding, size - 2 * padding, size - 2 * padding);
                
                PathGeometry shield = new PathGeometry();
                PathFigure figure = new PathFigure { StartPoint = new Point(rect.Left, rect.Top) };
                figure.Segments.Add(new LineSegment(new Point(rect.Right, rect.Top), true));
                figure.Segments.Add(new LineSegment(new Point(rect.Right, rect.Top + rect.Height * 0.5), true));
                figure.Segments.Add(new QuadraticBezierSegment(new Point(rect.Left + rect.Width * 0.5, rect.Bottom), new Point(rect.Left, rect.Top + rect.Height * 0.5), true));
                figure.IsClosed = true;
                shield.Figures.Add(figure);

                dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(2, 132, 199)), new Pen(new SolidColorBrush(Color.FromRgb(56, 189, 248)), size * 0.05), shield);

                dc.DrawEllipse(new SolidColorBrush(Colors.White), null, new Point(rect.Left + rect.Width * 0.5, rect.Top + rect.Height * 0.35), size * 0.12, size * 0.12);
                dc.DrawRectangle(new SolidColorBrush(Colors.White), null, new Rect(rect.Left + rect.Width * 0.45, rect.Top + rect.Height * 0.45, rect.Width * 0.1, rect.Height * 0.25));
            }

            RenderTargetBitmap rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(drawingVisual);
            rtb.Freeze();
            return rtb;
        }

        private static void NotifyUpdateStaged(Birooni.Client.Updates.AppUpdateInfo update)
        {
            try
            {
                var notes = string.IsNullOrWhiteSpace(update.ReleaseNotes) ? "" : $"\n\nWhat's new in v{update.LatestVersion}:\n{update.ReleaseNotes}";
                MessageBox.Show(
                    $"Family Loader has automatically downloaded the v{update.LatestVersion} update in the background!{notes}\n\nThe update is staged and will apply automatically when you close Revit.",
                    "Family Loader Auto-Update",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch
            {
                // Silently ignore
            }
        }
    }
}
