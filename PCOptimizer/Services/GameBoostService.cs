using System;
using System.Windows.Threading;

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
        private static DispatcherTimer? _timer;
        private static GameTargetService.Target? _target;
        private static DateTime _startedAt;

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
            _timer!.Start();
            CompetitiveTelemetryService.Start(target.Pid, target.Name, "Turbo atual");

            Logger.Info($"Turbo ligado em {target.Name} (PID {target.Pid}) — {n} programa(s) movido(s)");
            Notify();
            return $"Turbo em {target.Name} — {n} programa(s) movido(s) para os E-cores";
        }

        /// <summary>Desliga o turbo e devolve todos os programas ao estado original.</summary>
        public static string ReleaseAll(string reason)
        {
            _timer?.Stop();

            int n = CoreParkingService.RestoreAll();
            CompetitiveTelemetryService.Stop(reason);

            if (_target != null)
            {
                Logger.Info($"Turbo desligado ({reason}) — {n} programa(s) restaurado(s)");
                _target.Dispose();
                _target = null;
            }

            Notify();
            return n > 0 ? $"Turbo desligado — {n} programa(s) restaurado(s)" : "Turbo desligado";
        }

        /// <summary>Chamado pelo detector de tela cheia.</summary>
        public static void OnGameStateChanged(bool gameRunning)
        {
            if (!SettingsService.Current.GameBoostEnabled) return;
            if (CompetitiveModeService.IsActive) return;

            if (gameRunning)
            {
                if (_target != null) return;  // já ativo
                var t = GameTargetService.FromForegroundWindow();
                if (t == null) { Logger.Warn("Turbo: não consegui identificar o jogo"); return; }
                ApplyTo(t, manual: false);
            }
            // Sair da tela cheia NÃO desliga: alt-tab no meio da partida é normal.
            // O turbo só sai quando o processo do jogo morre (visto no timer).
        }

        private static void EnsureTimer()
        {
            if (_timer != null) return;
            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _timer.Tick += (_, _) => Tick();
        }

        private static void Tick()
        {
            try
            {
                if (_target == null) { _timer?.Stop(); return; }

                if (!_target.IsAlive) { ReleaseAll("jogo encerrado"); return; }

                if (DateTime.Now - _startedAt > MaxDuration)
                {
                    Logger.Warn("Turbo: ativo há mais de 6 h — soltando por segurança");
                    ReleaseAll("tempo limite");
                    return;
                }

                // Programas abertos durante a partida também vão para os E-cores.
                // ParkAll só olha PIDs novos, então isto é barato.
                int added = CoreParkingService.ParkAll(_target.Pid, _target.ExePath,
                                                       SettingsService.Current.GameBoostLowerPriority);
                CompetitiveTelemetryService.Sample();
                if (added > 0) Notify();
            }
            catch (Exception ex) { Logger.Error(ex, "GameBoost.Tick"); }
        }
    }
}
