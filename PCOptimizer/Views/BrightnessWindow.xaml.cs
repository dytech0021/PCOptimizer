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
            public bool IsSoftware { get; init; }
            public string DeviceKey { get; init; } = "";
            public int ScreenLeft { get; init; }
            public int ScreenTop { get; init; }
            public int ScreenWidth { get; init; }
            public int ScreenHeight { get; init; }
            public Slider SliderBrightness { get; init; } = null!;
            public TextBlock TxtBrightness { get; init; } = null!;
            public Slider? SliderContrast { get; init; }
            public TextBlock? TxtContrast { get; init; }
            // Throttle "último valor vence": -1 = nada pendente
            public int PendingBrightness = -1;
            public bool BrightnessBusy;
            public int PendingContrast = -1;
            public bool ContrastBusy;
            public bool SupportsHdr { get; init; }
            public bool HdrEnabled { get; set; }
            public uint HdrAdapterIdLow { get; init; }
            public int HdrAdapterIdHigh { get; init; }
            public uint HdrTargetId { get; init; }
        }

        private bool _initialized;
        private readonly List<MonitorControl> _monitorControls = new();
        private bool _capturingHotkey;
        private bool _winNlInitializing;
        private DateTime _lastWinNlChange;

        public BrightnessWindow()
        {
            InitializeComponent();
            Loaded += BrightnessWindow_Loaded;

            // Fade-in + leve deslize sempre que a janela aparece
            IsVisibleChanged += (_, ev) =>
            {
                if (ev.NewValue is not true) return;
                var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1,
                    TimeSpan.FromMilliseconds(220));
                BeginAnimation(OpacityProperty, fade);
                if (Content is FrameworkElement root)
                {
                    var tt = new TranslateTransform();
                    root.RenderTransform = tt;
                    var slide = new System.Windows.Media.Animation.DoubleAnimation(14, 0,
                        TimeSpan.FromMilliseconds(220))
                    { EasingFunction = new System.Windows.Media.Animation.CubicEase() };
                    tt.BeginAnimation(TranslateTransform.YProperty, slide);
                }
            };
            TxtHotkey.Text = SettingsService.Current.HotkeyDisplay;
            RefreshPresetButtons();
            SliderNightLight.Value = SettingsService.Current.NightLightIntensity;
            if (SettingsService.Current.NightLightEnabled)
                ChkNightLight.IsChecked = true;

            _winNlInitializing = true;
            bool winNlOn = NightLightService.GetWindowsNightLightEnabled();
            ChkWinNightLight.IsChecked = winNlOn;
            if (winNlOn)
            {
                WinNightLightPanel.Visibility = Visibility.Visible;
                int winNlIntensity = NightLightService.GetWindowsNightLightIntensity();
                SliderWinNightLight.Value = winNlIntensity;
                TxtWinNightLightValue.Text = $"{winNlIntensity}%";
            }
            _winNlInitializing = false;
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

                bool isWmi = entries.TrueForAll(m => m.IsWmi);
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

            // Contrast slider (DDC/CI only) — não existe em WMI nem no modo software
            Slider? sliderC = null;
            TextBlock? txtC = null;

            if (!entry.IsWmi && !entry.IsSoftware)
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
            else if (entry.IsWmi)
            {
                var note = new TextBlock
                {
                    Text = "Painel do notebook — somente brilho",
                    FontSize = 9, Opacity = 0.65, Margin = new Thickness(0, 0, 0, 4)
                };
                note.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                container.Children.Add(note);
            }

            // Monitor sem DDC/CI nem WMI: brilho por software (escurecimento via overlay).
            if (entry.IsSoftware)
            {
                var swNote = new TextBlock
                {
                    Text = "🖌 Brilho por software (escurece a imagem) — DDC/CI indisponível. " +
                           "Para brilho real do backlight, ative \"DDC/CI\" no menu (OSD) do monitor.",
                    FontSize = 9, Opacity = 0.85, Margin = new Thickness(0, 0, 0, 4),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xF9, 0xE2, 0xAF))
                };
                container.Children.Add(swNote);
            }

            var mc = new MonitorControl
            {
                Index = entry.Index, IsWmi = entry.IsWmi,
                IsSoftware   = entry.IsSoftware,
                DeviceKey    = entry.DeviceKey,
                ScreenLeft   = entry.ScreenLeft,
                ScreenTop    = entry.ScreenTop,
                ScreenWidth  = entry.ScreenWidth,
                ScreenHeight = entry.ScreenHeight,
                SliderBrightness = sliderB, TxtBrightness = txtB,
                SliderContrast   = sliderC, TxtContrast   = txtC,
                SupportsHdr      = entry.SupportsHdr,
                HdrEnabled       = entry.HdrEnabled,
                HdrAdapterIdLow  = entry.HdrAdapterIdLow,
                HdrAdapterIdHigh = entry.HdrAdapterIdHigh,
                HdrTargetId      = entry.HdrTargetId
            };
            _monitorControls.Add(mc);

            if (entry.SupportsHdr)
                container.Children.Add(MakeHdrButton(mc));

            // Events — aplica o primeiro valor NA HORA; durante o arraste, envia
            // sempre o valor mais recente assim que o anterior termina (sem debounce)
            sliderB.ValueChanged += async (_, ev) =>
            {
                if (!_initialized) return;
                int val = (int)ev.NewValue;
                txtB.Text = $"{val}%";
                mc.PendingBrightness = val;
                if (mc.BrightnessBusy) return;
                mc.BrightnessBusy = true;
                try
                {
                    while (mc.PendingBrightness >= 0)
                    {
                        int v = mc.PendingBrightness;
                        mc.PendingBrightness = -1;
                        if (mc.IsWmi)
                            await Task.Run(() => MonitorService.SetWmiBrightness(v));
                        else if (mc.IsSoftware)
                            // Overlay roda na thread de UI (operação instantânea)
                            SoftwareBrightnessService.SetBrightness(
                                mc.DeviceKey, mc.ScreenLeft, mc.ScreenTop,
                                mc.ScreenWidth, mc.ScreenHeight, v);
                        else
                            await Task.Run(() => MonitorService.SetBrightnessForIndex(mc.Index, v));
                    }
                }
                catch (Exception ex) { TxtStatus.Text = $"Erro brilho: {ex.Message}"; }
                finally { mc.BrightnessBusy = false; }
            };

            if (sliderC != null && txtC != null)
            {
                var capturedTxtC = txtC;
                sliderC.ValueChanged += async (_, ev) =>
                {
                    if (!_initialized || !entry.SupportsContrast) return;
                    int val = (int)ev.NewValue;
                    capturedTxtC.Text = $"{val}%";
                    mc.PendingContrast = val;
                    if (mc.ContrastBusy) return;
                    mc.ContrastBusy = true;
                    try
                    {
                        while (mc.PendingContrast >= 0)
                        {
                            int v = mc.PendingContrast;
                            mc.PendingContrast = -1;
                            await Task.Run(() => MonitorService.SetContrastForIndex(mc.Index, v));
                        }
                    }
                    catch (Exception ex) { TxtStatus.Text = $"Erro contraste: {ex.Message}"; }
                    finally { mc.ContrastBusy = false; }
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

            // Debounce: evita gravar no registro + broadcast a cada pixel do arraste
            _lastWinNlChange = DateTime.Now;
            var stamp = _lastWinNlChange;
            await Task.Delay(150);
            if (stamp != _lastWinNlChange) return;
            await Task.Run(() => NightLightService.SetWindowsNightLightIntensity(value));
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

        // ── HDR toggle ────────────────────────────────────────────────────────

        private Button MakeHdrButton(MonitorControl mc)
        {
            var btn = new Button
            {
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0),
                Padding = new Thickness(12, 5, 12, 5),
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ToolTip = "Alternar HDR (High Dynamic Range)"
            };
            ApplyHdrButtonStyle(btn, mc.HdrEnabled);

            btn.Click += async (_, _) =>
            {
                bool newState = !mc.HdrEnabled;
                bool ok = await Task.Run(() =>
                    HdrService.SetHdrEnabled(mc.HdrAdapterIdLow, mc.HdrAdapterIdHigh, mc.HdrTargetId, newState));
                if (ok)
                {
                    mc.HdrEnabled = newState;
                    ApplyHdrButtonStyle(btn, newState);
                    TxtStatus.Text = newState ? "HDR ativado" : "HDR desativado";
                }
                else
                {
                    TxtStatus.Text = "Não foi possível alterar o HDR";
                }
            };
            return btn;
        }

        private static void ApplyHdrButtonStyle(Button btn, bool enabled)
        {
            btn.Content     = enabled ? "HDR: Ligado" : "HDR: Desligado";
            btn.Background  = new SolidColorBrush(enabled
                ? Color.FromRgb(0x1B, 0x4E, 0x2D) : Color.FromRgb(0x1B, 0x3A, 0x4E));
            btn.Foreground  = new SolidColorBrush(enabled
                ? Color.FromRgb(0xA6, 0xE3, 0xA1) : Color.FromRgb(0x89, 0xB4, 0xFA));
            btn.BorderBrush = new SolidColorBrush(enabled
                ? Color.FromRgb(0x2A, 0x5E, 0x3A) : Color.FromRgb(0x2A, 0x4A, 0x5E));
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
