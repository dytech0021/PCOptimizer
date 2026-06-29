using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace PCOptimizer.Services
{
    internal enum StartupKind { RegistryRun, StartupFolder }

    public class StartupEntry
    {
        public string Name { get; set; } = "";
        public string Command { get; set; } = "";
        public string Source { get; set; } = "";
        public bool IsEnabled { get; set; } = true;

        // ── Roteamento interno (não exibido na UI) ──
        internal StartupKind Kind { get; set; }
        internal bool MachineHive { get; set; }   // true = HKLM, false = HKCU
        internal bool Is32Bit { get; set; }       // WOW6432Node → StartupApproved\Run32
        internal string ValueName { get; set; } = ""; // nome do valor Run, ou nome do arquivo (.lnk)
        internal string FilePath { get; set; } = "";  // caminho do arquivo (pasta) — vazio p/ registro
        internal bool LegacyDisabled { get; set; }     // desativado pelo método antigo (renomeado)
    }

    /// <summary>
    /// Gerencia os programas de inicialização exatamente como a aba "Inicialização"
    /// do Gerenciador de Tarefas: lê as mesmas fontes (Run de usuário/sistema, 32 bits
    /// e pastas de inicialização do usuário e de todos) e usa as chaves StartupApproved
    /// para ler/alterar o estado ativado/desativado — o mesmo lugar que o Windows usa.
    /// Assim, o que você desativa aqui aparece desativado no Gerenciador de Tarefas, e
    /// vice-versa.
    /// </summary>
    public static class StartupManager
    {
        private const string RunCU   = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunLM   = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string RunLM32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
        private const string RunCU32 = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";

        private const string ApprovedBase =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

        // Compatibilidade com versões antigas do PC Optimizer, que desativavam
        // renomeando o valor/arquivo em vez de usar o StartupApproved.
        private const string DisabledSuffix = "_PCOptimizer_Disabled";

        public static List<StartupEntry> GetStartupEntries()
        {
            var entries = new List<StartupEntry>();

            // Registro — mesmas chaves que o Gerenciador de Tarefas lê.
            ReadRun(Registry.CurrentUser,  RunCU,   machine: false, is32: false, "Usuário",     entries);
            ReadRun(Registry.LocalMachine, RunLM,   machine: true,  is32: false, "Sistema",     entries);
            ReadRun(Registry.LocalMachine, RunLM32, machine: true,  is32: true,  "Sistema 32b", entries);
            ReadRun(Registry.CurrentUser,  RunCU32, machine: false, is32: true,  "Usuário 32b", entries);

            // Pastas de inicialização (usuário e todos os usuários).
            ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                       machine: false, "Pasta", entries);
            ReadFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
                       machine: true, "Pasta (Todos)", entries);

            return entries;
        }

        private static void ReadRun(RegistryKey root, string keyPath, bool machine, bool is32,
                                    string sourceLabel, List<StartupEntry> entries)
        {
            try
            {
                using var key = root.OpenSubKey(keyPath);
                if (key == null) return;

                foreach (var name in key.GetValueNames())
                {
                    if (string.IsNullOrEmpty(name)) continue;

                    bool legacy = name.EndsWith(DisabledSuffix, StringComparison.Ordinal);
                    string display = legacy
                        ? name.Substring(0, name.Length - DisabledSuffix.Length)
                        : name;

                    string approvedSub = is32 ? "Run32" : "Run";
                    bool enabled = legacy
                        ? false
                        : ReadApprovedEnabled(machine, approvedSub, name);

                    entries.Add(new StartupEntry
                    {
                        Name           = display,
                        Command        = key.GetValue(name)?.ToString() ?? "",
                        Source         = sourceLabel,
                        IsEnabled      = enabled,
                        Kind           = StartupKind.RegistryRun,
                        MachineHive    = machine,
                        Is32Bit        = is32,
                        ValueName      = name, // nome real no registro (com sufixo, se legado)
                        LegacyDisabled = legacy
                    });
                }
            }
            catch (Exception ex) { Logger.Error(ex, "ReadRun " + keyPath); }
        }

        private static void ReadFolder(string folder, bool machine, string sourceLabel,
                                       List<StartupEntry> entries)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

            try
            {
                foreach (var file in Directory.GetFiles(folder))
                {
                    string fileName = Path.GetFileName(file);
                    if (string.Equals(fileName, "desktop.ini", StringComparison.OrdinalIgnoreCase))
                        continue;

                    bool legacy = fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                    string realName = legacy
                        ? fileName.Substring(0, fileName.Length - ".disabled".Length)
                        : fileName;

                    bool enabled = legacy
                        ? false
                        : ReadApprovedEnabled(machine, "StartupFolder", realName);

                    entries.Add(new StartupEntry
                    {
                        Name           = Path.GetFileNameWithoutExtension(realName),
                        Command        = file,
                        Source         = sourceLabel,
                        IsEnabled      = enabled,
                        Kind           = StartupKind.StartupFolder,
                        MachineHive    = machine,
                        ValueName      = realName, // nome do arquivo p/ StartupApproved
                        FilePath       = file,
                        LegacyDisabled = legacy
                    });
                }
            }
            catch (Exception ex) { Logger.Error(ex, "ReadFolder " + folder); }
        }

        public static void SetEnabled(StartupEntry entry, bool enabled)
        {
            try
            {
                if (entry.LegacyDisabled)
                {
                    // Entrada desativada pelo método antigo: ao reativar, restaura o
                    // nome/arquivo original e deixa o StartupApproved coerente.
                    RestoreLegacy(entry, enabled);
                    return;
                }

                if (entry.Kind == StartupKind.RegistryRun)
                    SetApproved(entry.MachineHive, entry.Is32Bit ? "Run32" : "Run", entry.ValueName, enabled);
                else
                    SetApproved(entry.MachineHive, "StartupFolder", entry.ValueName, enabled);
            }
            catch (Exception ex) { Logger.Error(ex, "SetEnabled " + entry.Name); }
        }

        private static void RestoreLegacy(StartupEntry entry, bool enabled)
        {
            if (entry.Kind == StartupKind.RegistryRun)
            {
                var root = entry.MachineHive ? Registry.LocalMachine : Registry.CurrentUser;
                string keyPath = entry.MachineHive
                    ? (entry.Is32Bit ? RunLM32 : RunLM)
                    : (entry.Is32Bit ? RunCU32 : RunCU);
                using var key = root.OpenSubKey(keyPath, writable: true);
                if (key == null) return;

                if (enabled)
                {
                    var value = key.GetValue(entry.ValueName); // valor com sufixo
                    if (value != null)
                    {
                        key.SetValue(entry.Name, value);
                        key.DeleteValue(entry.ValueName, throwOnMissingValue: false);
                    }
                    SetApproved(entry.MachineHive, entry.Is32Bit ? "Run32" : "Run", entry.Name, true);
                }
            }
            else // pasta de inicialização legada (.disabled)
            {
                if (enabled && entry.FilePath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                {
                    string enabledPath = entry.FilePath.Substring(0, entry.FilePath.Length - ".disabled".Length);
                    File.Move(entry.FilePath, enabledPath);
                    SetApproved(entry.MachineHive, "StartupFolder", Path.GetFileName(enabledPath), true);
                }
            }
        }

        /// <summary>
        /// Lê o estado em StartupApproved. Ausente = ativado (padrão do Windows).
        /// O byte 0 do valor binário: bit 0 ligado (ímpar) = desativado.
        /// </summary>
        private static bool ReadApprovedEnabled(bool machineHive, string approvedSub, string valueName)
        {
            try
            {
                var root = machineHive ? Registry.LocalMachine : Registry.CurrentUser;
                using var key = root.OpenSubKey($@"{ApprovedBase}\{approvedSub}");
                if (key?.GetValue(valueName) is byte[] data && data.Length > 0)
                    return (data[0] & 1) == 0;
            }
            catch (Exception ex) { Logger.Error(ex, "ReadApprovedEnabled " + valueName); }
            return true;
        }

        /// <summary>
        /// Escreve o estado em StartupApproved no mesmo formato do Gerenciador de Tarefas:
        /// 12 bytes, byte 0 = 0x02 (ativado) ou 0x03 (desativado) + FILETIME do desligamento.
        /// </summary>
        private static void SetApproved(bool machineHive, string approvedSub, string valueName, bool enabled)
        {
            try
            {
                var root = machineHive ? Registry.LocalMachine : Registry.CurrentUser;
                using var key = root.CreateSubKey($@"{ApprovedBase}\{approvedSub}");
                if (key == null) return;

                var data = new byte[12];
                data[0] = (byte)(enabled ? 0x02 : 0x03);
                if (!enabled)
                    BitConverter.GetBytes(DateTime.Now.ToFileTime()).CopyTo(data, 4);

                key.SetValue(valueName, data, RegistryValueKind.Binary);
            }
            catch (Exception ex) { Logger.Error(ex, "SetApproved " + valueName); }
        }
    }
}
