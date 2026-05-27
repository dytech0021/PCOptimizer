using System;
using System.Threading.Tasks;
using System.Windows;
using PCOptimizer.Services;

namespace PCOptimizer.Views
{
    public partial class BrightnessWindow : Window
    {
        private bool _initialized;
        private DateTime _lastBrightnessChange = DateTime.MinValue;
        private DateTime _lastContrastChange = DateTime.MinValue;

        public BrightnessWindow()
        {
            InitializeComponent();
            Loaded += BrightnessWindow_Loaded;
        }

        private async void BrightnessWindow_Loaded(object sender, RoutedEventArgs e)
        {
            TxtStatus.Text = "Lendo monitores...";

            try
            {
                var result = await Task.Run(() => MonitorService.GetAverageValues());

                if (result.Count == 0)
                {
                    TxtMonitorCount.Text = "Nenhum monitor compatível encontrado";
                    TxtStatus.Text = "Seu monitor não suporta DDC/CI";
                    return;
                }

                TxtMonitorCount.Text = result.Count == 1
                    ? "Ajustando 1 monitor"
                    : $"Ajustando {result.Count} monitores ao mesmo tempo";

                SliderBrightness.Value = result.Brightness;
                SliderContrast.Value = result.Contrast;
                TxtBrightnessValue.Text = $"{result.Brightness}%";
                TxtContrastValue.Text = $"{result.Contrast}%";

                SliderBrightness.IsEnabled = true;
                SliderContrast.IsEnabled = true;
                TxtStatus.Text = "Pronto — arraste os controles";
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Erro: {ex.Message}";
            }

            _initialized = true;
        }

        private async void SliderBrightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_initialized) return;

            int value = (int)e.NewValue;
            TxtBrightnessValue.Text = $"{value}%";

            _lastBrightnessChange = DateTime.Now;
            var thisChange = _lastBrightnessChange;
            await Task.Delay(150);
            if (thisChange != _lastBrightnessChange) return;

            await Task.Run(() => MonitorService.SetBrightnessAll(value));
        }

        private async void SliderContrast_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_initialized) return;

            int value = (int)e.NewValue;
            TxtContrastValue.Text = $"{value}%";

            _lastContrastChange = DateTime.Now;
            var thisChange = _lastContrastChange;
            await Task.Delay(150);
            if (thisChange != _lastContrastChange) return;

            await Task.Run(() => MonitorService.SetContrastAll(value));
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
