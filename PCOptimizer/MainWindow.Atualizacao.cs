using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using PCOptimizer.Services;
using PCOptimizer.Views;

namespace PCOptimizer
{
    // Parte da janela principal: atualização do app e selo de versão.
    public partial class MainWindow
    {
        private async Task CheckForUpdatesAsync()
        {
            // --updated is passed by the updater on relaunch to prevent infinite loop
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--updated") >= 0)
            {
                UpdaterService.CleanupBackup();
                return;
            }

            try
            {
            var info = await UpdateService.CheckForUpdateAsync();
            if (info == null || !info.UpdateAvailable) return;

            // Se uma otimização começou enquanto a rede respondia, não interrompe:
            // o diálogo sequestraria a barra de progresso e o update fecharia o app
            // no meio de um SFC/defrag. A oferta volta na próxima abertura.
            if (_isRunning) return;

            var result = MessageBox.Show(
                $"Uma nova versão do PC Optimizer está disponível! 🎉\n\n" +
                $"Versão instalada: {info.CurrentVersion}\n" +
                $"Nova versão: {info.LatestVersion}\n\n" +
                $"Deseja abrir a página de download agora?",
                "Atualização disponível",
                MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (result == MessageBoxResult.Yes)
            {
                if (!string.IsNullOrEmpty(info.DownloadUrl))
                {
                    await DownloadAndApplyUpdateAsync(info.DownloadUrl);
                }
                else
                {
                    // Sem .exe na release — abre a página para download manual
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(info.ReleaseUrl) { UseShellExecute = true });
                    }
                    catch { /* navegador indisponível — ignora */ }
                }
            }
            }
            catch (Exception ex) { Logger.Error(ex, "CheckForUpdatesAsync"); }
        }

        private async Task DownloadAndApplyUpdateAsync(string downloadUrl)
        {
            string? currentExe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentExe))
            {
                // Sem caminho do executável — abre a página para download manual
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(downloadUrl) { UseShellExecute = true }); }
                catch { }
                return;
            }

            // Baixa para %TEMP% — sempre gravável, independente de onde o exe está instalado.
            string newPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "PCOptimizer_update.exe");

            BtnRun.IsEnabled = false;
            Progress.Visibility = Visibility.Visible;
            Progress.IsIndeterminate = true; // indeterminado até o primeiro dado chegar
            Progress.Value = 0;
            TxtProgress.Visibility = Visibility.Visible;
            TxtProgress.Text = "Conectando...";

            var sw = Stopwatch.StartNew();
            var progress = new Progress<(long bytesRead, long totalBytes)>(t =>
            {
                var (bytes, total) = t;
                double elapsed = sw.Elapsed.TotalSeconds;
                double speedBps = elapsed > 0.1 ? bytes / elapsed : 0;
                string speedStr = speedBps >= 1_048_576
                    ? $"{speedBps / 1_048_576:F1} MB/s"
                    : $"{speedBps / 1024:F0} KB/s";

                if (total > 0)
                {
                    double pct = (double)bytes / total * 100;
                    Progress.IsIndeterminate = false;
                    Progress.Value = pct;
                    TxtProgress.Text = $"Baixando {speedStr} · {bytes / 1_048_576.0:F1}/{total / 1_048_576.0:F1} MB · {pct:F0}%";
                }
                else
                {
                    Progress.IsIndeterminate = true;
                    TxtProgress.Text = $"Baixando {speedStr} · {bytes / 1_048_576.0:F1} MB...";
                }
            });

            try
            {
                await UpdaterService.DownloadAsync(downloadUrl, newPath, progress);
                TxtProgress.Text = "Reiniciando para aplicar a atualização...";
                UpdaterService.ApplyAndRestart(newPath);
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "DownloadAndApplyUpdateAsync");
                try { if (System.IO.File.Exists(newPath)) System.IO.File.Delete(newPath); } catch { }

                Progress.Visibility = Visibility.Collapsed;
                TxtProgress.Visibility = Visibility.Collapsed;
                BtnRun.IsEnabled = true;

                MessageBox.Show(
                    $"Não foi possível baixar a atualização automaticamente:\n{ex.Message}\n\n" +
                    "Abrindo a página de download para baixar manualmente.",
                    "Atualização", MessageBoxButton.OK, MessageBoxImage.Warning);

                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(downloadUrl) { UseShellExecute = true }); }
                catch { }
            }
        }

        // Clicar no selo de versão copia o log de diagnóstico para a área de
        // transferência — atalho discreto para enviar o log ao suporte.
        private void VersionBadge_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                string log = Logger.GetRecentLog();
                Clipboard.SetText(log);
                MessageBox.Show(
                    "Log de diagnóstico copiado para a área de transferência.\n\n" +
                    "Cole (Ctrl+V) na conversa com o suporte para ajudar a identificar o problema.\n\n" +
                    "Arquivos completos em:\n" + Logger.LogDirectory,
                    "Log copiado", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "VersionBadge_Click");
                MessageBox.Show("Não foi possível copiar o log: " + ex.Message,
                    "Log", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
