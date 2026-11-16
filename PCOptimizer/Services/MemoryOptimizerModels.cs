using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PCOptimizer.Services
{
    // Tipos do otimizador de memória (operações, níveis, leituras e resultados).

    /// <summary>Áreas da memória que o usuário pode mandar limpar, uma a uma.</summary>
    [Flags]
    public enum MemoryOperation
    {
        None               = 0,
        /// <summary>Esvazia o working set de programas abertos (nunca o jogo).</summary>
        TrimPrograms       = 1,
        /// <summary>Encolhe o working set do cache de arquivos do sistema.</summary>
        SystemFileCache    = 2,
        /// <summary>Descarta só o cache em espera que o Windows marcou como descartável.</summary>
        StandbyLowPriority = 4,
        /// <summary>Descarta TODO o cache em espera. Só no botão manual.</summary>
        StandbyFull        = 8,
        /// <summary>Grava a memória modificada no disco (sempre DEPOIS da limpeza).</summary>
        ModifiedList       = 16,
        /// <summary>Esvazia serviços do Windows (sessão 0), menos os de áudio, rede, Xbox e anticheat.</summary>
        TrimServices       = 32,
        /// <summary>Esvazia apps com janela na tela que não são jogo (navegador, Office, launchers).</summary>
        TrimVisibleApps    = 64,
        /// <summary>Baixa a prioridade de memória de processos sem janela (reversível).</summary>
        LowerMemoryPriority = 128,
    }

    /// <summary>Intensidade da otimização de memória.</summary>
    public enum MemoryLevel { Leve, Agressivo, Maximo }

    /// <summary>O que cada nível faz. Ver <see cref="MemoryOptimizerPolicy.SpecFor"/>.</summary>
    public sealed record MemoryLevelSpec(
        MemoryLevel Level,
        MemoryOperation ExtraOps,
        MemoryOperation ManualOnly,
        long MinTrimWorkingSetBytes,
        int MaxTrimProcesses,
        bool AllowWindowsDir);

    /// <summary>Em qual passada um processo pode ser esvaziado.</summary>
    public enum TrimKind { Normal, Service, VisibleApp }

    /// <summary>Por que um processo foi (ou não) escolhido.</summary>
    public enum TrimVerdict { Trim, System, Self, Protected, ProtectedName, TooSmall, ServiceDenied, NotThisPass }

    /// <summary>De onde veio o pedido de limpeza.</summary>
    public enum MemoryTrigger { Manual, Batch, Tray, Auto }

    public enum MemoryRunOutcome { Done, NothingToDo, NeedsAdmin, Paused, Busy, Failed }

    public enum MemoryGaugeLevel { Normal, Warning, Danger }

    public enum AutoSkipReason { None, Disabled, Game, NoReading, Cooldown, BelowThreshold, NotIdle, NothingToDo, ManualOnlyChecked }

    /// <summary>
    /// Foto da memória. Os campos das listas são null quando não dá para lê-las
    /// (sem administrador) — nunca inventamos o tamanho do cache.
    /// </summary>
    public readonly record struct MemorySnapshot(
        ulong TotalBytes, ulong AvailableBytes,
        ulong? FreeBytes, ulong? StandbyBytes, ulong? ModifiedBytes)
    {
        public bool IsValid  => TotalBytes > 0;
        public bool HasLists => FreeBytes.HasValue && StandbyBytes.HasValue && ModifiedBytes.HasValue;

        /// <summary>
        /// "Em uso" como o Gerenciador de Tarefas mostra: o total menos o
        /// disponível (livre + cache em espera) e, quando conhecida, menos a
        /// memória modificada — que o Gerenciador desenha à parte.
        /// </summary>
        public ulong InUseBytes
        {
            get
            {
                if (TotalBytes == 0 || AvailableBytes >= TotalBytes) return 0;
                ulong used = TotalBytes - AvailableBytes;
                if (ModifiedBytes is ulong m) used = used > m ? used - m : 0;
                return used;
            }
        }

        public double InUsePercent => TotalBytes == 0 ? 0 : InUseBytes * 100.0 / TotalBytes;
    }

    /// <summary>Um processo visto no retrato do sistema, sem abrir nada.</summary>
    public readonly record struct ProcessCandidate(int Pid, string Name, int SessionId, long WorkingSetBytes);

    /// <summary>O que NÃO pode ser tocado numa passada de limpeza.</summary>
    public sealed record TrimContext(
        int MyPid,
        IReadOnlySet<int> ProtectedPids,
        IReadOnlyCollection<string> ExtraProtectedNames,
        IReadOnlyCollection<string> ProtectedDirs,
        IReadOnlyCollection<string> UserProtected,
        string WindowsDir)
    {
        private static readonly IReadOnlySet<int> Empty = new HashSet<int>();

        /// <summary>Apps com janela na tela que não são jogo (e seus filhos): só no nível Máximo.</summary>
        public IReadOnlySet<int> VisibleNonGamePids { get; init; } = Empty;

        /// <summary>Serviços que nunca são esvaziados (áudio, rede, Xbox, anticheat).</summary>
        public IReadOnlySet<int> ServiceDenyPids { get; init; } = Empty;
    }

    /// <summary>Plano de uma execução: o que roda e o que ficou de fora, e por quê.</summary>
    public readonly record struct MemoryPlan(
        MemoryOperation Ops, MemoryOperation SkippedNoAdmin, MemoryOperation SkippedByPolicy);

    public sealed record AutoInputs(
        bool Enabled, int ThresholdPercent, MemorySnapshot Snapshot, bool GameOpen,
        bool RequireIdle, bool IsIdle, DateTime NowUtc, DateTime? LastRunUtc,
        TimeSpan Cooldown, MemoryOperation Configured, MemoryLevel Level = MemoryLevel.Leve);

    public readonly record struct AutoDecision(bool Run, AutoSkipReason Reason);

    /// <summary>Um processo que ficou de fora, para explicar um ganho pequeno.</summary>
    public readonly record struct ProtectedEntry(string Name, string Reason, ulong Bytes);

    /// <summary>Medições extras de uma execução, para o texto do resultado.</summary>
    public sealed record MemoryRunExtras(
        MemoryLevel Level,
        int TrimmedProcesses,
        long CompressedDeltaBytes,
        IReadOnlyList<ProtectedEntry> TopProtected);

    public sealed record MemoryRunResult(
        MemoryRunOutcome Outcome, MemorySnapshot Before, MemorySnapshot After,
        MemoryOperation Done, MemoryOperation Failed, MemoryOperation SkippedNoAdmin,
        int TrimmedProcesses, string Text);

    /// <summary>Retângulo em pixels, com bordas exclusivas à direita/embaixo (como RECT).</summary>
    public readonly record struct Rect32(int Left, int Top, int Right, int Bottom)
    {
        public long Width  => (long)Right - Left;
        public long Height => (long)Bottom - Top;
    }

    /// <summary>O que se sabe de uma janela de topo, lido sem tocar no processo dono.</summary>
    public readonly record struct WindowFacts(
        Rect32 Rect, Rect32 Monitor, bool Visible, bool Minimized, bool Cloaked, bool Zoomed,
        long Style, long ExStyle, string ClassName, string OwnerName, bool OwnerIsSelf);
}
