using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCOptimizer.Services;

namespace PCOptimizer.Views
{
    public partial class BrightnessWindow : Window
    {
        private sealed class MonitorControl
        {
            public int Index { get; init; }
            public bool IsWmi { get; init; }
            public required Slider SliderBrightness { get; init; }
            public required TextBlock TxtBrightness { get; init; }
            public Slider? SliderContrast { get; init; }
            public TextBlock? TxtContrast { get; init; }
            public DateTime LastBrightnessChange;
            public DateTime LastContrastChange;
        }

        private bool _initialized;
        private readonly List<MonitorControl> _monitorControls = new();
        private bool _capturingHotkey;

        public BrightnessWindow()
        {
            InitializeComponent();
            Loaded += BrightnessWindow_Loaded;
            TxtHotkey.Text = SettingsService.Current.HotkeyDisplay;
            RefreshPresetButtons();
            SliderNightLight.Value = SettingsService.Current.NightLightIntensity;
            if (SettingsService.Current.NightLightEnabled)
                ChkNightLight.IsChecked = true;
        }

        private void RefreshPresetButtons()
        {
            var p1 = SettingsService.Current.Preset1;
            var p2 = SettingsService.Current.Preset2;
            var p3 = SettingsService.Current.Preset3;

            TxtPreset1Icon.Text = p1.Icon; TxtPreset1Name.Text = p1.Name; TxtPreset1Values.Text = $"{p1.Brightness}% / {p1.Contrast}%";
            TxtPreset2Icon.Text = p2.Icon; TxtPreset2Name.Text = p2.Name; TxtPreset2Values.Text = $"{p2.Brightness}% / {p2.Contrast}%";
            TxtPreset3Icon.Text = p3.Icon; TxtPreset3Name.Text = p3.Name; TxtPreset3Values.Text = $"{p3.Brightness}% / {p3.Contrast}%";
        }

        private async void BrightnessWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // Restaura o contador se já houver um desligamento agendado
            if (ShutdownService.ScheduledAt is not null)
            {
                TimerPanel.Visibility = Visibility.Visible;
                BtnTimerCancel.Visibility = Visibility.Visible;
                StartCountdown();
            }

            TxtStatus.Text = "Lendo monitores...";

            try
            {
                var entries = await Task.Run(() => MonitorService.GetMonitorEntries());

                if (entries.Count == 0)
                {
                    TxtMonitorCount.Text = "Nenhum monitor compatível";
                    TxtStatus.Text = "Monitor não suporta DDC/CI nem WMI";
                    _initialized = true;
                    return;
                }

                bool isWmi = entries.Exists(m => m.IsWmi);
                TxtMonitorCount.Text = isWmi
                    ? "Notebook — controle via WMI"
                    : entries.Count == 1 ? "1 monitor" : $"{entries.Count} monitores";

                BuildMonitorPanels(entries);

                TxtStatus.Text = isWmi ? "Modo notebook — somente brilho disponível"
                                       : "Pronto — arraste os controles";
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Erro: {ex.Message}";
            }

            _initialized = true;
        }

        private void BuildMonitorPanels(List<MonitorEntry> entries)
        {
            PnlMonitors.Children.Clear();
            _monitorControls.Clear();

            for (int i = 0; i < entries.Count; i++)
                PnlMonitors.Children.Add(CreateMonitorRow(entries[i], i > 0));
        }

        private FrameworkElement CreateMonitorRow(MonitorEntry entry, bool addSeparator)
        {
            var container = new StackPanel();

            if (addSeparator)
            {
                var sep = new Border { Height = 1, Margin = new Thickness(0, 6, 0, 8) };
                sep.SetResourceReference(Border.BackgroundProperty, "BorderColor");
                container.Children.Add(sep);
            }

            // Monitor name — editable TextBox styled as label (double-click to rename)
            string displayName = SettingsService.Current.MonitorAliases.TryGetValue(entry.HardwareId, out var alias)
                ? alias : entry.Name;

            var nameEdit = new TextBox
            {
                Text = displayName,
                FontSize = 11, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                IsReadOnly = true,
                Cursor = Cursors.Arrow,
                Padding = new Thickness(1, 0, 1, 0),
                MaxWidth = 150,
                ToolTip = "Duplo clique para renomear"
            };
            nameEdit.SetResourceReference(TextBox.ForegroundProperty, "TextPrimary");

            string hwId = entry.HardwareId;
            string originalName = entry.Name;

            nameEdit.MouseDoubleClick += (_, _) =>
            {
                nameEdit.IsReadOnly = false;
                nameEdit.Cursor = Cursors.IBeam;
                nameEdit.SetResourceReference(TextBox.BorderBrushProperty, "BorderColor");
                nameEdit.BorderThickness = new Thickness(0, 0, 0, 1);
                nameEdit.SelectAll();
                nameEdit.Focus();
            };

            void CommitRename()
            {
                string newName = nameEdit.Text.Trim();
                if (string.IsNullOrEmpty(newName)) newName = originalName;
                nameEdit.Text = newName;
                nameEdit.IsReadOnly = true;
                nameEdit.Cursor = Cursors.Arrow;
                nameEdit.BorderThickness = new Thickness(0);
                if (!string.IsNullOrEmpty(hwId))
                {
                    SettingsService.Current.MonitorAliases[hwId] = newName;
                    SettingsService.Save();
                }
            }

            void CancelRename()
            {
                nameEdit.Text = SettingsService.Current.MonitorAliases.TryGetValue(hwId, out var a)
                    ? a : originalName;
                nameEdit.IsReadOnly = true;
                nameEdit.Cursor = Cursors.Arrow;
                nameEdit.BorderThickness = new Thickness(0);
            }

            nameEdit.LostFocus  += (_, _) => CommitRename();
            nameEdit.KeyDown    += (_, ke) =>
            {
                if (ke.Key == Key.Enter)  { CommitRename(); Keyboard.ClearFocus(); ke.Handled = true; }
                if (ke.Key == Key.Escape) { CancelRename(); Keyboard.ClearFocus(); ke.Handled = true; }
            };

            var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
            var nameIcon = new TextBlock { Text = "🖥", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            nameRow.Children.Add(nameIcon);
            nameRow.Children.Add(nameEdit);
            container.Children.Add(nameRow);

            // Brightness slider
            var sliderB = new Slider
            {
                Minimum = 0, Maximum = 100, Value = entry.Brightness,
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = entry.SupportsBrightness
            };
            var txtB = new TextBlock
            {
                Text = $"{entry.Brightness}%",
                FontSize = 12, FontWeight = FontWeights.Bold, Width = 38,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            txtB.SetResourceReference(TextBlock.ForegroundProperty, "ButtonPrimaryBg");
            container.Children.Add(MakeSliderRow("☀️", sliderB, txtB, new Thickness(0, 0, 0, 6)));

            // Contrast slider (DDC/CI only)
            Slider? sliderC = null;
            TextBlock? txtC = null;

            if (!entry.IsWmi)
            {
                sliderC = new Slider
                {
                    Minimum = 0, Maximum = 100,
                    Value = entry.SupportsContrast ? entry.Contrast : 50,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsEnabled = entry.SupportsContrast
                };
                txtC = new TextBlock
                {
                    Text = entry.SupportsContrast ? $"{entry.Contrast}%" : "N/A",
                    FontSize = 12, FontWeight = FontWeights.Bold, Width = 38,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                txtC.SetResourceReference(TextBlock.ForegroundProperty, "ButtonPrimaryBg");
                container.Children.Add(MakeSliderRow("🌗", sliderC, txtC, new Thickness(0, 0, 0, 4)));
            }
            else
            {
                var note = new TextBlock
                {
                    Text = "Modo notebook — somente brilho",
                    FontSize = 9, Opacity = 0.65, Margin = new Thickness(0, 0, 0, 4)
                };
                note.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                container.Children.Add(note);
            }

            var mc = new MonitorControl
            {
                Index = entry.Index, IsWmi = entry.IsWmi,
                SliderBrightness = sliderB, TxtBrightness = txtB,
                SliderContrast = sliderC, TxtContrast = txtC
            };
            _monitorControls.Add(mc);

            // Events
            sliderB.ValueChanged += async (_, ev) =>
            {
                if (!_initialized) return;
                int val = (int)ev.NewValue;
                txtB.Text = $"{val}%";
                mc.LastBrightnessChange = DateTime.Now;
                var stamp = mc.LastBrightnessChange;
                await Task.Delay(150);
                if (stamp != mc.LastBrightnessChange) return;
                try
                {
                    if (mc.IsWmi) await Task.Run(() => MonitorService.SetWmiBrightness(val));
                    else          await Task.Run(() => MonitorService.SetBrightnessForIndex(mc.Index, val));
                }
                catch (Exception ex) { TxtStatus.Text = $"Erro brilho: {ex.Message}"; }
            };

            if (sliderC != null && txtC != null)
            {
                var capturedTxtC = txtC;
                sliderC.ValueChanged += async (_, ev) =>
                {
                    if (!_initialized || !entry.SupportsContrast) return;
                    int val = (int)ev.NewValue;
                    capturedTxtC.Text = $"{val}%";
                    mc.LastContrastChange = DateTime.Now;
                    var stamp = mc.LastContrastChange;
                    await Task.Delay(150);
                    if (stamp != mc.LastContrastChange) return;
                    try { await Task.Run(() => MonitorService.SetContrastForIndex(mc.Index, val)); }
                    catch (Exception ex) { TxtStatus.Text = $"Erro contraste: {ex.Message}"; }
                };
            }

            return container;
        }

        private static Grid MakeSliderRow(string icon, Slider slider, TextBlock txtValue, Thickness margin)
        {
            var grid = new Grid { Margin = margin };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });

            var ic = new TextBlock { Text = icon, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(ic, 0);
            Grid.SetColumn(slider, 1);
            Grid.SetColumn(txtValue, 2);

            grid.Children.Add(ic);
            grid.Children.Add(slider);
            grid.Children.Add(txtValue);
            return grid;
        }

        private async void ApplyPreset(PresetData preset)
        {
            if (!_initialized) return;

            foreach (var mc in _monitorControls)
            {
                mc.SliderBrightness.Value = preset.Brightness;
                if (mc.SliderContrast != null)
                    mc.SliderContrast.Value = preset.Contrast;
            }

            try
            {
                await Task.Run(() =>
                {
                    MonitorService.SetBrightnessAll(preset.Brightness);
                    MonitorService.SetContrastAll(preset.Contrast);
                });
                TxtStatus.Text = $"Preset \"{preset.Name}\" aplicado";
            }
            catch (Exception ex) { TxtStatus.Text = $"Erro ao aplicar preset: {ex.Message}"; }
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

        private void SliderNightLight_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtNightLightValue == null || ChkNightLight == null) return;
            int value = (int)e.NewValue;
            TxtNightLightValue.Text = $"{value}%";
            if (ChkNightLight.IsChecked == true)
            {
                NightLightService.SetIntensity(value);
                SettingsService.Current.NightLightIntensity = value;
                SettingsService.Save();
            }
        }

        private async void BtnScreenshot_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string? path = await ScreenshotService.CaptureAreaAsync(this);
                if (path != null) TxtStatus.Text = "📸 Captura salva";
            }
            catch (Exception ex) { TxtStatus.Text = $"Erro: {ex.Message}"; }
        }

        // ── Timer de desligamento ─────────────────────────────────────────────

        private System.Windows.Threading.DispatcherTimer? _countdownTimer;

        private void BtnTimerToggle_Click(object sender, RoutedEventArgs e)
        {
            TimerPanel.Visibility = TimerPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        }

        private void TimerPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && int.TryParse(fe.Tag?.ToString(), out int minutes))
                ScheduleShutdown(minutes);
        }

        private void TimerCustom_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(TxtTimerCustom.Text.Trim(), out int minutes) && minutes > 0)
                ScheduleShutdown(minutes);
            else
                TxtStatus.Text = "Digite um número de minutos válido";
        }

        private void TimerCancel_Click(object sender, RoutedEventArgs e)
        {
            if (ShutdownService.Cancel())
            {
                StopCountdown();
                TxtStatus.Text = "Desligamento cancelado";
            }
        }

        private void ScheduleShutdown(int minutes)
        {
            if (ShutdownService.Schedule(minutes))
            {
                BtnTimerCancel.Visibility = Visibility.Visible;
                StartCountdown();
                TxtStatus.Text = $"PC desligará em {minutes} min";
            }
            else
            {
                TxtStatus.Text = "Não foi possível agendar o desligamento";
            }
        }

        private void StartCountdown()
        {
            _countdownTimer ??= new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _countdownTimer.Tick -= Countdown_Tick;
            _countdownTimer.Tick += Countdown_Tick;
            _countdownTimer.Start();
            UpdateCountdownText();
        }

        private void StopCountdown()
        {
            _countdownTimer?.Stop();
            TxtTimerStatus.Text = "";
            BtnTimerCancel.Visibility = Visibility.Collapsed;
        }

        private void Countdown_Tick(object? sender, EventArgs e) => UpdateCountdownText();

        private void UpdateCountdownText()
        {
            if (ShutdownService.ScheduledAt is not { } at) { StopCountdown(); return; }
            var remaining = at - DateTime.Now;
            if (remaining <= TimeSpan.Zero) { TxtTimerStatus.Text = "00:00"; _countdownTimer?.Stop(); return; }
            TxtTimerStatus.Text = remaining.TotalHours >= 1
                ? $"{(int)remaining.TotalHours}:{remaining.Minutes:D2}:{remaining.Seconds:D2}"
                : $"{remaining.Minutes:D2}:{remaining.Seconds:D2}";
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Hide();

        protected override void OnClosing(CancelEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }

        private void Header_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
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
            if (!_capturingHotkey) { base.OnKeyDown(e); return; }

            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            if (key == Key.Escape) { CancelHotkeyCapture(); e.Handled = true; return; }

            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                     or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            { e.Handled = true; return; }

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
