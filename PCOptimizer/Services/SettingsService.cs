using System;
using System.IO;
using System.Text.Json;

namespace PCOptimizer.Services
{
    public class AppSettings
    {
        public string Theme { get; set; } = "Light";
        public uint HotkeyModifiers { get; set; } = 0x0006; // MOD_CONTROL | MOD_SHIFT
        public uint HotkeyVk { get; set; } = 0x42;          // 'B'
        public string HotkeyDisplay { get; set; } = "Ctrl+Shift+B";
    }

    public static class SettingsService
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCOptimizer", "settings.json");

        public static AppSettings Current { get; private set; } = new();

        public static void Load()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                var json = File.ReadAllText(SettingsPath);
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                Current = JsonSerializer.Deserialize<AppSettings>(json, opts) ?? new();
            }
            catch { Current = new(); }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}
