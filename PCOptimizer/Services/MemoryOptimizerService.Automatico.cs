using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCOptimizer.Services
{
    // Parte do otimizador de memória: limpeza automática.
    public static partial class MemoryOptimizerService
    {
        // ── Automático ────────────────────────────────────────────────────────

        private static readonly object AutoLock = new();
        private static Timer? _timer;
        private static int _tickRunning;
        private static TimeSpan _cooldown = MemoryOptimizerPolicy.BaseCooldown;
        private static DateTime? _gamePauseUntilUtc;
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
            var nowUtc = DateTime.UtcNow;
            if (QuickGameCheck() || (_gamePauseUntilUtc is DateTime until && nowUtc < until))
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
                Configured: s.MemoryOps,
                Level: CurrentLevel));

            SetSkip(decision.Reason, before);
            if (!decision.Run) return;

            var r = OptimizeCore(s.MemoryOps, MemoryTrigger.Auto);
            if (r.Outcome == MemoryRunOutcome.Paused)
            {
                // A varredura completa achou um jogo que a checagem leve não vê
                // (em outro monitor, minimizado, em janela): não varre de novo a
                // cada minuto enquanto ele estiver aberto.
                Idle.Reset();
                _gamePauseUntilUtc = DateTime.UtcNow + MemoryOptimizerPolicy.GamePauseBackoff;
                SetSkip(AutoSkipReason.Game, AutoSkipReason.None);
                return;
            }
            if (r.Outcome is MemoryRunOutcome.NeedsAdmin or MemoryRunOutcome.Failed)
            {
                // Sem permissão ou com erro: espera o intervalo normal em vez de
                // tentar uma otimização completa a cada minuto.
                _lastRunUtc = DateTime.UtcNow;
                return;
            }
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
            if (!s.MemoryAutoOptimize) return "";

            string text = $"Automático acima de {MemoryOptimizerPolicy.ClampThreshold(s.MemoryAutoThresholdPercent)}%";
            text += " · nível " + MemoryOptimizerPolicy.LevelName(CurrentLevel);
            if (s.MemoryAutoOnlyWhenIdle) text += " · com PC ocioso";
            switch (LastAutoSkip)
            {
                case AutoSkipReason.Game:
                    text += " · pausado: jogo aberto";
                    break;
                case AutoSkipReason.NotIdle:
                    text += " · aguardando PC ocioso";
                    break;
                case AutoSkipReason.Cooldown when _cooldown > MemoryOptimizerPolicy.BaseCooldown && _lastRunUtc is DateTime last:
                    int left = (int)Math.Ceiling((last + _cooldown - DateTime.UtcNow).TotalMinutes);
                    if (left > 0) text += $" · aguardando {left} min (a última limpeza não segurou)";
                    break;
                case AutoSkipReason.NothingToDo:
                    text += " · nenhuma área marcada";
                    break;
                case AutoSkipReason.ManualOnlyChecked:
                    text += " · só áreas manuais marcadas (rodam só no botão)";
                    break;
            }
            return text;
        }
    }
}
