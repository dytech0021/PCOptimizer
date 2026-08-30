using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PCOptimizer.Services
{
    /// <summary>
    /// CPU Sets são uma preferência "suave": o Windows tenta usar os processadores
    /// indicados, mas ainda pode escapar deles quando necessário. Isso preserva o
    /// Thread Director e evita o gargalo da afinidade rígida.
    /// </summary>
    internal static class CpuSetService
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemCpuSetInformation(IntPtr information,
            uint bufferLength, out uint returnedLength, IntPtr process, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessDefaultCpuSets(IntPtr process,
            [Out] uint[]? cpuSetIds, uint cpuSetIdCount, out uint requiredIdCount);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessDefaultCpuSets(IntPtr process,
            uint[]? cpuSetIds, uint cpuSetIdCount);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private const uint PROCESS_SET_LIMITED_INFORMATION   = 0x2000;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        public static uint[] GetIdsForMask(ulong logicalMask)
        {
            var result = new List<uint>();
            IntPtr buffer = IntPtr.Zero;
            try
            {
                GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint length, IntPtr.Zero, 0);
                if (length == 0) return Array.Empty<uint>();

                buffer = Marshal.AllocHGlobal((int)length);
                if (!GetSystemCpuSetInformation(buffer, length, out uint returned,
                                                IntPtr.Zero, 0))
                    return Array.Empty<uint>();

                uint offset = 0;
                while (offset + 8 <= returned)
                {
                    IntPtr entry = IntPtr.Add(buffer, (int)offset);
                    int size = Marshal.ReadInt32(entry, 0);
                    int type = Marshal.ReadInt32(entry, 4);
                    if (size < 8 || offset + (uint)size > returned) break;

                    // SYSTEM_CPU_SET_INFORMATION.CpuSet:
                    // Id +8, Group +12, LogicalProcessorIndex +14.
                    if (type == 0 && size >= 20)
                    {
                        uint id = unchecked((uint)Marshal.ReadInt32(entry, 8));
                        ushort group = unchecked((ushort)Marshal.ReadInt16(entry, 12));
                        byte logical = Marshal.ReadByte(entry, 14);
                        if (group == 0 && logical < 64 &&
                            (logicalMask & (1UL << logical)) != 0)
                            result.Add(id);
                    }
                    offset += (uint)size;
                }
            }
            catch (EntryPointNotFoundException) { }
            catch (Exception ex) { Logger.Error(ex, "CpuSet.GetIdsForMask"); }
            finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); }
            return result.ToArray();
        }

        public static bool TrySet(int pid, uint[] ids, out uint[] previous)
        {
            previous = Array.Empty<uint>();
            if (ids.Length == 0) return false;

            IntPtr handle = OpenProcess(PROCESS_SET_LIMITED_INFORMATION |
                                        PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return false;
            try
            {
                previous = Read(handle);
                return SetProcessDefaultCpuSets(handle, ids, (uint)ids.Length);
            }
            catch (EntryPointNotFoundException) { return false; }
            catch (Exception ex) { Logger.Error(ex, $"CpuSet.TrySet({pid})"); return false; }
            finally { CloseHandle(handle); }
        }

        public static bool Restore(int pid, uint[] previous)
        {
            IntPtr handle = OpenProcess(PROCESS_SET_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return false;
            try
            {
                return SetProcessDefaultCpuSets(handle,
                    previous.Length == 0 ? null : previous, (uint)previous.Length);
            }
            catch { return false; }
            finally { CloseHandle(handle); }
        }

        private static uint[] Read(IntPtr handle)
        {
            try
            {
                GetProcessDefaultCpuSets(handle, null, 0, out uint count);
                if (count == 0) return Array.Empty<uint>();
                var ids = new uint[count];
                return GetProcessDefaultCpuSets(handle, ids, count, out _)
                    ? ids : Array.Empty<uint>();
            }
            catch { return Array.Empty<uint>(); }
        }
    }
}
