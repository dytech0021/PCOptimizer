using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Liga e desliga monitores individualmente (desanexar/reanexar da área de
    /// trabalho), como faz o "Mostrar apenas em 1" das Configurações do Windows,
    /// mas escolhendo QUAL monitor. A resolução e a posição são guardadas antes
    /// de desanexar, para a reativação devolver a tela ao lugar de onde saiu.
    ///
    /// Um monitor desanexado some das enumerações normais — por isso a listagem
    /// aqui usa EnumDisplayDevices, que enxerga anexados e desanexados.
    /// </summary>
    public static class MonitorEnableService
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(string? device, uint devNum,
            ref DISPLAY_DEVICE dd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE dm);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ChangeDisplaySettingsEx(string? deviceName, ref DEVMODE dm,
            IntPtr hwnd, uint flags, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ChangeDisplaySettingsEx(string? deviceName, IntPtr dm,
            IntPtr hwnd, uint flags, IntPtr lParam);

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

        private const int  ENUM_CURRENT_SETTINGS = -1;
        private const uint CDS_UPDATEREGISTRY = 0x00000001;
        private const uint CDS_NORESET        = 0x10000000;
        private const int  DM_POSITION    = 0x00000020;
        private const int  DM_PELSWIDTH   = 0x00080000;
        private const int  DM_PELSHEIGHT  = 0x00100000;
        private const int  DISP_CHANGE_SUCCESSFUL = 0;

        private const uint ATTACHED_TO_DESKTOP = 0x1;
        private const uint PRIMARY_DEVICE      = 0x4;
        private const uint MIRRORING_DRIVER    = 0x8;

        public sealed class DisplayDeviceInfo
        {
            public string Device { get; init; } = "";   // \\.\DISPLAY1
            public string Description { get; init; } = "";
            public bool Attached { get; init; }
            public bool Primary { get; init; }
        }

        private static DEVMODE NewDevMode() => new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

        /// <summary>Todas as saídas de vídeo reais — anexadas E desanexadas.</summary>
        public static List<DisplayDeviceInfo> ListAll()
        {
            var list = new List<DisplayDeviceInfo>();
            try
            {
                for (uint i = 0; ; i++)
                {
                    var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                    if (!EnumDisplayDevices(null, i, ref dd, 0)) break;

                    // Ignora drivers de espelhamento (ferramentas de acesso remoto)
                    if ((dd.StateFlags & MIRRORING_DRIVER) != 0) continue;

                    list.Add(new DisplayDeviceInfo
                    {
                        Device      = dd.DeviceName,
                        Description = GetMonitorDescription(dd.DeviceName, dd.DeviceString),
                        Attached    = (dd.StateFlags & ATTACHED_TO_DESKTOP) != 0,
                        Primary     = (dd.StateFlags & PRIMARY_DEVICE) != 0
                    });
                }
            }
            catch (Exception ex) { Logger.Error(ex, "MonitorEnableService.ListAll"); }
            return list;
        }

        /// <summary>Nome do monitor ligado nessa saída (cai no do adaptador se não houver).</summary>
        private static string GetMonitorDescription(string device, string fallback)
        {
            try
            {
                var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (EnumDisplayDevices(device, 0, ref mon, 0) &&
                    !string.IsNullOrWhiteSpace(mon.DeviceString))
                    return mon.DeviceString;
            }
            catch { }
            return fallback;
        }

        /// <summary>Quantos monitores estão ativos agora.</summary>
        public static int AttachedCount()
        {
            int n = 0;
            foreach (var d in ListAll()) if (d.Attached) n++;
            return n;
        }

        /// <summary>
        /// Desativa (desanexa) um monitor. Guarda resolução, taxa e posição para a
        /// reativação. NÃO desativa se for o último ativo — ficar sem imagem
        /// nenhuma seria irreversível pela própria interface do app.
        /// </summary>
        public static (bool Ok, string Message) Disable(string device)
        {
            try
            {
                if (AttachedCount() <= 1)
                    return (false, "Este é o único monitor ativo — não dá para desativar");

                var cur = NewDevMode();
                if (!EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref cur))
                    return (false, "Não consegui ler o modo atual do monitor");

                // Guarda para restaurar depois
                SettingsService.Current.DisabledMonitors[device] =
                    $"{cur.dmPelsWidth}x{cur.dmPelsHeight}x{cur.dmDisplayFrequency}x{cur.dmPositionX}x{cur.dmPositionY}";
                SettingsService.Save();

                var dm = NewDevMode();
                dm.dmFields      = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT;
                dm.dmPelsWidth   = 0;   // 0x0 = desanexa da área de trabalho
                dm.dmPelsHeight  = 0;
                dm.dmPositionX   = cur.dmPositionX;
                dm.dmPositionY   = cur.dmPositionY;

                int rc = ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero,
                                                 CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
                if (rc != DISP_CHANGE_SUCCESSFUL)
                {
                    SettingsService.Current.DisabledMonitors.Remove(device);
                    SettingsService.Save();
                    return (false, $"O Windows recusou desativar (código {rc})");
                }

                Apply();
                MonitorService.MarkDisplaysChanged();
                return (true, "Monitor desativado");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "MonitorEnableService.Disable");
                return (false, "Erro: " + ex.Message);
            }
        }

        /// <summary>Reativa um monitor, devolvendo-o à resolução e posição de antes.</summary>
        public static (bool Ok, string Message) Enable(string device)
        {
            try
            {
                int w = 0, h = 0, hz = 0, x = 0, y = 0;
                if (SettingsService.Current.DisabledMonitors.TryGetValue(device, out var saved))
                {
                    var p = saved.Split('x');
                    if (p.Length == 5)
                    {
                        int.TryParse(p[0], out w); int.TryParse(p[1], out h);
                        int.TryParse(p[2], out hz); int.TryParse(p[3], out x);
                        int.TryParse(p[4], out y);
                    }
                }

                // Sem registro (ou corrompido): usa o modo preferido do monitor
                if (w <= 0 || h <= 0)
                {
                    var pref = NewDevMode();
                    if (EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref pref)
                        && pref.dmPelsWidth > 0)
                    {
                        w = pref.dmPelsWidth; h = pref.dmPelsHeight; hz = pref.dmDisplayFrequency;
                    }
                    else
                    {
                        var nat = DisplayResolutionService.GetNative(device);
                        if (nat == null) return (false, "Não consegui descobrir a resolução do monitor");
                        w = nat.Value.W; h = nat.Value.H;
                    }
                }

                var dm = NewDevMode();
                dm.dmFields      = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT;
                dm.dmPelsWidth   = w;
                dm.dmPelsHeight  = h;
                dm.dmPositionX   = x;
                dm.dmPositionY   = y;
                if (hz > 0)
                {
                    dm.dmFields |= 0x00400000; // DM_DISPLAYFREQUENCY
                    dm.dmDisplayFrequency = hz;
                }

                int rc = ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero,
                                                 CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
                if (rc != DISP_CHANGE_SUCCESSFUL)
                    return (false, $"O Windows recusou reativar (código {rc})");

                Apply();
                SettingsService.Current.DisabledMonitors.Remove(device);
                SettingsService.Save();
                MonitorService.MarkDisplaysChanged();
                return (true, "Monitor reativado");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "MonitorEnableService.Enable");
                return (false, "Erro: " + ex.Message);
            }
        }

        /// <summary>Efetiva as mudanças acumuladas com CDS_NORESET.</summary>
        private static void Apply()
            => ChangeDisplaySettingsEx(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);

        /// <summary>Reativa TUDO que o app desativou — saída de emergência.</summary>
        public static int EnableAllSaved()
        {
            int n = 0;
            foreach (var dev in new List<string>(SettingsService.Current.DisabledMonitors.Keys))
                if (Enable(dev).Ok) n++;
            return n;
        }
    }
}
