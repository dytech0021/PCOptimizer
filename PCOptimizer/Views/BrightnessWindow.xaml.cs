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
            public string HardwareId { get; init; } = "";
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
        private int _winNlSerial;

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

                // A janela é reutilizada com Hide/Show: reenumera os monitores a
                // cada reabertura para refletir telas plugadas/removidas nesse meio
                // tempo (na primeira exibição, _initialized=false e o Loaded cuida).
                if (_initialized) _ = ReloadMonitorsAsync();
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

            // Seções recolhidas por padrão para a janela ficar limpa — mas abrem
            // sozinhas quando há algo ativo ali dentro, senão o usuário não teria
            // como perceber (nem desfazer) o que está valendo.
            if (SettingsService.Current.NightLightEnabled || winNlOn)
            {
                NightSection.Visibility = Visibility.Visible;
                TxtNightArrow.Text = "▾";
            }
            if (SettingsService.Current.DisabledMonitors.Count > 0 ||
                RemoteAccessService.IsActive)
            {
                DisplaysPanel.Visibility = Visibility.Visible;
                TxtDisplaysArrow.Text = "▾";
                BuildDisplayToggles();
            }

            InitAdvColor();
        }

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

        private void NightSection_Toggle(object sender, MouseButtonEventArgs e)
        {
            bool show = NightSection.Visibility != Visibility.Visible;
            NightSection.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            TxtNightArrow.Text = show ? "▾" : "▸";
        }

        private void Displays_Toggle(object sender, MouseButtonEventArgs e)
        {
            bool show = DisplaysPanel.Visibility != Visibility.Visible;
            DisplaysPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            TxtDisplaysArrow.Text = show ? "▾" : "▸";
            if (show) BuildDisplayToggles();
        }

        /// <summary>
        /// Uma linha por saída de vídeo, com botão de ligar/desligar. Usa a lista
        /// que enxerga também as telas desanexadas — senão não haveria como
        /// reativar o que foi desligado.
        /// </summary>
        private void BuildDisplayToggles()
        {
            PnlDisplayToggles.Children.Clear();
            var devices = MonitorEnableService.ListAll();

            foreach (var d in devices)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var name = new TextBlock
                {
                    Text = d.Description + (d.Primary ? "  (principal)" : ""),
                    FontSize = 11, FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 175
                };
                name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
                var sub = new TextBlock
                {
                    Text = d.Attached ? "Ativa" : "Desativada",
                    FontSize = 9, Opacity = 0.7
                };
                sub.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                label.Children.Add(name);
                label.Children.Add(sub);
                row.Children.Add(label);

                var btn = new Button
                {
                    Content = d.Attached ? "Desativar" : "Ativar",
                    FontSize = 10, Padding = new Thickness(12, 5, 12, 5),
                    Cursor = Cursors.Hand, BorderThickness = new Thickness(1),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(btn, 1);
                if (d.Attached)
                {
                    btn.Background  = Brushes.Transparent;
                    btn.Foreground  = new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8));
                    btn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x5A, 0x2A, 0x35));
                }
                else
                {
                    btn.Background  = new SolidColorBrush(Color.FromRgb(0x16, 0x33, 0x22));
                    btn.Foreground  = new SolidColorBrush(Color.FromRgb(0x86, 0xEF, 0xAC));
                    btn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x1C, 0x51, 0x38));
                }

                string dev = d.Device;
                bool attached = d.Attached;
                btn.Click += async (_, _) =>
                {
                    btn.IsEnabled = false;
                    TxtStatus.Text = attached ? "Desativando monitor..." : "Reativando monitor...";

                    // Anexar/desanexar mexe no vídeo: preserva o HDR das outras
                    // telas e cala a correção automática de cor.
                    var hdrWasOn = HdrService.SnapshotHdrOnTargets();
                    RemoteAccessService.SuppressAutoFixFor(20);

                    var r = await Task.Run(() => attached
                        ? MonitorEnableService.Disable(dev)
                        : MonitorEnableService.Enable(dev));
                    TxtStatus.Text = r.Message;

                    await Task.Delay(1500);      // o vídeo precisa assentar
                    await Task.Run(() => HdrService.RestoreHdrOnTargets(hdrWasOn));
                    BuildDisplayToggles();
                    await ReloadMonitorsAsync();
                    btn.IsEnabled = true;
                };
                row.Children.Add(btn);
                PnlDisplayToggles.Children.Add(row);
            }
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

            await ReloadMonitorsAsync();
        }

        private bool _reloadingMonitors;

        private async Task ReloadMonitorsAsync()
        {
            if (_reloadingMonitors) return;
            _reloadingMonitors = true;
            TxtStatus.Text = "Lendo monitores...";

            try
            {
                var entries = await Task.Run(() => MonitorService.GetMonitorEntries());

                if (entries.Count == 0)
                {
                    TxtMonitorCount.Text = "Nenhum monitor compatível";
                    TxtStatus.Text = "Monitor não suporta DDC/CI nem WMI";
                    return;
                }

                bool isWmi = entries.TrueForAll(m => m.IsWmi);
                TxtMonitorCount.Text = isWmi
                    ? "Notebook — controle via WMI"
                    : entries.Count == 1 ? "1 monitor" : $"{entries.Count} monitores";

                BuildMonitorPanels(entries);
                SoftwareBrightnessService.SynchronizeMonitors(entries);

                TxtStatus.Text = isWmi ? "Modo notebook — somente brilho disponível"
                                       : "Pronto — arraste os controles";
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Erro: {ex.Message}";
            }
            finally
            {
                _initialized = true;
                _reloadingMonitors = false;
                UpdateRemoteModeButton();
            }
        }

        // ── Modo Acesso Remoto: 1 tela + 1080p + sem HDR, num botão só ───────

        private void UpdateRemoteModeButton()
        {
            if (RemoteAccessService.IsActive)
            {
                // Enquanto ativo, o botão de sair NUNCA some — inclusive após
                // reiniciar o PC no modo remoto.
                RemoteRow.Visibility = Visibility.Visible;
                BtnRemoteMode.Content = "↩ Sair do Modo Acesso Remoto";
                return;
            }

            // Só aparece quando há o que preparar: mais de uma tela ativa ou
            // resolução diferente de 1080p (no PC de acesso comum, fica oculto).
            var cur = DisplayResolutionService.GetCurrent();
            bool hasWork = MonitorTopologyService.ActiveScreenCount() > 1
                        || (cur != null && (cur.Value.W != 1920 || cur.Value.H != 1080));
            RemoteRow.Visibility = hasWork ? Visibility.Visible : Visibility.Collapsed;
            if (hasWork)
                BtnRemoteMode.Content = "🖥 Modo Acesso Remoto (1 tela · 1080p · sem HDR)";
        }

        private void BtnRemoteTune_Click(object sender, RoutedEventArgs e)
        {
            var win = new RemoteTuneWindow { Owner = this };
            win.ShowDialog();
            UpdateRemoteModeButton();
        }

        private async void BtnRemoteMode_Click(object sender, RoutedEventArgs e)
        {
            BtnRemoteMode.IsEnabled = false;
            try
            {
                if (!RemoteAccessService.IsActive)
                {
                    var c = MessageBox.Show(
                        "Ativar o Modo Acesso Remoto?\n\n" +
                        "Em todos os casos: fica só a tela principal, resolução " +
                        "1920×1080 (16:9) e as cores automáticas do Windows são " +
                        "desligadas (é o que deixa a imagem remota saturada).\n\n" +
                        "SIM = também desliga o HDR (religa na saída)\n" +
                        "NÃO = mantém o HDR ligado — escolha esta se a imagem no " +
                        "AnyDesk ficar saturada ou escura com o HDR desligado\n" +
                        "CANCELAR = não fazer nada\n\n" +
                        "Tudo é revertido por este mesmo botão. Emergência: Win+P.",
                        "PC Optimizer", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                    if (c == MessageBoxResult.Cancel) return;

                    TxtStatus.Text = "Ativando modo acesso remoto...";
                    TxtStatus.Text = await RemoteAccessService.EnterAsync(
                        keepHdr: c == MessageBoxResult.No);
                }
                else
                {
                    TxtStatus.Text = "Restaurando a configuração normal...";
                    TxtStatus.Text = await RemoteAccessService.ExitAsync();
                }

                // Dá um instante para o vídeo assentar e re-enumera — painel e
                // overlays passam a refletir a configuração real.
                await Task.Delay(1500);
                await ReloadMonitorsAsync();
            }
            finally
            {
                BtnRemoteMode.IsEnabled = true;
                UpdateRemoteModeButton();
            }
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

            // Com HDR ativo, o monitor ignora brilho DDC/CI e gamma ramp — o controle
            // real é o "brilho do conteúdo SDR" do Windows. Lê o valor atual para o
            // slider partir do ponto certo.
            int hdrSdrPct = entry.HdrSdrBrightness;

            // Brightness slider
            var sliderB = new Slider
            {
                Minimum = 0, Maximum = 100,
                Value = hdrSdrPct >= 0 ? hdrSdrPct : entry.Brightness,
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = entry.SupportsBrightness || hdrSdrPct >= 0
            };
            var txtB = new TextBlock
            {
                Text = $"{(hdrSdrPct >= 0 ? hdrSdrPct : entry.Brightness)}%",
                FontSize = 12, FontWeight = FontWeights.Bold, Width = 38,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            txtB.SetResourceReference(TextBlock.ForegroundProperty, "ButtonPrimaryBg");
            container.Children.Add(MakeSliderRow("☀️", sliderB, txtB, new Thickness(0, 0, 0, 6)));

            if (entry.HdrEnabled)
            {
                var hdrNote = new TextBlock
                {
                    Text = "HDR ativo — o brilho ajusta o conteúdo SDR (o mesmo controle " +
                           "das Configurações do Windows; o monitor ignora o brilho comum em HDR)",
                    FontSize = 9, Opacity = 0.85, Margin = new Thickness(0, 0, 0, 4),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x89, 0xB4, 0xFA))
                };
                container.Children.Add(hdrNote);
            }

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
                HardwareId   = entry.HardwareId,
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

            // Ultrawide (21:9 e mais largos): oferece o modo 16:9 com barras
            // pretas, usado em CS/R6 e afins. A checagem é pela resolução NATIVA
            // do painel — pela atual, o botão sumia assim que o 16:9 era aplicado
            // (a tela deixa de ser "larga") e não dava mais para reverter.
            if (DisplayResolutionService.IsUltrawidePanel(mc.DeviceKey)
                || FindGameArKey(mc.HardwareId) != null)
                container.Children.Add(MakeGameArButton(mc));

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
                        // HDR ativo: ajusta o brilho do conteúdo SDR (DDC/gamma são
                        // ignorados pelo monitor em HDR). Se a chamada falhar
                        // (Windows antigo), cai nos caminhos normais abaixo.
                        if (mc.HdrEnabled && await Task.Run(() => HdrService.SetSdrBrightness(
                                mc.HdrAdapterIdLow, mc.HdrAdapterIdHigh, mc.HdrTargetId, v)))
                            continue;
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

        private async void BtnMonitorsOff_Click(object sender, RoutedEventArgs e)
        {
            TxtStatus.Text = "Desligando em 3s — pare de mexer o mouse...";
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            await MonitorPowerService.TurnOffAsync(hwnd);
            TxtStatus.Text = "Monitores desligados — mexa o mouse ou tecle para religar";
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
            // Limite de 30 dias: acima disso minutes*60 estoura o int no
            // ShutdownService e o shutdown.exe rejeita o /t, mas a UI mostraria
            // uma contagem regressiva de um desligamento que nunca foi agendado.
            if (int.TryParse(TxtTimerCustom.Text.Trim(), out int minutes)
                && minutes > 0 && minutes <= 43_200)
                ScheduleShutdown(minutes);
            else
                TxtStatus.Text = "Digite minutos entre 1 e 43200 (30 dias)";
        }

        private void TimerCancel_Click(object sender, RoutedEventArgs e)
        {
            if (ShutdownService.Cancel())
            {
                StopCountdown();
                TxtStatus.Text = "Desligamento cancelado";
            }
        }

        private async void ScheduleShutdown(int minutes)
        {
            // Fora da thread de UI: Schedule espera o shutdown.exe /a terminar
            // (até 5 s), e isso congelava a janela ao clicar num preset de timer.
            TxtStatus.Text = "Agendando...";
            bool ok = await Task.Run(() => ShutdownService.Schedule(minutes));
            if (ok)
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
                btn.IsEnabled = false;
                bool ok;
                try
                {
                    ok = await HdrService.SetHdrEnabledVerifiedAsync(
                        mc.HdrAdapterIdLow, mc.HdrAdapterIdHigh, mc.HdrTargetId, newState);
                }
                finally { btn.IsEnabled = true; }
                if (ok)
                {
                    mc.HdrEnabled = newState;
                    ApplyHdrButtonStyle(btn, newState);
                    TxtStatus.Text = newState ? "HDR ativado" : "HDR desativado";

                    // O modo de controle de brilho muda junto com o HDR (SDR white
                    // level vs DDC) — espera a troca de modo assentar e reconstrói
                    // as linhas para o slider partir do valor certo.
                    _ = ReloadMonitorsAsync();
                }
                else
                {
                    TxtStatus.Text = "Não foi possível alterar o HDR";
                }
            };
            return btn;
        }

        /// <summary>
        /// Modo 16:9 com barras pretas para jogos competitivos (CS, R6…) em
        /// monitores ultrawide: troca a resolução para o melhor modo 16:9 e pede
        /// ao monitor, por DDC/CI, para NÃO esticar a imagem — o que sobra das
        /// laterais fica preto. Reversível pelo mesmo botão.
        /// </summary>
        /// <summary>
        /// Acha a chave salva desse monitor. Além da correspondência exata, aceita
        /// a mesma base antes do "#": o sufixo de desempate de monitores iguais
        /// mudou entre versões, e sem isso o estado salvo ficava órfão — o botão
        /// tentava aplicar de novo em vez de reverter.
        /// </summary>
        private static string? FindGameArKey(string hardwareId)
        {
            var d = SettingsService.Current.GameArPrevMode;
            if (string.IsNullOrEmpty(hardwareId)) return null;
            if (d.ContainsKey(hardwareId)) return hardwareId;

            string baseId = hardwareId.Split('#')[0];
            foreach (var k in d.Keys)
                if (k.Split('#')[0].Equals(baseId, StringComparison.OrdinalIgnoreCase))
                    return k;
            return null;
        }

        private Button MakeGameArButton(MonitorControl mc)
        {
            var btn = new Button
            {
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 6, 0, 2),
                Padding = new Thickness(12, 9, 12, 9),
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ToolTip = "Joga em 16:9 com barras pretas nas laterais (CS, R6…)"
            };

            bool active = FindGameArKey(mc.HardwareId) != null;
            ApplyGameArButtonStyle(btn, active);

            btn.Click += async (_, _) =>
            {
                btn.IsEnabled = false;
                try
                {
                    // Trocar de resolução faz o Windows reaplicar a configuração
                    // daquele modo e pode derrubar o HDR. Anota o que estava ligado
                    // para devolver no fim — o 16:9 é sobre proporção, não sobre cor.
                    var hdrWasOn = HdrService.SnapshotHdrOnTargets();
                    RemoteAccessService.SuppressAutoFixFor(15);

                    var s = SettingsService.Current;
                    string? savedKey = FindGameArKey(mc.HardwareId);
                    if (savedKey == null)
                    {
                        var target = DisplayResolutionService.FindBest169(mc.DeviceKey);
                        if (target == null)
                        {
                            TxtStatus.Text = "Este monitor não oferece um modo 16:9";
                            return;
                        }
                        var cur = DisplayResolutionService.GetCurrentFor(mc.DeviceKey);
                        if (cur == null) { TxtStatus.Text = "Não consegui ler a resolução"; return; }

                        TxtStatus.Text = $"Aplicando {target.Value.W}×{target.Value.H}...";
                        bool ok = await Task.Run(() => DisplayResolutionService.SetFor(
                            mc.DeviceKey, target.Value.W, target.Value.H, cur.Value.Hz));
                        if (!ok) { TxtStatus.Text = "O monitor não aceitou o modo 16:9"; return; }

                        s.GameArPrevMode[mc.HardwareId] = $"{cur.Value.W}x{cur.Value.H}x{cur.Value.Hz}";
                        SettingsService.Save();

                        // Pede ao monitor para preservar a proporção (barras pretas).
                        bool ddc = await Task.Run(() => MonitorService.SetAspectScaling(mc.Index, true));
                        TxtStatus.Text = ddc
                            ? $"16:9 ({target.Value.W}×{target.Value.H}) com barras pretas"
                            : $"16:9 aplicado. Se esticar, ajuste a proporção no menu do " +
                              "monitor ou no painel da placa de vídeo";
                        ApplyGameArButtonStyle(btn, true);
                    }
                    else
                    {
                        string prev = s.GameArPrevMode[savedKey];
                        var p = prev.Split('x');
                        bool ok = false;
                        if (p.Length == 3 && int.TryParse(p[0], out int w) &&
                            int.TryParse(p[1], out int h) && int.TryParse(p[2], out int hz))
                        {
                            TxtStatus.Text = "Voltando ao ultrawide...";
                            ok = await Task.Run(() => DisplayResolutionService.SetFor(mc.DeviceKey, w, h, hz));
                            await Task.Run(() => MonitorService.SetAspectScaling(mc.Index, false));
                        }
                        else
                        {
                            // Registro corrompido: volta para a resolução NATIVA
                            var nat = DisplayResolutionService.GetNative(mc.DeviceKey);
                            if (nat != null)
                                ok = await Task.Run(() => DisplayResolutionService.SetFor(
                                    mc.DeviceKey, nat.Value.W, nat.Value.H));
                        }

                        s.GameArPrevMode.Remove(savedKey);
                        SettingsService.Save();
                        TxtStatus.Text = ok
                            ? "Resolução ultrawide restaurada"
                            : "⚠ Não consegui restaurar — use Configurações → Vídeo";
                        ApplyGameArButtonStyle(btn, false);
                    }

                    await Task.Delay(1500);

                    // Devolve o HDR que a troca de resolução tenha derrubado
                    int back = await Task.Run(() => HdrService.RestoreHdrOnTargets(hdrWasOn));
                    if (back > 0) await Task.Delay(1200);

                    await ReloadMonitorsAsync();
                }
                finally { btn.IsEnabled = true; }
            };
            return btn;
        }

        private static void ApplyGameArButtonStyle(Button btn, bool active)
        {
            btn.Content = active ? "🎮 Voltar ao ultrawide (21:9)"
                                 : "🎮 Jogar em 16:9 (barras pretas)";
            if (active)
            {
                // Ativo em laranja forte: é o estado em que o usuário precisa
                // achar o botão rápido para voltar ao normal.
                btn.Background  = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
                btn.Foreground  = new SolidColorBrush(Color.FromRgb(0x1A, 0x12, 0x06));
                btn.BorderBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
            }
            else
            {
                btn.Background  = new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B));
                btn.Foreground  = new SolidColorBrush(Color.FromRgb(0x93, 0xC5, 0xFD));
                btn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x4A, 0x6B));
            }
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
