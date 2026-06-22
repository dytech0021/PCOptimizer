using System;

namespace PCOptimizer.Models
{
    /// <summary>Gravidade de um item encontrado pelo scanner.</summary>
    public enum ThreatLevel
    {
        High       = 0, // malware/adware conhecido — remover
        Pup        = 1, // programa potencialmente indesejado (toolbar, "otimizador")
        Suspicious = 2  // suspeito — revisar antes de remover
    }

    /// <summary>Onde o item foi encontrado.</summary>
    public enum ScanCategory
    {
        Program,
        Startup,
        Registry,
        ScheduledTask,
        BrowserExtension,
        Process
    }

    /// <summary>
    /// Um item localizado durante a verificação de junkware/PUP. Carrega a própria
    /// ação de remoção (closure) montada pelo scanner que o detectou.
    /// </summary>
    public sealed class ScanFinding
    {
        public ScanCategory Category { get; init; }
        public ThreatLevel  Level    { get; init; }

        /// <summary>Nome amigável (programa, extensão, tarefa…).</summary>
        public string Name { get; init; } = "";

        /// <summary>Caminho, chave de registro ou local exato.</summary>
        public string Location { get; init; } = "";

        /// <summary>Texto curto explicando o motivo da detecção.</summary>
        public string Detail { get; init; } = "";

        /// <summary>Marcado para remoção na UI.</summary>
        public bool Selected { get; set; }

        /// <summary>
        /// Executa a remoção (deletar valor, mover arquivo p/ quarentena, matar processo…).
        /// Retorna true se removeu com sucesso. Null = sem ação automática (ex.: abrir desinstalador).
        /// </summary>
        public Func<bool>? Remove { get; init; }

        public string CategoryLabel => Category switch
        {
            ScanCategory.Program          => "Programa",
            ScanCategory.Startup          => "Inicialização",
            ScanCategory.Registry         => "Registro",
            ScanCategory.ScheduledTask    => "Tarefa agendada",
            ScanCategory.BrowserExtension => "Extensão",
            ScanCategory.Process          => "Processo",
            _                             => "Item"
        };

        public string LevelLabel => Level switch
        {
            ThreatLevel.High       => "Alto risco",
            ThreatLevel.Pup        => "Indesejado (PUP)",
            ThreatLevel.Suspicious => "Suspeito",
            _                      => ""
        };

        /// <summary>Cor (hex) do selo de gravidade.</summary>
        public string LevelColor => Level switch
        {
            ThreatLevel.High       => "#F38BA8", // vermelho
            ThreatLevel.Pup        => "#FAB387", // laranja
            ThreatLevel.Suspicious => "#F9E2AF", // amarelo
            _                      => "#A6ADC8"
        };
    }
}
