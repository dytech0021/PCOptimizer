using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Otimizador de memória RAM, no estilo do Wise Memory Optimizer — mas sem o
    /// que faz esses programas atrapalharem jogos:
    /// - nunca roda com jogo ABERTO (não só em primeiro plano);
    /// - nunca abre o processo do jogo, nem os filhos dele;
    /// - nunca usa o "esvaziar tudo" do kernel, que não permite exceções;
    /// - mede antes e depois e diz a verdade sobre o que mudou.
    ///
    /// Não altera nada do Game Boost nem do Controle de CPU: deles só LÊ o que já
    /// é público (GameBoostService.IsActive/TargetName,
    /// GameAwarenessService.IsGameRunning) e a lista de protegidos do usuário.
    /// </summary>
    public static class MemoryOptimizerService
    {
        public static event Action? StatusChanged;

        private static int _running;
        public static bool IsRunning => Volatile.Read(ref _running) != 0;

        public static string? LastResultText { get; private set; }

        private static void Notify()
        {
            try { StatusChanged?.Invoke(); }
            catch (Exception ex) { Logger.Error(ex, "MemoryOptimizer.StatusChanged"); }
        }

        // ── Privilégios ───────────────────────────────────────────────────────

        private static readonly object PrivLock = new();
        private static bool _privTried, _canPurgeLists, _canTrimFileCache;

        /// <summary>
        /// Habilita, uma vez, os dois privilégios que o administrador já tem
        /// desligados por padrão. Precisa acontecer ANTES da primeira leitura das
        /// listas: sem isso a leitura falha e o app diria "requer administrador"
        /// mesmo rodando como administrador.
        /// </summary>
        private static void EnsurePrivileges()
        {
            lock (PrivLock)
            {
                if (_privTried) return;
                _privTried = true;
                try
                {
                    _canPurgeLists    = MemoryNative.TryEnablePrivilege(MemoryNative.SE_PROF_SINGLE_PROCESS_NAME);
                    _canTrimFileCache = MemoryNative.TryEnablePrivilege(MemoryNative.SE_INCREASE_QUOTA_NAME);
                }
                catch (Exception ex) { Logger.Error(ex, "MemoryOptimizer.EnsurePrivileges"); }
                Logger.Info($"Memória: privilégios — listas={_canPurgeLists} cacheDeArquivos={_canTrimFileCache}");
            }
        }

        /// <summary>As limpezas de cache estão disponíveis (administrador)?</summary>
        public static bool CacheOpsAvailable
        {
            get { EnsurePrivileges(); return _canPurgeLists && _canTrimFileCache; }
        }

        // ── Leitura ───────────────────────────────────────────────────────────

        /// <summary>0 = ainda não tentou, 1 = lê as listas, -1 = desligado nesta sessão.</summary>
        private static int _listsState;

        /// <summary>As listas (livre / cache em espera) não puderam ser lidas por falta de permissão.</summary>
        public static bool ListsNeedAdmin => Volatile.Read(ref _listsState) < 0;

        /// <summary>Foto da memória. Barata (duas chamadas), pode ser chamada de qualquer thread.</summary>
        public static MemorySnapshot ReadSnapshot()
        {
            try
            {
                var ms = new MemoryNative.MEMORYSTATUSEX { dwLength = 64 };
                if (!MemoryNative.GlobalMemoryStatusEx(ref ms)) return default;

                if (Volatile.Read(ref _listsState) >= 0 && IntPtr.Size == 8 && MemoryNative.LayoutsOk)
                {
                    EnsurePrivileges();
                    int st = MemoryNative.NtQuerySystemInformation(MemoryNative.SystemMemoryListInformation,
                        out var info, MemoryNative.MemoryListInfoSize, out _);
                    if (st == MemoryNative.STATUS_SUCCESS)
                    {
                        Volatile.Write(ref _listsState, 1);
                        return MemoryOptimizerPolicy.FromLists(ms.ullTotalPhys, ms.ullAvailPhys,
                            new ulong[]
                            {
                                info.Standby0, info.Standby1, info.Standby2, info.Standby3,
                                info.Standby4, info.Standby5, info.Standby6, info.Standby7,
                            },
                            info.ZeroPageCount, info.FreePageCount,
                            info.ModifiedPageCount, info.ModifiedNoWritePageCount,
                            Environment.SystemPageSize);
                    }

                    // Só desliga de vez se nunca funcionou: uma falha passageira
                    // depois de já ter lido não tira a informação da tela.
                    if (Volatile.Read(ref _listsState) == 0)
                    {
                        Volatile.Write(ref _listsState, -1);
                        Logger.Warn($"Memória: não consegui ler as listas (NTSTATUS 0x{st:X8}) — " +
                                    "mostrando só o disponível");
                    }
                }
                return new MemorySnapshot(ms.ullTotalPhys, ms.ullAvailPhys, null, null, null);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "MemoryOptimizer.ReadSnapshot");
                return default;
            }
        }

        // ── Detecção de jogo (só leitura) ─────────────────────────────────────

        /// <summary>
        /// Checagem leve, para o tick do automático: estado já conhecido do Game
        /// Boost e do detector de tela cheia, o estado de notificações do Windows
        /// e a janela da frente.
        /// </summary>
        private static bool QuickGameCheck()
        {
            if (GameBoostService.IsActive || GameAwarenessService.IsGameRunning) return true;
            if (ShellSaysFullscreen()) return true;

            IntPtr prev = TrySetPerMonitorDpi();
            try
            {
                IntPtr fg = MemoryNative.GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                MemoryNative.GetWindowThreadProcessId(fg, out int pid);
                var facts = ReadWindowFacts(fg, pid, name: ProcessNameOf(pid));
                return facts is WindowFacts f && MemoryOptimizerPolicy.IsGameLikeWindow(f);
            }
            finally { RestoreDpi(prev); }
        }

        /// <summary>QUNS 2/3/4: um app em tela cheia (inclusive sem borda) está na frente.</summary>
        private static bool ShellSaysFullscreen()
        {
            try
            {
                return MemoryNative.SHQueryUserNotificationState(out int state) == 0
                       && state is 2 or 3 or 4;
            }
            catch { return false; }
        }

        /// <summary>
        /// Checagem completa, antes de QUALQUER limpeza: há jogo aberto, mesmo que
        /// minimizado ou em outro monitor? Devolve também o retrato das janelas,
        /// reaproveitado para proteger quem o usuário está usando.
        /// </summary>
        private static bool IsGameOpen(WindowScan scan)
        {
            if (GameBoostService.IsActive || GameAwarenessService.IsGameRunning) return true;
            if (ShellSaysFullscreen()) return true;
            return scan.GamePids.Count > 0;
        }

        private sealed class WindowScan
        {
            public readonly HashSet<int> GamePids = new();
            public readonly HashSet<int> BigWindowPids = new();
        }

        private static WindowScan ScanWindows(IReadOnlyDictionary<int, string> namesByPid)
        {
            var scan = new WindowScan();
            IntPtr prev = TrySetPerMonitorDpi();
            MemoryNative.EnumWindowsProc cb = (hwnd, _) =>
            {
                // Exceção não pode atravessar o EnumWindows nativo: engole e segue.
                try
                {
                    if (!MemoryNative.IsWindowVisible(hwnd)) return true;
                    MemoryNative.GetWindowThreadProcessId(hwnd, out int pid);
                    if (pid <= 4) return true;
                    namesByPid.TryGetValue(pid, out string? name);
                    if (ReadWindowFacts(hwnd, pid, name ?? "") is not WindowFacts f) return true;
                    if (MemoryOptimizerPolicy.IsGameLikeWindow(f)) scan.GamePids.Add(pid);
                    else if (!f.Minimized && MemoryOptimizerPolicy.IsBigVisibleWindow(f)) scan.BigWindowPids.Add(pid);
                }
                catch { }
                return true;
            };
            try { MemoryNative.EnumWindows(cb, IntPtr.Zero); }
            catch (Exception ex) { Logger.Error(ex, "MemoryOptimizer.ScanWindows"); }
            finally
            {
                GC.KeepAlive(cb);
                RestoreDpi(prev);
            }
            return scan;
        }

        /// <summary>Lê o que importa de uma janela, sem abrir o processo dono.</summary>
        private static WindowFacts? ReadWindowFacts(IntPtr hwnd, int pid, string name)
        {
            bool minimized = MemoryNative.IsIconic(hwnd);
            MemoryNative.RECT r;
            if (minimized)
            {
                // Minimizada: vale o tamanho RESTAURADO — um jogo em tela cheia
                // minimizado por alt-tab continua sendo um jogo aberto.
                var wp = new MemoryNative.WINDOWPLACEMENT
                {
                    length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryNative.WINDOWPLACEMENT>()
                };
                if (!MemoryNative.GetWindowPlacement(hwnd, ref wp)) return null;
                r = wp.rcNormalPosition;
            }
            else if (!MemoryNative.GetWindowRect(hwnd, out r)) return null;

            IntPtr mon = MemoryNative.MonitorFromWindow(hwnd, MemoryNative.MONITOR_DEFAULTTONEAREST);
            var mi = new MemoryNative.MONITORINFO
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryNative.MONITORINFO>()
            };
            if (mon == IntPtr.Zero || !MemoryNative.GetMonitorInfo(mon, ref mi)) return null;

            bool cloaked = false;
            try
            {
                cloaked = MemoryNative.DwmGetWindowAttribute(hwnd, MemoryNative.DWMWA_CLOAKED, out int c, sizeof(int)) == 0
                          && c != 0;
            }
            catch { }

            var cls = new StringBuilder(256);
            MemoryNative.GetClassName(hwnd, cls, cls.Capacity);

            return new WindowFacts(
                new Rect32(r.Left, r.Top, r.Right, r.Bottom),
                new Rect32(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom),
                Visible: true,
                Minimized: minimized,
                Cloaked: cloaked,
                Zoomed: MemoryNative.IsZoomed(hwnd),
                Style: MemoryNative.GetWindowLong(hwnd, MemoryNative.GWL_STYLE),
                ExStyle: MemoryNative.GetWindowLong(hwnd, MemoryNative.GWL_EXSTYLE),
                ClassName: cls.ToString(),
                OwnerName: name,
                OwnerIsSelf: pid == Environment.ProcessId);
        }

        /// <summary>
        /// O app não declara DPI por monitor no manifesto; para comparar a janela
        /// com o monitor em pixels reais, a thread muda de contexto só durante a
        /// leitura.
        /// </summary>
        private static IntPtr TrySetPerMonitorDpi()
        {
            try { return MemoryNative.SetThreadDpiAwarenessContext(MemoryNative.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
            catch { return IntPtr.Zero; }
        }

        private static void RestoreDpi(IntPtr previous)
        {
            if (previous == IntPtr.Zero) return;
            try { MemoryNative.SetThreadDpiAwarenessContext(previous); } catch { }
        }

        /// <summary>Nome do executável, abrindo o processo só para LEITURA.</summary>
        private static string ProcessNameOf(int pid)
        {
            try
            {
                string? path = MemoryNative.ImagePathReadOnly(pid);
                return path == null ? "" : MemoryOptimizerPolicy.FileNameWithoutExtension(path);
            }
            catch { return ""; }
        }

        // ── Execução ──────────────────────────────────────────────────────────

        public static Task<MemoryRunResult> OptimizeAsync(MemoryOperation configured, MemoryTrigger trigger)
            => Task.Run(() => OptimizeCore(configured, trigger));

        private static DateTime? _lastRunUtc;

        private static MemoryRunResult OptimizeCore(MemoryOperation configured, MemoryTrigger trigger)
        {
            if (Interlocked.Exchange(ref _running, 1) != 0)
            {
                string busy = MemoryOptimizerPolicy.DescribeResult(MemoryRunOutcome.Busy, default, default,
                    MemoryOperation.None, MemoryOperation.None, DateTime.Now);
                return new MemoryRunResult(MemoryRunOutcome.Busy, default, default,
                    MemoryOperation.None, MemoryOperation.None, MemoryOperation.None, 0, busy);
            }

            Notify();
            try
            {
                EnsurePrivileges();

                var processes = SnapshotProcesses();
                var namesByPid = new Dictionary<int, string>();
                foreach (var p in processes) namesByPid[p.Pid] = p.Name;
                var scan = ScanWindows(namesByPid);

                if (IsGameOpen(scan))
                {
                    LastResultText = MemoryOptimizerPolicy.GamePausedText;
                    Logger.Info($"Memória ({trigger}): pausado — jogo aberto");
                    return new MemoryRunResult(MemoryRunOutcome.Paused, default, default,
                        MemoryOperation.None, MemoryOperation.None, MemoryOperation.None, 0, LastResultText);
                }

                var plan = MemoryOptimizerPolicy.BuildPlan(configured, trigger, _canPurgeLists, _canTrimFileCache);
                var before = ReadSnapshot();

                var done = MemoryOperation.None;
                var failed = MemoryOperation.None;
                var noAdmin = plan.SkippedNoAdmin;
                int trimmed = 0;

                foreach (var step in MemoryOptimizerPolicy.OrderedSteps(plan.Ops))
                {
                    try
                    {
                        int status = step switch
                        {
                            MemoryOperation.ModifiedList       => ListCommand(MemoryNative.MemoryFlushModifiedList),
                            MemoryOperation.StandbyFull        => ListCommand(MemoryNative.MemoryPurgeStandbyList),
                            MemoryOperation.StandbyLowPriority => ListCommand(MemoryNative.MemoryPurgeLowPriorityStandbyList),
                            MemoryOperation.SystemFileCache    => TrimFileCache(),
                            MemoryOperation.TrimPrograms       => TrimPrograms(processes, scan, out trimmed),
                            _ => MemoryNative.STATUS_SUCCESS,
                        };

                        if (status == MemoryNative.STATUS_SUCCESS) done |= step;
                        else if (status == MemoryNative.STATUS_PRIVILEGE_NOT_HELD) noAdmin |= step;
                        else
                        {
                            failed |= step;
                            Logger.Warn($"Memória: {MemoryOptimizerPolicy.LabelOf(step)} falhou (0x{status:X8})");
                        }
                    }
                    catch (Exception ex)
                    {
                        failed |= step;
                        Logger.Error(ex, $"Memória: {MemoryOptimizerPolicy.LabelOf(step)}");
                    }
                }

                // Os contadores do kernel acompanham a hora; o respiro só evita
                // pegar a foto no meio da última troca de lista.
                Thread.Sleep(250);
                var after = ReadSnapshot();

                var outcome = MemoryOptimizerPolicy.Classify(done, failed, noAdmin, paused: false, busy: false);
                string text = MemoryOptimizerPolicy.DescribeResult(outcome, before, after, failed, noAdmin, DateTime.Now);
                LastResultText = text;
                if (outcome == MemoryRunOutcome.Done) _lastRunUtc = DateTime.UtcNow;

                Logger.Info($"Memória ({trigger}): {text} | feito={done} falhou={failed} " +
                            $"semAdmin={noAdmin} porRegra={plan.SkippedByPolicy} processos={trimmed}");
                return new MemoryRunResult(outcome, before, after, done, failed, noAdmin, trimmed, text);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "MemoryOptimizer.Optimize");
                LastResultText = "Não consegui otimizar — detalhes no log";
                return new MemoryRunResult(MemoryRunOutcome.Failed, default, default,
                    MemoryOperation.None, MemoryOperation.None, MemoryOperation.None, 0, LastResultText);
            }
            finally
            {
                Volatile.Write(ref _running, 0);
                Notify();
            }
        }

        private static int ListCommand(int command)
        {
            int cmd = command;
            return MemoryNative.NtSetSystemInformation(MemoryNative.SystemMemoryListInformation, ref cmd, sizeof(int));
        }

        private static int TrimFileCache()
        {
            if (MemoryNative.SetSystemFileCacheSize(nuint.MaxValue, nuint.MaxValue, 0))
                return MemoryNative.STATUS_SUCCESS;
            int err = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            return err == MemoryNative.ERROR_PRIVILEGE_NOT_HELD
                ? MemoryNative.STATUS_PRIVILEGE_NOT_HELD
                : unchecked((int)0x80070000) | err;   // HRESULT do erro Win32, só para o log
        }

        // ── Processos ─────────────────────────────────────────────────────────

        private sealed record ProcInfo(int Pid, int ParentPid, string Name, int SessionId, long WorkingSet);

        /// <summary>
        /// Retrato dos processos sem abrir nenhum: nome, sessão e working set vêm
        /// do retrato do sistema; o pai vem do Toolhelp.
        /// </summary>
        private static List<ProcInfo> SnapshotProcesses()
        {
            var parents = new Dictionary<int, int>();
            IntPtr snap = MemoryNative.CreateToolhelp32Snapshot(MemoryNative.TH32CS_SNAPPROCESS, 0);
            if (snap != IntPtr.Zero && snap != MemoryNative.INVALID_HANDLE_VALUE)
            {
                try
                {
                    var e = new MemoryNative.PROCESSENTRY32W
                    {
                        dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryNative.PROCESSENTRY32W>()
                    };
                    if (MemoryNative.Process32First(snap, ref e))
                        do { parents[(int)e.th32ProcessID] = (int)e.th32ParentProcessID; }
                        while (MemoryNative.Process32Next(snap, ref e));
                }
                finally { MemoryNative.CloseHandle(snap); }
            }

            var list = new List<ProcInfo>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    list.Add(new ProcInfo(p.Id, parents.TryGetValue(p.Id, out int parent) ? parent : 0,
                                          p.ProcessName, p.SessionId, p.WorkingSet64));
                }
                catch { /* processo saiu no meio do retrato */ }
                finally { p.Dispose(); }
            }
            return list;
        }

        /// <summary>
        /// Esvazia o working set dos maiores programas abertos — com tudo que o
        /// usuário está usando, e os filhos desses processos, de fora.
        /// </summary>
        private static int TrimPrograms(List<ProcInfo> processes, WindowScan scan, out int trimmed)
        {
            trimmed = 0;
            int myPid = Environment.ProcessId;

            // Quem o usuário está usando agora: janela da frente, janelas grandes
            // e (por segurança) qualquer janela com cara de jogo — com os filhos.
            var roots = new HashSet<int>(scan.GamePids);
            roots.UnionWith(scan.BigWindowPids);
            try
            {
                IntPtr fg = MemoryNative.GetForegroundWindow();
                if (fg != IntPtr.Zero)
                {
                    MemoryNative.GetWindowThreadProcessId(fg, out int fgPid);
                    if (fgPid > 4) roots.Add(fgPid);
                }
            }
            catch { }

            var protectedPids = MemoryOptimizerPolicy.ExpandProcessTree(
                processes.Select(p => (p.Pid, p.ParentPid)), roots);
            protectedPids.Add(myPid);

            // Pastas protegidas: a do jogo e a do app da frente (launchers e
            // processos auxiliares ficam ao lado do executável principal).
            var dirs = new List<string>();
            foreach (int pid in scan.GamePids)
                if (MemoryOptimizerPolicy.DirectoryOf(MemoryNative.ImagePathReadOnly(pid)) is string d) dirs.Add(d);

            var extraNames = new List<string>();
            if (GameBoostService.TargetName is string boostName && boostName.Length > 0) extraNames.Add(boostName);

            List<string> userProtected;
            try { userProtected = SettingsService.Current.GameBoostUserProtected?.ToList() ?? new List<string>(); }
            catch { userProtected = new List<string>(); }

            var ctx = new TrimContext(myPid, protectedPids, extraNames, dirs, userProtected,
                Environment.GetFolderPath(Environment.SpecialFolder.Windows));

            var candidates = processes.Select(p => new ProcessCandidate(p.Pid, p.Name, p.SessionId, p.WorkingSet));
            int count = 0;
            foreach (var target in MemoryOptimizerPolicy.PickTrimTargets(candidates, ctx))
            {
                IntPtr h = MemoryNative.OpenProcess(
                    MemoryNative.PROCESS_SET_QUOTA | MemoryNative.PROCESS_QUERY_LIMITED_INFORMATION, false, target.Pid);
                if (h == IntPtr.Zero) continue;   // protegido ou sem permissão — segue
                try
                {
                    string? path = MemoryNative.ImagePath(h);
                    if (!MemoryOptimizerPolicy.ImageNameMatches(path, target.Name)) continue;   // PID reaproveitado
                    if (MemoryOptimizerPolicy.PathExcluded(path, ctx)) continue;
                    if (MemoryNative.EmptyWorkingSet(h)) count++;
                }
                finally { MemoryNative.CloseHandle(h); }

                // Espaça as limpezas para o gravador de páginas e a compressão de
                // memória não receberem tudo de uma vez.
                Thread.Sleep(10);
            }
            trimmed = count;
            return MemoryNative.STATUS_SUCCESS;
        }

        // ── Automático ────────────────────────────────────────────────────────

        private static readonly object AutoLock = new();
        private static Timer? _timer;
        private static int _tickRunning;
        private static TimeSpan _cooldown = MemoryOptimizerPolicy.BaseCooldown;
        private static (DateTime AtUtc, ulong Before, ulong After)? _pendingCheck;
        private static bool? _lastAutoHeld;
        private static readonly CpuIdleTracker Idle =
            new(MemoryOptimizerPolicy.IdleCpuPercent, MemoryOptimizerPolicy.IdleRequired);

        public static AutoSkipReason LastAutoSkip { get; private set; } = AutoSkipReason.Disabled;

        /// <summary>
        /// Liga ou desliga o automático conforme as configurações. O timer só
        /// existe com a opção ligada; desligada, não sobra nada rodando.
        /// </summary>
        public static void ApplyAutoSetting()
        {
            lock (AutoLock)
            {
                if (SettingsService.Current.MemoryAutoOptimize)
                {
                    _timer ??= new Timer(OnTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    _timer.Change(MemoryOptimizerPolicy.AutoPollInterval, MemoryOptimizerPolicy.AutoPollInterval);
                    LastAutoSkip = AutoSkipReason.None;
                }
                else
                {
                    _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    LastAutoSkip = AutoSkipReason.Disabled;
                }
            }
            Notify();
        }

        public static void Stop()
        {
            lock (AutoLock)
            {
                _timer?.Dispose();
                _timer = null;
            }
        }

        private static void OnTick(object? state)
        {
            if (Interlocked.Exchange(ref _tickRunning, 1) != 0) return;
            try { TickCore(); }
            catch (Exception ex) { Logger.Error(ex, "MemoryOptimizer.Tick"); }
            finally { Volatile.Write(ref _tickRunning, 0); }
        }

        private static void TickCore()
        {
            var s = SettingsService.Current;
            if (!s.MemoryAutoOptimize || IsRunning) return;

            var before = LastAutoSkip;
            if (QuickGameCheck())
            {
                Idle.Reset();
                SetSkip(AutoSkipReason.Game, before);
                return;
            }

            if (s.MemoryAutoOnlyWhenIdle && MemoryNative.GetSystemTimes(out long idle, out long kernel, out long user))
                Idle.Push(idle, kernel, user, DateTime.UtcNow);

            var snap = ReadSnapshot();
            var now = DateTime.UtcNow;

            // O ganho da última limpeza automática se sustentou? Decide a espera.
            if (_pendingCheck is { } pc && now >= pc.AtUtc && snap.IsValid)
            {
                bool held = MemoryOptimizerPolicy.GainHeld(pc.Before, pc.After, snap.InUseBytes,
                                                           snap.TotalBytes, s.MemoryAutoThresholdPercent);
                _cooldown = MemoryOptimizerPolicy.NextCooldown(_cooldown, held);
                _lastAutoHeld = held;
                _pendingCheck = null;
                Logger.Info($"Memória (automático): ganho {(held ? "se sustentou" : "não se sustentou")} — " +
                            $"próxima espera {(int)_cooldown.TotalMinutes} min");
            }

            var decision = MemoryOptimizerPolicy.DecideAuto(new AutoInputs(
                Enabled: true,
                ThresholdPercent: s.MemoryAutoThresholdPercent,
                Snapshot: snap,
                GameOpen: false,
                RequireIdle: s.MemoryAutoOnlyWhenIdle,
                IsIdle: Idle.IsIdle(now),
                NowUtc: now,
                LastRunUtc: _lastRunUtc,
                Cooldown: _cooldown,
                Configured: s.MemoryOps));

            SetSkip(decision.Reason, before);
            if (!decision.Run) return;

            var r = OptimizeCore(s.MemoryOps, MemoryTrigger.Auto);
            if (r.Outcome != MemoryRunOutcome.Done) return;

            _pendingCheck = (DateTime.UtcNow + MemoryOptimizerPolicy.PersistCheckDelay,
                             r.Before.InUseBytes, r.After.InUseBytes);

            // Aviso só quando vale a pena: ganho de verdade e sem histórico de
            // ganho que evapora — nada de balão a cada meia hora por nada.
            ulong freed = r.Before.InUseBytes > r.After.InUseBytes ? r.Before.InUseBytes - r.After.InUseBytes : 0;
            if (s.MemoryAutoNotify && freed >= MemoryOptimizerPolicy.SmallGainBytes && _lastAutoHeld != false)
                ShowBalloon("Memória otimizada", r.Text);
        }

        private static void SetSkip(AutoSkipReason reason, AutoSkipReason previous)
        {
            LastAutoSkip = reason;
            if (reason != previous) Notify();
        }

        /// <summary>Balão da bandeja na thread de UI (é um componente do WinForms).</summary>
        public static void ShowBalloon(string title, string text)
        {
            try
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                    new Action(() => TrayService.ShowBalloonTip(title, text)));
            }
            catch (Exception ex) { Logger.Error(ex, "MemoryOptimizer.ShowBalloon"); }
        }

        /// <summary>Linha de status do automático para o card (vazia quando desligado).</summary>
        public static string AutoStatusText()
        {
            var s = SettingsService.Current;
            if (IsRunning) return "Otimizando…";
            if (!s.MemoryAutoOptimize) return "";

            string text = $"Automático acima de {MemoryOptimizerPolicy.ClampThreshold(s.MemoryAutoThresholdPercent)}%";
            if (s.MemoryAutoOnlyWhenIdle) text += " · com PC ocioso";
            switch (LastAutoSkip)
            {
                case AutoSkipReason.Game:
                    text += " · pausado: jogo aberto";
                    break;
                case AutoSkipReason.NotIdle:
                    text += " · aguardando PC ocioso";
                    break;
                case AutoSkipReason.Cooldown when _cooldown > MemoryOptimizerPolicy.BaseCooldown:
                    text += $" · aguardando {(int)_cooldown.TotalMinutes} min (a última limpeza não segurou)";
                    break;
                case AutoSkipReason.NothingToDo:
                    text += " · nenhuma área marcada";
                    break;
            }
            return text;
        }
    }
}
