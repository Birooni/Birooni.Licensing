using System.Windows.Input;

namespace FamilyLoader.WPF
{
    public class TypeItemViewModel : ViewModelBase
    {
        private string _typeName = string.Empty;
        private string _customName = string.Empty;
        private string _iconPath = string.Empty;
        private bool _isIconOnly = false;

        public string TypeName
        {
            get => _typeName;
            set => SetProperty(ref _typeName, value);
        }

        public string CustomName
        {
            get => _customName;
            set => SetProperty(ref _customName, value);
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

        public ICommand BrowseIconCommand { get; }

        public TypeItemViewModel()
        {
            BrowseIconCommand = new RelayCommand(BrowseIcon);
        }

        private void BrowseIcon()
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
    }
}
