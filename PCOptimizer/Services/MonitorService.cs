using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace PCOptimizer.Services
{
    public class MonitorInfo
    {
        public IntPtr Handle { get; set; }
        public string Name { get; set; } = string.Empty;
        public uint MinBrightness { get; set; }
        public uint MaxBrightness { get; set; }
        public uint CurrentBrightness { get; set; }
        public uint MinContrast { get; set; }
        public uint MaxContrast { get; set; }
        public uint CurrentContrast { get; set; }
        public bool SupportsBrightness { get; set; }
        public bool SupportsContrast { get; set; }
    }

    public class MonitorEntry
    {
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public string HardwareId { get; set; } = "";
        public int Brightness { get; set; }
        public int Contrast { get; set; }
        public bool SupportsBrightness { get; set; }
        public bool SupportsContrast { get; set; }
        public bool IsWmi { get; set; }
    }

    public static class MonitorService
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        private delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumDelegate lpfnEnum, IntPtr dwData);

        [DllImport("dxva2.dll")]
        private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, ref uint pdwNumberOfPhysicalMonitors);

        [DllImport("dxva2.dll")]
        private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

        [DllImport("dxva2.dll")]
        private static extern bool DestroyPhysicalMonitor(IntPtr hMonitor);

        [DllImport("dxva2.dll")]
        private static extern bool GetMonitorBrightness(IntPtr hMonitor, ref uint minimumBrightness, ref uint currentBrightness, ref uint maxBrightness);

        [DllImport("dxva2.dll")]
        private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint newBrightness);

        [DllImport("dxva2.dll")]
        private static extern bool GetMonitorContrast(IntPtr hMonitor, ref uint minimumContrast, ref uint currentContrast, ref uint maxContrast);

        [DllImport("dxva2.dll")]
        private static extern bool SetMonitorContrast(IntPtr hMonitor, uint newContrast);

        public static List<MonitorInfo> GetMonitors()
        {
            var monitors = new List<MonitorInfo>();

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, hdc, lprc, dwData) =>
            {
                uint count = 0;
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, ref count) || count == 0)
                    return true;

                var phys = new PHYSICAL_MONITOR[count];
                if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, phys))
                    return true;

                foreach (var pm in phys)
                {
                    var info = new MonitorInfo
                    {
                        Handle = pm.hPhysicalMonitor,
                        Name = pm.szPhysicalMonitorDescription
                    };

                    uint bMin = 0, bCur = 0, bMax = 0;
                    if (GetMonitorBrightness(pm.hPhysicalMonitor, ref bMin, ref bCur, ref bMax))
                    {
                        info.MinBrightness = bMin;
                        info.CurrentBrightness = bCur;
                        info.MaxBrightness = bMax;
                        info.SupportsBrightness = true;
                    }

                    uint cMin = 0, cCur = 0, cMax = 0;
                    if (GetMonitorContrast(pm.hPhysicalMonitor, ref cMin, ref cCur, ref cMax))
                    {
                        info.MinContrast = cMin;
                        info.CurrentContrast = cCur;
                        info.MaxContrast = cMax;
                        info.SupportsContrast = true;
                    }

                    monitors.Add(info);
                }

                return true;
            }, IntPtr.Zero);

            return monitors;
        }

        // ── WMI fallback (notebooks / painéis sem DDC/CI) ─────────────────────

        private static bool HasWmiMonitors()
        {
            try
            {
                using var s = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
                using var r = s.Get();
                return r.Count > 0;
            }
            catch { return false; }
        }

        private static int GetWmiBrightness()
        {
            try
            {
                using var s = new ManagementObjectSearcher("root\\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
                foreach (ManagementObject obj in s.Get())
                    return Convert.ToInt32(obj["CurrentBrightness"]);
            }
            catch { }
            return 50;
        }

        public static bool SetWmiBrightness(int percent)
        {
            try
            {
                using var s = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
                foreach (ManagementObject obj in s.Get())
                {
                    obj.InvokeMethod("WmiSetBrightness", new object[] { (uint)0, (byte)percent });
                    return true;
                }
            }
            catch { }
            return false;
        }

        // ── EDID names via WmiMonitorID ────────────────────────────────────────

        private struct EdidInfo
        {
            public string Manufacturer;
            public string FriendlyName;
            public string HardwareId;
        }

        private static EdidInfo[] GetEdidInfos()
        {
            var result = new List<EdidInfo>();
            try
            {
                using var s = new ManagementObjectSearcher("root\\WMI",
                    "SELECT ManufacturerName, UserFriendlyName FROM WmiMonitorID");
                int idx = 0;
                foreach (ManagementObject obj in s.Get())
                {
                    string mfr      = WmiBytesToString(obj["ManufacturerName"]);
                    string friendly = WmiBytesToString(obj["UserFriendlyName"]);

                    string id = !string.IsNullOrEmpty(friendly)
                        ? $"{mfr}_{friendly.Replace(" ", "_")}"
                        : !string.IsNullOrEmpty(mfr) ? $"{mfr}_{idx}" : $"monitor_{idx}";

                    result.Add(new EdidInfo
                    {
                        Manufacturer = MapManufacturer(mfr),
                        FriendlyName = friendly,
                        HardwareId   = id
                    });
                    idx++;
                }
            }
            catch { }
            return result.ToArray();
        }

        private static string WmiBytesToString(object? value)
        {
            if (value is not ushort[] arr) return "";
            var sb = new StringBuilder();
            foreach (var c in arr) { if (c == 0) break; sb.Append((char)c); }
            return sb.ToString().Trim();
        }

        private static string MapManufacturer(string code) => code.ToUpperInvariant() switch
        {
            "DEL"                   => "Dell",
            "SAM" or "SDC"          => "Samsung",
            "LGD" or "GSM"          => "LG",
            "AUO"                   => "AUO",
            "BOE"                   => "BOE",
            "CMN" or "CMO" or "IVO" => "Innolux",
            "BNQ"                   => "BenQ",
            "HPN" or "HWP"          => "HP",
            "ACR"                   => "Acer",
            "VSC"                   => "ViewSonic",
            "NEC"                   => "NEC",
            "PHL"                   => "Philips",
            "AOC"                   => "AOC",
            "EIZ"                   => "EIZO",
            "SNY"                   => "Sony",
            "LEN"                   => "Lenovo",
            "SHP"                   => "Sharp",
            _                       => code
        };

        // ── Per-monitor API ───────────────────────────────────────────────────

        public static List<MonitorEntry> GetMonitorEntries()
        {
            var monitors = GetMonitors();
            var edids    = GetEdidInfos();

            // Pure WMI path: no DDC monitors at all (notebook with no external display)
            if (monitors.Count == 0 && HasWmiMonitors())
            {
                var ei = edids.Length > 0 ? edids[0] : default;
                string wmiName = !string.IsNullOrEmpty(ei.FriendlyName) ? ei.FriendlyName
                               : !string.IsNullOrEmpty(ei.Manufacturer) ? $"Painel {ei.Manufacturer}"
                               : "Painel do notebook";

                return new List<MonitorEntry>
                {
                    new MonitorEntry
                    {
                        Index              = 0,
                        Name               = wmiName,
                        HardwareId         = !string.IsNullOrEmpty(ei.HardwareId) ? ei.HardwareId : "monitor_0",
                        Brightness         = GetWmiBrightness(),
                        Contrast           = 50,
                        SupportsBrightness = true,
                        SupportsContrast   = false,
                        IsWmi              = true
                    }
                };
            }

            // DDC path — WMI fallback for any monitor DDC cannot control
            bool wmiAvailable     = HasWmiMonitors();
            int  wmiBrightnessVal = wmiAvailable ? GetWmiBrightness() : 50;

            var entries = new List<MonitorEntry>();
            for (int i = 0; i < monitors.Count; i++)
            {
                var m = monitors[i];

                // Resolve display name: prefer EDID friendly name over generic DDC description
                string ddcDesc   = m.Name.Trim();
                bool   ddcGeneric = string.IsNullOrEmpty(ddcDesc)
                    || ddcDesc.Equals("Generic PnP Monitor",     StringComparison.OrdinalIgnoreCase)
                    || ddcDesc.Equals("Generic Non-PnP Monitor", StringComparison.OrdinalIgnoreCase);

                string edidFriendly = i < edids.Length ? edids[i].FriendlyName : "";
                string edidMfr      = i < edids.Length ? edids[i].Manufacturer  : "";
                string hwId         = i < edids.Length && !string.IsNullOrEmpty(edids[i].HardwareId)
                                      ? edids[i].HardwareId : $"monitor_{i}";

                string name = !string.IsNullOrEmpty(edidFriendly) ? edidFriendly
                            : !ddcGeneric                          ? ddcDesc
                            : !string.IsNullOrEmpty(edidMfr)      ? $"Monitor {edidMfr}"
                            : $"Monitor {i + 1}";

                // DDC brightness/contrast values
                int  brightness        = 50, contrast = 50;
                bool supportsBrightness = m.SupportsBrightness;
                bool supportsContrast   = m.SupportsContrast;
                bool isWmi              = false;

                if (m.SupportsBrightness && m.MaxBrightness > m.MinBrightness)
                    brightness = (int)Math.Round((m.CurrentBrightness - m.MinBrightness) * 100.0
                                                  / (m.MaxBrightness - m.MinBrightness));
                if (m.SupportsContrast && m.MaxContrast > m.MinContrast)
                    contrast = (int)Math.Round((m.CurrentContrast - m.MinContrast) * 100.0
                                               / (m.MaxContrast - m.MinContrast));

                // WMI fallback when DDC can't control brightness (e.g. laptop panel in multi-monitor setup)
                if (!supportsBrightness && wmiAvailable)
                {
                    brightness         = wmiBrightnessVal;
                    supportsBrightness = true;
                    isWmi              = true;
                }

                entries.Add(new MonitorEntry
                {
                    Index              = i,
                    Name               = name,
                    HardwareId         = hwId,
                    Brightness         = brightness,
                    Contrast           = contrast,
                    SupportsBrightness = supportsBrightness,
                    SupportsContrast   = supportsContrast,
                    IsWmi              = isWmi
                });
                DestroyPhysicalMonitor(m.Handle);
            }

            return entries;
        }

        public static bool SetBrightnessForIndex(int monitorIndex, int percent)
        {
            percent = Math.Clamp(percent, 0, 100);
            var monitors = GetMonitors();
            bool success = false;

            for (int i = 0; i < monitors.Count; i++)
            {
                var m = monitors[i];
                if (i == monitorIndex && m.SupportsBrightness && m.MaxBrightness >= m.MinBrightness)
                {
                    uint range = m.MaxBrightness - m.MinBrightness;
                    success = SetMonitorBrightness(m.Handle, m.MinBrightness + (uint)(range * percent / 100.0));
                }
                DestroyPhysicalMonitor(m.Handle);
            }
            return success;
        }

        public static bool SetContrastForIndex(int monitorIndex, int percent)
        {
            percent = Math.Clamp(percent, 0, 100);
            var monitors = GetMonitors();
            bool success = false;

            for (int i = 0; i < monitors.Count; i++)
            {
                var m = monitors[i];
                if (i == monitorIndex && m.SupportsContrast && m.MaxContrast >= m.MinContrast)
                {
                    uint range = m.MaxContrast - m.MinContrast;
                    success = SetMonitorContrast(m.Handle, m.MinContrast + (uint)(range * percent / 100.0));
                }
                DestroyPhysicalMonitor(m.Handle);
            }
            return success;
        }

        // ── All-monitors helpers (used by presets) ────────────────────────────

        public static int SetBrightnessAll(int percent)
        {
            percent = Math.Clamp(percent, 0, 100);
            var monitors = GetMonitors();
            int success = 0;

            foreach (var m in monitors)
            {
                if (m.SupportsBrightness && m.MaxBrightness >= m.MinBrightness)
                {
                    uint range = m.MaxBrightness - m.MinBrightness;
                    if (SetMonitorBrightness(m.Handle, m.MinBrightness + (uint)(range * percent / 100.0)))
                        success++;
                }
                DestroyPhysicalMonitor(m.Handle);
            }

            if (success == 0 && HasWmiMonitors())
                success = SetWmiBrightness(percent) ? 1 : 0;

            return success;
        }

        public static int SetContrastAll(int percent)
        {
            percent = Math.Clamp(percent, 0, 100);
            var monitors = GetMonitors();
            int success = 0;

            foreach (var m in monitors)
            {
                if (m.SupportsContrast && m.MaxContrast >= m.MinContrast)
                {
                    uint range = m.MaxContrast - m.MinContrast;
                    if (SetMonitorContrast(m.Handle, m.MinContrast + (uint)(range * percent / 100.0)))
                        success++;
                }
                DestroyPhysicalMonitor(m.Handle);
            }

            return success;
        }

        public static (int Brightness, int Contrast, int Count, bool IsWmi) GetAverageValues()
        {
            var entries = GetMonitorEntries();
            if (entries.Count == 0) return (50, 50, 0, false);
            if (entries[0].IsWmi) return (entries[0].Brightness, 50, 1, true);

            int totalB = 0, totalC = 0;
            foreach (var e in entries) { totalB += e.Brightness; totalC += e.Contrast; }
            return (totalB / entries.Count, totalC / entries.Count, entries.Count, false);
        }
    }
}
