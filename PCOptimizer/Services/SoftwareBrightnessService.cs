using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Brilho por software para monitores que não respondem a DDC/CI nem WMI
    /// (típico de monitor simples ligado por HDMI num desktop). Sobrepõe uma
    /// camada preta semitransparente e click-through SOBRE a área daquele monitor,
    /// escurecendo a imagem sem depender do hardware. É o mesmo princípio do overlay
    /// de luz noturna deste app, que já evita SetDeviceGammaRamp por ele falhar na
    /// maioria das GPUs modernas.
    ///
    /// Posicionamento: o app é PerMonitorV2, então rcMonitor (GetMonitorInfo) e
    /// SetWindowPos compartilham o mesmo espaço em pixels físicos — posicionamos
    /// direto, sem conversão de DPI.
    ///
    /// Limitação: só escurece (não aumenta o backlight) e pode ficar coberto por
    /// jogos em tela cheia exclusiva. Em janela/borderless funciona normalmente.
    /// </summary>
    public static class SoftwareBrightnessService
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_TOOLWINDOW = 0x80;
        private const int WS_EX_LAYERED = 0x80000;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        // Opacidade máxima do escurecimento (a 0% de brilho) — nunca preto total,
        // para o usuário sempre conseguir ver e voltar a subir o brilho.
        private const double MaxDim = 0.85;

        private sealed class Overlay
        {
            public Window Window = null!;
            public int Percent = 100;
        }

        private static readonly object _lock = new();
        private static readonly Dictionary<string, Overlay> _overlays =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Último brilho de software aplicado a esse monitor (100 = sem escurecimento).</summary>
        public static int GetBrightness(string key)
        {
            lock (_lock)
                return _overlays.TryGetValue(key, out var o) ? o.Percent : 100;
        }

        /// <summary>
        /// Ajusta o escurecimento de software do monitor identificado por <paramref name="key"/>,
        /// posicionado em (left, top) com (width, height) em pixels físicos.
        /// percent 100 = sem escurecimento (overlay oculto); 0 = escurecimento máximo.
        /// </summary>
        public static bool SetBrightness(string key, int left, int top, int width, int height, int percent)
        {
            if (Application.Current == null) return false;
            if (!Application.Current.Dispatcher.CheckAccess())
                return Application.Current.Dispatcher.Invoke(
                    () => SetBrightness(key, left, top, width, height, percent));

            percent = Math.Clamp(percent, 0, 100);

            Overlay ov;
            lock (_lock)
            {
                if (!_overlays.TryGetValue(key, out ov!))
                {
                    ov = new Overlay { Window = CreateOverlay() };
                    _overlays[key] = ov;
                }
            }

            ov.Percent = percent;

            // Brilho cheio: nada a escurecer — esconde o overlay.
            if (percent >= 100)
            {
                ov.Window.Hide();
                return true;
            }

            ov.Window.Opacity = (100 - percent) / 100.0 * MaxDim;
            if (!ov.Window.IsVisible) ov.Window.Show();

            // Posiciona em pixels físicos sobre o monitor-alvo (mantém no topo,
            // sem roubar o foco). SetWindowPos é autoritativo após o Show do WPF.
            var hwnd = new WindowInteropHelper(ov.Window).Handle;
            SetWindowPos(hwnd, HWND_TOPMOST, left, top, width, height,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
            return true;
        }

        private static Window CreateOverlay()
        {
            var w = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Black,
                ShowInTaskbar = false,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                IsHitTestVisible = false,
                Focusable = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 0, Top = 0, Width = 1, Height = 1,
                Title = "PCOptimizerDimOverlay"
            };

            w.SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                // Click-through + não ativa + fora do Alt+Tab
                SetWindowLong(hwnd, GWL_EXSTYLE,
                    ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            };

            return w;
        }

        /// <summary>Remove todo o escurecimento de software (todos os monitores).</summary>
        public static void ResetAll()
        {
            if (Application.Current == null) return;
            if (!Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.Invoke(ResetAll);
                return;
            }
            lock (_lock)
            {
                foreach (var o in _overlays.Values)
                {
                    o.Window.Hide();
                    o.Percent = 100;
                }
            }
        }
    }
}
