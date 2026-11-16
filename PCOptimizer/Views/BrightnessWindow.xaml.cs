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
    // As outras partes desta janela ficam em BrightnessWindow.*.cs, uma por assunto.
    public partial class BrightnessWindow : Window
    {
        private sealed class MonitorControl
        {
            public int Index { get; init; }
            public bool IsWmi { get; init; }
            public bool IsSoftware { get; init; }
            // Identidade estável do monitor — indexa o overlay de brilho.
            public string OverlayKey { get; init; } = "";
            // \\.\DISPLAYn — o único aceito pelas APIs de resolução.
            public string GdiDevice { get; init; } = "";
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
