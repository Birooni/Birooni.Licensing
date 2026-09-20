using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace FamilyLoader.WPF
{
    public class FamilyItemViewModel : ViewModelBase
    {
        private string _rfaPath = string.Empty;
        private string _familyName = string.Empty;
        private string _iconPath = string.Empty;
        private bool _isIconOnly = false;
        private bool _isTypesAsPushButtons = false;
        private int _stackRows = 1;
        public ObservableCollection<TypeItemViewModel> Types { get; } = new ObservableCollection<TypeItemViewModel>();
        private readonly Action<FamilyItemViewModel> _onDelete;

        public FamilyItemViewModel(Action<FamilyItemViewModel> onDelete)
        {
            _onDelete = onDelete ?? throw new ArgumentNullException(nameof(onDelete));
            BrowseRfaCommand = new RelayCommand(BrowseRfa);
            BrowseIconCommand = new RelayCommand(BrowseIcon);
            OpenTypesCommand = new RelayCommand(OpenTypes);
            DeleteCommand = new RelayCommand(() => _onDelete(this));
        }

        public string RfaPath
        {
            get => _rfaPath;
            set
            {
                if (SetProperty(ref _rfaPath, value))
                {
                    Types.Clear();

                    // Auto-set the family name from the file name when first browsing
                    if (string.IsNullOrWhiteSpace(FamilyName))
                    {
                        FamilyName = Path.GetFileNameWithoutExtension(value);
                    }

                    if (_isTypesAsPushButtons && !HasMultipleTypes())
                    {
                        _isTypesAsPushButtons = false;
                        OnPropertyChanged(nameof(IsTypesAsPushButtons));
                    }
                }
            }
        }

        public string FamilyName
        {
            get => _familyName;
            set => SetProperty(ref _familyName, value);
        }

        public string IconPath
        {
            get => _iconPath;
            set => SetProperty(ref _iconPath, value);
        }

        public bool IsIconOnly
        {
            get => _isIconOnly;
            set => SetProperty(ref _isIconOnly, value);
        }

        public bool IsTypesAsPushButtons
        {
            get => _isTypesAsPushButtons;
            set
            {
                if (value == _isTypesAsPushButtons)
                    return;

                if (value && !HasMultipleTypes())
                {
                    MessageBox.Show(
                        "Single type family",
                        "Family Loader",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);

                    Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() =>
                    {
                        OnPropertyChanged(nameof(IsTypesAsPushButtons));
                    }));
                    return;
                }

                SetProperty(ref _isTypesAsPushButtons, value);
            }
        }

        public IReadOnlyList<int> StackRowOptions { get; } = new[] { 1, 2, 3 };

        public int StackRows
        {
            get => _stackRows;
            set
            {
                int clamped = value == 1 || value == 2 || value == 3 ? value : 1;
                SetProperty(ref _stackRows, clamped);
            }
        }

        public void InitializeTypesAsPushButtons(bool value)
        {
            _isTypesAsPushButtons = value && HasMultipleTypes();
        }

        private bool HasMultipleTypes()
        {
            if (string.IsNullOrWhiteSpace(RfaPath) || !File.Exists(RfaPath))
                return false;

            return RfaUtils.GetFamilyTypesFast(RfaPath).Count > 1;
        }

        public ICommand BrowseRfaCommand { get; }
        public ICommand BrowseIconCommand { get; }
        public ICommand OpenTypesCommand { get; }
        public ICommand DeleteCommand { get; }

        public void BrowseRfa()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Revit Family Files (*.rfa)|*.rfa",
                Title = "Select Revit Family File"
            };

            if (dialog.ShowDialog() == true)
            {
                RfaPath = dialog.FileName;
            }
        }

        public void BrowseIcon()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Image Files (*.png;*.jpg;*.jpeg;*.bmp;*.ico)|*.png;*.jpg;*.jpeg;*.bmp;*.ico",
                Title = "Select Icon Image File"
            };

            if (dialog.ShowDialog() == true)
            {
                IconPath = dialog.FileName;
            }
        }

        private void OpenTypes()
        {
            if (string.IsNullOrWhiteSpace(RfaPath) || !File.Exists(RfaPath))
            {
                System.Windows.MessageBox.Show("Please select a valid Revit Family File first.", "Invalid File", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }

            var rfaTypes = RfaUtils.GetFamilyTypesFast(RfaPath);
            
            // Merge existing configs with rfa types
            var newTypesList = new ObservableCollection<TypeItemViewModel>();
            foreach (var tName in rfaTypes)
            {
                var existing = Types.FirstOrDefault(t => t.TypeName == tName);
                if (existing != null)
                {
                    newTypesList.Add(existing);
                }
                else
                {
                    newTypesList.Add(new TypeItemViewModel { TypeName = tName, CustomName = tName });
                }
            }
            Types.Clear();
            foreach (var t in newTypesList)
            {
                Types.Add(t);
            }

            var dialog = new TypesConfigWindow(this);
            dialog.ShowDialog();
        }

        public void Delete()
        {
            _onDelete(this);
        }
    }
}
