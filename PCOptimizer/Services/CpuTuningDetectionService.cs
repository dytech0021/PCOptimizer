using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Detecta ferramentas de tuning/monitoramento já presentes no PC e aproveita
    /// o que estiver instalado: se houver uma ferramenta de undervolt dedicada
    /// (ThrottleStop/XTU para Intel, Ryzen Master para AMD), o app a abre direto,
    /// sem precisar baixar nada. Monitores (HWiNFO/CPU-Z/AIDA64/Afterburner) também
    /// são reportados — úteis para acompanhar tensão/temperatura durante o ajuste.
    /// </summary>
    public static class CpuTuningDetectionService
    {
        public enum ToolKind { Undervolt, Monitor }

        public record DetectedTool(string Name, ToolKind Kind, string? InstalledPath, bool Running);

        private record Candidate(string Name, ToolKind Kind, string[] Paths, string[] ProcessNames);

        private static readonly Candidate[] Catalog =
        {
            // Ferramentas de undervolt dedicadas
            new("ThrottleStop", ToolKind.Undervolt,
                new[]
                {
                    @"C:\Program Files\ThrottleStop\ThrottleStop.exe",
                    @"C:\Program Files (x86)\ThrottleStop\ThrottleStop.exe",
                    @"C:\ThrottleStop\ThrottleStop.exe",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                        "ThrottleStop", "ThrottleStop.exe"),
                },
                new[] { "ThrottleStop" }),
            new("Intel XTU", ToolKind.Undervolt,
                new[]
                {
                    @"C:\Program Files (x86)\Intel\Intel(R) Extreme Tuning Utility\Client\XtuShell.exe",
                    @"C:\Program Files\Intel\Intel(R) Extreme Tuning Utility\Client\XtuShell.exe",
                },
                new[] { "XtuShell", "XTU" }),
            new("Ryzen Master", ToolKind.Undervolt,
                new[]
                {
                    @"C:\Program Files\AMD\RyzenMaster\bin\AMD Ryzen Master.exe",
                    @"C:\Program Files\AMD\RyzenMaster\bin\Ryzen Master.exe",
                },
                new[] { "AMD Ryzen Master", "Ryzen Master" }),

            // Monitores com driver de MSR (úteis para acompanhar o ajuste)
            new("HWiNFO64", ToolKind.Monitor,
                new[]
                {
                    @"C:\Program Files\HWiNFO64\HWiNFO64.exe",
                    @"C:\Program Files (x86)\HWiNFO64\HWiNFO64.exe",
                },
                new[] { "HWiNFO64", "HWiNFO" }),
            new("CPU-Z", ToolKind.Monitor,
                new[]
                {
                    @"C:\Program Files\CPUID\CPU-Z\cpuz.exe",
                    @"C:\Program Files (x86)\CPUID\CPU-Z\cpuz.exe",
                },
                new[] { "cpuz", "cpuz_x64" }),
            new("AIDA64", ToolKind.Monitor,
                new[]
                {
                    @"C:\Program Files (x86)\FinalWire\AIDA64\aida64.exe",
                    @"C:\Program Files\FinalWire\AIDA64\aida64.exe",
                },
                new[] { "aida64" }),
            new("MSI Afterburner", ToolKind.Monitor,
                new[]
                {
                    @"C:\Program Files\MSI Afterburner\MSIAfterburner.exe",
                    @"C:\Program Files (x86)\MSI Afterburner\MSIAfterburner.exe",
                },
                new[] { "MSIAfterburner" }),
        };

        public static List<DetectedTool> Detect()
        {
            // Snapshot único dos processos — evita varrer a lista por candidato
            HashSet<string> running;
            try
            {
                running = Process.GetProcesses()
                    .Select(p => { try { return p.ProcessName; } catch { return ""; } })
                    .Where(n => n.Length > 0)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var result = new List<DetectedTool>();
            foreach (var c in Catalog)
            {
                string? path = c.Paths.FirstOrDefault(File.Exists);
                bool isRunning = c.ProcessNames.Any(running.Contains);
                if (path != null || isRunning)
                    result.Add(new DetectedTool(c.Name, c.Kind, path, isRunning));
            }
            return result;
        }

        /// <summary>Melhor ferramenta de undervolt já instalada para a CPU atual (null se nenhuma).</summary>
        public static DetectedTool? BestInstalledUndervoltTool(IEnumerable<DetectedTool> tools)
        {
            return tools
                .Where(t => t.Kind == ToolKind.Undervolt && t.InstalledPath != null)
                .OrderByDescending(t => t.Running) // prioriza a que já está aberta
                .FirstOrDefault();
        }
    }
}
