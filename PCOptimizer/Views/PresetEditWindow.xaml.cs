using System.Windows;
using System.Windows.Input;
using PCOptimizer.Services;

namespace PCOptimizer.Views
{
    public partial class PresetEditWindow : Window
    {
        public PresetData Result { get; private set; }

        public PresetEditWindow(PresetData preset)
        {
            InitializeComponent();
            Result = preset;

            TxtName.Text = preset.Name;
            SliderBrightness.Value = preset.Brightness;
            SliderContrast.Value = preset.Contrast;
            TxtBrightness.Text = $"{preset.Brightness}%";
            TxtContrast.Text = $"{preset.Contrast}%";
        }

        private void SliderBrightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtBrightness != null)
                TxtBrightness.Text = $"{(int)e.NewValue}%";
        }

        private void SliderContrast_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtContrast != null)
                TxtContrast.Text = $"{(int)e.NewValue}%";
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            // Nome vazio deixaria o botão do preset sem rótulo — mantém o anterior.
            string name = TxtName.Text.Trim();
            if (name.Length == 0) name = Result.Name;

            Result = new PresetData
            {
                Name = name,
                Icon = Result.Icon,
                Brightness = (int)SliderBrightness.Value,
                Contrast = (int)SliderContrast.Value
            };
            DialogResult = true;
            Close();
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
