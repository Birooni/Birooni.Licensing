using System.Windows;

namespace FamilyLoader.WPF
{
    public partial class TypesConfigWindow : Window
    {
        public TypesConfigWindow(FamilyItemViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }

        private void SaveClose_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = true;
            this.Close();
        }
    }
}
