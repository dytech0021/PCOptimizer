using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Game Boost para processadores sem P-cores/E-cores (ex.: Xeon E5-2680 v3).
    /// Enquanto o jogo roda, os outros programas ficam presos nos 2 últimos
    /// núcleos físicos; o jogo fica com o resto livre. Como no Game Boost
    /// híbrido, o processo do jogo NUNCA é tocado (anticheat) — só os outros.
    ///
    /// Independente do Game Boost híbrido: serviço, timer e arquivo de estado
    /// próprios. Nada dele é alterado.
    /// </summary>
    public static class CoreReserveService
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessAffinityMask(IntPtr h, out UIntPtr processMask, out UIntPtr systemMask);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessAffinityMask(IntPtr h, UIntPtr mask);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetPriorityClass(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetPriorityClass(IntPtr h, uint priorityClass);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr h, uint flags, System.Text.StringBuilder exeName, ref uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ProcessIdToSessionId(int pid, out int sessionId);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

        private const int RelationProcessorCore = 0;
        private const uint PROCESS_SET_INFORMATION = 0x0200;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;

        /// <summary>Nunca movidos — a mesma lista do Game Boost híbrido, copiada.</summary>
        private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "EasyAntiCheat", "EasyAntiCheat_EOS", "start_protected_game", "BEService", "BEDaisy",
            "BattlEyeLauncher", "vgc", "vgtray", "GameMon", "GameMon64", "npggNT", "steamservice",
            "GameOverlayUI", "audiodg", "dwm", "explorer", "csrss", "wininit", "winlogon", "smss",
            "services", "lsass", "fontdrvhost", "Registry", "MemCompression", "Memory Compression",
            "obs64", "obs32", "nvcontainer", "NVDisplay.Container",
        };

        // ── Topologia ─────────────────────────────────────────────────────────

        private sealed record CoreLayout(List<ulong> CoreMasks, bool Hybrid, bool MultiGroup);
        private static CoreLayout? _layout;

        /// <summary>Uma máscara por núcleo físico (grupo 0), lida uma vez.</summary>
        private static CoreLayout Layout()
        {
            if (_layout != null) return _layout;
            var masks = new List<ulong>();
            var classes = new HashSet<byte>();
            bool multi = Environment.ProcessorCount > 64;
            IntPtr buf = IntPtr.Zero;
            try
            {
                uint len = 0;
                GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref len);
                if (len > 0)
                {
                    buf = Marshal.AllocHGlobal((int)len);
                    if (GetLogicalProcessorInformationEx(RelationProcessorCore, buf, ref len))
                    {
                        int gaSize = IntPtr.Size + 8;
                        uint off = 0;
                        while (off + 8 <= len)
                        {
                            IntPtr e = IntPtr.Add(buf, (int)off);
                            int rel = Marshal.ReadInt32(e, 0), size = Marshal.ReadInt32(e, 4);
                            if (size <= 0) break;
                            if (rel == RelationProcessorCore)
                            {
                                classes.Add(Marshal.ReadByte(e, 9));
                                int groups = (ushort)Marshal.ReadInt16(e, 30);
                                for (int i = 0; i < groups; i++)
                                {
                                    int ga = 32 + i * gaSize;
                                    if (off + (uint)(ga + gaSize) > len) break;
                                    ulong mask = unchecked((ulong)Marshal.ReadIntPtr(e, ga).ToInt64());
                                    if ((ushort)Marshal.ReadInt16(e, ga + IntPtr.Size) != 0) { multi = true; continue; }
                                    masks.Add(mask);
                                }
                            }
                            off += (uint)size;
                        }
                    }
                }
            }
            catch (Exception ex) { Logger.Error(ex, "CoreReserve.Layout"); }
            finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }

            _layout = new CoreLayout(masks, classes.Count > 1, multi);
            Logger.Info($"Núcleos reservados: {CoreReservePolicy.Describe(masks)} | " +
                        $"fundo=0x{CoreReservePolicy.BackgroundMask(masks):X} híbrido={_layout.Hybrid}");
            return _layout;
        }

        public static bool IsSupported
        {
            get
            {
                var l = Layout();
                return CoreReservePolicy.IsSupported(l.CoreMasks.Count, l.Hybrid, l.MultiGroup);
            }
        }

        // ── Estado ────────────────────────────────────────────────────────────

        private static Timer? _timer;
        private static GameTargetService.Target? _target;
        private static DateTime _startedAt;
        private static int _tickRunning;
        private static readonly object OperationLock = new();
        private static readonly TimeSpan MaxDuration = TimeSpan.FromHours(6);

        private sealed class Moved
        {
            public int Pid { get; set; }
            public string Name { get; set; } = "";
            public long Created { get; set; }
            public ulong PrevAffinity { get; set; }
            public uint PrevPriority { get; set; }
            public bool AffinityChanged { get; set; }
            public bool PriorityChanged { get; set; }
        }

        private static readonly Dictionary<int, Moved> _moved = new();
        private static readonly HashSet<int> _seen = new();
        /// <summary>Contagem para a tela, lida sem esperar o lock (a passada pode demorar).</summary>
        private static int _movedCount;

        public static bool IsActive => _target != null;
        public static string? TargetName => _target?.Name;
        public static event Action? StatusChanged;

        private static void Notify()
        {
            try { StatusChanged?.Invoke(); }
            catch (Exception ex) { Logger.Error(ex, "CoreReserve.StatusChanged"); }
        }

        public static string StatusText()
        {
            var l = Layout();
            if (l.Hybrid) return "Processador híbrido — use o Game Boost acima";
            if (l.MultiGroup) return "Mais de 64 núcleos lógicos — o recurso não se aplica";
            if (!IsSupported) return CoreReservePolicy.Describe(l.CoreMasks);
            if (_target == null) return CoreReservePolicy.Describe(l.CoreMasks);
            int n = Volatile.Read(ref _movedCount);
            return $"Ativo em {_target.Name} — {n} programa(s) nos {CoreReservePolicy.BackgroundCores} últimos núcleos";
        }

        // ── Ligar / desligar ──────────────────────────────────────────────────

        public static string ApplyTo(GameTargetService.Target target, bool manual)
        {
            lock (OperationLock)
            {
                if (!IsSupported)
                {
                    target.Dispose();
                    return "Este processador não se aplica a este modo";
                }
                RestoreOrphansFromPreviousRun();
                if (_target != null) ReleaseCore("troca de alvo");

                target.Manual = manual;
                _target = target;
                _startedAt = DateTime.Now;
                _seen.Clear();
                int n = MoveAll();

                _timer ??= new Timer(OnTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _timer.Change(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
                Logger.Info($"Núcleos reservados: ligado em {target.Name} (PID {target.Pid}) — {n} programa(s) movido(s)");
                Notify();
                return $"Ativo em {target.Name} — {n} programa(s) nos {CoreReservePolicy.BackgroundCores} últimos núcleos";
            }
        }

        public static string ReleaseAll(string reason)
        {
            lock (OperationLock) return ReleaseCore(reason);
        }

        private static string ReleaseCore(string reason)
        {
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            List<Moved> list = _moved.Values.ToList();
            _moved.Clear();
            _seen.Clear();
            Volatile.Write(ref _movedCount, 0);
            int n = list.Count(Restore);
            SaveState();
            if (_target != null)
            {
                Logger.Info($"Núcleos reservados: desligado ({reason}) — {n} programa(s) restaurado(s)");
                _target.Dispose();
                _target = null;
            }
            Notify();
            return n > 0 ? $"Desligado — {n} programa(s) restaurado(s)" : "Desligado";
        }

        private static bool _initialized;

        /// <summary>Assina o detector de jogo uma vez. Não liga o detector.</summary>
        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            GameAwarenessService.GameStateChanged += running =>
            {
                try { OnGameStateChanged(running); }
                catch (Exception ex) { Logger.Error(ex, "CoreReserve.GameStateChanged"); }
            };
        }

        /// <summary>Detector de tela cheia: liga sozinho se a opção estiver marcada.</summary>
        public static void OnGameStateChanged(bool gameRunning)
        {
            if (!gameRunning || !SettingsService.Current.CoreReserveAuto || _target != null || !IsSupported) return;
            var t = GameTargetService.FromForegroundWindow();
            if (t == null) return;
            _ = Task.Run(() => ApplyTo(t, manual: false));
        }

        private static async void OnTick(object? state)
        {
            if (Interlocked.Exchange(ref _tickRunning, 1) != 0) return;
            try
            {
                string? stop = null;
                int added = 0;
                await Task.Run(() =>
                {
                    lock (OperationLock)
                    {
                        if (_target == null) { stop = "alvo indisponível"; return; }
                        if (!_target.IsAlive) { stop = "jogo encerrado"; return; }
                        if (DateTime.Now - _startedAt > MaxDuration) { stop = "tempo limite"; return; }
                        added = MoveAll();
                    }
                });
                if (stop != null) ReleaseAll(stop);
                else if (added > 0) Notify();
            }
            catch (Exception ex) { Logger.Error(ex, "CoreReserve.Tick"); }
            finally { Volatile.Write(ref _tickRunning, 0); }
        }

        // ── Mover processos ───────────────────────────────────────────────────

        /// <summary>Move os processos elegíveis. Só olha PIDs novos depois da 1ª passada.</summary>
        private static int MoveAll()
        {
            if (_target == null) return 0;
            ulong bg = CoreReservePolicy.BackgroundMask(Layout().CoreMasks);
            if (bg == 0) return 0;

            string? gameDir = null, gameName = null;
            try
            {
                if (!string.IsNullOrEmpty(_target.ExePath))
                {
                    gameDir = Path.GetDirectoryName(_target.ExePath);
                    gameName = Path.GetFileNameWithoutExtension(_target.ExePath);
                }
            }
            catch { }

            int myPid = Environment.ProcessId, moved = 0;
            bool lower = SettingsService.Current.CoreReserveLowerPriority;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    int pid = p.Id;
                    string name = p.ProcessName;
                    if (!_seen.Add(pid)) continue;
                    if (ShouldSkip(pid, name, myPid, _target.Pid, gameName, gameDir)) continue;
                    if (Move(pid, name, bg, lower)) moved++;
                }
                catch { }
                finally { p.Dispose(); }
            }
            if (moved > 0) SaveState();
            Volatile.Write(ref _movedCount, _moved.Count);
            return moved;
        }

        private static bool ShouldSkip(int pid, string name, int myPid, int gamePid, string? gameName, string? gameDir)
        {
            if (pid <= 4 || pid == myPid || pid == gamePid || string.IsNullOrEmpty(name)) return true;
            if (gameName != null && name.Equals(gameName, StringComparison.OrdinalIgnoreCase)) return true;
            if (ProcessIdToSessionId(pid, out int sess) && sess == 0) return true;
            if (ProtectedNames.Contains(name)) return true;
            var user = SettingsService.Current.GameBoostUserProtected;
            if (user != null && user.Any(u => u.Equals(name, StringComparison.OrdinalIgnoreCase))) return true;

            string? path = ExePath(pid);
            if (path == null) return true;
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (path.StartsWith(Path.Combine(win, "System32") + "\\", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(Path.Combine(win, "SysWOW64") + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            // Auxiliares do próprio jogo (launcher, anticheat ao lado do exe)
            if (gameDir != null && MemoryOptimizerPolicy.IsUnder(path, gameDir)) return true;
            // Outro jogo aberto junto: não é "programa de fundo"
            if (MemoryOptimizerPolicy.IsGameInstallPath(path) || MemoryOptimizerPolicy.IsKnownGameProcess(name)) return true;
            return false;
        }

        private static string? ExePath(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new System.Text.StringBuilder(1024);
                uint size = 1024;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        private static bool Move(int pid, string name, ulong bgMask, bool lowerPriority)
        {
            IntPtr h = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                if (!GetProcessAffinityMask(h, out UIntPtr prevMask, out UIntPtr sysMask)) return false;
                ulong prev = (ulong)prevMask, target = bgMask & (ulong)sysMask;
                if (target == 0 || prev == target) return false;
                if (!GetProcessTimes(h, out long created, out _, out _, out _)) return false;

                uint prevPrio = GetPriorityClass(h);
                bool affOk = SetProcessAffinityMask(h, (UIntPtr)target);
                bool prioOk = lowerPriority && prevPrio != 0 && prevPrio != BELOW_NORMAL_PRIORITY_CLASS
                              && SetPriorityClass(h, BELOW_NORMAL_PRIORITY_CLASS);
                if (!affOk && !prioOk) return false;

                _moved[pid] = new Moved
                {
                    Pid = pid, Name = name, Created = created, PrevAffinity = prev, PrevPriority = prevPrio,
                    AffinityChanged = affOk, PriorityChanged = prioOk,
                };
                return true;
            }
            catch (Exception ex) { Logger.Error(ex, $"CoreReserve.Move({name})"); return false; }
            finally { CloseHandle(h); }
        }

        /// <summary>Devolve afinidade e prioridade — só se for o MESMO processo (PID não reciclado).</summary>
        private static bool Restore(Moved m)
        {
            IntPtr h = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, m.Pid);
            if (h == IntPtr.Zero) return false;   // já fechou: nada a devolver
            try
            {
                if (!GetProcessTimes(h, out long created, out _, out _, out _) || created != m.Created) return false;
                bool ok = true;
                if (m.AffinityChanged) ok &= SetProcessAffinityMask(h, (UIntPtr)m.PrevAffinity);
                if (m.PriorityChanged && m.PrevPriority != 0) ok &= SetPriorityClass(h, m.PrevPriority);
                return ok;
            }
            catch { return false; }
            finally { CloseHandle(h); }
        }

        // ── Recuperação (app morto no meio) ───────────────────────────────────

        private static string StatePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCOptimizer", "reserve-state.json");

        private sealed class StateFile
        {
            public long BootSeconds { get; set; }
            public List<Moved> Entries { get; set; } = new();
        }

        private static long BootSeconds() =>
            (DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64)).Ticks / TimeSpan.TicksPerSecond;

        private static void SaveState()
        {
            try
            {
                if (_moved.Count == 0) { if (File.Exists(StatePath)) File.Delete(StatePath); return; }
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
                File.WriteAllText(StatePath, JsonSerializer.Serialize(
                    new StateFile { BootSeconds = BootSeconds(), Entries = _moved.Values.ToList() }));
            }
            catch (Exception ex) { Logger.Error(ex, "CoreReserve.SaveState"); }
        }

        /// <summary>
        /// Na abertura: se o app foi morto com o modo ligado, devolve os
        /// programas. Arquivo de outro boot é descartado (PIDs reciclados).
        /// </summary>
        public static void RestoreOrphansFromPreviousRun()
        {
            try
            {
                if (_target != null || !File.Exists(StatePath)) return;
                var state = JsonSerializer.Deserialize<StateFile>(File.ReadAllText(StatePath));
                if (state != null && Math.Abs(state.BootSeconds - BootSeconds()) <= 5)
                {
                    int n = state.Entries.Count(Restore);
                    Logger.Warn($"Núcleos reservados: {n} programa(s) devolvido(s) de uma execução interrompida");
                }
                File.Delete(StatePath);
            }
            catch (Exception ex) { Logger.Error(ex, "CoreReserve.RestoreOrphans"); }
        }
    }
}
