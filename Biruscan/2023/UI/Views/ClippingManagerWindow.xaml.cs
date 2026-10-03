using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Biruscan.Models;
using Biruscan.Services;

namespace Biruscan.UI.Views
{
    public partial class ClippingManagerWindow : Window
    {
        private readonly Document _doc;
        private readonly ClippingService _clipService;
        private readonly PointCloudService _pcService;
        
        private ObservableCollection<ClippingGroup> _groups;
        private Autodesk.Revit.UI.ExternalEvent _externalEvent;
        private RevitEventHandler _handler;
        private Autodesk.Revit.UI.UIApplication _uiapp;

        public static bool IsSyncingFromRevit = false;
        public static ClippingManagerWindow Instance { get; private set; }

        private static bool _isSubscribed = false;

        public ClippingManagerWindow(Document doc, ClippingService clipService, PointCloudService pcService, Autodesk.Revit.UI.ExternalEvent exEvent, RevitEventHandler handler, Autodesk.Revit.UI.UIApplication uiapp)
        {
            _doc = doc;
            _clipService = clipService;
            _pcService = pcService;
            _externalEvent = exEvent;
            _handler = handler;
            _uiapp = uiapp;

            InitializeComponent();
            Instance = this;

            if (!_isSubscribed)
            {
                _uiapp.Application.DocumentChanged += Application_DocumentChanged;
                _isSubscribed = true;
            }
        }

        private static void Application_DocumentChanged(object sender, Autodesk.Revit.DB.Events.DocumentChangedEventArgs e)
        {
            if (Instance == null) return;
            
            var window = Instance;
            // Always sync UI with the true state in Extensible Storage
            var pci = window._clipService.GetPointCloudInstance();
            if (pci != null)
            {
                window.Dispatcher.Invoke(() => 
                {
                    if (Instance == null) return;
                    Services.ClippingSyncService.SyncUiFromState(pci, window._clipService.GetClippingGroups());
                    window.RefreshData();
                });
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            Instance = null;
            base.OnClosed(e);
        }

        public void RefreshDataPublic()
        {
            RefreshData();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var pci = _pcService.GetFirstPointCloudInstance();
            if (pci != null)
                txtStatus.Text = "Loaded: " + pci.Name;

            if (StatusTextBlock != null)
                StatusTextBlock.Text = "Ready.";

            WireToolButton(ShowHideCloudToolButton, "VisibilityToggleCommand", "Show/Hide Cloud");
            WireToolButton(SectionBoxToolButton, "SectionBoxCommand", "Section Box");
            WireToolButton(RectangularCutToolButton, "RectangularCutCommand", "Rectangular Fence");
            WireToolButton(PolygonCutToolButton, "PolygonalCutCommand", "Polygon Fence");
            WireToolButton(HorizontalSliceToolButton, "HorizontalSliceCommand", "Horizontal Slice");
            WireToolButton(VerticalSliceToolButton, "VerticalSliceCommand", "Vertical Slice");
            WireToolButton(BackwardSliceToolButton, "SliceBackwardCommand", "Slice Back");
            WireToolButton(ForwardSliceToolButton, "SliceForwardCommand", "Slice Forward");
            WireToolButton(ClearCutsToolButton, "ClearCutsCommand", "Clear Cuts");
            WireToolButton(RegenerateToolButton, "ClearCutsCommand", "Regenerate");
            WireToolButton(ElbowToolButton, "MepAutoElbowCommand", "Elbow");
            WireToolButton(TeeToolButton, "MepAutoTeeCommand", "Tee");
            WireToolButton(UnionToolButton, "MepAutoUnionCommand", "Union");
            WireToolButton(ReducerToolButton, "MepAutoReducerCommand", "Reducer");
            WireToolButton(UnifyToolButton, "MepAutoUnifyCommand", "Unify");
            WireToolButton(CrossToolButton, "MepAutoCrossCommand", "Cross");
            WireToolButton(AutoToolButton, "MepAutoConnectCommand", "Auto");

            RefreshData();
        }

        private void ShowHideCloudToolButton_OnClick(object sender, RoutedEventArgs e)
        {
            PostRibbonCommand("View", "Biruscan.Commands.VisibilityToggleCommand");
        }

        private void SectionBoxToolButton_OnClick(object sender, RoutedEventArgs e)
        {
            PostRibbonCommand("View", "Biruscan.Commands.SectionBoxCommand");
        }

        private void RectangularCutToolButton_OnClick(object sender, RoutedEventArgs e)
        {
            PostRibbonCommand("Clipping", "Biruscan.Commands.RectangularCutCommand");
        }

        private void PolygonCutToolButton_OnClick(object sender, RoutedEventArgs e)
        {
            PostRibbonCommand("Clipping", "Biruscan.Commands.PolygonalCutCommand");
        }

        private void HorizontalSliceToolButton_OnClick(object sender, RoutedEventArgs e)
        {
            PostRibbonCommand("Clipping", "Biruscan.Commands.HorizontalSliceCommand");
        }

        private void VerticalSliceToolButton_OnClick(object sender, RoutedEventArgs e)
        {
            PostRibbonCommand("Clipping", "Biruscan.Commands.VerticalSliceCommand");
        }

        private void BackwardSliceToolButton_OnClick(object sender, RoutedEventArgs e)
        {
            PostRibbonCommand("Clipping", "Biruscan.Commands.SliceBackwardCommand");
        }

        private void ForwardSliceToolButton_OnClick(object sender, RoutedEventArgs e)
        {
            PostRibbonCommand("Clipping", "Biruscan.Commands.SliceForwardCommand");
        }

        private void ClearCutsToolButton_OnClick(object sender, RoutedEventArgs e)
        {
            PostRibbonCommand("Clipping", "Biruscan.Commands.ClearCutsCommand");
        }

        private void ElbowToolButton_OnClick(object sender, RoutedEventArgs e)
        {
            PostRibbonCommand("MEP Tools", "Biruscan.Commands.MepAutoElbowCommand", "MepOperationsDropdown");
        }

        private void TeeToolButton_OnClick(object sender, RoutedEventArgs e)
        {
            PostRibbonCommand("MEP Tools", "Biruscan.Commands.MepAutoTeeCommand", "MepOperationsDropdown");
        }

        private void UnionToolButton_OnClick(object sender, RoutedEventArgs e)
        {
            PostRibbonCommand("MEP Tools", "Biruscan.Commands.MepAutoUnionCommand", "MepOperationsDropdown");
        }

        private void ReducerToolButton_OnClick(object sender, RoutedEventArgs e)
        {
            PostRibbonCommand("MEP Tools", "Biruscan.Commands.MepAutoReducerCommand", "MepOperationsDropdown");
        }

        private void UnifyToolButton_OnClick(object sender, RoutedEventArgs e)
        {
            PostRibbonCommand("MEP Tools", "Biruscan.Commands.MepAutoUnifyCommand", "MepOperationsDropdown");
        }

        private void CrossToolButton_OnClick(object sender, RoutedEventArgs e)
        {
            PostRibbonCommand("MEP Tools", "Biruscan.Commands.MepAutoCrossCommand", "MepOperationsDropdown");
        }

        private void AutoToolButton_OnClick(object sender, RoutedEventArgs e)
        {
            PostRibbonCommand("MEP Tools", "Biruscan.Commands.MepAutoConnectCommand", "MepOperationsDropdown");
        }

        private static void WireToolButton(Button button, string iconName, string label)
        {
            if (button == null) return;
            ImageSource icon = RibbonIconService.GetIcon(iconName);
            if (icon == null)
            {
                button.Content = label;
                return;
            }

            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            content.Children.Add(new Image
            {
                Source = icon,
                Width = 16,
                Height = 16,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            content.Children.Add(new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center
            });
            button.Content = content;
        }

        private void PostRibbonCommand(string panelName, string commandClassName, string pulldownName = null)
        {
            if (_uiapp == null) return;
            string tab = "Biruscan";
            string[] candidates =
            {
                $"CustomCtrl_%CustomCtrl_%{tab}%{panelName}%{commandClassName}",
                $"CustomCtrl_%{tab}%{panelName}%{commandClassName}",
                pulldownName == null ? null : $"CustomCtrl_%CustomCtrl_%{tab}%{panelName}%{pulldownName}%{commandClassName}",
                pulldownName == null ? null : $"CustomCtrl_%{tab}%{panelName}%{pulldownName}%{commandClassName}",
                pulldownName == null ? null : $"CustomCtrl_%CustomCtrl_%{tab}%{panelName}%Operation%{commandClassName}",
                pulldownName == null ? null : $"CustomCtrl_%{tab}%{panelName}%Operation%{commandClassName}"
            };

            foreach (string candidate in candidates)
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                try
                {
                    RevitCommandId commandId = RevitCommandId.LookupCommandId(candidate);
                    if (commandId == null) continue;
                    _uiapp.PostCommand(commandId);
                    if (StatusTextBlock != null)
                        StatusTextBlock.Text = "Ready.";
                    return;
                }
                catch { }
            }

            if (StatusTextBlock != null)
                StatusTextBlock.Text = "Could not start " + commandClassName + ".";
        }

        private void RefreshData()
        {
            _groups = new ObservableCollection<ClippingGroup>(_clipService.GetClippingGroups());
            lstGroups.ItemsSource = _groups;
            
            // Re-select active group
            var active = _groups.FirstOrDefault(g => g.IsActive);
            if (active != null)
                lstGroups.SelectedItem = active;
            else if (_groups.Count > 0)
                lstGroups.SelectedIndex = 0;
        }

        private void LstGroups_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (lstGroups.SelectedItem is ClippingGroup selectedGroup)
            {
                dgClippings.ItemsSource = selectedGroup.Clippings;
            }
            else
            {
                dgClippings.ItemsSource = null;
            }
        }

        private void DgClippings_CellEditEnding(object sender, System.Windows.Controls.DataGridCellEditEndingEventArgs e)
        {
            // Give the UI a tiny moment to commit the value
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ApplyActiveGroupPublic();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void CheckBox_Click(object sender, RoutedEventArgs e)
        {
            // Only trigger a transaction if the user manually clicked it, not if we're syncing from Revit
            if (!IsSyncingFromRevit)
            {
                // Push the UI update through dispatcher to let binding finish
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ApplyActiveGroupPublic();
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void BtnDeleteClipping_Click(object sender, RoutedEventArgs e)
        {
            if (dgClippings.SelectedItem is ClippingRecord record && lstGroups.SelectedItem is ClippingGroup group)
            {
                group.Clippings.Remove(record);
                ApplyActiveGroupPublic();
            }
        }

        private void BtnClearClippings_Click(object sender, RoutedEventArgs e)
        {
            if (lstGroups.SelectedItem is ClippingGroup group)
            {
                group.Clippings.Clear();
                ApplyActiveGroupPublic();
            }
        }

        private void BtnEnableAll_Click(object sender, RoutedEventArgs e)
        {
            if (lstGroups.SelectedItem is ClippingGroup group)
            {
                foreach (var clip in group.Clippings) clip.IsActive = true;
                dgClippings.Items.Refresh();
                ApplyActiveGroupPublic();
            }
        }

        private void BtnDisableAll_Click(object sender, RoutedEventArgs e)
        {
            if (lstGroups.SelectedItem is ClippingGroup group)
            {
                foreach (var clip in group.Clippings) clip.IsActive = false;
                dgClippings.Items.Refresh();
                ApplyActiveGroupPublic();
            }
        }

        private void BtnCreateGroup_Click(object sender, RoutedEventArgs e)
        {
            var newGroup = new ClippingGroup { Name = $"Group {_groups.Count + 1}" };
            var rawGroups = _clipService.GetClippingGroups();
            rawGroups.Add(newGroup);
            RefreshData();
            lstGroups.SelectedItem = newGroup;
        }

        private void BtnDeleteGroup_Click(object sender, RoutedEventArgs e)
        {
            if (lstGroups.SelectedItem is ClippingGroup group)
            {
                var rawGroups = _clipService.GetClippingGroups();
                rawGroups.Remove(group);
                if (group.IsActive)
                {
                    if (rawGroups.Count > 0)
                    {
                        rawGroups[0].IsActive = true;
                        ApplyActiveGroupPublic();
                    }
                    else
                    {
                        ResetAllCutsPublic();
                    }
                }
                RefreshData();
            }
        }

        private void BtnActivateGroup_Click(object sender, RoutedEventArgs e)
        {
            if (lstGroups.SelectedItem is ClippingGroup group)
            {
                var rawGroups = _clipService.GetClippingGroups();
                foreach (var g in rawGroups) g.IsActive = false;
                group.IsActive = true;
                
                RefreshData();
                ApplyActiveGroupPublic();
            }
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            // Clear every clipping in every group, then re-apply: the active group becomes
            // empty → FilterAction = None → the full cloud is restored (not just the list).
            foreach (var g in _clipService.GetClippingGroups()) g.Clippings.Clear();
            ApplyActiveGroupPublic();
            RefreshData();
        }



        public void ApplyActiveGroupPublic()
        {
            if (_externalEvent != null && _handler != null)
            {
                _handler.SetAction(() => ApplyActiveGroup());
                _externalEvent.Raise();
            }
            else
            {
                ApplyActiveGroup();
            }
        }

        public void ResetAllCutsPublic()
        {
            if (_externalEvent != null && _handler != null)
            {
                _handler.SetAction(() => _clipService.ResetAllCuts());
                _externalEvent.Raise();
            }
            else
            {
                _clipService.ResetAllCuts();
            }
        }

        private void ApplyActiveGroup()
        {
            try
            {
                // RebuildCropFromActiveGroup handles BOTH cases: when the active group has
                // active clippings it isolates them; when it has none it sets
                // FilterAction = None, which actually removes the cut from the cloud.
                // (ResetAllCuts did NOT clear the native isolate filter — that's why
                // "Clear Clippings" emptied the list but left the cloud still cut.)
                _clipService.RebuildCropFromActiveGroup();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error applying cuts: " + ex.Message);
            }
        }
    }
}
