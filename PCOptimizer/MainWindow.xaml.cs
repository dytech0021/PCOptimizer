using System.Windows;

namespace PCOptimizer
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void BtnOptimizeAll_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Otimização iniciada!", "PC Optimizer", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
