using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace PCOptimizer.Services
{
    public class MonitorInfo
    {
        public IntPtr Handle { get; set; }        // hPhysicalMonitor (DDC/CI)
        public IntPtr LogicalHandle { get; set; } // hMonitor from EnumDisplayMonitors
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
        public bool SupportsHdr { get; set; }
        public bool HdrEnabled { get; set; }
        public uint HdrAdapterIdLow { get; set; }
        public int HdrAdapterIdHigh { get; set; }
        public uint HdrTargetId { get; set; }
    }

    public static class MonitorService
    {
        // ── DDC/CI P/Invoke ───────────────────────────────────────────────────

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

        [DllImport("dxva2.dll")]
        private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte vcpCode,
            out int pvct, out uint currentValue, out uint maximumValue);

        [DllImport("dxva2.dll")]
        private static extern bool SetVCPFeature(IntPtr hMonitor, byte vcpCode, uint newValue);

        private const byte VCP_LUMINANCE = 0x10;

        // ── Display device info P/Invoke (for PnP ID correlation) ─────────────

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice; // e.g. "\\.\DISPLAY1"
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAY_DEVICE
        {
            public uint cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]  public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum,
            ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        // Returns the PnP hardware ID of the monitor attached to this logical display handle.
        // e.g. "DEL4079" for a Dell monitor — matches the segment in WmiMonitorID.InstanceName.
        private static string GetMonitorPnpId(IntPtr hMonitor)
        {
            try
            {
                var mi = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
                if (!GetMonitorInfo(hMonitor, ref mi)) return "";

                for (uint i = 0; ; i++)
                {
                    var dd = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
                    if (!EnumDisplayDevices(mi.szDevice, i, ref dd, 0)) break;

                    // DeviceID: "MONITOR\DEL4079\{GUID}\0001"
                    string devId = dd.DeviceID;
                    if (devId.StartsWith("MONITOR\\", StringComparison.OrdinalIgnoreCase))
                    {
                        int start = 8;
                        int end = devId.IndexOf('\\', start);
                        if (end > start)
                            return devId.Substring(start, end - start); // e.g. "DEL4079"
                    }
                }
            }
            catch { }
            return "";
        }

        // ── DDC monitor enumeration ───────────────────────────────────────────

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
                        LogicalHandle = hMonitor,
                        Name = pm.szPhysicalMonitorDescription
                    };

                    uint bMin = 0, bCur = 0, bMax = 0;
                    if (GetMonitorBrightness(pm.hPhysicalMonitor, ref bMin, ref bCur, ref bMax)
                        && bMax > bMin)
                    {
                        info.MinBrightness      = bMin;
                        info.CurrentBrightness  = bCur;
                        info.MaxBrightness      = bMax;
                        info.SupportsBrightness = true;
                    }

                    uint cMin = 0, cCur = 0, cMax = 0;
                    if (GetMonitorContrast(pm.hPhysicalMonitor, ref cMin, ref cCur, ref cMax)
                        && cMax > cMin)
                    {
                        info.MinContrast     = cMin;
                        info.CurrentContrast = cCur;
                        info.MaxContrast     = cMax;
                        info.SupportsContrast = true;
                    }

                    // VCP 0x10 (Luminance) raw fallback — handles monitors where GetMonitorBrightness
                    // fails or returns a zero range (e.g. KaBuM MG900 and similar DDC quirks).
                    if (!info.SupportsBrightness &&
                        GetVCPFeatureAndVCPFeatureReply(pm.hPhysicalMonitor, VCP_LUMINANCE,
                            out _, out uint vcpCur, out uint vcpMax) && vcpMax > 0)
                    {
                        info.MinBrightness      = 0;
                        info.CurrentBrightness  = vcpCur;
                        info.MaxBrightness      = vcpMax;
                        info.SupportsBrightness = true;
                    }

                    // Last resort: monitor responds to DDC/CI (contrast works) but brightness is
                    // unreadable. Expose a 0-100 slider; SetMonitorBrightness/SetVCPFeature may
                    // still work write-only.
                    if (!info.SupportsBrightness && info.SupportsContrast)
                    {
                        info.SupportsBrightness = true;
                        info.MinBrightness      = 0;
                        info.MaxBrightness      = 100;
                        info.CurrentBrightness  = 50;
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

        // ── EDID names via WmiMonitorID (keyed by PnP hardware ID) ───────────

        private struct EdidInfo
        {
            public string Manufacturer;
            public string FriendlyName;
            public string HardwareId;
        }

        // Returns a queue per PnP ID so that identical monitors (same model) are
        // consumed in the order WMI reports them — which should match connector order.
        private static Dictionary<string, Queue<EdidInfo>> GetEdidInfosByPnpId()
        {
            var result = new Dictionary<string, Queue<EdidInfo>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var s = new ManagementObjectSearcher("root\\WMI",
                    "SELECT ManufacturerName, UserFriendlyName, InstanceName FROM WmiMonitorID");
                int idx = 0;
                foreach (ManagementObject obj in s.Get())
                {
                    // InstanceName: "DISPLAY\DEL4079\4&path&0&UID256_0"
                    string instance = obj["InstanceName"]?.ToString() ?? "";
                    string[] parts  = instance.Split('\\');
                    string pnpId    = parts.Length >= 2 ? parts[1] : $"_idx{idx}";

                    string mfr      = WmiBytesToString(obj["ManufacturerName"]);
                    string friendly = WmiBytesToString(obj["UserFriendlyName"]);
                    string hwId     = !string.IsNullOrEmpty(friendly)
                        ? $"{mfr}_{friendly.Replace(" ", "_")}"
                        : !string.IsNullOrEmpty(mfr) ? $"{mfr}_{idx}" : $"monitor_{idx}";

                    if (!result.ContainsKey(pnpId))
                        result[pnpId] = new Queue<EdidInfo>();

                    result[pnpId].Enqueue(new EdidInfo
                    {
                        Manufacturer = MapManufacturer(mfr),
                        FriendlyName = friendly,
                        HardwareId   = hwId
                    });
                    idx++;
                }
            }
            catch { }
            return result;
        }

        // Returns the first EDID info (WMI-only path, no DDC)
        private static EdidInfo GetFirstEdidInfo()
        {
            try
            {
                using var s = new ManagementObjectSearcher("root\\WMI",
                    "SELECT ManufacturerName, UserFriendlyName FROM WmiMonitorID");
                foreach (ManagementObject obj in s.Get())
                {
                    string mfr      = WmiBytesToString(obj["ManufacturerName"]);
                    string friendly = WmiBytesToString(obj["UserFriendlyName"]);
                    return new EdidInfo
                    {
                        Manufacturer = MapManufacturer(mfr),
                        FriendlyName = friendly,
                        HardwareId   = !string.IsNullOrEmpty(friendly)
                            ? $"{mfr}_{friendly.Replace(" ", "_")}" : "monitor_0"
                    };
                }
            }
            catch { }
            return default;
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

            // Pure WMI path: no DDC monitors at all (notebook with no external display)
            if (monitors.Count == 0 && HasWmiMonitors())
            {
                var ei = GetFirstEdidInfo();
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

            // DDC path — correlate EDID names via PnP ID, WMI fallback for uncontrollable panels
            var edidByPnp     = GetEdidInfosByPnpId();
            bool wmiAvailable = HasWmiMonitors();
            int  wmiBrightness = wmiAvailable ? GetWmiBrightness() : 50;
            var  hdrInfos      = HdrService.GetAllHdrInfo();

            var entries = new List<MonitorEntry>();
            for (int i = 0; i < monitors.Count; i++)
            {
                var m = monitors[i];

                // Reliable name lookup: match by PnP ID from EnumDisplayDevices
                string pnpId = GetMonitorPnpId(m.LogicalHandle);
                EdidInfo edid = default;
                if (!string.IsNullOrEmpty(pnpId) &&
                    edidByPnp.TryGetValue(pnpId, out var q) && q.Count > 0)
                    edid = q.Dequeue();

                // Name resolution priority: EDID friendly name > non-generic DDC desc > manufacturer
                string ddcDesc   = m.Name.Trim();
                bool   ddcGeneric = string.IsNullOrEmpty(ddcDesc)
                    || ddcDesc.Equals("Generic PnP Monitor",     StringComparison.OrdinalIgnoreCase)
                    || ddcDesc.Equals("Generic Non-PnP Monitor", StringComparison.OrdinalIgnoreCase);

                // Prepend manufacturer to friendly name if not already included
                string bestFriendly = !string.IsNullOrEmpty(edid.FriendlyName)
                    ? (!string.IsNullOrEmpty(edid.Manufacturer) &&
                       !edid.FriendlyName.StartsWith(edid.Manufacturer, StringComparison.OrdinalIgnoreCase)
                       ? $"{edid.Manufacturer} {edid.FriendlyName}"
                       : edid.FriendlyName)
                    : "";

                string name = !string.IsNullOrEmpty(bestFriendly) ? bestFriendly
                            : !ddcGeneric                          ? ddcDesc
                            : !string.IsNullOrEmpty(edid.Manufacturer) ? $"Monitor {edid.Manufacturer}"
                            : $"Monitor {i + 1}";

                string hwId = !string.IsNullOrEmpty(edid.HardwareId) ? edid.HardwareId
                            : !string.IsNullOrEmpty(pnpId)            ? pnpId
                            : $"monitor_{i}";

                // DDC brightness/contrast
                int  brightness         = 50, contrast = 50;
                bool supportsBrightness = m.SupportsBrightness;
                bool supportsContrast   = m.SupportsContrast;
                bool isWmi              = false;

                if (m.SupportsBrightness && m.MaxBrightness > m.MinBrightness)
                    brightness = (int)Math.Round((m.CurrentBrightness - m.MinBrightness) * 100.0
                                                  / (m.MaxBrightness - m.MinBrightness));
                if (m.SupportsContrast && m.MaxContrast > m.MinContrast)
                    contrast = (int)Math.Round((m.CurrentContrast - m.MinContrast) * 100.0
                                               / (m.MaxContrast - m.MinContrast));

                // WMI fallback when DDC cannot control brightness (e.g. laptop internal panel)
                if (!supportsBrightness && wmiAvailable)
                {
                    brightness         = wmiBrightness;
                    supportsBrightness = true;
                    isWmi              = true;
                }

                // HDR info — correlate by screen position (rcMonitor.left/top == DisplayConfig source position)
                int srcX = 0, srcY = 0;
                var mInfoEx = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(m.LogicalHandle, ref mInfoEx))
                {
                    srcX = mInfoEx.rcMonitor.left;
                    srcY = mInfoEx.rcMonitor.top;
                }
                var hdr = hdrInfos.Find(h => h.SourceX == srcX && h.SourceY == srcY);

                entries.Add(new MonitorEntry
                {
                    Index              = i,
                    Name               = name,
                    HardwareId         = hwId,
                    Brightness         = brightness,
                    Contrast           = contrast,
                    SupportsBrightness = supportsBrightness,
                    SupportsContrast   = supportsContrast,
                    IsWmi              = isWmi,
                    SupportsHdr        = hdr != null,
                    HdrEnabled         = hdr?.IsEnabled ?? false,
                    HdrAdapterIdLow    = hdr?.AdapterIdLow ?? 0,
                    HdrAdapterIdHigh   = hdr?.AdapterIdHigh ?? 0,
                    HdrTargetId        = hdr?.TargetId ?? 0
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
                if (i == monitorIndex && m.SupportsBrightness)
                {
                    uint target = m.MaxBrightness > m.MinBrightness
                        ? m.MinBrightness + (uint)((m.MaxBrightness - m.MinBrightness) * percent / 100.0)
                        : (uint)percent;
                    success = SetMonitorBrightness(m.Handle, target)
                           || SetVCPFeature(m.Handle, VCP_LUMINANCE, target);
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
                if (m.SupportsBrightness)
                {
                    uint target = m.MaxBrightness > m.MinBrightness
                        ? m.MinBrightness + (uint)((m.MaxBrightness - m.MinBrightness) * percent / 100.0)
                        : (uint)percent;
                    if (SetMonitorBrightness(m.Handle, target) || SetVCPFeature(m.Handle, VCP_LUMINANCE, target))
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
