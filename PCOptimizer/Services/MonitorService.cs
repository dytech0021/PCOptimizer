using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;

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

        // ── WMI fallback (notebooks com painel integrado sem DDC/CI) ──────────

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

        private static bool SetWmiBrightness(int percent)
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

        // ─────────────────────────────────────────────────────────────────────

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
                    uint newValue = m.MinBrightness + (uint)(range * percent / 100.0);
                    if (SetMonitorBrightness(m.Handle, newValue))
                        success++;
                }
                DestroyPhysicalMonitor(m.Handle);
            }

            // Fallback WMI para notebooks sem DDC/CI
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
                    uint newValue = m.MinContrast + (uint)(range * percent / 100.0);
                    if (SetMonitorContrast(m.Handle, newValue))
                        success++;
                }
                DestroyPhysicalMonitor(m.Handle);
            }

            return success;
        }

        public static (int Brightness, int Contrast, int Count, bool IsWmi) GetAverageValues()
        {
            var monitors = GetMonitors();
            int totalBright = 0, brightCount = 0;
            int totalContrast = 0, contrastCount = 0;

            foreach (var m in monitors)
            {
                if (m.SupportsBrightness && m.MaxBrightness > m.MinBrightness)
                {
                    int pct = (int)((m.CurrentBrightness - m.MinBrightness) * 100.0 / (m.MaxBrightness - m.MinBrightness));
                    totalBright += pct;
                    brightCount++;
                }
                if (m.SupportsContrast && m.MaxContrast > m.MinContrast)
                {
                    int pct = (int)((m.CurrentContrast - m.MinContrast) * 100.0 / (m.MaxContrast - m.MinContrast));
                    totalContrast += pct;
                    contrastCount++;
                }
                DestroyPhysicalMonitor(m.Handle);
            }

            // Fallback WMI para notebooks
            if (monitors.Count == 0 && HasWmiMonitors())
            {
                int wmiB = GetWmiBrightness();
                return (wmiB, 50, 1, true);
            }

            int avgBright = brightCount > 0 ? totalBright / brightCount : 50;
            int avgContrast = contrastCount > 0 ? totalContrast / contrastCount : 50;
            return (avgBright, avgContrast, monitors.Count, false);
        }
    }
}
