using System;
using System.Threading;
using System.Threading.Tasks;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Turbo de Jogo: enquanto o jogo roda, confina os outros programas aos
    /// E-cores para deixar os P-cores livres. Nunca escreve no processo do jogo.
    ///
    /// Timer próprio (e não o do GameAwarenessService) para o botão manual
    /// funcionar mesmo com a detecção automática desligada.
    /// </summary>
    public static class GameBoostService
    {
        private static Timer? _timer;
        private static GameTargetService.Target? _target;
        private static DateTime _startedAt;
        private static readonly object OperationLock = new();
        private static int _tickRunning;

        /// <summary>Um jogo virou zumbi: solta tudo em vez de segurar para sempre.</summary>
        private static readonly TimeSpan MaxDuration = TimeSpan.FromHours(6);

        public static bool IsActive => _target != null;
        public static string? TargetName => _target?.Name;
        public static int ParkedCount => CoreParkingService.ParkedCount;

        /// <summary>Avisa a interface para se atualizar, sem ela precisar de timer.</summary>
        public static event Action? StatusChanged;

        private static void Notify()
        {
            try { StatusChanged?.Invoke(); }
            catch (Exception ex) { Logger.Error(ex, "GameBoost.StatusChanged"); }
        }

        public static string StatusText()
        {
            var topo = CpuTopologyService.Get();
            if (!topo.CanPark)
                return topo.MultiGroup
                    ? "Este PC tem mais de 64 núcleos lógicos — o recurso não se aplica"
                    : $"{CpuTopologyService.Describe()} — precisa de processador híbrido (P + E cores)";

            if (_target == null)
                return $"{CpuTopologyService.Describe()} — pronto para liberar os P-cores";

            int n = CoreParkingService.ParkedCount;
            return $"Turbo em {_target.Name} — {n} programa(s) nos E-cores";
        }

        /// <summary>Ativa o turbo para um jogo. Devolve o que aconteceu, para a UI.</summary>
        public static string ApplyTo(GameTargetService.Target target, bool manual)
        {
            lock (OperationLock) return ApplyCore(target, manual);
        }

        private static string ApplyCore(GameTargetService.Target target, bool manual)
        {
            if (BoostStateStore.HasPendingRecovery)
            {
                BoostStateStore.RestoreOrphansFromPreviousRun();
                if (BoostStateStore.HasPendingRecovery)
                {
                    target.Dispose();
                    return "Há configurações anteriores pendentes de restauração";
                }
            }
            var topo = CpuTopologyService.Get();
            if (!topo.CanPark)
            {
                target.Dispose();
                return "Este processador não tem E-cores para onde mover os programas";
            }

            // Se já havia um alvo, solta antes de trocar
            if (_target != null) ReleaseAll("troca de alvo");

            target.Manual = manual;
            _target = target;
            _startedAt = DateTime.Now;

            CoreParkingService.ResetSeen();
            int n = CoreParkingService.ParkAll(target.Pid, target.ExePath,
                                               SettingsService.Current.GameBoostLowerPriority);

            EnsureTimer();
            _timer!.Change(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
            GameSessionTelemetryService.Start(target.Pid, target.Name, "Turbo atual");

            Logger.Info($"Turbo ligado em {target.Name} (PID {target.Pid}) — {n} programa(s) movido(s)");
            Notify();
            return $"Turbo em {target.Name} — {n} programa(s) movido(s) para os E-cores";
        }

        /// <summary>Desliga o turbo e devolve todos os programas ao estado original.</summary>
        public static string ReleaseAll(string reason)
        {
            lock (OperationLock) return ReleaseCore(reason);
        }

        private static string ReleaseCore(string reason)
        {
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            int n = CoreParkingService.RestoreAll();
            GameSessionTelemetryService.Stop(reason);

            if (_target != null)
            {
                Logger.Info($"Turbo desligado ({reason}) — {n} programa(s) restaurado(s)");
                _target.Dispose();
                _target = null;
            }

            Notify();
            if (BoostStateStore.HasPendingRecovery)
                return $"Turbo desligado — {n} restaurado(s), restauração pendente preservada";
            return n > 0 ? $"Turbo desligado — {n} programa(s) restaurado(s)" : "Turbo desligado";
        }

        /// <summary>Chamado pelo detector de tela cheia.</summary>
        public static void OnGameStateChanged(bool gameRunning)
        {
            if (!SettingsService.Current.GameBoostEnabled) return;

            if (gameRunning)
            {
                if (_target != null) return;  // já ativo
                var t = GameTargetService.FromForegroundWindow();
                if (t == null) { Logger.Warn("Turbo: não consegui identificar o jogo"); return; }
                _ = Task.Run(() => ApplyTo(t, manual: false));
            }
            // Sair da tela cheia NÃO desliga: alt-tab no meio da partida é normal.
            // O turbo só sai quando o processo do jogo morre (visto no timer).
        }

        private static void EnsureTimer()
        {
            if (_timer != null) return;
            _timer = new Timer(OnTimerTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        private static async void OnTimerTick(object? state)
        {
            if (Interlocked.Exchange(ref _tickRunning, 1) != 0) return;
            try
            {
                var (stopReason, added) = await Task.Run(TickBackground);
                if (stopReason != null) ReleaseAll(stopReason);
                if (added > 0) Notify();
            }
            catch (Exception ex) { Logger.Error(ex, "GameBoost.Tick"); }
            finally { Volatile.Write(ref _tickRunning, 0); }
        }

        private static (string? StopReason, int Added) TickBackground()
        {
            lock (OperationLock)
            {
                if (_target == null) return ("alvo indisponível", 0);
                if (!_target.IsAlive) return ("jogo encerrado", 0);
                if (DateTime.Now - _startedAt > MaxDuration)
                {
                    Logger.Warn("Turbo: ativo há mais de 6 h — soltando por segurança");
                    return ("tempo limite", 0);
                }

                int added = CoreParkingService.ParkAll(_target.Pid, _target.ExePath,
                    SettingsService.Current.GameBoostLowerPriority);
                GameSessionTelemetryService.Sample();
                return (null, added);
            }
        }
    }
}
