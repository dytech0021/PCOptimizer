using System.Windows;
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
            Result = new PresetData
            {
                Name = TxtName.Text.Trim(),
                Icon = Result.Icon,
                Brightness = (int)SliderBrightness.Value,
                Contrast = (int)SliderContrast.Value
            };
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
