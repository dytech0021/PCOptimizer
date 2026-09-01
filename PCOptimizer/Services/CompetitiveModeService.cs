using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Alternativa ao Turbo existente, voltada a jogos competitivos. Para o Dota
    /// 2 usa todos os threads dos P-cores como preferência (não limite rígido),
    /// E-cores para o fundo e um perfil temporário de energia.
    /// </summary>
    public static class CompetitiveModeService
    {
        public sealed class Profile
        {
            public string Name { get; init; } = "Competitivo padrão";
            public int PCoreEpp { get; init; } = 5;
            public int ECoreEpp { get; init; } = 25;
            public int BoostMode { get; init; } = 4;
            public double BackgroundThresholdPercent { get; init; } = 3.0;
        }

        private sealed class TargetRecovery
        {
            public int Pid { get; set; }
            public long StartUtcTicks { get; set; }
            public uint[] OriginalCpuSets { get; set; } = Array.Empty<uint>();
        }

        private static readonly Profile Dota2Profile = new()
        {
            Name = "Dota 2 — baixa latência",
            PCoreEpp = 0,
            ECoreEpp = 20,
            BoostMode = 2,
            BackgroundThresholdPercent = 2.0
        };

        private static readonly Profile DefaultProfile = new();
        private static readonly TimeSpan MaxDuration = TimeSpan.FromHours(8);
        private static Timer? _timer;
        private static readonly object OperationLock = new();
        private static GameTargetService.Target? _target;
        private static DateTime _started;
        private static uint[] _targetPreviousCpuSets = Array.Empty<uint>();
        private static bool _targetCpuSetsChanged;
        private static int _ticks;
        private static int _tickRunning;
        private static string _lastResult = "";

        private static string StatePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCOptimizer", "competitive-target.json");

        public static bool IsActive => _target != null;
        public static string? TargetName => _target?.Name;
        public static event Action? StatusChanged;

        public static string StatusText()
        {
            var topology = CpuTopologyService.Get();
            if (!topology.CanPark)
                return "Requer processador híbrido com P-cores e E-cores";
            if (_target == null)
                return string.IsNullOrEmpty(_lastResult)
                    ? "Perfil Dota 2: P-cores preferidos + fundo dinâmico nos E-cores"
                    : _lastResult;
            return $"{DisplayName(_target.Name)} — {CompetitiveBackgroundService.ManagedCount} no fundo, " +
                   $"{CompetitiveBackgroundService.RestrainedCount} contido(s)";
        }

        public static string ApplyTo(GameTargetService.Target target)
        {
            lock (OperationLock) return ApplyCore(target);
        }

        private static string ApplyCore(GameTargetService.Target target)
        {
            if (CompetitivePowerProfileService.HasPendingRecovery
                && !CompetitivePowerProfileService.Restore())
            {
                target.Dispose();
                return "Há um plano de energia anterior pendente de restauração";
            }
            if (File.Exists(StatePath))
            {
                RestoreOrphansFromPreviousRun();
                if (File.Exists(StatePath))
                {
                    target.Dispose();
                    return "Há uma preferência de CPU anterior pendente de restauração";
                }
            }
            var topology = CpuTopologyService.Get();
            if (!topology.CanPark)
            {
                target.Dispose();
                return "Este processador não oferece P/E-cores compatíveis";
            }

            if (_target != null) ReleaseAll("troca de jogo");
            if (GameBoostService.IsActive) GameBoostService.ReleaseAll("Modo Competitivo ativado");

            Profile profile = GetProfile(target.Name);
            uint[] pCoreIds = CpuSetService.GetIdsForMask(topology.PCoreMask);
            if (!CpuSetService.TrySet(target.Pid, pCoreIds, out _targetPreviousCpuSets))
            {
                target.Dispose();
                return "O Windows recusou a preferência de P-cores para o jogo";
            }

            _targetCpuSetsChanged = true;
            SaveTargetRecovery(target, _targetPreviousCpuSets);

            if (!CompetitiveBackgroundService.Start(target.Pid, target.ExePath,
                                                     profile.BackgroundThresholdPercent))
            {
                CpuSetService.Restore(target.Pid, _targetPreviousCpuSets);
                ClearTargetRecovery();
                _targetCpuSetsChanged = false;
                target.Dispose();
                return "Não consegui mapear os E-cores para o modo competitivo";
            }

            bool powerOk = CompetitivePowerProfileService.Apply(profile);
            _target = target;
            _started = DateTime.Now;
            _ticks = 0;
            _lastResult = "";
            CompetitiveTelemetryService.Start(target.Pid, target.Name, "Competitivo");
            EnsureTimer();
            _timer!.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

            Logger.Info($"Competitivo: {profile.Name} ativado em {target.Name} (PID {target.Pid})");
            Notify();
            return powerOk
                ? $"{profile.Name} ativo — P-cores preferidos"
                : $"{profile.Name} ativo — perfil de energia parcialmente aplicado";
        }

        public static string ReleaseAll(string reason)
        {
            lock (OperationLock) return ReleaseCore(reason);
        }

        private static string ReleaseCore(string reason)
        {
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            int restored = CompetitiveBackgroundService.RestoreAll();

            bool targetRestored = true;
            if (_target != null && _targetCpuSetsChanged)
                targetRestored = CpuSetService.Restore(_target.Pid, _targetPreviousCpuSets);
            if (RecoveryPolicy.CanClear(targetRestored))
            {
                _targetCpuSetsChanged = false;
                _targetPreviousCpuSets = Array.Empty<uint>();
                ClearTargetRecovery();
            }

            bool powerRestored = CompetitivePowerProfileService.Restore();
            string telemetry = CompetitiveTelemetryService.Stop(reason);

            if (_target != null)
            {
                Logger.Info($"Competitivo desligado ({reason}) — {restored} processo(s) restaurado(s)");
                _target.Dispose();
                _target = null;
            }
            _lastResult = string.IsNullOrEmpty(telemetry)
                ? "Modo Competitivo desligado"
                : $"Modo desligado — {telemetry}";
            if (!targetRestored || !powerRestored)
                _lastResult += " — restauração pendente preservada";
            Notify();
            return _lastResult;
        }

        public static void RestoreOrphansFromPreviousRun()
        {
            CompetitiveBackgroundService.RestoreOrphansFromPreviousRun();
            try
            {
                if (!File.Exists(StatePath)) return;
                var state = JsonSerializer.Deserialize<TargetRecovery>(File.ReadAllText(StatePath));
                if (state == null) return;
                bool resolved;
                try
                {
                    using var process = Process.GetProcessById(state.Pid);
                    resolved = process.StartTime.ToUniversalTime().Ticks != state.StartUtcTicks
                            || CpuSetService.Restore(state.Pid, state.OriginalCpuSets);
                }
                catch (ArgumentException) { resolved = true; }
                catch (InvalidOperationException) { resolved = true; }
                if (RecoveryPolicy.CanClear(resolved)) ClearTargetRecovery();
            }
            catch (Exception ex) { Logger.Error(ex, "CompetitiveMode.RestoreOrphan"); }
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
                string? stopReason = await Task.Run(TickBackground);
                if (stopReason != null) ReleaseAll(stopReason);
                else if (_ticks % 3 == 0) Notify();
            }
            catch (Exception ex) { Logger.Error(ex, "CompetitiveMode.Tick"); }
            finally { Volatile.Write(ref _tickRunning, 0); }
        }

        private static string? TickBackground()
        {
            lock (OperationLock)
            {
                if (_target == null) return "alvo indisponível";
                if (!_target.IsAlive) return "jogo encerrado";
                if (DateTime.Now - _started > MaxDuration)
                    return "limite de segurança de 8 h";

                _ticks++;
                CompetitiveBackgroundService.Tick(scanNew: _ticks % 3 == 0);
                CompetitiveTelemetryService.Sample();
                return null;
            }
        }

        private static Profile GetProfile(string processName) =>
            processName.Equals("dota2", StringComparison.OrdinalIgnoreCase)
                ? Dota2Profile : DefaultProfile;

        private static string DisplayName(string processName) =>
            processName.Equals("dota2", StringComparison.OrdinalIgnoreCase) ? "Dota 2 competitivo" : processName;

        private static void SaveTargetRecovery(GameTargetService.Target target, uint[] previous)
        {
            try
            {
                long start;
                using (var process = Process.GetProcessById(target.Pid))
                    start = process.StartTime.ToUniversalTime().Ticks;
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
                File.WriteAllText(StatePath, JsonSerializer.Serialize(new TargetRecovery
                {
                    Pid = target.Pid,
                    StartUtcTicks = start,
                    OriginalCpuSets = previous
                }));
            }
            catch (Exception ex) { Logger.Error(ex, "CompetitiveMode.SaveRecovery"); }
        }

        private static void ClearTargetRecovery()
        {
            try { if (File.Exists(StatePath)) File.Delete(StatePath); } catch { }
        }

        private static void Notify()
        {
            try { StatusChanged?.Invoke(); } catch { }
        }
    }
}
