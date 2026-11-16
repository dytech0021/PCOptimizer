using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using PCOptimizer.Services;
using PCOptimizer.Views;

namespace PCOptimizer
{
    // Parte da janela principal: tema claro/escuro e cor da barra de título.
    public partial class MainWindow
    {
        private void BtnThemeToggle_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.Toggle();
            ApplyTitleBarColor();
            Log($"🎨 Tema alterado para {(ThemeManager.Current == AppTheme.Dark ? "escuro" : "claro")}");
        }

        // ── Barra de título nativa acompanha a cor do tema ─────────────────
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20; // Win10 1809+
        private const int DWMWA_CAPTION_COLOR = 35;           // Win11 — COLORREF 0x00BBGGRR

        private void ApplyTitleBarColor()
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                bool dark = ThemeManager.Current == AppTheme.Dark;
                int darkMode = dark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

                // Mesma cor do WindowBg de cada tema (formato COLORREF: 0x00BBGGRR):
                // preto puro no escuro, #EEF1F4 no claro.
                int caption = dark ? 0x000000 : 0xF4F1EE;
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            }
            catch { /* DWM indisponível (Win10 antigo) — barra fica padrão */ }
        }
    }
}
