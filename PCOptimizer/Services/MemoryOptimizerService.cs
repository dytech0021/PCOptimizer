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
    ///
    /// As outras partes desta classe ficam em MemoryOptimizerService.*.cs, uma por assunto.
    /// </summary>
    public static partial class MemoryOptimizerService
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

        // ── Execução ──────────────────────────────────────────────────────────

        public static Task<MemoryRunResult> OptimizeAsync(MemoryOperation configured, MemoryTrigger trigger)
            => Task.Run(() => OptimizeCore(configured, trigger));

        private static DateTime? _lastRunUtc;

        /// <summary>Nível salvo (migra o antigo "Modo agressivo").</summary>
        public static MemoryLevel CurrentLevel =>
            MemoryOptimizerPolicy.ResolveLevel(SettingsService.Current.MemoryLevel, SettingsService.Current.MemoryAggressive);

        private static MemoryRunResult OptimizeCore(MemoryOperation configured, MemoryTrigger trigger)
        {
            var level = CurrentLevel;
            var spec = MemoryOptimizerPolicy.SpecFor(level);
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

                if (IsGameOpen(scan, out string? blocker))
                {
                    string paused = MemoryOptimizerPolicy.PausedText(blocker);
                    if (trigger != MemoryTrigger.Auto) paused += " · " + DateTime.Now.ToString("HH:mm");
                    // Pausa do automático não sobrescreve o último resultado da tela
                    // nem enche o log a cada minuto — quem avisa é a linha de status.
                    if (trigger != MemoryTrigger.Auto)
                    {
                        LastResultText = paused;
                        Logger.Info($"Memória ({trigger}): {paused}");
                    }
                    return new MemoryRunResult(MemoryRunOutcome.Paused, default, default,
                        MemoryOperation.None, MemoryOperation.None, MemoryOperation.None, 0, paused);
                }

                var plan = MemoryOptimizerPolicy.BuildPlan(configured, trigger, _canPurgeLists, _canTrimFileCache, level);
                var before = ReadSnapshot();
                long storeBefore = CompressionStoreBytes(processes);

                var protection = BuildProtection(processes, scan,
                    needServices: (plan.Ops & MemoryOperation.TrimServices) != 0);
                var ctx = protection.Context;

                var done = MemoryOperation.None;
                var failed = MemoryOperation.None;
                var noAdmin = plan.SkippedNoAdmin;
                int trimmed = 0;

                foreach (var step in MemoryOptimizerPolicy.OrderedSteps(plan.Ops))
                {
                    try
                    {
                        // A memória modificada vai por último: dá meio segundo para
                        // as páginas tiradas dos programas chegarem nela.
                        if (step == MemoryOperation.ModifiedList && trimmed > 0) Thread.Sleep(500);

                        int status = step switch
                        {
                            MemoryOperation.ModifiedList        => ListCommand(MemoryNative.MemoryFlushModifiedList),
                            MemoryOperation.StandbyFull         => ListCommand(MemoryNative.MemoryPurgeStandbyList),
                            MemoryOperation.StandbyLowPriority  => ListCommand(MemoryNative.MemoryPurgeLowPriorityStandbyList),
                            MemoryOperation.SystemFileCache     => TrimFileCache(),
                            MemoryOperation.LowerMemoryPriority => LowerPriorities(processes, ctx, scan),
                            MemoryOperation.TrimPrograms        => TrimPass(processes, ctx, spec, TrimKind.Normal, ref trimmed),
                            MemoryOperation.TrimServices        => TrimServicesPass(processes, ctx, spec, ref trimmed),
                            MemoryOperation.TrimVisibleApps     => TrimPass(processes, ctx, spec, TrimKind.VisibleApp, ref trimmed),
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

                var after = (done & MemoryOperation.ModifiedList) != 0 ? WaitModifiedSettled() : SettleShort();

                long storeDelta = CompressionStoreBytes(null) - storeBefore;
                var extras = new MemoryRunExtras(level, trimmed, storeDelta,
                    TopProtected(processes, ctx, spec, protection.Reasons));

                var outcome = MemoryOptimizerPolicy.Classify(done, failed, noAdmin, paused: false, busy: false);
                string text = MemoryOptimizerPolicy.DescribeResult(outcome, before, after, failed, noAdmin,
                    plan.SkippedByPolicy, DateTime.Now, extras);
                LastResultText = text;
                if (outcome == MemoryRunOutcome.Done) _lastRunUtc = DateTime.UtcNow;

                Logger.Info($"Memória ({trigger}, {MemoryOptimizerPolicy.LevelName(level)}): {text} | feito={done} " +
                            $"falhou={failed} semAdmin={noAdmin} porRegra={plan.SkippedByPolicy} processos={trimmed} " +
                            $"compressão={storeDelta / (1 << 20)} MB");
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
    }
}
