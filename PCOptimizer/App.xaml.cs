using System.Windows;
using PCOptimizer.Services;
using PCOptimizer.Views;

namespace PCOptimizer
{
    public partial class App : Application
    {
        private BrightnessWindow? _brightnessWindow;
        private bool _firstHide = true;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            SettingsService.Load();
            ThemeManager.Initialize();
            TrayService.Initialize();

            TrayService.ShowBrightnessRequested += ToggleBrightnessWindow;
            TrayService.ExitRequested += Shutdown;
            HotkeyService.HotkeyPressed += ToggleBrightnessWindow;
        }

        internal void InitHotkey(Window window)
        {
            HotkeyService.Initialize(window);
        }

        internal void ToggleBrightnessWindow()
        {
            if (_brightnessWindow == null || !_brightnessWindow.IsLoaded)
            {
                _brightnessWindow = new BrightnessWindow();
                _brightnessWindow.Owner = MainWindow;
            }

            if (_brightnessWindow.IsVisible)
            {
                _brightnessWindow.Hide();
                if (_firstHide)
                {
                    _firstHide = false;
                    TrayService.ShowBalloonTip("PC Optimizer",
                        $"Brilho e Contraste minimizado para a bandeja.\nUse {SettingsService.Current.HotkeyDisplay} para abrir.");
                }
            }
            else
            {
                _brightnessWindow.Show();
                _brightnessWindow.Activate();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            HotkeyService.Dispose();
            TrayService.Dispose();
            base.OnExit(e);
        }
    }
}
