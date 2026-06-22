using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;
using PCOptimizer.Models;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Verificação estilo ADWCleaner: procura junkware/PUP/adware em programas
    /// instalados, inicialização, registro, tarefas agendadas, extensões de
    /// navegador e processos ativos. Cada achado carrega a própria ação de
    /// remoção (com backup em quarentena).
    /// </summary>
    public static class JunkwareScanner
    {
        /// <param name="progress">Recebe o nome da etapa atual (para a barra de progresso).</param>
        public static List<ScanFinding> Scan(Action<string>? progress = null)
        {
            var findings = new List<ScanFinding>();

            Run(progress, "Programas instalados",  () => ScanInstalledPrograms(findings));
            Run(progress, "Inicialização (registro)", () => ScanRegistryRunKeys(findings));
            Run(progress, "Inicialização (pastas)",   () => ScanStartupFolders(findings));
            Run(progress, "Tarefas agendadas",        () => ScanScheduledTasks(findings));
            Run(progress, "Extensões de navegador",   () => ScanBrowserExtensions(findings));
            Run(progress, "Processos ativos",         () => ScanProcesses(findings));

            // Alto risco primeiro; suspeitos por último.
            return findings.OrderBy(f => (int)f.Level).ThenBy(f => f.Name).ToList();
        }

        private static void Run(Action<string>? progress, string step, Action body)
        {
            try { progress?.Invoke(step); body(); }
            catch (Exception ex) { Logger.Error(ex, "JunkwareScanner: " + step); }
        }

        // ───────────────────────── Programas instalados ─────────────────────────
        private static void ScanInstalledPrograms(List<ScanFinding> findings)
        {
            var roots = new (RegistryKey Hive, string Path)[]
            {
                (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.CurrentUser,  @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            };

            foreach (var (hive, path) in roots)
            {
                using var baseKey = hive.OpenSubKey(path);
                if (baseKey == null) continue;

                foreach (var subName in baseKey.GetSubKeyNames())
                {
                    using var k = baseKey.OpenSubKey(subName);
                    string? name = k?.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    string? sig = ThreatSignatures.MatchPup(name);
                    if (sig == null) continue;

                    string uninstall = (k?.GetValue("UninstallString") as string) ?? "";
                    string displayName = name; // captura para o closure

                    findings.Add(new ScanFinding
                    {
                        Category = ScanCategory.Program,
                        Level    = ThreatLevel.Pup,
                        Name     = displayName,
                        Location = string.IsNullOrWhiteSpace(uninstall) ? path + "\\" + subName : uninstall,
                        Detail   = $"Programa potencialmente indesejado (assinatura: {sig}). " +
                                   "Abrirá o desinstalador oficial.",
                        Selected = false, // desinstalar programa exige confirmação do usuário
                        Remove   = string.IsNullOrWhiteSpace(uninstall)
                            ? null
                            : () => LaunchUninstaller(uninstall)
                    });
                }
            }
        }

        private static bool LaunchUninstaller(string uninstallString)
        {
            try
            {
                // UninstallString pode vir com aspas e argumentos: "C:\app\unins.exe" /S
                string file = uninstallString.Trim();
                string args = "";
                if (file.StartsWith("\""))
                {
                    int end = file.IndexOf('"', 1);
                    if (end > 0) { args = file.Substring(end + 1).Trim(); file = file.Substring(1, end - 1); }
                }
                else
                {
                    int sp = file.IndexOf(" /");
                    if (sp > 0) { args = file.Substring(sp + 1); file = file.Substring(0, sp); }
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName        = file,
                    Arguments       = args,
                    UseShellExecute = true
                });
                ScanQuarantine.Log($"DESINSTALADOR aberto: {uninstallString}");
                return true;
            }
            catch (Exception ex) { Logger.Error(ex, "LaunchUninstaller"); return false; }
        }

        // ───────────────────────── Registro (Run) ─────────────────────────
        private static void ScanRegistryRunKeys(List<ScanFinding> findings)
        {
            var runKeys = new (RegistryKey Hive, string HiveLabel, string Path)[]
            {
                (Registry.CurrentUser,  "HKCU", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
                (Registry.CurrentUser,  "HKCU", @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
                (Registry.LocalMachine, "HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
                (Registry.LocalMachine, "HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
                (Registry.LocalMachine, "HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
            };

            foreach (var (hive, label, path) in runKeys)
            {
                using var key = hive.OpenSubKey(path);
                if (key == null) continue;

                foreach (var valueName in key.GetValueNames())
                {
                    string data = (key.GetValue(valueName) as string) ?? "";
                    string? sig = ThreatSignatures.MatchStartup(valueName)
                               ?? ThreatSignatures.MatchStartup(data);
                    if (sig == null) continue;

                    // Captura locais para o closure de remoção.
                    RegistryKey capturedHive = hive;
                    string capturedPath = path;
                    string capturedName = valueName;
                    string fullRegPath = (label == "HKLM"
                        ? "HKEY_LOCAL_MACHINE\\" : "HKEY_CURRENT_USER\\") + path;

                    findings.Add(new ScanFinding
                    {
                        Category = ScanCategory.Registry,
                        Level    = ThreatLevel.High,
                        Name     = valueName,
                        Location = $"{label}\\...\\Run → {data}",
                        Detail   = $"Inicialização automática suspeita (assinatura: {sig}).",
                        Selected = true,
                        Remove   = () =>
                        {
                            ScanQuarantine.BackupRegistryKey(fullRegPath);
                            try
                            {
                                using var wk = capturedHive.OpenSubKey(capturedPath, writable: true);
                                wk?.DeleteValue(capturedName, throwOnMissingValue: false);
                                ScanQuarantine.Log($"REMOVIDO registro Run: {fullRegPath} → {capturedName}");
                                return true;
                            }
                            catch (Exception ex) { Logger.Error(ex, "Remove Run value"); return false; }
                        }
                    });
                }
            }
        }

        // ───────────────────────── Pastas de inicialização ─────────────────────────
        private static void ScanStartupFolders(List<ScanFinding> findings)
        {
            var folders = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)
            };

            foreach (var folder in folders.Distinct())
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;

                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    string fileName = Path.GetFileName(file);
                    string? sig = ThreatSignatures.MatchStartup(fileName);
                    if (sig == null) continue;

                    string captured = file;
                    findings.Add(new ScanFinding
                    {
                        Category = ScanCategory.Startup,
                        Level    = ThreatLevel.High,
                        Name     = fileName,
                        Location = file,
                        Detail   = $"Atalho/arquivo de inicialização suspeito (assinatura: {sig}).",
                        Selected = true,
                        Remove   = () => ScanQuarantine.MoveToQuarantine(captured)
                    });
                }
            }
        }

        // ───────────────────────── Tarefas agendadas ─────────────────────────
        private static void ScanScheduledTasks(List<ScanFinding> findings)
        {
            // schtasks /query lista as tarefas; uma por linha no formato CSV.
            string output = RunCapture("schtasks.exe", "/query /fo LIST", 20000);
            if (string.IsNullOrEmpty(output)) return;

            foreach (var line in output.Split('\n'))
            {
                if (!line.TrimStart().StartsWith("TaskName:", StringComparison.OrdinalIgnoreCase)) continue;
                string taskName = line.Substring(line.IndexOf(':') + 1).Trim();
                if (string.IsNullOrWhiteSpace(taskName)) continue;

                string? sig = ThreatSignatures.MatchTask(taskName);
                if (sig == null) continue;

                string captured = taskName;
                findings.Add(new ScanFinding
                {
                    Category = ScanCategory.ScheduledTask,
                    Level    = ThreatLevel.High,
                    Name     = taskName,
                    Location = "Agendador de Tarefas",
                    Detail   = $"Tarefa agendada criada por adware (assinatura: {sig}).",
                    Selected = true,
                    Remove   = () =>
                    {
                        // Exporta o XML para a quarentena antes de apagar (restaurável).
                        string xmlPath = ScanQuarantine.PathFor("task_" + captured + ".xml");
                        try
                        {
                            string xml = RunCapture("schtasks.exe", $"/query /tn \"{captured}\" /xml", 10000);
                            if (!string.IsNullOrWhiteSpace(xml)) File.WriteAllText(xmlPath, xml);
                        }
                        catch { /* backup é best-effort */ }

                        bool ok = ProcessRunner.Run("schtasks.exe", $"/delete /tn \"{captured}\" /f", 10000);
                        ScanQuarantine.Log($"REMOVIDA tarefa: {captured} ({(ok ? "ok" : "falhou")})");
                        return ok;
                    }
                });
            }
        }

        // ───────────────────────── Extensões de navegador ─────────────────────────
        private static void ScanBrowserExtensions(List<ScanFinding> findings)
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var browsers = new (string Name, string Root)[]
            {
                ("Chrome", Path.Combine(local, @"Google\Chrome\User Data")),
                ("Edge",   Path.Combine(local, @"Microsoft\Edge\User Data")),
                ("Brave",  Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data")),
            };

            foreach (var (browserName, root) in browsers)
            {
                if (!Directory.Exists(root)) continue;

                // Perfis: Default, Profile 1, Profile 2… — ignora diretórios auxiliares do User Data.
                foreach (var profile in Directory.EnumerateDirectories(root))
                {
                    string profileName = Path.GetFileName(profile);
                    if (profileName != "Default" &&
                        !profileName.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase) &&
                        !profileName.StartsWith("Guest Profile", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string extDir = Path.Combine(profile, "Extensions");
                    if (!Directory.Exists(extDir)) continue;

                    foreach (var idDir in Directory.EnumerateDirectories(extDir))
                    {
                        string extId = Path.GetFileName(idDir);
                        if (!ThreatSignatures.IsAdwareExtension(extId)) continue;

                        string captured = idDir;
                        findings.Add(new ScanFinding
                        {
                            Category = ScanCategory.BrowserExtension,
                            Level    = ThreatLevel.High,
                            Name     = $"{browserName}: {extId}",
                            Location = idDir,
                            Detail   = "Extensão de navegador na lista de adware conhecido.",
                            Selected = true,
                            Remove   = () => ScanQuarantine.MoveToQuarantine(captured)
                        });
                    }
                }
            }
        }

        // ───────────────────────── Processos ativos ─────────────────────────
        private static void ScanProcesses(List<ScanFinding> findings)
        {
            foreach (var proc in Process.GetProcesses())
            {
                string procName;
                try { procName = proc.ProcessName; }
                catch { continue; }

                if (!ThreatSignatures.IsMalwareProcess(procName)) continue;

                string path = "";
                try { path = proc.MainModule?.FileName ?? ""; } catch { /* acesso negado */ }

                int capturedId = proc.Id;
                string capturedName = procName;
                string capturedPath = path;

                findings.Add(new ScanFinding
                {
                    Category = ScanCategory.Process,
                    Level    = ThreatLevel.High,
                    Name     = procName + ".exe",
                    Location = string.IsNullOrEmpty(path) ? "(caminho protegido)" : path,
                    Detail   = "Processo ativo na lista de malware/adware (será encerrado).",
                    Selected = true,
                    Remove   = () =>
                    {
                        try
                        {
                            using var p = Process.GetProcessById(capturedId);
                            p.Kill(true);
                            p.WaitForExit(5000);
                        }
                        catch (Exception ex) { Logger.Warn($"Não encerrou {capturedName}: {ex.Message}"); }

                        // Tenta colocar o executável em quarentena (se não estiver em uso/protegido).
                        bool moved = false;
                        if (!string.IsNullOrEmpty(capturedPath) && File.Exists(capturedPath))
                            moved = ScanQuarantine.MoveToQuarantine(capturedPath);

                        ScanQuarantine.Log($"PROCESSO encerrado: {capturedName} (arquivo movido={moved})");
                        return true;
                    }
                });
            }
        }

        private static string RunCapture(string file, string args, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName               = file,
                    Arguments              = args,
                    RedirectStandardOutput = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                };
                using var p = Process.Start(psi);
                if (p == null) return "";
                // Lê stdout assíncrono para o timeout de WaitForExit funcionar corretamente.
                // ReadToEnd() bloquearia a thread até o processo sair, tornando o timeout inútil.
                var outputTask = p.StandardOutput.ReadToEndAsync();
                bool exited = p.WaitForExit(timeoutMs);
                if (!exited) { try { p.Kill(true); } catch { } p.WaitForExit(2000); }
                return outputTask.GetAwaiter().GetResult();
            }
            catch (Exception ex) { Logger.Error(ex, "RunCapture " + file); return ""; }
        }
    }
}
