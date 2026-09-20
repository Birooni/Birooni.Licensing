using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace FamilyLoader.WPF
{
    public class TabViewModel : ViewModelBase
    {
        private string _name;

        public TabViewModel(string name)
        {
            _name = name;
            Panels = new ObservableCollection<PanelViewModel>();
            AddPanelCommand = new RelayCommand(AddPanel);
            RemovePanelCommand = new RelayCommand<PanelViewModel>(RemovePanel);
        }

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public ObservableCollection<PanelViewModel> Panels { get; }

        public ICommand AddPanelCommand { get; }
        public ICommand RemovePanelCommand { get; }

        public void AddPanel()
        {
            Panels.Add(new PanelViewModel($"Panel {Panels.Count + 1}"));
        }

        public void RemovePanel(PanelViewModel panel)
        {
            if (panel == null) return;

            if (panel.Families.Any(f => !string.IsNullOrWhiteSpace(f.RfaPath)))
            {
                MessageBox.Show("Cannot remove this panel because it still contains families.\nPlease remove all families first.", "Cannot Remove Panel", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Panels.Remove(panel);
        }
    }
}
