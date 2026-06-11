using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace PCOptimizer.Services
{
    public static class CpuTuningDetectionService
    {
        public enum ToolKind { Undervolt, Monitor }

        public record DetectedTool(string Name, ToolKind Kind, string? InstalledPath, bool Running);

        private record Candidate(
            string Name,
            ToolKind Kind,
            string[] Paths,
            string[] ProcessNames,
            // Busca de fallback no registro: DisplayName contém essa string
            string? RegistrySearch = null,
            // Nomes de exe para tentar dentro de InstallLocation (+ subpasta bin\)
            string[]? RegistryExeNames = null);

        private static readonly Candidate[] Catalog =
        {
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
                new[] { "XtuShell", "XTU" },
                "Extreme Tuning Utility",
                new[] { "XtuShell.exe", "XTU.exe" }),

            new("Ryzen Master", ToolKind.Undervolt,
                new[]
                {
                    @"C:\Program Files\AMD\RyzenMaster\bin\AMD Ryzen Master.exe",
                    @"C:\Program Files\AMD\RyzenMaster\bin\Ryzen Master.exe",
                    @"C:\Program Files\AMD\RyzenMaster\bin\AMDRyzenMaster.exe",
                    @"C:\Program Files\AMD\RyzenMaster\bin\RyzenMaster.exe",
                    @"C:\Program Files\AMD\RyzenMaster\AMD Ryzen Master.exe",
                    @"C:\Program Files\AMD\RyzenMaster\AMDRyzenMaster.exe",
                    @"C:\Program Files\AMD\AMD Ryzen Master\bin\AMD Ryzen Master.exe",
                    @"C:\Program Files\AMD\AMD Ryzen Master\bin\AMDRyzenMaster.exe",
                },
                new[] { "AMD Ryzen Master", "RyzenMaster", "Ryzen Master", "AMDRyzenMaster" },
                "Ryzen Master",
                new[] { "AMD Ryzen Master.exe", "Ryzen Master.exe", "AMDRyzenMaster.exe", "RyzenMaster.exe" }),

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
            // Snapshot dos processos: nome -> caminho do executável
            var running = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        string name = p.ProcessName;
                        if (name.Length == 0) continue;
                        string? exePath = null;
                        try { exePath = p.MainModule?.FileName; } catch { }
                        if (!running.TryGetValue(name, out var existing) || existing == null)
                            running[name] = exePath;
                    }
                    catch { }
                }
            }
            catch { }

            var result = new List<DetectedTool>();
            foreach (var c in Catalog)
            {
                string? path = c.Paths.FirstOrDefault(File.Exists);
                bool isRunning = c.ProcessNames.Any(running.ContainsKey);

                // Processo rodando em pasta fora do padrão: pega caminho do processo vivo
                if (path == null && isRunning)
                {
                    path = c.ProcessNames
                        .Select(n => running.TryGetValue(n, out var fp) ? fp : null)
                        .FirstOrDefault(fp => !string.IsNullOrEmpty(fp) && File.Exists(fp));
                }

                // Fallback: busca no registro de programas instalados do Windows
                if (path == null && c.RegistrySearch != null && c.RegistryExeNames != null)
                    path = FindViaRegistry(c.RegistrySearch, c.RegistryExeNames);

                if (path != null || isRunning)
                    result.Add(new DetectedTool(c.Name, c.Kind, path, isRunning));
            }
            return result;
        }

        /// <summary>
        /// Procura no registro de desinstalação do Windows por uma entrada cujo
        /// DisplayName contenha <paramref name="displayNameContains"/>, depois
        /// tenta localizar o exe pelos caminhos: DisplayIcon, InstallLocation\ e
        /// InstallLocation\bin\.
        /// </summary>
        public static string? FindViaRegistry(string displayNameContains, string[] exeNames)
        {
            string[] uninstallRoots = {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            };

            foreach (var root in uninstallRoots)
            {
                try
                {
                    using var hive = Registry.LocalMachine.OpenSubKey(root);
                    if (hive == null) continue;

                    foreach (var subName in hive.GetSubKeyNames())
                    {
                        try
                        {
                            using var sub = hive.OpenSubKey(subName);
                            if (sub == null) continue;

                            var displayName = sub.GetValue("DisplayName") as string;
                            if (displayName == null ||
                                !displayName.Contains(displayNameContains, StringComparison.OrdinalIgnoreCase))
                                continue;

                            // DisplayIcon costuma apontar diretamente para o exe ("path\app.exe,0")
                            var icon = sub.GetValue("DisplayIcon") as string;
                            if (!string.IsNullOrWhiteSpace(icon))
                            {
                                var iconPath = icon.Split(',')[0].Trim('"').Trim();
                                if (Path.GetExtension(iconPath).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                                    && File.Exists(iconPath))
                                    return iconPath;
                            }

                            // InstallLocation: tenta pasta raiz e subpasta bin\
                            var installDir = sub.GetValue("InstallLocation") as string;
                            if (!string.IsNullOrWhiteSpace(installDir))
                            {
                                foreach (var exe in exeNames)
                                {
                                    foreach (var candidate in new[]
                                    {
                                        Path.Combine(installDir, exe),
                                        Path.Combine(installDir, "bin", exe),
                                    })
                                    {
                                        if (File.Exists(candidate)) return candidate;
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            return null;
        }

        /// <summary>Melhor ferramenta de undervolt já instalada para a CPU atual (null se nenhuma).</summary>
        public static DetectedTool? BestInstalledUndervoltTool(IEnumerable<DetectedTool> tools)
        {
            return tools
                .Where(t => t.Kind == ToolKind.Undervolt && t.InstalledPath != null)
                .OrderByDescending(t => t.Running)
                .FirstOrDefault();
        }
    }
}
