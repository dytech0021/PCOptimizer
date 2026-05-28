using System;
using System.IO;
using System.Text.Json;

namespace PCOptimizer.Services
{
    public class PresetData
    {
        public string Name { get; set; } = "";
        public string Icon { get; set; } = "";
        public int Brightness { get; set; }
        public int Contrast { get; set; }
    }

    public class AppSettings
    {
        public string Theme { get; set; } = "Light";
        public uint HotkeyModifiers { get; set; } = 0x0006; // MOD_CONTROL | MOD_SHIFT
        public uint HotkeyVk { get; set; } = 0x42;          // 'B'
        public string HotkeyDisplay { get; set; } = "Ctrl+Shift+B";

        public PresetData Preset1 { get; set; } = new() { Name = "Noturno", Icon = "🌙", Brightness = 20, Contrast = 40 };
        public PresetData Preset2 { get; set; } = new() { Name = "Normal", Icon = "☀️", Brightness = 50, Contrast = 50 };
        public PresetData Preset3 { get; set; } = new() { Name = "Máximo", Icon = "🔆", Brightness = 100, Contrast = 80 };

        public bool NightLightEnabled { get; set; }
        public int NightLightIntensity { get; set; } = 40;
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
