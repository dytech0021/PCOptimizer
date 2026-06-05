using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
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
            if (HandleUpdateInstall(e.Args)) return;

            base.OnStartup(e);
            SettingsService.Load();
            ThemeManager.Initialize();
            TrayService.Initialize();

            TrayService.ShowBrightnessRequested += ToggleBrightnessWindow;
            TrayService.ExitRequested += Shutdown;
            HotkeyService.HotkeyPressed += ToggleBrightnessWindow;
        }

        private bool HandleUpdateInstall(string[] args)
        {
            string? processPath = Environment.ProcessPath;

            // Case 1: launched by the updater with --install-over (new update mechanism)
            int idx = Array.IndexOf(args, "--install-over");
            if (idx >= 0 && idx + 2 < args.Length && !string.IsNullOrEmpty(processPath))
            {
                string targetPath = args[idx + 1];
                int.TryParse(args[idx + 2], out int oldPid);
                InstallSelfOver(processPath, targetPath, oldPid, showUi: false);
                Shutdown();
                return true;
            }

            // Case 2: user double-clicked the .new file directly
            if (processPath?.EndsWith(".new", StringComparison.OrdinalIgnoreCase) == true)
            {
                InstallSelfOver(processPath, processPath[..^4], oldPid: -1, showUi: true);
                Shutdown();
                return true;
            }

            // Case 3: leftover .new file from a previous failed update attempt — apply it now
            if (!args.Contains("--updated") && !string.IsNullOrEmpty(processPath))
            {
                string pending = processPath + ".new";
                if (File.Exists(pending))
                {
                    var psi = new ProcessStartInfo { FileName = pending, UseShellExecute = false };
                    psi.ArgumentList.Add("--install-over");
                    psi.ArgumentList.Add(processPath);
                    psi.ArgumentList.Add(Environment.ProcessId.ToString());
                    Process.Start(psi);
                    Shutdown();
                    return true;
                }
            }

            return false;
        }

        private static void InstallSelfOver(string sourcePath, string targetPath, int oldPid, bool showUi)
        {
            if (oldPid > 0)
            {
                try { using var p = Process.GetProcessById(oldPid); p.WaitForExit(30_000); }
                catch { }
                Thread.Sleep(1000);
            }
            else
            {
                // Short wait so the caller process has time to exit
                Thread.Sleep(2000);
            }

            for (int attempt = 0; attempt < 15; attempt++)
            {
                try
                {
                    File.Move(sourcePath, targetPath, overwrite: true);
                    var psi = new ProcessStartInfo { FileName = targetPath, UseShellExecute = false };
                    psi.ArgumentList.Add("--updated");
                    Process.Start(psi);
                    return;
                }
                catch
                {
                    if (showUi && attempt == 0)
                        MessageBox.Show(
                            "Feche o PC Optimizer antes de continuar a atualização.",
                            "Atualizando PC Optimizer",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    Thread.Sleep(1000);
                }
            }

            // Fallback: launch target as-is
            try { Process.Start(new ProcessStartInfo { FileName = targetPath, UseShellExecute = false }); }
            catch { }
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
