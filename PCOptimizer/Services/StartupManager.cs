using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace PCOptimizer.Services
{
    public class StartupEntry
    {
        public string Name { get; set; } = "";
        public string Command { get; set; } = "";
        public string Source { get; set; } = "";
        public bool IsEnabled { get; set; } = true;
    }

    public static class StartupManager
    {
        private const string RunKeyCurrentUser = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunKeyLocalMachine = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string DisabledSuffix = "_PCOptimizer_Disabled";

        public static List<StartupEntry> GetStartupEntries()
        {
            var entries = new List<StartupEntry>();

            ReadRegistryEntries(Registry.CurrentUser, RunKeyCurrentUser, "HKCU", entries);
            ReadRegistryEntries(Registry.LocalMachine, RunKeyLocalMachine, "HKLM", entries);
            ReadStartupFolder(entries);

            return entries;
        }

        private static void ReadRegistryEntries(RegistryKey root, string keyPath, string source, List<StartupEntry> entries)
        {
            try
            {
                using var key = root.OpenSubKey(keyPath);
                if (key == null) return;

                foreach (var name in key.GetValueNames())
                {
                    if (string.IsNullOrEmpty(name)) continue;

                    bool isDisabled = name.EndsWith(DisabledSuffix);
                    string displayName = isDisabled
                        ? name.Substring(0, name.Length - DisabledSuffix.Length)
                        : name;

                    entries.Add(new StartupEntry
                    {
                        Name = displayName,
                        Command = key.GetValue(name)?.ToString() ?? "",
                        Source = source,
                        IsEnabled = !isDisabled
                    });
                }
            }
            catch { }
        }

        private static void ReadStartupFolder(List<StartupEntry> entries)
        {
            var startupPath = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            if (!Directory.Exists(startupPath)) return;

            try
            {
                foreach (var file in Directory.GetFiles(startupPath))
                {
                    var fileName = Path.GetFileName(file);
                    bool isDisabled = fileName.EndsWith(".disabled");

                    entries.Add(new StartupEntry
                    {
                        Name = isDisabled
                            ? Path.GetFileNameWithoutExtension(fileName.Substring(0, fileName.Length - ".disabled".Length))
                            : Path.GetFileNameWithoutExtension(file),
                        Command = file,
                        Source = "Startup Folder",
                        IsEnabled = !isDisabled
                    });
                }
            }
            catch { }
        }

        public static void SetEnabled(StartupEntry entry, bool enabled)
        {
            if (entry.Source == "Startup Folder")
            {
                ToggleStartupFolderEntry(entry, enabled);
            }
            else
            {
                ToggleRegistryEntry(entry, enabled);
            }
        }

        private static void ToggleRegistryEntry(StartupEntry entry, bool enabled)
        {
            var root = entry.Source == "HKCU" ? Registry.CurrentUser : Registry.LocalMachine;
            var keyPath = entry.Source == "HKCU" ? RunKeyCurrentUser : RunKeyLocalMachine;

            try
            {
                using var key = root.OpenSubKey(keyPath, writable: true);
                if (key == null) return;

                if (enabled)
                {
                    var disabledName = entry.Name + DisabledSuffix;
                    var value = key.GetValue(disabledName);
                    if (value != null)
                    {
                        key.SetValue(entry.Name, value);
                        key.DeleteValue(disabledName, throwOnMissingValue: false);
                    }
                }
                else
                {
                    var value = key.GetValue(entry.Name);
                    if (value != null)
                    {
                        key.SetValue(entry.Name + DisabledSuffix, value);
                        key.DeleteValue(entry.Name, throwOnMissingValue: false);
                    }
                }
            }
            catch { }
        }

        private static void ToggleStartupFolderEntry(StartupEntry entry, bool enabled)
        {
            try
            {
                if (enabled)
                {
                    var disabledPath = entry.Command;
                    if (disabledPath.EndsWith(".disabled"))
                    {
                        var enabledPath = disabledPath.Substring(0, disabledPath.Length - ".disabled".Length);
                        File.Move(disabledPath, enabledPath);
                    }
                }
                else
                {
                    if (!entry.Command.EndsWith(".disabled"))
                    {
                        File.Move(entry.Command, entry.Command + ".disabled");
                    }
                }
            }
            catch { }
        }
    }
}
