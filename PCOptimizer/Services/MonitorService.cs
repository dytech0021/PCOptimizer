using System;
using System.Collections.Generic;
using System.Linq;
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
        // Modelo informado pelo PRÓPRIO monitor, pelo mesmo canal DDC/CI usado
        // para brilho/contraste. Como vem do mesmo handle que controlamos, é
        // impossível ficar trocado entre monitores.
        public string DdcModel { get; set; } = "";
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
        public bool IsSoftware { get; set; }      // brilho via overlay (sem DDC/CI nem WMI)
        public string DeviceKey { get; set; } = ""; // identidade p/ o overlay de software
        public int ScreenLeft { get; set; }       // bounds em pixels físicos (PerMonitorV2)
        public int ScreenTop { get; set; }
        public int ScreenWidth { get; set; }
        public int ScreenHeight { get; set; }
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

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetCapabilitiesStringLength(IntPtr hMonitor, out uint length);

        [DllImport("dxva2.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern bool CapabilitiesRequestAndCapabilitiesReply(
            IntPtr hMonitor, StringBuilder asciiString, uint length);

        private const byte VCP_LUMINANCE = 0x10;
        // VCP 0x86 "Display Scaling": 0x02 = imagem máxima SEM distorcer a
        // proporção (gera as barras pretas), 0x03 = estica para preencher.
        private const byte VCP_DISPLAY_SCALING = 0x86;
        private const uint SCALING_ASPECT  = 0x02;
        private const uint SCALING_STRETCH = 0x03;

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
        /// <summary>
        /// Caminho da interface do monitor (\\?\DISPLAY#MG900#...#{guid}) — o
        /// MESMO formato que o DisplayConfig devolve em monitorDevicePath, o que
        /// permite casar as duas enumerações sem depender de posição/ordem.
        /// </summary>
        /// <summary>
        /// Pergunta o MODELO ao próprio monitor pelo canal DDC/CI (string de
        /// capacidades, campo "model"). Vem do mesmo handle que usamos para
        /// brilho/contraste — então o nome nunca pode pertencer a outro monitor.
        /// Retorna "" quando o monitor não informa o modelo.
        /// </summary>
        // O modelo nunca muda para um mesmo monitor, e a consulta é lenta (ida e
        // volta no barramento DDC). Guarda por monitor e só refaz se o vídeo mudar.
        private static readonly Dictionary<string, string> _ddcModelCache =
            new(StringComparer.OrdinalIgnoreCase);

        private static string GetDdcModelCached(string key, IntPtr hPhysicalMonitor)
        {
            if (!string.IsNullOrEmpty(key))
            {
                lock (_ddcModelCache)
                    if (_ddcModelCache.TryGetValue(key, out var hit)) return hit;
            }

            string model = GetDdcModel(hPhysicalMonitor);

            if (!string.IsNullOrEmpty(key))
                lock (_ddcModelCache) _ddcModelCache[key] = model;
            return model;
        }

        private static string GetDdcModel(IntPtr hPhysicalMonitor)
        {
            try
            {
                if (!GetCapabilitiesStringLength(hPhysicalMonitor, out uint len)
                    || len == 0 || len > 65536) return "";

                var sb = new StringBuilder((int)len + 1);
                if (!CapabilitiesRequestAndCapabilitiesReply(hPhysicalMonitor, sb, len)) return "";

                // Formato: (prot(monitor)type(lcd)model(MG900)cmds(...)vcp(...))
                var m = System.Text.RegularExpressions.Regex.Match(
                    sb.ToString(), @"model\(([^)]+)\)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                return m.Success ? m.Groups[1].Value.Trim() : "";
            }
            catch { return ""; }
        }

        private static string GetMonitorInterfacePath(IntPtr hMonitor)
        {
            try
            {
                var mi = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
                if (!GetMonitorInfo(hMonitor, ref mi)) return "";

                for (uint i = 0; ; i++)
                {
                    var dd = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
                    // 1 = EDD_GET_DEVICE_INTERFACE_NAME
                    if (!EnumDisplayDevices(mi.szDevice, i, ref dd, 1)) break;
                    if (!string.IsNullOrEmpty(dd.DeviceID) &&
                        dd.DeviceID.StartsWith(@"\\?\", StringComparison.Ordinal))
                        return dd.DeviceID;
                }
            }
            catch { }
            return "";
        }

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

        // ── Cache de handles físicos ──────────────────────────────────────────
        // Enumerar monitores via DDC/CI custa centenas de ms (leituras I2C).
        // Os handles ficam vivos entre os sets para a barra responder na hora;
        // se um set falhar (monitor reconectado), re-enumera e tenta de novo.

        private static readonly object _cacheLock = new();
        private static List<MonitorInfo>? _cache;

        // Só re-enumera quando a configuração de vídeo REALMENTE muda. Antes o
        // cache era descartado a cada abertura da janela, refazendo toda a sondagem
        // DDC (brilho, contraste e a consulta de modelo, que é lenta) — era isso
        // que deixava a janela de brilho demorada para abrir.
        private static volatile bool _displayDirty = true;

        static MonitorService()
        {
            try
            {
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) =>
                {
                    _displayDirty = true;
                    _edidCache = null;
                    _hasWmiCache = null;
                    lock (_ddcModelCache) _ddcModelCache.Clear();
                    DisplayResolutionService.InvalidateNativeCache();
                    // As barras secundárias mudam junto com os monitores
                    TaskbarTransparencyService.InvalidateBarsCache();
                };
            }
            catch { /* sem bomba de mensagens — segue sem invalidação automática */ }
        }

        /// <summary>Força re-enumeração na próxima leitura (troca de monitores).</summary>
        public static void MarkDisplaysChanged() => _displayDirty = true;

        private static List<MonitorInfo> GetCachedMonitors()
        {
            lock (_cacheLock) { return _cache ??= GetMonitors(); }
        }

        private static void InvalidateCache()
        {
            lock (_cacheLock)
            {
                if (_cache != null)
                    foreach (var m in _cache) DestroyPhysicalMonitor(m.Handle);
                _cache = null;
            }
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

                string modelKey = GetMonitorInterfacePath(hMonitor);
                int physIdx = 0;

                foreach (var pm in phys)
                {
                    var info = new MonitorInfo
                    {
                        Handle = pm.hPhysicalMonitor,
                        LogicalHandle = hMonitor,
                        Name = pm.szPhysicalMonitorDescription,
                        DdcModel = GetDdcModelCached(
                            modelKey.Length > 0 ? $"{modelKey}#{physIdx++}" : "", pm.hPhysicalMonitor)
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

                    // DDC/CI falha esporadicamente (barramento I2C lento/ocupado) —
                    // uma segunda tentativa após pausa resolve muitos casos.
                    if (!info.SupportsBrightness && !info.SupportsContrast)
                    {
                        System.Threading.Thread.Sleep(120);

                        if (GetMonitorBrightness(pm.hPhysicalMonitor, ref bMin, ref bCur, ref bMax)
                            && bMax > bMin)
                        {
                            info.MinBrightness      = bMin;
                            info.CurrentBrightness  = bCur;
                            info.MaxBrightness      = bMax;
                            info.SupportsBrightness = true;
                        }
                        else if (GetVCPFeatureAndVCPFeatureReply(pm.hPhysicalMonitor, VCP_LUMINANCE,
                                     out _, out uint vcpCur2, out uint vcpMax2) && vcpMax2 > 0)
                        {
                            info.MinBrightness      = 0;
                            info.CurrentBrightness  = vcpCur2;
                            info.MaxBrightness      = vcpMax2;
                            info.SupportsBrightness = true;
                        }

                        if (GetMonitorContrast(pm.hPhysicalMonitor, ref cMin, ref cCur, ref cMax)
                            && cMax > cMin)
                        {
                            info.MinContrast      = cMin;
                            info.CurrentContrast  = cCur;
                            info.MaxContrast      = cMax;
                            info.SupportsContrast = true;
                        }
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

        // Consulta WMI custa centenas de ms e o resultado só muda quando o vídeo
        // muda — a janela de brilho reabre com frequência e pagava isso toda vez.
        private static bool? _hasWmiCache;

        private static bool HasWmiMonitors()
        {
            if (_hasWmiCache.HasValue) return _hasWmiCache.Value;
            _hasWmiCache = HasWmiMonitorsUncached();
            return _hasWmiCache.Value;
        }

        private static bool HasWmiMonitorsUncached()
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
                using var results = s.Get();
                foreach (ManagementObject obj in results)
                    using (obj) return Convert.ToInt32(obj["CurrentBrightness"]);
            }
            catch { }
            return 50;
        }

        public static bool SetWmiBrightness(int percent)
        {
            try
            {
                using var s = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
                using var results = s.Get();
                foreach (ManagementObject obj in results)
                    using (obj)
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
        // Consultas WMI custam centenas de ms; o resultado só muda quando o vídeo
        // muda, então fica guardado (a lista crua, para remontar as filas).
        private static List<(string PnpId, EdidInfo Info)>? _edidCache;

        private static Dictionary<string, Queue<EdidInfo>> GetEdidInfosByPnpId()
        {
            if (_edidCache != null)
            {
                var cached = new Dictionary<string, Queue<EdidInfo>>(StringComparer.OrdinalIgnoreCase);
                foreach (var (pnp, info) in _edidCache)
                {
                    if (!cached.TryGetValue(pnp, out var q))
                        cached[pnp] = q = new Queue<EdidInfo>();
                    q.Enqueue(info);
                }
                return cached;
            }

            var result = new Dictionary<string, Queue<EdidInfo>>(StringComparer.OrdinalIgnoreCase);
            var flat = new List<(string, EdidInfo)>();
            try
            {
                using var s = new ManagementObjectSearcher("root\\WMI",
                    "SELECT ManufacturerName, UserFriendlyName, InstanceName FROM WmiMonitorID");
                using var results = s.Get();
                int idx = 0;
                foreach (ManagementObject obj in results)
                {
                    using var _ = obj; // libera o handle COM já nesta iteração
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

                    flat.Add((pnpId, new EdidInfo
                    {
                        Manufacturer = MapManufacturer(mfr),
                        FriendlyName = friendly,
                        HardwareId   = hwId
                    }));
                    result[pnpId].Enqueue(new EdidInfo
                    {
                        Manufacturer = MapManufacturer(mfr),
                        FriendlyName = friendly,
                        HardwareId   = hwId
                    });
                    idx++;
                }
                _edidCache = flat;
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
                using var results = s.Get();
                foreach (ManagementObject obj in results)
                {
                    using var _ = obj; // libera o handle COM já nesta iteração
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
            // Re-enumera só se o vídeo mudou desde a última vez
            if (_displayDirty)
            {
                InvalidateCache();
                _displayDirty = false;
            }
            var monitors = GetCachedMonitors();

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

                // O nome vindo do DisplayConfig (hdr.FriendlyName, resolvido mais
                // abaixo) tem prioridade: vem do MESMO path que os dados de HDR,
                // então é impossível trocar entre monitores. A via WMI (fila por
                // PnP ID) invertia os nomes entre monitores IGUAIS, porque casava
                // duas enumerações diferentes só pela ordem.
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

                // HDR info — correlate by screen position (rcMonitor.left/top == DisplayConfig source position)
                // Também guarda os bounds em px físicos e o nome do device (\\.\DISPLAYn),
                // usados para posicionar o overlay de brilho por software.
                int srcX = 0, srcY = 0, scrW = 0, scrH = 0;
                string deviceKey = "";
                var mInfoEx = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(m.LogicalHandle, ref mInfoEx))
                {
                    srcX = mInfoEx.rcMonitor.left;
                    srcY = mInfoEx.rcMonitor.top;
                    scrW = mInfoEx.rcMonitor.right - mInfoEx.rcMonitor.left;
                    scrH = mInfoEx.rcMonitor.bottom - mInfoEx.rcMonitor.top;
                    deviceKey = mInfoEx.szDevice;
                }
                // Correlação EXATA pelo caminho da interface do monitor; a posição
                // na área de trabalho fica só como reserva (era ela que trocava os
                // nomes entre os monitores).
                string ifacePath = GetMonitorInterfacePath(m.LogicalHandle);
                var hdr = !string.IsNullOrEmpty(ifacePath)
                    ? hdrInfos.Find(h => !string.IsNullOrEmpty(h.DevicePath) &&
                                         string.Equals(h.DevicePath, ifacePath,
                                                       StringComparison.OrdinalIgnoreCase))
                    : null;
                hdr ??= hdrInfos.Find(h => h.SourceX == srcX && h.SourceY == srcY);

                // Nome exato do DisplayConfig (mesmo path do HDR) vence a via WMI
                if (!string.IsNullOrEmpty(hdr?.FriendlyName))
                {
                    name = hdr!.FriendlyName;
                    if (!string.IsNullOrEmpty(edid.Manufacturer) &&
                        !name.StartsWith(edid.Manufacturer, StringComparison.OrdinalIgnoreCase))
                        name = $"{edid.Manufacturer} {name}";
                }

                // MELHOR FONTE: o modelo que o PRÓPRIO monitor informa pelo canal
                // DDC/CI. Vem do mesmo handle que controla o brilho deste item da
                // lista, então nome e controles não têm como pertencer a monitores
                // diferentes — que era a origem da inversão MG900/MG800.
                if (!string.IsNullOrEmpty(m.DdcModel))
                {
                    name = m.DdcModel;
                    if (!string.IsNullOrEmpty(edid.Manufacturer) &&
                        !name.StartsWith(edid.Manufacturer, StringComparison.OrdinalIgnoreCase))
                        name = $"{edid.Manufacturer} {name}";
                    // Identificador estável: o caminho da interface não muda de
                    // valor quando o monitor troca de posição (a posição, sim) —
                    // assim os apelidos salvos continuam no monitor certo.
                    string disc = !string.IsNullOrEmpty(ifacePath)
                        ? Math.Abs(ifacePath.GetHashCode()).ToString("x8")
                        : $"{srcX}x{srcY}";
                    hwId = $"ddc_{m.DdcModel}_{disc}";
                }

                // WMI fallback SÓ para o painel interno do notebook — WmiSetBrightness
                // não controla monitores externos; aplicá-lo neles fazia a barra do
                // monitor externo mudar o brilho do painel do notebook.
                bool isInternalPanel = hdr?.IsInternal ?? (monitors.Count == 1);
                if (!supportsBrightness && wmiAvailable && isInternalPanel)
                {
                    brightness         = wmiBrightness;
                    supportsBrightness = true;
                    isWmi              = true;
                }

                // Sem DDC/CI e sem WMI (monitor simples por HDMI): brilho por software
                // — escurece a imagem via overlay. Melhor que um controle morto.
                bool isSoftware = false;
                string swKey = !string.IsNullOrEmpty(deviceKey) ? deviceKey : hwId;
                if (!supportsBrightness)
                {
                    brightness         = SoftwareBrightnessService.GetBrightness(swKey);
                    supportsBrightness = true;
                    isSoftware         = true;
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
                    IsWmi              = isWmi,
                    IsSoftware         = isSoftware,
                    DeviceKey          = swKey,
                    ScreenLeft         = srcX,
                    ScreenTop          = srcY,
                    ScreenWidth        = scrW,
                    ScreenHeight       = scrH,
                    // hdr != null só significa "path ativo" — o botão de HDR aparecia
                    // até em monitor SDR comum. O que vale é o bit de suporte.
                    SupportsHdr        = hdr?.IsSupported ?? false,
                    HdrEnabled         = hdr?.IsEnabled ?? false,
                    HdrAdapterIdLow    = hdr?.AdapterIdLow ?? 0,
                    HdrAdapterIdHigh   = hdr?.AdapterIdHigh ?? 0,
                    HdrTargetId        = hdr?.TargetId ?? 0
                });
                // Handle fica vivo no cache para os sets serem instantâneos
            }

            // Monitores IGUAIS geram o mesmo HardwareId (vem do modelo do EDID) —
            // aí os apelidos que o usuário salva se misturam entre eles. Só nesse
            // caso desempata pelo id do conector, preservando os apelidos já
            // salvos de quem não tem duplicata.
            var dupes = entries.GroupBy(e => e.HardwareId)
                               .Where(g => g.Count() > 1)
                               .Select(g => g.Key)
                               .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (dupes.Count > 0)
                foreach (var e in entries)
                    if (dupes.Contains(e.HardwareId))
                        // TargetId é 0 quando a correlação por posição falha — aí
                        // dois monitores iguais receberiam o MESMO sufixo e a
                        // colisão continuaria. O índice desempata nesse caso.
                        e.HardwareId = e.HdrTargetId != 0
                            ? $"{e.HardwareId}#{e.HdrTargetId}"
                            : $"{e.HardwareId}#i{e.Index}";

            return entries;
        }

        public static bool SetBrightnessForIndex(int monitorIndex, int percent)
        {
            percent = Math.Clamp(percent, 0, 100);
            if (TrySetBrightness(GetCachedMonitors(), monitorIndex, percent)) return true;
            // Handle pode ter ficado inválido (monitor des/reconectado) — re-enumera
            InvalidateCache();
            return TrySetBrightness(GetCachedMonitors(), monitorIndex, percent);
        }

        private static bool TrySetBrightness(List<MonitorInfo> monitors, int i, int percent)
        {
            if (i < 0 || i >= monitors.Count) return false;
            var m = monitors[i];
            if (!m.SupportsBrightness) return false;
            uint target = m.MaxBrightness > m.MinBrightness
                ? m.MinBrightness + (uint)((m.MaxBrightness - m.MinBrightness) * percent / 100.0)
                : (uint)percent;
            bool ok = SetMonitorBrightness(m.Handle, target)
                   || SetVCPFeature(m.Handle, VCP_LUMINANCE, target);
            // Mantém o cache coerente: como não re-enumeramos a cada abertura da
            // janela, sem isto ela reabriria mostrando o valor antigo.
            if (ok) m.CurrentBrightness = target;
            return ok;
        }

        /// <summary>
        /// Pede ao MONITOR para preservar a proporção da imagem (barras pretas)
        /// ou esticar para preencher a tela, via DDC/CI (VCP 0x86). Nem todo
        /// monitor implementa esse código — retorna false quando não aceita, e aí
        /// resta ajustar no menu do monitor ou no painel da placa de vídeo.
        /// </summary>
        /// <summary>
        /// Relatório do que o app enxerga de cada monitor e de QUAL fonte veio o
        /// nome. Serve para diagnosticar nomes trocados sem ficar no chute.
        /// </summary>
        public static string Diagnose()
        {
            var sb = new StringBuilder();
            try
            {
                var entries = GetMonitorEntries();
                var raw = GetCachedMonitors();
                sb.AppendLine($"Monitores detectados: {entries.Count}");
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    string ddc = i < raw.Count ? raw[i].DdcModel : "";
                    string desc = i < raw.Count ? raw[i].Name : "";
                    sb.AppendLine($"\n[{i}] {e.Name}");
                    sb.AppendLine($"   modelo via DDC : {(ddc.Length > 0 ? ddc : "(monitor não informa)")}");
                    sb.AppendLine($"   descrição DDC  : {desc}");
                    sb.AppendLine($"   posição/tamanho: {e.ScreenLeft},{e.ScreenTop} {e.ScreenWidth}x{e.ScreenHeight}");
                    sb.AppendLine($"   device         : {e.DeviceKey}");
                    sb.AppendLine($"   id salvo       : {e.HardwareId}");
                    sb.AppendLine($"   brilho/contr.  : {e.SupportsBrightness}/{e.SupportsContrast}  HDR: {e.SupportsHdr}");
                }
            }
            catch (Exception ex) { sb.AppendLine("Erro: " + ex.Message); }
            return sb.ToString();
        }

        public static bool SetAspectScaling(int monitorIndex, bool preserveAspect)
        {
            var monitors = GetCachedMonitors();
            if (monitorIndex < 0 || monitorIndex >= monitors.Count) return false;
            return SetVCPFeature(monitors[monitorIndex].Handle, VCP_DISPLAY_SCALING,
                preserveAspect ? SCALING_ASPECT : SCALING_STRETCH);
        }

        public static bool SetContrastForIndex(int monitorIndex, int percent)
        {
            percent = Math.Clamp(percent, 0, 100);
            if (TrySetContrast(GetCachedMonitors(), monitorIndex, percent)) return true;
            InvalidateCache();
            return TrySetContrast(GetCachedMonitors(), monitorIndex, percent);
        }

        private static bool TrySetContrast(List<MonitorInfo> monitors, int i, int percent)
        {
            if (i < 0 || i >= monitors.Count) return false;
            var m = monitors[i];
            if (!m.SupportsContrast || m.MaxContrast < m.MinContrast) return false;
            uint range = m.MaxContrast - m.MinContrast;
            uint target = m.MinContrast + (uint)(range * percent / 100.0);
            bool ok = SetMonitorContrast(m.Handle, target);
            if (ok) m.CurrentContrast = target;   // mantém o cache coerente
            return ok;
        }

        // ── All-monitors helpers (used by presets) ────────────────────────────

        public static int SetBrightnessAll(int percent)
        {
            percent = Math.Clamp(percent, 0, 100);
            int success = SetBrightnessAllCore(GetCachedMonitors(), percent);
            if (success == 0)
            {
                InvalidateCache();
                success = SetBrightnessAllCore(GetCachedMonitors(), percent);
            }

            if (success == 0 && HasWmiMonitors())
                success = SetWmiBrightness(percent) ? 1 : 0;

            return success;
        }

        private static int SetBrightnessAllCore(List<MonitorInfo> monitors, int percent)
        {
            int success = 0;
            foreach (var m in monitors)
            {
                if (!m.SupportsBrightness) continue;
                uint target = m.MaxBrightness > m.MinBrightness
                    ? m.MinBrightness + (uint)((m.MaxBrightness - m.MinBrightness) * percent / 100.0)
                    : (uint)percent;
                if (SetMonitorBrightness(m.Handle, target) || SetVCPFeature(m.Handle, VCP_LUMINANCE, target))
                    success++;
            }
            return success;
        }

        public static int SetContrastAll(int percent)
        {
            percent = Math.Clamp(percent, 0, 100);
            int success = SetContrastAllCore(GetCachedMonitors(), percent);
            if (success == 0)
            {
                InvalidateCache();
                success = SetContrastAllCore(GetCachedMonitors(), percent);
            }
            return success;
        }

        private static int SetContrastAllCore(List<MonitorInfo> monitors, int percent)
        {
            int success = 0;
            foreach (var m in monitors)
            {
                if (!m.SupportsContrast || m.MaxContrast < m.MinContrast) continue;
                uint range = m.MaxContrast - m.MinContrast;
                if (SetMonitorContrast(m.Handle, m.MinContrast + (uint)(range * percent / 100.0)))
                    success++;
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
