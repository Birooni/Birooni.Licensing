using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FamilyLoader.WPF
{
    public partial class SettingsWindow : Window
    {
        private readonly SettingsViewModel _viewModel;
        private Point _startPoint;
        private bool _isDragging;
        
        private Point _dataGridStartPoint;
        private bool _isDataGridDragging;

        public SettingsWindow()
        {
            InitializeComponent();
            
            _viewModel = new SettingsViewModel();
            _viewModel.CloseAction = () => this.Close();
            DataContext = _viewModel;
        }

        private void TabItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _startPoint = e.GetPosition(null);
        }

        private void TabItem_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && !_isDragging)
            {
                Point position = e.GetPosition(null);
                if (Math.Abs(position.X - _startPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(position.Y - _startPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    if (sender is TabItem tabItem && tabItem.DataContext is PanelViewModel)
                    {
                        _isDragging = true;
                        DragDrop.DoDragDrop(tabItem, tabItem.DataContext, DragDropEffects.Move);
                        _isDragging = false;
                    }
                }
            }
        }

        private void TabItem_Drop(object sender, DragEventArgs e)
        {
            if (sender is TabItem targetTabItem && targetTabItem.DataContext is PanelViewModel targetPanel)
            {
                var sourcePanel = e.Data.GetData(typeof(PanelViewModel)) as PanelViewModel;
                if (sourcePanel != null && sourcePanel != targetPanel && _viewModel.SelectedTab != null)
                {
                    int sourceIndex = _viewModel.SelectedTab.Panels.IndexOf(sourcePanel);
                    int targetIndex = _viewModel.SelectedTab.Panels.IndexOf(targetPanel);

                    if (sourceIndex != -1 && targetIndex != -1)
                    {
                        _viewModel.SelectedTab.Panels.Move(sourceIndex, targetIndex);
                    }
                }
            }
        }

        private void DataGridRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dataGridStartPoint = e.GetPosition(null);
        }

        private void DataGridRow_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && !_isDataGridDragging)
            {
                Point position = e.GetPosition(null);
                if (Math.Abs(position.X - _dataGridStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(position.Y - _dataGridStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    if (sender is DataGridRow row && row.Item is FamilyItemViewModel)
                    {
                        _isDataGridDragging = true;
                        DragDrop.DoDragDrop(row, row.Item, DragDropEffects.Move);
                        _isDataGridDragging = false;
                    }
                }
            }
        }

        private void DataGridRow_Drop(object sender, DragEventArgs e)
        {
            if (sender is DataGridRow targetRow && targetRow.Item is FamilyItemViewModel targetFamily && _viewModel.SelectedTab != null)
            {
                var sourceFamily = e.Data.GetData(typeof(FamilyItemViewModel)) as FamilyItemViewModel;
                if (sourceFamily != null && sourceFamily != targetFamily)
                {
                    var panel = _viewModel.SelectedTab.Panels.FirstOrDefault(p => p.Families.Contains(targetFamily));
                    if (panel != null && panel.Families.Contains(sourceFamily))
                    {
                        int sourceIndex = panel.Families.IndexOf(sourceFamily);
                        int targetIndex = panel.Families.IndexOf(targetFamily);

                        if (sourceIndex != -1 && targetIndex != -1)
                        {
                            panel.Families.Move(sourceIndex, targetIndex);
                        }
                    }
                }
            }
        }

        private void BtnLicense_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new FamilyLoader.UI.LicenseDialog();
            dlg.Owner = this;
            dlg.ShowDialog();
        }
    }
}
