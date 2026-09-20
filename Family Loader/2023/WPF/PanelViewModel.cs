using System.Collections.ObjectModel;
using System.Windows.Input;

namespace FamilyLoader.WPF
{
    public class PanelViewModel : ViewModelBase
    {
        private string _name;

        public PanelViewModel(string name)
        {
            _name = name;
            Families = new ObservableCollection<FamilyItemViewModel>();
            AddFamilyCommand = new RelayCommand(AddFamily);
            AddMultipleFamiliesCommand = new RelayCommand(AddMultipleFamilies);
        }

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public ObservableCollection<FamilyItemViewModel> Families { get; }

        public ICommand AddFamilyCommand { get; }

        public ICommand AddMultipleFamiliesCommand { get; }

        public void AddEmptyFamilyRow()
        {
            Families.Add(new FamilyItemViewModel(RemoveFamily));
        }

        public void AddFamily()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Revit Family Files (*.rfa)|*.rfa",
                Title = "Select Revit Family File"
            };

            if (dialog.ShowDialog() == true)
            {
                // Remove empty default rows if it's currently empty or has only one empty row
                if (Families.Count == 1 && string.IsNullOrWhiteSpace(Families[0].RfaPath))
                {
                    Families.Clear();
                }

                var newFamily = new FamilyItemViewModel(RemoveFamily)
                {
                    RfaPath = dialog.FileName,
                    FamilyName = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName)
                };
                Families.Add(newFamily);
            }
        }

        public void AddMultipleFamilies()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Revit Family Files (*.rfa)|*.rfa",
                Title = "Select Multiple Revit Family Files",
                Multiselect = true
            };

            if (dialog.ShowDialog() == true)
            {
                // Remove empty default rows if it's currently empty or has only one empty row
                if (Families.Count == 1 && string.IsNullOrWhiteSpace(Families[0].RfaPath))
                {
                    Families.Clear();
                }

                foreach (var fileName in dialog.FileNames)
                {
                    var newFamily = new FamilyItemViewModel(RemoveFamily)
                    {
                        RfaPath = fileName,
                        FamilyName = System.IO.Path.GetFileNameWithoutExtension(fileName)
                    };
                    Families.Add(newFamily);
                }
            }
        }

        private void RemoveFamily(FamilyItemViewModel item)
        {
            Families.Remove(item);
        }
    }
}
