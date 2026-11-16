using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCOptimizer.Services;

namespace PCOptimizer.Views
{
    // Parte da janela de brilho: cor avançada (gama, temperatura, RGB e saturação).
    public partial class BrightnessWindow
    {
        // ── Cor avançada (gama / temperatura / RGB via gamma ramp) ───────────

        // Começa TRUE: no XAML compilado o ValueChanged dispara DURANTE o
        // InitializeComponent (coerção de Minimum/Value), quando os rótulos ainda
        // são null — o campo só vira false no fim do InitAdvColor.
        private bool _advInit = true;
        private int _advSaveSerial;

        private void InitAdvColor()
        {
            _advInit = true;
            var s = SettingsService.Current;
            SliderGamma.Value     = Math.Clamp(s.GammaValue, 0.5, 2.5);
            SliderColorTemp.Value = Math.Clamp(s.ColorTempK, 2000, 10000);
            SliderGainR.Value     = Math.Clamp(s.GainR, 25, 100);
            SliderGainG.Value     = Math.Clamp(s.GainG, 25, 100);
            SliderGainB.Value     = Math.Clamp(s.GainB, 25, 100);

            // Saturação só existe via driver NVIDIA — some em AMD/Intel em vez de
            // ficar um controle morto na tela.
            if (NvapiService.IsVibranceAvailable())
            {
                SaturationRow.Visibility = Visibility.Visible;
                int cur = NvapiService.GetDigitalVibrance();
                SliderSaturation.Value = s.Saturation > 0 ? s.Saturation : cur;
            }
            _advInit = false;
            UpdateAdvColorLabels();

            // Ajuste ativo salvo: abre a seção para o usuário ver o que está valendo.
            if (!GammaRampService.IsDefault(s.GammaValue, s.ColorTempK, s.GainR, s.GainG, s.GainB))
            {
                AdvColorPanel.Visibility = Visibility.Visible;
                TxtAdvColorArrow.Text = "▾";
            }
        }

        private void UpdateAdvColorLabels()
        {
            TxtGammaValue.Text     = SliderGamma.Value.ToString("F2",
                System.Globalization.CultureInfo.InvariantCulture);
            TxtColorTempValue.Text = $"{(int)SliderColorTemp.Value}K";
            // 0–63 do driver mostrado como 0–100% para o usuário
            TxtSaturationValue.Text = $"{(int)Math.Round(SliderSaturation.Value / 63.0 * 100)}%";
            TxtGainRValue.Text     = $"{(int)SliderGainR.Value}%";
            TxtGainGValue.Text     = $"{(int)SliderGainG.Value}%";
            TxtGainBValue.Text     = $"{(int)SliderGainB.Value}%";
        }

        private void AdvColor_Toggle(object sender, MouseButtonEventArgs e)
        {
            bool show = AdvColorPanel.Visibility != Visibility.Visible;
            AdvColorPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            TxtAdvColorArrow.Text = show ? "▾" : "▸";
        }

        private async void AdvColor_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // TxtGainBValue é o ÚLTIMO elemento da seção a ser criado pelo XAML —
            // guardar nele cobre qualquer disparo durante o InitializeComponent.
            if (_advInit || TxtGainBValue == null) return;
            UpdateAdvColorLabels();

            var s = SettingsService.Current;
            s.GammaValue = Math.Round(SliderGamma.Value, 2);
            s.ColorTempK = (int)SliderColorTemp.Value;
            s.GainR      = (int)SliderGainR.Value;
            s.GainG      = (int)SliderGainG.Value;
            s.GainB      = (int)SliderGainB.Value;

            // Fora da thread de UI: Apply faz EnumDisplayDevices + CreateDC +
            // SetDeviceGammaRamp por monitor, e isso a cada tick do arraste
            // engasgava o slider.
            bool ok = await Task.Run(() =>
                GammaRampService.Apply(s.GammaValue, s.ColorTempK, s.GainR, s.GainG, s.GainB));
            if (!ok) TxtStatus.Text = "⚠ A placa de vídeo recusou o ajuste de cor";
            else if (_monitorControls.Any(m => m.HdrEnabled))
                TxtStatus.Text = "Nota: monitores com HDR ativo ignoram gama/RGB — use o brilho SDR";

            // Debounce do Save: grava no disco só quando o usuário para de arrastar.
            int serial = ++_advSaveSerial;
            await Task.Delay(250);
            if (serial == _advSaveSerial) SettingsService.Save();
        }

        private async void SliderSaturation_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_advInit || TxtSaturationValue == null) return;
            UpdateAdvColorLabels();

            int level = (int)SliderSaturation.Value;
            bool ok = await Task.Run(() => NvapiService.SetDigitalVibrance(level));
            if (!ok) TxtStatus.Text = "⚠ O driver de vídeo recusou o ajuste de saturação";

            SettingsService.Current.Saturation = level;
            int serial = ++_advSaveSerial;
            await Task.Delay(250);
            if (serial == _advSaveSerial) SettingsService.Save();
        }

        private void BtnAdvColorReset_Click(object sender, RoutedEventArgs e)
        {
            _advInit = true;
            SliderGamma.Value     = GammaRampService.DefaultGamma;
            SliderColorTemp.Value = GammaRampService.DefaultKelvin;
            SliderGainR.Value     = GammaRampService.DefaultGain;
            SliderGainG.Value     = GammaRampService.DefaultGain;
            SliderGainB.Value     = GammaRampService.DefaultGain;
            SliderSaturation.Value = NvapiService.DvcDefault;
            _advInit = false;
            UpdateAdvColorLabels();

            GammaRampService.Reset();
            NvapiService.SetDigitalVibrance(NvapiService.DvcDefault);

            var s = SettingsService.Current;
            s.Saturation = NvapiService.DvcDefault;
            s.GammaValue = GammaRampService.DefaultGamma;
            s.ColorTempK = GammaRampService.DefaultKelvin;
            s.GainR = s.GainG = s.GainB = GammaRampService.DefaultGain;
            SettingsService.Save();

            TxtStatus.Text = "Cores restauradas ao padrão";
        }
    }
}
