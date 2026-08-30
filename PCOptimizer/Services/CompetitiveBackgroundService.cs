using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Dá preferência aos E-cores para tarefas de fundo. Só aplica EcoQoS e
    /// prioridade baixa quando uma delas realmente começa a disputar CPU — uma
    /// versão deliberadamente conservadora do ProBalance.
    /// </summary>
    internal static class CompetitiveBackgroundService
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetPriorityClass(IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetPriorityClass(IntPtr process, uint priorityClass);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessInformation(IntPtr process,
            int processInformationClass, out PowerThrottlingState information,
            uint informationSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(IntPtr process,
            int processInformationClass, ref PowerThrottlingState information,
            uint informationSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ProcessIdToSessionId(int pid, out int sessionId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags,
            StringBuilder exeName, ref uint size);

        private const uint PROCESS_SET_INFORMATION           = 0x0200;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint BELOW_NORMAL_PRIORITY_CLASS       = 0x00004000;
        private const int  ProcessPowerThrottling            = 4;
        private const uint ExecutionSpeed                    = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct PowerThrottlingState
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        public sealed class Entry
        {
            public int Pid { get; set; }
            public string Name { get; set; } = "";
            public long StartUtcTicks { get; set; }
            public uint[] OriginalCpuSets { get; set; } = Array.Empty<uint>();
            public uint OriginalPriority { get; set; }
            public uint OriginalEcoState { get; set; }
            public bool CpuSetsChanged { get; set; }
            public bool PriorityChanged { get; set; }
            public bool EcoChanged { get; set; }

            // Somente amostragem em memória; não é necessário para restauração.
            public long LastCpuTicks { get; set; }
            public long LastSampleUtcTicks { get; set; }
            public int CoolTicks { get; set; }
        }

        private static readonly HashSet<string> ProtectedNames =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "audiodg", "dwm", "explorer", "csrss", "wininit", "winlogon",
                "smss", "services", "lsass", "fontdrvhost", "Registry",
                "MemCompression", "System", "Idle", "PCOptimizer",
                "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService", "BEDaisy",
                "vgc", "vgtray", "GameMon", "GameMon64", "steamservice",
                "GameOverlayUI", "obs64", "obs32", "nvcontainer",
                "NVDisplay.Container"
            };

        private static readonly object Sync = new();
        private static readonly Dictionary<int, Entry> Entries = new();
        private static uint[] _eCoreIds = Array.Empty<uint>();
        private static int _gamePid;
        private static string? _gameDir;
        private static double _threshold;

        private static string StatePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCOptimizer", "competitive-processes.json");

        public static int ManagedCount { get { lock (Sync) return Entries.Count; } }
        public static int RestrainedCount
        {
            get { lock (Sync) return Entries.Values.Count(e => e.EcoChanged || e.PriorityChanged); }
        }

        public static bool Start(int gamePid, string? gamePath, double thresholdPercent)
        {
            _eCoreIds = CpuSetService.GetIdsForMask(CpuTopologyService.Get().ECoreMask);
            if (_eCoreIds.Length == 0) return false;
            _gamePid = gamePid;
            _gameDir = string.IsNullOrEmpty(gamePath) ? null : Path.GetDirectoryName(gamePath);
            _threshold = thresholdPercent;
            ScanNewProcesses();
            return true;
        }

        public static void Tick(bool scanNew)
        {
            if (scanNew) ScanNewProcesses();

            List<Entry> snapshot;
            lock (Sync) snapshot = Entries.Values.ToList();
            foreach (var entry in snapshot)
            {
                try
                {
                    using var process = Process.GetProcessById(entry.Pid);
                    if (process.StartTime.ToUniversalTime().Ticks != entry.StartUtcTicks)
                    {
                        RemoveDead(entry.Pid);
                        continue;
                    }

                    long now = DateTime.UtcNow.Ticks;
                    long cpu = process.TotalProcessorTime.Ticks;
                    if (entry.LastSampleUtcTicks != 0)
                    {
                        double elapsed = TimeSpan.FromTicks(now - entry.LastSampleUtcTicks).TotalSeconds;
                        double cpuSeconds = TimeSpan.FromTicks(cpu - entry.LastCpuTicks).TotalSeconds;
                        double percent = elapsed <= 0 ? 0 :
                            cpuSeconds / elapsed * 100.0 / Math.Max(1, Environment.ProcessorCount);

                        if (percent >= _threshold)
                        {
                            entry.CoolTicks = 0;
                            Restrain(entry);
                        }
                        else if ((entry.EcoChanged || entry.PriorityChanged) && ++entry.CoolTicks >= 3)
                        {
                            Unrestrain(entry);
                            entry.CoolTicks = 0;
                        }
                    }
                    entry.LastCpuTicks = cpu;
                    entry.LastSampleUtcTicks = now;
                }
                catch { RemoveDead(entry.Pid); }
            }
        }

        private static void ScanNewProcesses()
        {
            Process[] processes;
            try { processes = Process.GetProcesses(); }
            catch { return; }

            bool changed = false;
            foreach (var process in processes)
            {
                try
                {
                    int pid = process.Id;
                    string name = process.ProcessName;
                    lock (Sync) if (Entries.ContainsKey(pid)) continue;
                    if (ShouldSkip(pid, name)) continue;

                    long start = process.StartTime.ToUniversalTime().Ticks;
                    if (!CpuSetService.TrySet(pid, _eCoreIds, out uint[] previous)) continue;

                    var entry = new Entry
                    {
                        Pid = pid,
                        Name = name,
                        StartUtcTicks = start,
                        OriginalCpuSets = previous,
                        CpuSetsChanged = true,
                        OriginalPriority = ReadPriority(pid),
                        OriginalEcoState = ReadEcoState(pid),
                        LastCpuTicks = process.TotalProcessorTime.Ticks,
                        LastSampleUtcTicks = DateTime.UtcNow.Ticks
                    };
                    lock (Sync) Entries[pid] = entry;
                    changed = true;
                }
                catch { }
                finally { process.Dispose(); }
            }
            if (changed) SaveState();
        }

        private static bool ShouldSkip(int pid, string name)
        {
            if (pid <= 4 || pid == Environment.ProcessId || pid == _gamePid) return true;
            if (string.IsNullOrWhiteSpace(name) || ProtectedNames.Contains(name)) return true;
            if (ProcessIdToSessionId(pid, out int session) && session == 0) return true;

            string? path = GetPath(pid);
            if (path == null) return false;
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (path.StartsWith(Path.Combine(windows, "System32"), StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(Path.Combine(windows, "SysWOW64"), StringComparison.OrdinalIgnoreCase))
                return true;
            if (_gameDir != null && path.StartsWith(_gameDir, StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        private static string? GetPath(int pid)
        {
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var text = new StringBuilder(1024);
                uint size = 1024;
                return QueryFullProcessImageName(handle, 0, text, ref size) ? text.ToString() : null;
            }
            catch { return null; }
            finally { CloseHandle(handle); }
        }

        private static void Restrain(Entry entry)
        {
            if (!entry.EcoChanged && SetEco(entry.Pid, true)) entry.EcoChanged = true;
            if (!entry.PriorityChanged && SetPriority(entry.Pid, BELOW_NORMAL_PRIORITY_CLASS))
                entry.PriorityChanged = true;
            SaveState();
        }

        private static void Unrestrain(Entry entry)
        {
            if (entry.EcoChanged)
            {
                SetEco(entry.Pid, (entry.OriginalEcoState & ExecutionSpeed) != 0);
                entry.EcoChanged = false;
            }
            if (entry.PriorityChanged && entry.OriginalPriority != 0)
            {
                SetPriority(entry.Pid, entry.OriginalPriority);
                entry.PriorityChanged = false;
            }
            SaveState();
        }

        public static int RestoreAll()
        {
            List<Entry> snapshot;
            lock (Sync)
            {
                snapshot = Entries.Values.ToList();
                Entries.Clear();
            }
            int restored = 0;
            foreach (var entry in snapshot)
                if (RestoreEntry(entry)) restored++;
            ClearState();
            _gamePid = 0;
            _gameDir = null;
            return restored;
        }

        public static void RestoreOrphansFromPreviousRun()
        {
            try
            {
                if (!File.Exists(StatePath)) return;
                var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(StatePath));
                if (entries != null)
                {
                    int n = 0;
                    foreach (var entry in entries) if (RestoreEntry(entry)) n++;
                    if (n > 0) Logger.Warn($"Competitivo: {n} processo(s) restaurado(s) após interrupção");
                }
            }
            catch (Exception ex) { Logger.Error(ex, "CompetitiveBackground.RestoreOrphans"); }
            finally { ClearState(); }
        }

        private static bool RestoreEntry(Entry entry)
        {
            try
            {
                using var process = Process.GetProcessById(entry.Pid);
                if (process.StartTime.ToUniversalTime().Ticks != entry.StartUtcTicks) return false;
            }
            catch { return false; }

            bool ok = false;
            if (entry.CpuSetsChanged)
                ok |= CpuSetService.Restore(entry.Pid, entry.OriginalCpuSets);
            if (entry.EcoChanged)
                ok |= SetEco(entry.Pid, (entry.OriginalEcoState & ExecutionSpeed) != 0);
            if (entry.PriorityChanged && entry.OriginalPriority != 0)
                ok |= SetPriority(entry.Pid, entry.OriginalPriority);
            return ok;
        }

        private static void RemoveDead(int pid)
        {
            lock (Sync) Entries.Remove(pid);
            SaveState();
        }

        private static uint ReadPriority(int pid)
        {
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return 0;
            try { return GetPriorityClass(handle); }
            catch { return 0; }
            finally { CloseHandle(handle); }
        }

        private static bool SetPriority(int pid, uint priority)
        {
            IntPtr handle = OpenProcess(PROCESS_SET_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return false;
            try { return SetPriorityClass(handle, priority); }
            catch { return false; }
            finally { CloseHandle(handle); }
        }

        private static uint ReadEcoState(int pid)
        {
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return 0;
            try
            {
                return GetProcessInformation(handle, ProcessPowerThrottling,
                    out PowerThrottlingState state, (uint)Marshal.SizeOf<PowerThrottlingState>())
                    ? state.StateMask : 0;
            }
            catch { return 0; }
            finally { CloseHandle(handle); }
        }

        private static bool SetEco(int pid, bool enabled)
        {
            IntPtr handle = OpenProcess(PROCESS_SET_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return false;
            try
            {
                var state = new PowerThrottlingState
                {
                    Version = 1,
                    ControlMask = ExecutionSpeed,
                    StateMask = enabled ? ExecutionSpeed : 0
                };
                return SetProcessInformation(handle, ProcessPowerThrottling, ref state,
                    (uint)Marshal.SizeOf<PowerThrottlingState>());
            }
            catch (EntryPointNotFoundException) { return false; }
            catch { return false; }
            finally { CloseHandle(handle); }
        }

        private static void SaveState()
        {
            try
            {
                List<Entry> snapshot;
                lock (Sync) snapshot = Entries.Values.ToList();
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
                string tmp = StatePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot));
                File.Move(tmp, StatePath, true);
            }
            catch { }
        }

        private static void ClearState()
        {
            try { if (File.Exists(StatePath)) File.Delete(StatePath); } catch { }
        }
    }
}
