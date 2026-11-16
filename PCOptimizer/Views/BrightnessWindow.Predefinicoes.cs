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
    // Parte da janela de brilho: predefinições de brilho e contraste.
    public partial class BrightnessWindow
    {
        private void RefreshPresetButtons()
        {
            var p1 = SettingsService.Current.Preset1;
            var p2 = SettingsService.Current.Preset2;
            var p3 = SettingsService.Current.Preset3;

            TxtPreset1Icon.Text = p1.Icon; TxtPreset1Name.Text = p1.Name; TxtPreset1Values.Text = $"{p1.Brightness}% / {p1.Contrast}%";
            TxtPreset2Icon.Text = p2.Icon; TxtPreset2Name.Text = p2.Name; TxtPreset2Values.Text = $"{p2.Brightness}% / {p2.Contrast}%";
            TxtPreset3Icon.Text = p3.Icon; TxtPreset3Name.Text = p3.Name; TxtPreset3Values.Text = $"{p3.Brightness}% / {p3.Contrast}%";
        }

        private void ApplyPreset(PresetData preset)
        {
            if (!_initialized) return;

            // Basta mover os sliders: os ValueChanged já aplicam em TODOS os tipos
            // de monitor (DDC por índice, WMI e brilho por software) com throttle.
            // Chamar SetBrightnessAll/SetContrastAll em paralelo causava escrita
            // dupla e CONCORRENTE no mesmo handle DDC/CI (I2C não é thread-safe).
            foreach (var mc in _monitorControls)
            {
                mc.SliderBrightness.Value = preset.Brightness;
                if (mc.SliderContrast != null)
                    mc.SliderContrast.Value = preset.Contrast;
            }

            TxtStatus.Text = $"Preset \"{preset.Name}\" aplicado";
        }

        private void EditPreset(PresetData preset, System.Action<PresetData> save)
        {
            var editor = new PresetEditWindow(preset) { Owner = this };
            if (editor.ShowDialog() == true)
            {
                save(editor.Result);
                SettingsService.Save();
                RefreshPresetButtons();
                TxtStatus.Text = $"Preset \"{editor.Result.Name}\" salvo";
            }
        }

        private void Preset1_Click(object sender, RoutedEventArgs e) => ApplyPreset(SettingsService.Current.Preset1);
        private void Preset2_Click(object sender, RoutedEventArgs e) => ApplyPreset(SettingsService.Current.Preset2);
        private void Preset3_Click(object sender, RoutedEventArgs e) => ApplyPreset(SettingsService.Current.Preset3);

        private void Preset1_Edit(object sender, MouseButtonEventArgs e) =>
            EditPreset(SettingsService.Current.Preset1, p => SettingsService.Current.Preset1 = p);
        private void Preset2_Edit(object sender, MouseButtonEventArgs e) =>
            EditPreset(SettingsService.Current.Preset2, p => SettingsService.Current.Preset2 = p);
        private void Preset3_Edit(object sender, MouseButtonEventArgs e) =>
            EditPreset(SettingsService.Current.Preset3, p => SettingsService.Current.Preset3 = p);
    }
}
