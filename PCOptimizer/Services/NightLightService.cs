using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Luz noturna via camada de sobreposição (overlay) transparente e click-through.
    /// Funciona em qualquer PC — não depende de SetDeviceGammaRamp, que falha na
    /// maioria das placas de vídeo modernas.
    /// </summary>
    public static class NightLightService
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_TOOLWINDOW = 0x80;
        private const int WS_EX_LAYERED = 0x80000;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private static Window? _overlay;

        /// <summary>
        /// Aplica o filtro. intensity: 0 = desligado, 100 = máximo (tela bem alaranjada).
        /// </summary>
        public static void SetIntensity(int intensity)
        {
            // Garante execução na thread de UI
            if (Application.Current == null) return;
            if (!Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.Invoke(() => SetIntensity(intensity));
                return;
            }

            if (intensity <= 0)
            {
                Reset();
                return;
            }

            EnsureOverlay();

            // intensity 0-100 → opacidade 0 a 0.55 (acima disso fica ilegível)
            _overlay!.Opacity = (intensity / 100.0) * 0.55;

            if (!_overlay.IsVisible)
                _overlay.Show();

            RepositionToVirtualScreen();
        }

        private static void EnsureOverlay()
        {
            if (_overlay != null) return;

            _overlay = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                // Tom âmbar quente característico de luz noturna
                Background = new SolidColorBrush(Color.FromRgb(255, 130, 20)),
                ShowInTaskbar = false,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                IsHitTestVisible = false,
                Focusable = false,
                ShowActivated = false,
                Title = "NightLightOverlay"
            };

            _overlay.SourceInitialized += (s, e) =>
            {
                var hwnd = new WindowInteropHelper(_overlay).Handle;
                int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                // Click-through + sem ativar + não aparece no Alt+Tab
                SetWindowLong(hwnd, GWL_EXSTYLE,
                    ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            };

            RepositionToVirtualScreen();
        }

        private static void RepositionToVirtualScreen()
        {
            if (_overlay == null) return;
            // Cobre TODOS os monitores (área virtual completa)
            _overlay.Left = SystemParameters.VirtualScreenLeft;
            _overlay.Top = SystemParameters.VirtualScreenTop;
            _overlay.Width = SystemParameters.VirtualScreenWidth;
            _overlay.Height = SystemParameters.VirtualScreenHeight;
        }

        /// <summary>
        /// Desliga a luz noturna.
        /// </summary>
        public static void Reset()
        {
            if (Application.Current == null) return;
            if (!Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.Invoke(Reset);
                return;
            }

            _overlay?.Hide();
        }

        // ── Luz Noturna nativa do Windows (via registro CloudStore) ───────────

        private const string WinNlStatePath =
            @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.bluelightreductionstate\Current";
        private const string WinNlSettingsPath =
            @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.settings\Current";

        public static bool GetWindowsNightLightEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(WinNlStatePath);
                if (key?.GetValue("Data") is byte[] data && data.Length > 24)
                    return data[24] == 0x15;
            }
            catch { }
            return false;
        }

        public static bool SetWindowsNightLight(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(WinNlStatePath, writable: true);
                if (key?.GetValue("Data") is not byte[] data || data.Length <= 24) return false;
                data[24] = enabled ? (byte)0x15 : (byte)0x12;
                key.SetValue("Data", data, RegistryValueKind.Binary);
                return true;
            }
            catch { return false; }
        }

        public static int GetWindowsNightLightIntensity()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(WinNlSettingsPath);
                if (key?.GetValue("Data") is not byte[] data) return 50;
                // CF 28 pattern → next 2 bytes are temperature LE (0=off, 4803=maximum warmth)
                for (int i = 0; i < data.Length - 3; i++)
                {
                    if (data[i] == 0xCF && data[i + 1] == 0x28)
                    {
                        int raw = data[i + 2] | (data[i + 3] << 8);
                        return Math.Clamp((int)Math.Round(raw * 100.0 / 4803), 0, 100);
                    }
                }
            }
            catch { }
            return 50;
        }

        public static bool SetWindowsNightLightIntensity(int percent)
        {
            try
            {
                percent = Math.Clamp(percent, 0, 100);
                using var key = Registry.CurrentUser.OpenSubKey(WinNlSettingsPath, writable: true);
                if (key?.GetValue("Data") is not byte[] data) return false;
                int raw = (int)Math.Round(percent * 4803.0 / 100);
                for (int i = 0; i < data.Length - 3; i++)
                {
                    if (data[i] == 0xCF && data[i + 1] == 0x28)
                    {
                        data[i + 2] = (byte)(raw & 0xFF);
                        data[i + 3] = (byte)(raw >> 8);
                        key.SetValue("Data", data, RegistryValueKind.Binary);
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }
    }
}
