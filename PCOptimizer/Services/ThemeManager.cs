using System;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace PCOptimizer.Services
{
    public enum AppTheme { Light, Dark }

    public static class ThemeManager
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCOptimizer", "settings.json");

        public static AppTheme Current { get; private set; } = AppTheme.Light;

        public static event EventHandler ThemeChanged;

        public static void Initialize()
        {
            Current = Load();
            ApplyTheme(Current);
        }

        public static void Toggle()
        {
            Current = Current == AppTheme.Light ? AppTheme.Dark : AppTheme.Light;
            ApplyTheme(Current);
            Save(Current);
            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }

        private static void ApplyTheme(AppTheme theme)
        {
            var dict = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/Themes/{theme}Theme.xaml", UriKind.Absolute)
            };

            var merged = Application.Current.Resources.MergedDictionaries;
            merged.Clear();
            merged.Add(dict);
        }

        private static AppTheme Load()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return AppTheme.Light;
                var json = File.ReadAllText(SettingsPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("theme", out var t))
                {
                    return t.GetString() == "Dark" ? AppTheme.Dark : AppTheme.Light;
                }
            }
            catch { }
            return AppTheme.Light;
        }

        private static void Save(AppTheme theme)
        {
            try
            {
                var dir = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(new { theme = theme.ToString() });
                File.WriteAllText(SettingsPath, json);
            }
            catch { }
        }
    }
}
