using System;
using System.Collections.Generic;
using System.Threading;
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

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SendNotifyMessage(IntPtr hWnd, uint msg, UIntPtr wParam, string? lParam);

        private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
        private const uint WM_SETTINGCHANGE = 0x001A;

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
        //
        // O Windows guarda a luz noturna em um blob binário (formato Microsoft Bond).
        // Estrutura conhecida (Win10 1903+ / Win11):
        //   Estado (bluelightreductionstate):
        //     data[18]      = 0x15 ligado, 0x13 desligado
        //     data[23..24]  = marcador 0x10 0x00 presente SÓ quando ligado (o blob
        //                     cresce 2 bytes ao ligar e encolhe ao desligar)
        //   Intensidade (settings):
        //     data[0x23]    = byte baixo da temperatura
        //     data[0x24]    = byte alto da temperatura  (Kelvin 1200=máx, 6500=neutro)
        //   Em AMBOS é obrigatório incrementar o contador de timestamp (bytes 10..14)
        //   antes de gravar, senão o Windows ignora/reverte a alteração.

        private const int MinKelvin = 1200; // 100% de intensidade (mais quente)
        private const int MaxKelvin = 6500; // 0% de intensidade (neutro)

        // Serializa as gravações — o ciclo off→on da intensidade não pode
        // entrelaçar com outro toggle vindo da UI ou de um arraste seguinte.
        private static readonly object _winNlLock = new();

        // Localiza a subchave correta — o nome do container e da folha variam entre versões do Windows.
        // Estratégia: tenta os nomes conhecidos primeiro, depois enumera todas as subchaves de
        // DefaultAccount\Current\ procurando qualquer container que contenha o nome do recurso.
        private static string? FindNlKeyPath(bool settings)
        {
            const string baseP =
                @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\";
            string targetFragment = settings
                ? "bluelightreduction.settings"
                : "bluelightreduction.bluelightreductionstate";
            string[] knownLeaves = settings
                ? new[] { "Current", "windows.data.bluelightreduction.settings" }
                : new[] { "Current", "windows.data.bluelightreduction.bluelightreductionstate" };

            try
            {
                using var baseKey = Registry.CurrentUser.OpenSubKey(baseP);
                if (baseKey == null) return null;

                foreach (var containerName in baseKey.GetSubKeyNames())
                {
                    if (containerName.IndexOf(targetFragment, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    // Tenta as folhas conhecidas primeiro
                    foreach (var leaf in knownLeaves)
                    {
                        string p = baseP + containerName + "\\" + leaf;
                        using var k = Registry.CurrentUser.OpenSubKey(p);
                        if (k?.GetValue("Data") is byte[]) return p;
                    }

                    // Enumera todas as folhas sob este container como fallback
                    using var containerKey = baseKey.OpenSubKey(containerName);
                    if (containerKey == null) continue;
                    foreach (var leafName in containerKey.GetSubKeyNames())
                    {
                        string p = baseP + containerName + "\\" + leafName;
                        using var k = Registry.CurrentUser.OpenSubKey(p);
                        if (k?.GetValue("Data") is byte[]) return p;
                    }
                }
            }
            catch { }
            return null;
        }

        // Incrementa o primeiro byte do contador (10..14) que não seja 0xFF.
        private static void BumpTimestamp(List<byte> data)
        {
            for (int i = 10; i <= 14 && i < data.Count; i++)
            {
                if (data[i] != 0xFF) { data[i]++; break; }
            }
        }

        public static bool GetWindowsNightLightEnabled()
        {
            try
            {
                string? path = FindNlKeyPath(settings: false);
                if (path == null) return false;
                using var key = Registry.CurrentUser.OpenSubKey(path);
                if (key?.GetValue("Data") is byte[] data && data.Length > 18)
                    return data[18] == 0x15;
            }
            catch { }
            return false;
        }

        public static bool SetWindowsNightLight(bool enabled)
        {
            lock (_winNlLock)
            try
            {
                string? path = FindNlKeyPath(settings: false);
                if (path == null)
                {
                    Logger.Warn("SetWindowsNightLight: chave do registro (state) não encontrada");
                    return false;
                }
                using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);
                if (key?.GetValue("Data") is not byte[] data || data.Length <= 18) return false;

                bool currentlyEnabled = data[18] == 0x15;
                var list = new List<byte>(data);

                if (enabled && !currentlyEnabled)
                {
                    list[18] = 0x15;
                    if (list.Count >= 23) list.InsertRange(23, new byte[] { 0x10, 0x00 });
                    else                  list.AddRange(new byte[] { 0x10, 0x00 });
                }
                else if (!enabled && currentlyEnabled)
                {
                    list[18] = 0x13;
                    if (list.Count >= 25) list.RemoveRange(23, 2);
                }
                // se já estiver no estado desejado, ainda assim regravamos com novo
                // timestamp para garantir que o Windows reavalie.

                BumpTimestamp(list);
                key.SetValue("Data", list.ToArray(), RegistryValueKind.Binary);
                SendNotifyMessage(HWND_BROADCAST, WM_SETTINGCHANGE, UIntPtr.Zero, "ImmersiveColorSet");
                return true;
            }
            catch (Exception ex) { Logger.Error(ex, "SetWindowsNightLight"); return false; }
        }

        public static int GetWindowsNightLightIntensity()
        {
            try
            {
                string? path = FindNlKeyPath(settings: true);
                if (path == null) return 50;
                using var key = Registry.CurrentUser.OpenSubKey(path);
                if (key?.GetValue("Data") is not byte[] data || data.Length <= 0x24) return 50;

                int loTemp = data[0x23];
                int hiTemp = data[0x24];
                int kelvin = (hiTemp * 64) + ((loTemp - 128) / 2);
                kelvin = Math.Clamp(kelvin, MinKelvin, MaxKelvin);
                int percent = (int)Math.Round(
                    100.0 - ((kelvin - MinKelvin) / (double)(MaxKelvin - MinKelvin)) * 100.0);
                return Math.Clamp(percent, 0, 100);
            }
            catch { }
            return 50;
        }

        public static bool SetWindowsNightLightIntensity(int percent)
        {
            lock (_winNlLock)
            try
            {
                percent = Math.Clamp(percent, 0, 100);
                string? path = FindNlKeyPath(settings: true);
                if (path == null)
                {
                    Logger.Warn("SetWindowsNightLightIntensity: chave do registro (settings) não encontrada");
                    return false;
                }
                using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);
                if (key?.GetValue("Data") is not byte[] data || data.Length <= 0x24) return false;

                int kelvin = (int)Math.Round(MaxKelvin - (percent / 100.0) * (MaxKelvin - MinKelvin));
                int hiTemp = kelvin / 64;
                int loTemp = ((kelvin - (hiTemp * 64)) * 2) + 128;

                var list = new List<byte>(data);
                list[0x23] = (byte)loTemp;
                list[0x24] = (byte)hiTemp;
                BumpTimestamp(list);

                key.SetValue("Data", list.ToArray(), RegistryValueKind.Binary);
                SendNotifyMessage(HWND_BROADCAST, WM_SETTINGCHANGE, UIntPtr.Zero, "ImmersiveColorSet");

                // O Windows só reaplica a temperatura quando o ESTADO transiciona —
                // gravar apenas o blob de settings não tem efeito ao vivo. Cicla
                // off→on (com pausa para o watcher do registro ver as duas
                // transições) para aplicar o novo valor imediatamente.
                if (GetWindowsNightLightEnabled())
                {
                    SetWindowsNightLight(false);
                    Thread.Sleep(60);
                    SetWindowsNightLight(true);
                }
                return true;
            }
            catch (Exception ex) { Logger.Error(ex, "SetWindowsNightLightIntensity"); return false; }
        }
    }
}
