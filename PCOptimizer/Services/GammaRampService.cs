using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Gama, temperatura de cor e ganho RGB por canal via gamma ramp da GPU
    /// (SetDeviceGammaRamp), aplicado a TODOS os monitores ativos. Não depende de
    /// DDC/CI — funciona em qualquer monitor, inclusive painel de notebook.
    ///
    /// Observações:
    ///  - O Windows restringe ramps muito agressivas por padrão; liberamos o range
    ///    completo via GdiIcmGammaRange (a mesma técnica do f.lux) — o app roda
    ///    como admin, então a escrita em HKLM funciona.
    ///  - O ajuste se perde no reboot; o app reaplica o que foi salvo ao abrir.
    /// </summary>
    public static class GammaRampService
    {
        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern bool SetDeviceGammaRamp(IntPtr hdc, ref GammaRamp ramp);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateDC(string? driver, string device, string? output, IntPtr devMode);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(string? device, uint iDevNum,
            ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]  public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        private const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct GammaRamp
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Red;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Green;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Blue;
        }

        public const double DefaultGamma  = 1.0;
        public const int    DefaultKelvin = 6500;
        public const int    DefaultGain   = 100;

        private static bool _rangeExpanded;

        /// <summary>
        /// Aplica gama (0.5–2.5), temperatura de cor (2000–10000 K, 6500 = neutro)
        /// e ganho por canal (25–100%) em todos os monitores. Retorna true se pelo
        /// menos um monitor aceitou a ramp.
        /// </summary>
        public static bool Apply(double gamma, int kelvin, int gainR, int gainG, int gainB)
        {
            gamma  = Math.Clamp(gamma, 0.5, 2.5);
            kelvin = Math.Clamp(kelvin, 2000, 10000);
            var (tr, tg, tb) = KelvinToRgb(kelvin);
            double gr = Math.Clamp(gainR, 25, 100) / 100.0 * tr;
            double gg = Math.Clamp(gainG, 25, 100) / 100.0 * tg;
            double gb = Math.Clamp(gainB, 25, 100) / 100.0 * tb;

            EnsureExpandedGammaRange();

            var ramp = new GammaRamp
            {
                Red = new ushort[256], Green = new ushort[256], Blue = new ushort[256]
            };
            double inv = 1.0 / gamma;
            for (int i = 0; i < 256; i++)
            {
                double baseV = Math.Pow(i / 255.0, inv) * 65535.0;
                ramp.Red[i]   = (ushort)Math.Clamp(baseV * gr, 0, 65535);
                ramp.Green[i] = (ushort)Math.Clamp(baseV * gg, 0, 65535);
                ramp.Blue[i]  = (ushort)Math.Clamp(baseV * gb, 0, 65535);
            }

            bool any = ApplyRamp(ref ramp);

            if (!any)
                Logger.Warn($"Gamma ramp recusada pela GPU (γ={gamma:F2} {kelvin}K R={gainR} G={gainG} B={gainB})");
            return any;
        }

        /// <summary>Envia a rampa para todos os monitores ativos. true se algum aceitou.</summary>
        private static bool ApplyRamp(ref GammaRamp ramp)
        {
            bool any = false;
            try
            {
                var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
                {
                    if ((dd.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0)
                    {
                        IntPtr hdc = CreateDC(null, dd.DeviceName, null, IntPtr.Zero);
                        if (hdc != IntPtr.Zero)
                        {
                            try { if (SetDeviceGammaRamp(hdc, ref ramp)) any = true; }
                            finally { DeleteDC(hdc); }
                        }
                    }
                    dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
                }
            }
            catch (Exception ex) { Logger.Error(ex, "GammaRampService.ApplyRamp"); }
            return any;
        }

        /// <summary>
        /// Volta a rampa da GPU ao NEUTRO absoluto (rampa linear identidade).
        /// Não passa por Apply: a aproximação de corpo negro em 6500 K reduz o
        /// azul em ~2%, ou seja, "neutro" ficava levemente quente.
        /// </summary>
        public static bool Reset()
        {
            var ramp = new GammaRamp
            {
                Red = new ushort[256], Green = new ushort[256], Blue = new ushort[256]
            };
            for (int i = 0; i < 256; i++)
            {
                ushort v = (ushort)(i * 257); // 0..65535 exato
                ramp.Red[i] = ramp.Green[i] = ramp.Blue[i] = v;
            }
            return ApplyRamp(ref ramp);
        }

        /// <summary>Reaplica os ajustes salvos (chamar no startup). Padrão = nada a fazer.</summary>
        public static void RestoreFromSettings()
        {
            try
            {
                var s = SettingsService.Current;

                // Saturação vive no driver da GPU e também se perde no reboot
                if (s.Saturation != NvapiService.DvcDefault)
                    NvapiService.SetDigitalVibrance(s.Saturation);

                if (IsDefault(s.GammaValue, s.ColorTempK, s.GainR, s.GainG, s.GainB)) return;
                Apply(s.GammaValue, s.ColorTempK, s.GainR, s.GainG, s.GainB);
            }
            catch (Exception ex) { Logger.Error(ex, "GammaRampService.RestoreFromSettings"); }
        }

        public static bool IsDefault(double gamma, int kelvin, int r, int g, int b) =>
            Math.Abs(gamma - DefaultGamma) < 0.005 && kelvin == DefaultKelvin &&
            r == DefaultGain && g == DefaultGain && b == DefaultGain;

        /// <summary>
        /// Libera o range completo de gamma ramps (o Windows recusa ramps fortes
        /// por padrão). Mesma chave que o f.lux usa; best-effort, uma vez por sessão.
        /// </summary>
        private static void EnsureExpandedGammaRange()
        {
            if (_rangeExpanded) return;
            _rangeExpanded = true;
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM");
                if (Convert.ToInt32(key?.GetValue("GdiIcmGammaRange") ?? 0) != 256)
                    key?.SetValue("GdiIcmGammaRange", 256, RegistryValueKind.DWord);
            }
            catch (Exception ex) { Logger.Error(ex, "EnsureExpandedGammaRange"); }
        }

        // Aproximação de corpo negro (algoritmo de Tanner Helland) —
        // multiplicadores 0..1 por canal para uma dada temperatura em Kelvin.
        private static (double r, double g, double b) KelvinToRgb(int kelvin)
        {
            double t = kelvin / 100.0;
            double r = t <= 66 ? 255
                : Math.Clamp(329.698727446 * Math.Pow(t - 60, -0.1332047592), 0, 255);
            double g = t <= 66
                ? Math.Clamp(99.4708025861 * Math.Log(t) - 161.1195681661, 0, 255)
                : Math.Clamp(288.1221695283 * Math.Pow(t - 60, -0.0755148492), 0, 255);
            double b = t >= 66 ? 255
                : (t <= 19 ? 0 : Math.Clamp(138.5177312231 * Math.Log(t - 10) - 305.0447927307, 0, 255));
            return (r / 255.0, g / 255.0, b / 255.0);
        }
    }
}
