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
    // Parte da janela de brilho: luz noturna do app e do Windows.
    public partial class BrightnessWindow
    {
        private void NightSection_Toggle(object sender, MouseButtonEventArgs e)
        {
            bool show = NightSection.Visibility != Visibility.Visible;
            NightSection.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            TxtNightArrow.Text = show ? "▾" : "▸";
        }

        private void ChkNightLight_Checked(object sender, RoutedEventArgs e)
        {
            NightLightPanel.Visibility = Visibility.Visible;
            int intensity = (int)SliderNightLight.Value;
            NightLightService.SetIntensity(intensity);
            TxtStatus.Text = $"Luz noturna ativada ({intensity}%)";
            SettingsService.Current.NightLightEnabled = true;
            SettingsService.Current.NightLightIntensity = intensity;
            SettingsService.Save();
        }

        private void ChkNightLight_Unchecked(object sender, RoutedEventArgs e)
        {
            NightLightPanel.Visibility = Visibility.Collapsed;
            NightLightService.Reset();
            TxtStatus.Text = "Luz noturna desativada";
            SettingsService.Current.NightLightEnabled = false;
            SettingsService.Save();
        }

        private int _nlSaveSerial;

        private async void SliderNightLight_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtNightLightValue == null || ChkNightLight == null) return;
            int value = (int)e.NewValue;
            TxtNightLightValue.Text = $"{value}%";
            if (ChkNightLight.IsChecked == true)
            {
                NightLightService.SetIntensity(value); // overlay: instantâneo
                SettingsService.Current.NightLightIntensity = value;

                // Debounce do Save: gravar o JSON no disco a cada tick do arraste
                // (I/O síncrono na thread de UI) fazia o slider engasgar.
                int serial = ++_nlSaveSerial;
                await Task.Delay(250);
                if (serial == _nlSaveSerial) SettingsService.Save();
            }
        }

        private void ChkWinNightLight_Checked(object sender, RoutedEventArgs e)
        {
            WinNightLightPanel.Visibility = Visibility.Visible;
            if (_winNlInitializing) return;

            if (NightLightService.SetWindowsNightLight(true))
            {
                // Sincroniza o slider com a intensidade real do registro
                _winNlInitializing = true;
                int intensity = NightLightService.GetWindowsNightLightIntensity();
                SliderWinNightLight.Value = intensity;
                TxtWinNightLightValue.Text = $"{intensity}%";
                _winNlInitializing = false;
                TxtStatus.Text = "Luz noturna Windows ativada";
            }
            else
            {
                // Reverte o checkbox sem disparar o Unchecked de novo
                _winNlInitializing = true;
                ChkWinNightLight.IsChecked = false;
                WinNightLightPanel.Visibility = Visibility.Collapsed;
                _winNlInitializing = false;

                // Abre diretamente as Configurações de Luz Noturna do Windows para que o
                // usuário ative o recurso lá (o que cria a chave de registro necessária);
                // depois basta clicar na chave aqui novamente.
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "ms-settings:nightlight",
                        UseShellExecute = true
                    });
                    TxtStatus.Text = "Ative a Luz Noturna nas Configurações do Windows e tente novamente";
                }
                catch
                {
                    TxtStatus.Text = "Não foi possível ativar — abra Configurações > Sistema > Luz Noturna";
                }
            }
        }

        private void ChkWinNightLight_Unchecked(object sender, RoutedEventArgs e)
        {
            WinNightLightPanel.Visibility = Visibility.Collapsed;
            if (_winNlInitializing) return;
            NightLightService.SetWindowsNightLight(false);
            TxtStatus.Text = "Luz noturna Windows desativada";
        }

        private async void SliderWinNightLight_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtWinNightLightValue == null || _winNlInitializing) return;
            int value = (int)e.NewValue;
            TxtWinNightLightValue.Text = $"{value}%";
            if (ChkWinNightLight?.IsChecked != true) return;

            // Debounce via serial: só aplica quando o usuário para de arrastar.
            // Serial inteiro evita o problema de DateTime.Now retornar o mesmo
            // valor para dois eventos consecutivos.
            int serial = ++_winNlSerial;
            await Task.Delay(200);
            if (serial != _winNlSerial) return;
            await Task.Run(() => NightLightService.SetWindowsNightLightIntensity(value));
        }
    }
}
