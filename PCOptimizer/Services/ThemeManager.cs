using System;
using System.Windows;

namespace PCOptimizer.Services
{
    public enum AppTheme { Light, Dark }

    public static class ThemeManager
    {
        public static AppTheme Current { get; private set; } = AppTheme.Light;

        public static event EventHandler? ThemeChanged;

        public static void Initialize()
        {
            Current = SettingsService.Current.Theme == "Dark" ? AppTheme.Dark : AppTheme.Light;
            ApplyTheme(Current);
        }

        public static void Toggle()
        {
            Current = Current == AppTheme.Light ? AppTheme.Dark : AppTheme.Light;
            ApplyTheme(Current);
            SettingsService.Current.Theme = Current.ToString();
            SettingsService.Save();
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
    }
}
