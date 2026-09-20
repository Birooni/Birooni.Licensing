using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace FamilyLoader.WPF
{
    public class SettingsViewModel : ViewModelBase
    {
        public Action? CloseAction { get; set; }

        public SettingsViewModel()
        {
            Tabs = new ObservableCollection<TabViewModel>();
            
            SaveCommand = new RelayCommand(Save);
            CloseCommand = new RelayCommand(Close);
            ExportCommand = new RelayCommand(Export);
            ImportCommand = new RelayCommand(Import);
            
            AddTabCommand = new RelayCommand(AddTab);
            RemoveTabCommand = new RelayCommand<TabViewModel>(RemoveTab);

            // Load existing configuration on instantiation
            var config = ConfigManager.Load();
            LoadFromConfig(config);
        }

        public ObservableCollection<TabViewModel> Tabs { get; }
        
        private TabViewModel? _selectedTab;
        public TabViewModel? SelectedTab
        {
            get => _selectedTab;
            set => SetProperty(ref _selectedTab, value);
        }

        public ICommand SaveCommand { get; }
        public ICommand CloseCommand { get; }
        public ICommand ExportCommand { get; }
        public ICommand ImportCommand { get; }
        public ICommand AddTabCommand { get; }
        public ICommand RemoveTabCommand { get; }

        private void AddTab()
        {
            var newTab = new TabViewModel($"Tab {Tabs.Count + 1}");
            newTab.AddPanel(); // Every tab starts with at least one panel
            Tabs.Add(newTab);
            SelectedTab = newTab;
        }

        private void RemoveTab(TabViewModel tab)
        {
            if (tab != null)
            {
                if (tab.Panels.Count > 0)
                {
                    MessageBox.Show("Cannot remove this tab because it still contains panels.\nPlease remove all panels first.", "Cannot Remove Tab", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (Tabs.Count > 1)
                {
                    Tabs.Remove(tab);
                }
                else
                {
                    MessageBox.Show("You must keep at least one Tab.", "Cannot Remove Tab", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private void LoadFromConfig(Configuration config)
        {
            Tabs.Clear();
            foreach (var tCon in config.Tabs)
            {
                var tabVM = new TabViewModel(tCon.Name);
                foreach (var pCon in tCon.Panels)
                {
                    var panelVM = new PanelViewModel(pCon.Name);
                    foreach (var fCon in pCon.Families)
                    {
                        Action<FamilyItemViewModel> removeAction = item => panelVM.Families.Remove(item);
                        var familyVM = new FamilyItemViewModel(removeAction)
                        {
                            RfaPath = fCon.RfaPath,
                            FamilyName = fCon.FamilyName,
                            IconPath = fCon.IconPath,
                            IsIconOnly = fCon.IsIconOnly,
                            StackRows = fCon.StackRows
                        };
                        foreach (var tc in fCon.Types)
                        {
                            familyVM.Types.Add(new TypeItemViewModel
                            {
                                TypeName = tc.TypeName,
                                CustomName = tc.CustomName,
                                IconPath = tc.IconPath,
                                IsIconOnly = tc.IsIconOnly
                            });
                        }
                        familyVM.InitializeTypesAsPushButtons(fCon.IsTypesAsPushButtons);
                        panelVM.Families.Add(familyVM);
                    }
                    if (panelVM.Families.Count == 0)
                    {
                        panelVM.Families.Add(new FamilyItemViewModel(item => panelVM.Families.Remove(item)));
                    }
                    tabVM.Panels.Add(panelVM);
                }
                if (tabVM.Panels.Count == 0)
                {
                    tabVM.AddPanel();
                }
                Tabs.Add(tabVM);
            }

            if (Tabs.Count == 0)
            {
                AddTab();
            }
            
            SelectedTab = Tabs.FirstOrDefault();
        }

        public Configuration ConvertToConfig()
        {
            var config = new Configuration();
            foreach (var tabVM in Tabs)
            {
                var tabConfig = new TabConfig { Name = tabVM.Name };
                foreach (var panelVM in tabVM.Panels)
                {
                    var panelConfig = new PanelConfig { Name = panelVM.Name };
                    foreach (var familyVM in panelVM.Families)
                    {
                        if (!string.IsNullOrWhiteSpace(familyVM.RfaPath))
                        {
                            var fConfig = new FamilyConfig
                            {
                                RfaPath = familyVM.RfaPath,
                                FamilyName = familyVM.FamilyName,
                                IconPath = familyVM.IconPath,
                                IsIconOnly = familyVM.IsIconOnly,
                                IsTypesAsPushButtons = familyVM.IsTypesAsPushButtons,
                                StackRows = familyVM.StackRows
                            };
                            foreach (var tvm in familyVM.Types)
                            {
                                fConfig.Types.Add(new TypeConfig
                                {
                                    TypeName = tvm.TypeName,
                                    CustomName = tvm.CustomName,
                                    IconPath = tvm.IconPath,
                                    IsIconOnly = tvm.IsIconOnly
                                });
                            }
                            panelConfig.Families.Add(fConfig);
                        }
                    }
                    // Only add panel if it has a name or families? Or always add. Let's always add if user created it.
                    tabConfig.Panels.Add(panelConfig);
                }
                config.Tabs.Add(tabConfig);
            }
            return config;
        }

        public void Save()
        {
            try
            {
                var config = ConvertToConfig();
                ConfigManager.Save(config);
                
                MessageBox.Show(
                    "Settings saved successfully!\n\nNote: Please restart Revit to reload and refresh the Ribbon panel buttons.",
                    "Success",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void Close()
        {
            CloseAction?.Invoke();
        }

        public void Export()
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "JSON Files (*.json)|*.json",
                Title = "Export Settings",
                FileName = "family_loader_config.json"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var config = ConvertToConfig();
                    ConfigManager.Export(config, dialog.FileName);
                    MessageBox.Show("Settings exported successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to export: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        public void Import()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "JSON Files (*.json)|*.json",
                Title = "Import Settings"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var config = ConfigManager.Import(dialog.FileName);
                    LoadFromConfig(config);
                    MessageBox.Show("Settings imported successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to import: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }
}
