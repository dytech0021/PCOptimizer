using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PCOptimizer.Services
{
    // Parte das regras de memória: textos mostrados ao usuário.
    public static partial class MemoryOptimizerPolicy
    {
        // ── Textos ────────────────────────────────────────────────────────────

        public static MemoryGaugeLevel Level(double inUsePercent) =>
            inUsePercent >= 90 ? MemoryGaugeLevel.Danger
            : inUsePercent >= 70 ? MemoryGaugeLevel.Warning
            : MemoryGaugeLevel.Normal;

        /// <summary>Bytes em pt-BR: "512 MB" abaixo de 1 GB, "1,3 GB" a partir dele.</summary>
        public static string FormatBytes(ulong bytes)
        {
            const double MiB = 1024.0 * 1024.0, GiB = MiB * 1024.0;
            if (bytes < 1024UL * 1024 * 1024)
                return ((ulong)Math.Round(bytes / MiB)).ToString(CultureInfo.InvariantCulture) + " MB";
            return (bytes / GiB).ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + " GB";
        }

        public static (string Usage, string Detail) DescribeSnapshot(MemorySnapshot s, bool listsNeedAdmin)
        {
            if (!s.IsValid) return ("Não consegui ler a memória", "—");

            string usage = $"Em uso {Pct(s.InUsePercent)}% · {FormatBytes(s.InUseBytes)} de {FormatBytes(s.TotalBytes)}";
            string detail = $"Disponível {FormatBytes(s.AvailableBytes)}";
            if (s.HasLists)
            {
                detail += $" · livre {FormatBytes(s.FreeBytes!.Value)} · cache em espera {FormatBytes(s.StandbyBytes!.Value)}";
                if (s.ModifiedBytes!.Value >= SmallGainBytes)
                    detail += $" · modificada {FormatBytes(s.ModifiedBytes.Value)}";
            }
            else if (listsNeedAdmin)
            {
                detail += " · cache em espera: requer administrador";
            }
            return (usage, detail);
        }

        public static MemoryRunOutcome Classify(MemoryOperation done, MemoryOperation failed,
                                                MemoryOperation skippedNoAdmin, bool paused, bool busy)
        {
            if (busy) return MemoryRunOutcome.Busy;
            if (paused) return MemoryRunOutcome.Paused;
            if (done != MemoryOperation.None) return MemoryRunOutcome.Done;
            if (failed != MemoryOperation.None) return MemoryRunOutcome.Failed;
            if (skippedNoAdmin != MemoryOperation.None) return MemoryRunOutcome.NeedsAdmin;
            return MemoryRunOutcome.NothingToDo;
        }

        /// <summary>
        /// Texto do resultado. Honesto de propósito: esvaziar o cache em espera
        /// NÃO aumenta a RAM disponível (o cache já contava como disponível) — ele
        /// só vira memória livre, e o texto diz exatamente isso.
        /// </summary>
        public static string DescribeResult(MemoryRunOutcome outcome, MemorySnapshot before, MemorySnapshot after,
                                            MemoryOperation failed, MemoryOperation skippedNoAdmin, DateTime localTime)
            => DescribeResult(outcome, before, after, failed, skippedNoAdmin, MemoryOperation.None, localTime);

        public static string DescribeResult(MemoryRunOutcome outcome, MemorySnapshot before, MemorySnapshot after,
                                            MemoryOperation failed, MemoryOperation skippedNoAdmin,
                                            MemoryOperation skippedByPolicy, DateTime localTime)
            => DescribeResult(outcome, before, after, failed, skippedNoAdmin, skippedByPolicy, localTime, null);

        public static string DescribeResult(MemoryRunOutcome outcome, MemorySnapshot before, MemorySnapshot after,
                                            MemoryOperation failed, MemoryOperation skippedNoAdmin,
                                            MemoryOperation skippedByPolicy, DateTime localTime,
                                            MemoryRunExtras? extras)
        {
            string when = " · " + localTime.ToString("HH:mm", CultureInfo.InvariantCulture);
            switch (outcome)
            {
                case MemoryRunOutcome.Busy:        return "Já há uma otimização em andamento";
                case MemoryRunOutcome.Paused:      return GamePausedText;
                case MemoryRunOutcome.NeedsAdmin:  return "Requer administrador — nada foi alterado";
                case MemoryRunOutcome.Failed:      return "Não consegui otimizar — detalhes no log" + when;
                case MemoryRunOutcome.NothingToDo:
                    return (skippedByPolicy != MemoryOperation.None
                        ? "As áreas marcadas só rodam no botão “Otimizar agora”"
                        : "Nenhuma área para limpar agora") + when;
            }

            string text;
            ulong freed = before.InUseBytes > after.InUseBytes ? before.InUseBytes - after.InUseBytes : 0;
            ulong freeGain = 0;
            if (before.HasLists && after.HasLists && after.FreeBytes!.Value > before.FreeBytes!.Value)
                freeGain = after.FreeBytes.Value - before.FreeBytes.Value;

            ulong availGain = after.AvailableBytes > before.AvailableBytes ? after.AvailableBytes - before.AvailableBytes : 0;

            if (freed >= SmallGainBytes)
            {
                text = $"Em uso {Pct(before.InUsePercent)}% → {Pct(after.InUsePercent)}% (−{FormatBytes(freed)})";
                // Sem as listas, o disponível cresce exatamente o que o "em uso"
                // caiu — repetir o número não diz nada novo.
                if (availGain >= SmallGainBytes && before.HasLists && after.HasLists)
                    text += $" · disponível {FormatBytes(before.AvailableBytes)} → {FormatBytes(after.AvailableBytes)}";
                else if (freeGain >= SmallGainBytes)
                    text += $" · livre +{FormatBytes(freeGain)}";
            }
            else if (freeGain >= SmallGainBytes)
            {
                text = $"Cache em espera virou memória livre: +{FormatBytes(freeGain)} " +
                       "(o “em uso” não muda — o cache já contava como disponível)";
            }
            else if (extras is { TopProtected.Count: > 0 })
            {
                // Ganho pequeno porque o que mais ocupa estava protegido: dizer
                // o quê, em vez de culpar o Windows.
                text = $"Pouco a liberar (−{FormatBytes(freed)}) — o que mais ocupa está protegido: " +
                       string.Join(", ", extras.TopProtected.Take(3)
                           .Select(e => $"{StripExe(e.Name)} ({e.Reason}) {FormatBytes(e.Bytes)}"));
                if (extras.Level != MemoryLevel.Maximo
                    && extras.TopProtected.Any(e => e.Reason == ReasonOnScreen))
                    text += " · no nível Máximo apps na tela também entram";
            }
            else
            {
                text = $"Pouco a liberar (−{FormatBytes(freed)}) — o Windows já está gerenciando bem a memória";
            }

            if (extras is { CompressedDeltaBytes: >= 64L << 20 })
                text += $" · {FormatBytes((ulong)extras.CompressedDeltaBytes)} comprimidos na própria RAM";
            if (extras is { TrimmedProcesses: > 0 })
                text += $" · {extras.TrimmedProcesses} processo(s)";

            int noAdmin = PopCount(skippedNoAdmin);
            if (noAdmin > 0) text += $" · {noAdmin} limpeza(s) pulada(s): requer administrador";
            int fails = PopCount(failed);
            if (fails > 0) text += $" · {fails} falha(s) — veja o log";
            if (skippedByPolicy != MemoryOperation.None)
                text += " · " + string.Join(" e ", OrderedSteps(skippedByPolicy).Select(LabelOf)) + ": só no botão";
            return text + when;
        }

        public static string LabelOf(MemoryOperation op) => op switch
        {
            MemoryOperation.TrimPrograms        => "programas abertos",
            MemoryOperation.SystemFileCache     => "cache de arquivos do sistema",
            MemoryOperation.StandbyLowPriority  => "cache em espera de baixa prioridade",
            MemoryOperation.StandbyFull         => "cache em espera completo",
            MemoryOperation.ModifiedList        => "memória modificada",
            MemoryOperation.TrimServices        => "serviços do Windows",
            MemoryOperation.TrimVisibleApps     => "apps na tela",
            MemoryOperation.LowerMemoryPriority => "prioridade de memória",
            _ => op.ToString(),
        };

        // Motivos de proteção mostrados ao usuário
        public const string ReasonInUse     = "em uso";
        public const string ReasonOnScreen  = "janela na tela";
        public const string ReasonGame      = "jogo";
        public const string ReasonName      = "voz, música ou sistema";
        public const string ReasonService   = "serviço protegido";
        public const string ReasonGamePath  = "pasta de jogo";
    }
}
