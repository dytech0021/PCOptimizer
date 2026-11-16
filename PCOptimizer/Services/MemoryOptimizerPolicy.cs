using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Regras do otimizador de memória. Nada aqui fala com o Windows — o serviço
    /// lê o sistema e esta classe decide. Separado para poder ser coberto por
    /// teste: a regra de "nunca mexer no jogo" não pode falhar em silêncio.
    /// As outras partes desta classe ficam em MemoryOptimizerPolicy.*.cs, uma por assunto.
    /// </summary>
    public static partial class MemoryOptimizerPolicy
    {
        // ── Limites ────────────────────────────────────────────────────────────
        public const int   ThresholdMin = 50, ThresholdMax = 95, ThresholdStep = 5, ThresholdDefault = 85;
        public const long  MinTrimWorkingSetBytes = 64L << 20;   // 64 MB (nível Leve)
        public const int   MaxTrimProcesses = 40;
        /// <summary>Nível Agressivo: todo programa elegível a partir de 16 MB.</summary>
        public const long  AggressiveMinTrimWorkingSetBytes = 16L << 20;
        public const int   AggressiveMaxTrimProcesses = 400;
        /// <summary>Nível Máximo: a partir de 8 MB.</summary>
        public const long  MaximoMinTrimWorkingSetBytes = 8L << 20;
        public const int   MaximoMaxTrimProcesses = 1000;
        /// <summary>Memória modificada abaixo disso já "assentou" depois da gravação.</summary>
        public const ulong SettledModifiedBytes = 64UL << 20;
        public static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(2);
        public const ulong SmallGainBytes = 150UL << 20;         // abaixo disso = "pouco a liberar"

        public static readonly TimeSpan AutoPollInterval  = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan BaseCooldown      = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan MaxCooldown       = TimeSpan.FromHours(2);
        /// <summary>Menor que a espera: a checagem do ganho sempre acontece antes da próxima execução.</summary>
        public static readonly TimeSpan PersistCheckDelay = TimeSpan.FromMinutes(8);
        /// <summary>Jogo achado pela varredura completa: não varre de novo por esse tempo.</summary>
        public static readonly TimeSpan GamePauseBackoff  = TimeSpan.FromMinutes(5);

        public const int IdleCpuPercent = 10;
        public static readonly TimeSpan IdleRequired = TimeSpan.FromMinutes(5);

        /// <summary>Áreas que só rodam no botão "Otimizar agora", nunca sozinhas.</summary>
        public const MemoryOperation ManualOnly = MemoryOperation.StandbyFull | MemoryOperation.ModifiedList;

        public const MemoryOperation All =
            MemoryOperation.TrimPrograms | MemoryOperation.SystemFileCache |
            MemoryOperation.StandbyLowPriority | MemoryOperation.StandbyFull | MemoryOperation.ModifiedList |
            MemoryOperation.TrimServices | MemoryOperation.TrimVisibleApps | MemoryOperation.LowerMemoryPriority;

        /// <summary>O que vem marcado de fábrica: só o que não tem efeito colateral sério.</summary>
        public const MemoryOperation DefaultOps =
            MemoryOperation.TrimPrograms | MemoryOperation.SystemFileCache | MemoryOperation.StandbyLowPriority;

        /// <summary>
        /// O que o nível Agressivo soma às áreas marcadas. O cache em espera
        /// completo fica de fora em TODOS os níveis: ele faz o próximo jogo
        /// carregar tudo do disco de novo — e a regra é não afetar jogos.
        /// </summary>
        public const MemoryOperation AggressiveOps =
            MemoryOperation.TrimPrograms | MemoryOperation.SystemFileCache |
            MemoryOperation.StandbyLowPriority | MemoryOperation.ModifiedList |
            MemoryOperation.LowerMemoryPriority;

        /// <summary>O que o nível Máximo soma além do Agressivo.</summary>
        public const MemoryOperation MaximoOps =
            AggressiveOps | MemoryOperation.TrimServices | MemoryOperation.TrimVisibleApps;

        public static MemoryLevelSpec SpecFor(MemoryLevel level) => level switch
        {
            MemoryLevel.Agressivo => new(level, AggressiveOps, MemoryOperation.StandbyFull,
                                         AggressiveMinTrimWorkingSetBytes, AggressiveMaxTrimProcesses, false),
            // Apps com janela na tela só com o usuário olhando (botão e bandeja):
            // no lote e no automático ele pode estar fazendo outra coisa neles.
            MemoryLevel.Maximo    => new(level, MaximoOps,
                                         MemoryOperation.StandbyFull | MemoryOperation.TrimVisibleApps,
                                         MaximoMinTrimWorkingSetBytes, MaximoMaxTrimProcesses, true),
            _                     => new(MemoryLevel.Leve, MemoryOperation.None, ManualOnly,
                                         MinTrimWorkingSetBytes, MaxTrimProcesses, false),
        };

        /// <summary>
        /// Nível salvo, migrando configurações antigas: quem tinha ligado o
        /// "Modo agressivo" continua no Agressivo; os demais, no Leve.
        /// </summary>
        public static MemoryLevel ResolveLevel(MemoryLevel? saved, bool legacyAggressive) =>
            saved is MemoryLevel l && Enum.IsDefined(l) ? l
            : legacyAggressive ? MemoryLevel.Agressivo : MemoryLevel.Leve;

        public static string LevelName(MemoryLevel level) => level switch
        {
            MemoryLevel.Agressivo => "Agressivo",
            MemoryLevel.Maximo    => "Máximo",
            _                     => "Leve",
        };

        /// <summary>O que o nível faz, em português simples.</summary>
        public static string DescribeLevel(MemoryLevel level) => level switch
        {
            MemoryLevel.Agressivo =>
                "Todos os programas em segundo plano (a partir de 16 MB), grava a memória modificada " +
                "no disco e deixa quem não tem janela com prioridade de memória baixa — o próximo jogo " +
                "ganha espaço primeiro.",
            MemoryLevel.Maximo =>
                "Tudo do Agressivo, mais serviços do Windows e apps com janela na tela que não são jogo " +
                "(navegador, Office, launchers), menos o que você está usando agora. Eles podem levar " +
                "um instante para responder quando você voltar a eles.",
            _ =>
                "Os 40 maiores programas em segundo plano e os caches leves. Nada que está na tela é tocado.",
        };

        /// <summary>Áreas que dependem de privilégio de administrador habilitado.</summary>
        public const MemoryOperation ListOps =
            MemoryOperation.StandbyLowPriority | MemoryOperation.StandbyFull | MemoryOperation.ModifiedList;

        public const string GamePausedText = "Pausado: há um jogo ou app em tela cheia aberto — nada foi alterado";

        /// <summary>Texto da pausa nomeando quem bloqueou, para o usuário saber o porquê.</summary>
        public static string PausedText(string? blocker) =>
            string.IsNullOrWhiteSpace(blocker)
                ? GamePausedText
                : $"Pausado: {StripExe(blocker)} aberto (jogo ou tela cheia) — nada foi alterado";

        // ── Leitura ───────────────────────────────────────────────────────────

        /// <summary>
        /// Monta a foto a partir dos contadores do kernel (em PÁGINAS). Cache em
        /// espera = soma das 8 prioridades; livre = livre + zerada; modificada
        /// inclui a que não vai para o arquivo de paginação.
        /// </summary>
        public static MemorySnapshot FromLists(ulong totalPhys, ulong availPhys,
            IReadOnlyList<ulong> standbyPagesByPriority, ulong zeroPages, ulong freePages,
            ulong modifiedPages, ulong modifiedNoWritePages, int pageSize)
        {
            ulong page = (ulong)Math.Max(1, pageSize);
            ulong standby = 0;
            foreach (var p in standbyPagesByPriority) standby += p;
            return new MemorySnapshot(totalPhys, availPhys,
                (zeroPages + freePages) * page,
                standby * page,
                (modifiedPages + modifiedNoWritePages) * page);
        }

        // ── Plano ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Monta o plano a partir do que o usuário marcou.
        /// - Fora do botão manual, as áreas pesadas (cache completo, memória
        ///   modificada) saem: uma otimização em lote ou automática não pode jogar
        ///   fora todo o cache de arquivos sem o usuário estar olhando.
        /// - O cache completo já inclui o de baixa prioridade.
        /// - Sem privilégio, a área vira "pulada", não "falhou".
        /// </summary>
        public static MemoryPlan BuildPlan(MemoryOperation configured, MemoryTrigger trigger,
                                           bool canPurgeLists, bool canTrimFileCache,
                                           MemoryLevel level = MemoryLevel.Leve)
        {
            var ops = EffectiveOps(configured, level);
            var byPolicy = MemoryOperation.None;
            var noAdmin = MemoryOperation.None;

            // A bandeja é um clique do usuário, como o botão: só lote e automático
            // ficam sem as áreas que pedem alguém olhando.
            if (trigger is MemoryTrigger.Batch or MemoryTrigger.Auto
                || (trigger == MemoryTrigger.Tray && level != MemoryLevel.Maximo))
            {
                var manualOnly = ManualOnlyFor(level);
                byPolicy |= ops & manualOnly;
                ops &= ~manualOnly;
            }
            else if (trigger == MemoryTrigger.Tray)
            {
                // Bandeja no Máximo: apps na tela entram, cache completo não.
                byPolicy |= ops & MemoryOperation.StandbyFull;
                ops &= ~MemoryOperation.StandbyFull;
            }

            if ((ops & MemoryOperation.StandbyFull) != 0)
                ops &= ~MemoryOperation.StandbyLowPriority;

            if (!canPurgeLists)
            {
                noAdmin |= ops & ListOps;
                ops &= ~ListOps;
            }
            if (!canTrimFileCache)
            {
                noAdmin |= ops & MemoryOperation.SystemFileCache;
                ops &= ~MemoryOperation.SystemFileCache;
            }
            return new MemoryPlan(ops, noAdmin, byPolicy);
        }

        /// <summary>Áreas marcadas, somadas às do nível.</summary>
        public static MemoryOperation EffectiveOps(MemoryOperation configured, MemoryLevel level) =>
            ((configured & UserOps) | SpecFor(level).ExtraOps) & All;

        /// <summary>
        /// Áreas que o usuário marca nas caixas. Serviços, apps na tela e
        /// prioridade vêm só do nível — nunca de um valor salvo.
        /// </summary>
        public const MemoryOperation UserOps =
            MemoryOperation.TrimPrograms | MemoryOperation.SystemFileCache |
            MemoryOperation.StandbyLowPriority | MemoryOperation.StandbyFull | MemoryOperation.ModifiedList;

        /// <summary>
        /// Áreas que só rodam no botão. A partir do Agressivo a memória
        /// modificada pode rodar sozinha (só grava no disco; o cache continua);
        /// o cache completo nunca, porque faz o próximo jogo carregar do disco.
        /// </summary>
        public static MemoryOperation ManualOnlyFor(MemoryLevel level) => SpecFor(level).ManualOnly;

        /// <summary>
        /// Ordem de execução. Os caches vêm ANTES dos programas: as páginas tiradas
        /// dos programas caem num cache em espera recém-limpo e continuam em RAM —
        /// se o programa voltar a usá-las, custa um soft fault, não uma leitura de
        /// disco. A memória modificada vai por ÚLTIMO: é a limpeza que a enche, e
        /// gravá-la antes não tinha quase nada a gravar.
        /// </summary>
        public static IReadOnlyList<MemoryOperation> OrderedSteps(MemoryOperation ops)
        {
            var order = new[]
            {
                MemoryOperation.StandbyFull,
                MemoryOperation.StandbyLowPriority,
                MemoryOperation.SystemFileCache,
                MemoryOperation.LowerMemoryPriority,
                MemoryOperation.TrimPrograms,
                MemoryOperation.TrimServices,
                MemoryOperation.TrimVisibleApps,
                MemoryOperation.ModifiedList,
            };
            return order.Where(o => (ops & o) != 0).ToList();
        }

        // ── Automático ────────────────────────────────────────────────────────

        public static int ClampThreshold(int value)
        {
            int rounded = (int)Math.Round(value / (double)ThresholdStep, MidpointRounding.AwayFromZero) * ThresholdStep;
            return Math.Clamp(rounded, ThresholdMin, ThresholdMax);
        }

        /// <summary>Decide se o automático roda agora, na ordem em que as razões importam.</summary>
        public static AutoDecision DecideAuto(AutoInputs i)
        {
            if (!i.Enabled) return new(false, AutoSkipReason.Disabled);
            if (i.GameOpen) return new(false, AutoSkipReason.Game);
            if (!i.Snapshot.IsValid) return new(false, AutoSkipReason.NoReading);
            if (i.LastRunUtc is DateTime last && i.NowUtc - last < i.Cooldown)
                return new(false, AutoSkipReason.Cooldown);
            if (i.Snapshot.InUsePercent < ClampThreshold(i.ThresholdPercent))
                return new(false, AutoSkipReason.BelowThreshold);
            if (i.RequireIdle && !i.IsIdle) return new(false, AutoSkipReason.NotIdle);
            if (BuildPlan(i.Configured, MemoryTrigger.Auto, true, true, i.Level).Ops == MemoryOperation.None)
                return new(false, EffectiveOps(i.Configured, i.Level) == MemoryOperation.None
                    ? AutoSkipReason.NothingToDo
                    : AutoSkipReason.ManualOnlyChecked);
            return new(true, AutoSkipReason.None);
        }

        /// <summary>
        /// O ganho de uma limpeza automática se SUSTENTOU? Logo depois de esvaziar
        /// os programas o "em uso" sempre cai — eles puxam as páginas de volta em
        /// minutos. Conta como ganho real só se, um tempo depois, o uso não
        /// recuperou mais da metade da queda nem voltou a passar do limite.
        /// </summary>
        public static bool GainHeld(ulong beforeInUse, ulong afterInUse, ulong laterInUse,
                                    ulong totalBytes, int thresholdPercent)
        {
            if (totalBytes == 0) return false;
            ulong drop = beforeInUse > afterInUse ? beforeInUse - afterInUse : 0;
            if (drop < SmallGainBytes) return false;
            ulong recovered = laterInUse > afterInUse ? laterInUse - afterInUse : 0;
            if (recovered * 2 > drop) return false;
            return laterInUse * 100.0 / totalBytes < ClampThreshold(thresholdPercent);
        }

        /// <summary>Ganho que não se sustenta dobra a espera (até 4 h); ganho real volta a 30 min.</summary>
        public static TimeSpan NextCooldown(TimeSpan current, bool productive)
        {
            if (productive) return BaseCooldown;
            var doubled = TimeSpan.FromTicks(Math.Max(current.Ticks, BaseCooldown.Ticks) * 2);
            return doubled > MaxCooldown ? MaxCooldown : doubled;
        }

        // ── Auxiliares ────────────────────────────────────────────────────────

        private static string Pct(double v) =>
            Math.Round(v, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);

        private static int PopCount(MemoryOperation ops)
        {
            int n = 0;
            for (int v = (int)ops; v != 0; v &= v - 1) n++;
            return n;
        }

        public static string StripExe(string name) =>
            name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

        public static string FileNameWithoutExtension(string path)
        {
            int slash = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
            string file = slash >= 0 ? path[(slash + 1)..] : path;
            int dot = file.LastIndexOf('.');
            return dot > 0 ? file[..dot] : file;
        }

        /// <summary>
        /// <paramref name="path"/> está dentro de <paramref name="dir"/>? Respeita o
        /// separador: "D:\Jogos\Foo" não contém "D:\Jogos\FooBar".
        /// </summary>
        public static bool IsUnder(string path, string dir)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(dir)) return false;
            string d = dir.Replace('/', '\\').TrimEnd('\\') + "\\";
            string p = path.Replace('/', '\\');
            return p.StartsWith(d, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Pasta de um executável ("C:\a\b.exe" → "C:\a"), ou null.</summary>
        public static string? DirectoryOf(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            int slash = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
            return slash > 0 ? path[..slash] : null;
        }
    }
}
