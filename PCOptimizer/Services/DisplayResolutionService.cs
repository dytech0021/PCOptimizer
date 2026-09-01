using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Troca a resolução da tela PRINCIPAL — pensado para acesso remoto: um PC
    /// com tela 21:9 (ex.: 2560×1080) acessado de uma tela 16:9 aparece com
    /// barras/espremido; mudar o remoto para 1920×1080 faz a imagem casar 1:1.
    /// A resolução anterior fica salva nas configurações, então a reversão
    /// funciona mesmo depois de reiniciar o PC ou o app.
    /// </summary>
    public static class DisplayResolutionService
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ChangeDisplaySettingsEx(string? deviceName, ref DEVMODE devMode,
            IntPtr hwnd, uint flags, IntPtr lParam);

        private const int  ENUM_CURRENT_SETTINGS = -1;
        private const uint CDS_UPDATEREGISTRY    = 0x00000001;
        private const int  DISP_CHANGE_SUCCESSFUL = 0;

        private const int DM_PELSWIDTH        = 0x00080000;
        private const int DM_PELSHEIGHT       = 0x00100000;
        private const int DM_DISPLAYFREQUENCY = 0x00400000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int   dmFields;
            public int   dmPositionX;
            public int   dmPositionY;
            public int   dmDisplayOrientation;
            public int   dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int   dmBitsPerPel;
            public int   dmPelsWidth;
            public int   dmPelsHeight;
            public int   dmDisplayFlags;
            public int   dmDisplayFrequency;
            public int   dmICMMethod;
            public int   dmICMIntent;
            public int   dmMediaType;
            public int   dmDitherType;
            public int   dmReserved1;
            public int   dmReserved2;
            public int   dmPanningWidth;
            public int   dmPanningHeight;
        }

        private static string PrimaryDevice()
        {
            try { return System.Windows.Forms.Screen.PrimaryScreen?.DeviceName ?? ""; }
            catch { return ""; }
        }

        /// <summary>
        /// Só \\.\DISPLAYn é aceito por EnumDisplaySettings/ChangeDisplaySettingsEx.
        /// Chaves de identidade estável (ddc_MODELO_hash) já chegaram aqui por
        /// engano e falhavam em silêncio — pior, o null ia parar no cache e o
        /// erro grudava. Agora reclama no log em vez de sumir com o botão.
        /// </summary>
        public static bool IsGdiDeviceName(string? device) =>
            !string.IsNullOrEmpty(device) &&
            device.StartsWith(@"\\.\", StringComparison.Ordinal);

        private static void WarnBadDevice(string caller, string? device) =>
            Logger.Warn($"{caller}: \"{device}\" nao e um device GDI (\\\\.\\DISPLAYn) — " +
                        "provavelmente passaram a chave de identidade no lugar de GdiDevice");

        /// <summary>Resolução atual de um monitor específico (\\.\DISPLAYn).</summary>
        public static (int W, int H, int Hz)? GetCurrentFor(string device)
        {
            if (!IsGdiDeviceName(device)) { WarnBadDevice(nameof(GetCurrentFor), device); return null; }
            try
            {
                if (string.IsNullOrEmpty(device)) return null;
                var dm = NewDevMode();
                if (!EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref dm)) return null;
                return (dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency);
            }
            catch { return null; }
        }

        /// <summary>
        /// Melhor modo 16:9 suportado por esse monitor, preferindo a mesma altura
        /// do modo atual — num 2560×1080 (21:9) isso dá 1920×1080. null se não houver.
        /// </summary>
        public static (int W, int H)? FindBest169(string device)
        {
            if (!IsGdiDeviceName(device)) { WarnBadDevice(nameof(FindBest169), device); return null; }
            try
            {
                var cur = GetCurrentFor(device);
                if (cur == null) return null;

                (int W, int H)? best = null;
                var probe = NewDevMode();
                for (int i = 0; EnumDisplaySettings(device, i, ref probe); i++)
                {
                    int w = probe.dmPelsWidth, h = probe.dmPelsHeight;
                    if (h <= 0 || w <= 0) continue;
                    // 16:9 com tolerância (evita erro de arredondamento)
                    if (Math.Abs(w * 9.0 / h - 16.0) > 0.02) continue;
                    if (h > cur.Value.H) continue;          // não passa da altura do painel
                    if (best == null || w > best.Value.W) best = (w, h);
                }
                return best;
            }
            catch { return null; }
        }

        /// <summary>Aplica width×height a um monitor específico.</summary>
        public static bool SetFor(string device, int width, int height, int preferHz = 0)
            => SetMode(device, width, height, preferHz);

        // Enumerar TODOS os modos de vídeo é caro e a nativa não muda enquanto o
        // monitor for o mesmo — guardar evita repetir a cada abertura da janela.
        private static readonly System.Collections.Generic.Dictionary<string, (int W, int H)?> _nativeCache
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Limpa o cache de resoluções nativas (troca de monitores).</summary>
        public static void InvalidateNativeCache()
        {
            lock (_nativeCache) _nativeCache.Clear();
        }

        /// <summary>Resolução NATIVA (maior modo suportado) do monitor.</summary>
        public static (int W, int H)? GetNative(string device)
        {
            // Antes do cache de propósito: chave inválida não pode ser memorizada.
            if (!IsGdiDeviceName(device)) { WarnBadDevice(nameof(GetNative), device); return null; }
            if (!string.IsNullOrEmpty(device))
                lock (_nativeCache)
                    if (_nativeCache.TryGetValue(device, out var hit)) return hit;

            var found = GetNativeUncached(device);
            if (!string.IsNullOrEmpty(device))
                lock (_nativeCache) _nativeCache[device] = found;
            return found;
        }

        private static (int W, int H)? GetNativeUncached(string device)
        {
            try
            {
                if (string.IsNullOrEmpty(device)) return null;
                (int W, int H)? best = null;
                var probe = NewDevMode();
                for (int i = 0; EnumDisplaySettings(device, i, ref probe); i++)
                {
                    if (probe.dmPelsWidth <= 0 || probe.dmPelsHeight <= 0) continue;
                    if (best == null ||
                        (long)probe.dmPelsWidth * probe.dmPelsHeight >
                        (long)best.Value.W * best.Value.H)
                        best = (probe.dmPelsWidth, probe.dmPelsHeight);
                }
                return best;
            }
            catch { return null; }
        }

        /// <summary>
        /// Coloca UM monitor na maior resolução que ele suporta e, nela, na maior
        /// taxa de atualização disponível (SetMode com preferHz 0 já escolhe o
        /// maior Hz válido para aquela resolução).
        /// </summary>
        public static (bool Ok, int W, int H, int Hz) ApplyBestMode(string device)
        {
            var nat = GetNative(device);
            if (nat == null) return (false, 0, 0, 0);

            bool ok = SetMode(device, nat.Value.W, nat.Value.H);
            var now = GetCurrentFor(device);
            return (ok, now?.W ?? nat.Value.W, now?.H ?? nat.Value.H, now?.Hz ?? 0);
        }

        /// <summary>
        /// Aplica o melhor modo em TODAS as telas ativas. Devolve uma linha por
        /// monitor para o log.
        /// </summary>
        public static List<(string Device, bool Ok, int W, int H, int Hz)> MaximizeAll()
        {
            var results = new List<(string, bool, int, int, int)>();
            try
            {
                foreach (var scr in System.Windows.Forms.Screen.AllScreens)
                {
                    var before = GetCurrentFor(scr.DeviceName);
                    var r = ApplyBestMode(scr.DeviceName);

                    // Já estava no melhor modo? Conta como sucesso mesmo assim.
                    bool already = before != null && r.W == before.Value.W
                                && r.H == before.Value.H && r.Hz == before.Value.Hz;
                    results.Add((scr.DeviceName, r.Ok || already, r.W, r.H, r.Hz));
                }
            }
            catch (Exception ex) { Logger.Error(ex, "MaximizeAll"); }
            return results;
        }

        /// <summary>
        /// Lê o painel e decide o que o botão de 16:9 deve oferecer neste monitor.
        /// A regra em si mora em <see cref="GameArPolicy"/>, sem depender do
        /// Windows, para poder ser coberta por teste — aqui só entram as leituras.
        ///
        /// <paramref name="device"/> tem que ser o GdiDevice (\\.\DISPLAYn); a
        /// chave de identidade do monitor não funciona e o guard avisa no log.
        /// </summary>
        public static GameArAction DecideGameAr(string device, bool hasSavedState)
        {
            var cur = GetCurrentFor(device);   // traz o Hz junto; a regra só quer W×H
            (int W, int H)? size = null;
            if (cur != null) size = (cur.Value.W, cur.Value.H);
            return GameArPolicy.Decide(GetNative(device), size, hasSavedState);
        }

        private static DEVMODE NewDevMode() =>
            new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

        /// <summary>Resolução atual da tela principal (largura, altura, Hz) — null se falhar.</summary>
        public static (int W, int H, int Hz)? GetCurrent()
        {
            try
            {
                string dev = PrimaryDevice();
                if (dev.Length == 0) return null;
                var dm = NewDevMode();
                if (!EnumDisplaySettings(dev, ENUM_CURRENT_SETTINGS, ref dm)) return null;
                return (dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency);
            }
            catch { return null; }
        }

        /// <summary>
        /// Muda a tela principal para 1920×1080, guardando a resolução atual nas
        /// configurações para reversão. Usa o maior refresh que o monitor aceitar.
        /// </summary>
        public static bool ApplyRemote1080()
        {
            try
            {
                var cur = GetCurrent();
                if (cur == null) return false;
                if (cur.Value.W == 1920 && cur.Value.H == 1080) return true; // já está

                if (!SetPrimary(1920, 1080)) return false;

                var s = SettingsService.Current;
                s.RemotePrevWidth  = cur.Value.W;
                s.RemotePrevHeight = cur.Value.H;
                s.RemotePrevHz     = cur.Value.Hz;
                s.RemoteResActive  = true;
                SettingsService.Save();
                return true;
            }
            catch (Exception ex) { Logger.Error(ex, "ApplyRemote1080"); return false; }
        }

        /// <summary>Restaura a resolução nativa guardada nas configurações.</summary>
        public static bool RestoreNative()
        {
            try
            {
                var s = SettingsService.Current;
                if (s.RemotePrevWidth < 640 || s.RemotePrevHeight < 480) return false;
                if (!SetPrimary(s.RemotePrevWidth, s.RemotePrevHeight, s.RemotePrevHz))
                    return false;
                s.RemoteResActive = false;
                SettingsService.Save();
                return true;
            }
            catch (Exception ex) { Logger.Error(ex, "RestoreNative"); return false; }
        }

        /// <summary>
        /// Aplica width×height na tela principal. Se preferHz > 0 tenta esse
        /// refresh exato; senão (ou se não existir) usa o MAIOR disponível.
        /// </summary>
        private static bool SetPrimary(int width, int height, int preferHz = 0)
            => SetMode(PrimaryDevice(), width, height, preferHz);

        private static bool SetMode(string dev, int width, int height, int preferHz = 0)
        {
            if (!IsGdiDeviceName(dev)) { WarnBadDevice(nameof(SetMode), dev); return false; }

            // Enumera os modos suportados e escolhe o refresh certo — aplicar um
            // modo que o monitor não suporta faria a tela apagar até o timeout.
            int bestHz = 0;
            var probe = NewDevMode();
            for (int i = 0; EnumDisplaySettings(dev, i, ref probe); i++)
            {
                if (probe.dmPelsWidth != width || probe.dmPelsHeight != height) continue;
                if (probe.dmDisplayFrequency == preferHz) { bestHz = preferHz; break; }
                if (probe.dmDisplayFrequency > bestHz) bestHz = probe.dmDisplayFrequency;
            }
            if (bestHz == 0) return false; // monitor não suporta essa resolução

            var dm = NewDevMode();
            dm.dmPelsWidth        = width;
            dm.dmPelsHeight       = height;
            dm.dmDisplayFrequency = bestHz;
            dm.dmFields           = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;

            int rc = ChangeDisplaySettingsEx(dev, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
            Logger.Info($"Resolução {width}×{height}@{bestHz}: rc={rc}");
            return rc == DISP_CHANGE_SUCCESSFUL;
        }
    }
}
