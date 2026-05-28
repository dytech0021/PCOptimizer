using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using PCOptimizer.Services;

namespace PCOptimizer.Views
{
    public partial class BrightnessWindow : Window
    {
        private bool _initialized;
        private DateTime _lastBrightnessChange = DateTime.MinValue;
        private DateTime _lastContrastChange = DateTime.MinValue;
        private bool _capturingHotkey;

        public BrightnessWindow()
        {
            InitializeComponent();
            Loaded += BrightnessWindow_Loaded;
            TxtHotkey.Text = SettingsService.Current.HotkeyDisplay;
            RefreshPresetButtons();
        }

        private void RefreshPresetButtons()
        {
            var p1 = SettingsService.Current.Preset1;
            var p2 = SettingsService.Current.Preset2;
            var p3 = SettingsService.Current.Preset3;

            TxtPreset1Icon.Text = p1.Icon;
            TxtPreset1Name.Text = p1.Name;
            TxtPreset1Values.Text = $"{p1.Brightness}% / {p1.Contrast}%";

            TxtPreset2Icon.Text = p2.Icon;
            TxtPreset2Name.Text = p2.Name;
            TxtPreset2Values.Text = $"{p2.Brightness}% / {p2.Contrast}%";

            TxtPreset3Icon.Text = p3.Icon;
            TxtPreset3Name.Text = p3.Name;
            TxtPreset3Values.Text = $"{p3.Brightness}% / {p3.Contrast}%";
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

        private void Header_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        }

        private async void ApplyPreset(PresetData preset)
        {
            if (!_initialized) return;

            SliderBrightness.Value = preset.Brightness;
            SliderContrast.Value = preset.Contrast;

            await Task.Run(() =>
            {
                MonitorService.SetBrightnessAll(preset.Brightness);
                MonitorService.SetContrastAll(preset.Contrast);
            });

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

        private void Preset1_Edit(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
            EditPreset(SettingsService.Current.Preset1, p => SettingsService.Current.Preset1 = p);
        private void Preset2_Edit(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
            EditPreset(SettingsService.Current.Preset2, p => SettingsService.Current.Preset2 = p);
        private void Preset3_Edit(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
            EditPreset(SettingsService.Current.Preset3, p => SettingsService.Current.Preset3 = p);

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }

        private void BtnSetHotkey_Click(object sender, RoutedEventArgs e)
        {
            if (_capturingHotkey) return;
            _capturingHotkey = true;
            BtnSetHotkey.Content = "Pressione a combinação... (Esc cancela)";
            BtnSetHotkey.IsEnabled = false;
            TxtStatus.Text = "Aguardando atalho...";
            Focus();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (!_capturingHotkey)
            {
                base.OnKeyDown(e);
                return;
            }

            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key == Key.Escape)
            {
                CancelHotkeyCapture();
                e.Handled = true;
                return;
            }

            // Ignore standalone modifier keys
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                     or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            {
                e.Handled = true;
                return;
            }

            var modifiers = Keyboard.Modifiers;
            if (modifiers == ModifierKeys.None)
            {
                TxtStatus.Text = "Use pelo menos um modificador (Ctrl, Alt, Shift)";
                e.Handled = true;
                return;
            }

            uint win32Mods = 0;
            if ((modifiers & ModifierKeys.Control) != 0) win32Mods |= 0x0002;
            if ((modifiers & ModifierKeys.Shift) != 0)   win32Mods |= 0x0004;
            if ((modifiers & ModifierKeys.Alt) != 0)     win32Mods |= 0x0001;
            if ((modifiers & ModifierKeys.Windows) != 0) win32Mods |= 0x0008;

            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);

            var display = "";
            if ((modifiers & ModifierKeys.Control) != 0) display += "Ctrl+";
            if ((modifiers & ModifierKeys.Alt) != 0)     display += "Alt+";
            if ((modifiers & ModifierKeys.Shift) != 0)   display += "Shift+";
            if ((modifiers & ModifierKeys.Windows) != 0) display += "Win+";
            display += key.ToString();

            SettingsService.Current.HotkeyModifiers = win32Mods;
            SettingsService.Current.HotkeyVk = vk;
            SettingsService.Current.HotkeyDisplay = display;
            SettingsService.Save();
            HotkeyService.Register();

            TxtHotkey.Text = display;
            BtnSetHotkey.Content = "Alterar Atalho";
            BtnSetHotkey.IsEnabled = true;
            TxtStatus.Text = $"Atalho definido: {display}";
            _capturingHotkey = false;
            e.Handled = true;
        }

        private void CancelHotkeyCapture()
        {
            _capturingHotkey = false;
            BtnSetHotkey.Content = "Alterar Atalho";
            BtnSetHotkey.IsEnabled = true;
            TxtStatus.Text = "Pronto — arraste os controles";
        }
    }
}
