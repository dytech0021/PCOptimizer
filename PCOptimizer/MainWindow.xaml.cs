using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using PCOptimizer.Services;
using PCOptimizer.Views;

namespace PCOptimizer
{
    public partial class MainWindow : Window
    {
        private bool _isRunning;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                ((App)Application.Current).InitHotkey(this);
                ChkSelectAll.IsChecked = true;
                UpdateSelectedCount();
                _ = CheckForUpdatesAsync();
            };
        }

        private async Task CheckForUpdatesAsync()
        {
            try
            {
            var info = await UpdateService.CheckForUpdateAsync();
            if (info == null || !info.UpdateAvailable) return;

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
            catch { /* verificação de atualização nunca deve travar o app */ }
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

            string newPath = currentExe + ".new";

            BtnRun.IsEnabled = false;
            Progress.Visibility = Visibility.Visible;
            Progress.IsIndeterminate = false;
            Progress.Value = 0;
            TxtProgress.Visibility = Visibility.Visible;
            TxtProgress.Text = "Baixando atualização... 0%";

            var progress = new Progress<double>(p =>
            {
                Progress.Value = p * 100;
                TxtProgress.Text = $"Baixando atualização... {p * 100:F0}%";
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

        private void Log(string message)
        {
            TxtLog.Text += $"\n[{DateTime.Now:HH:mm:ss}] {message}";
            LogScroller.ScrollToEnd();
        }

        private string FormatBytes(long bytes)
        {
            if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F2} GB";
            if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F2} MB";
            if (bytes >= 1024) return $"{bytes / 1024.0:F2} KB";
            return $"{bytes} bytes";
        }

        private void UpdateProgress(int completed, int total, DateTime startTime)
        {
            if (total == 0) return;
            double pct = completed * 100.0 / total;
            Progress.Value = pct;

            string timeText = "";
            if (completed > 0 && completed < total)
            {
                double elapsed = (DateTime.Now - startTime).TotalSeconds;
                if (completed >= 2 && elapsed >= 1.5)
                {
                    double remaining = elapsed / completed * (total - completed);
                    if (remaining >= 60)
                        timeText = $" • ~{(int)Math.Ceiling(remaining / 60)}min restantes";
                    else if (remaining >= 10)
                        timeText = $" • ~{(int)(Math.Ceiling(remaining / 5) * 5)}s restantes";
                    else
                        timeText = " • quase pronto";
                }
                else
                {
                    timeText = " • calculando...";
                }
            }

            TxtProgress.Text = $"{completed} / {total} otimizações — {pct:F0}%{timeText}";
        }

        private void SetStatus(System.Windows.Controls.TextBlock status, string text, bool success)
        {
            status.Text = text;
            status.Foreground = new System.Windows.Media.SolidColorBrush(
                success ? System.Windows.Media.Color.FromRgb(166, 227, 161)
                        : System.Windows.Media.Color.FromRgb(243, 139, 168));
        }

        private void UpdateSelectedCount()
        {
            if (TxtSelected == null || ChkTemp == null || ChkBloatware == null)
                return;

            var boxes = new[]
            {
                ChkTemp, ChkDisk, ChkRecycleBin, ChkStartup, ChkServices,
                ChkNetwork, ChkRegistry, ChkCortana, ChkDefrag,
                ChkPowerPlan, ChkVisualEffects, ChkBackgroundApps, ChkStandbyRam,
                ChkGpuScheduling, ChkFastStartup, ChkTelemetry, ChkGameBar,
                ChkSsdTrim, ChkWinUpdateCache, ChkThumbnails,
                ChkHibernation, ChkSystemRepair, ChkBloatware
            };

            int total = boxes.Length;
            int selected = 0;
            foreach (var b in boxes)
                if (b != null && b.IsChecked == true) selected++;

            TxtSelected.Text = $"{selected} de {total} selecionados";
        }

        private void OptCheckChanged(object sender, RoutedEventArgs e)
        {
            if (TxtSelected != null) UpdateSelectedCount();
        }

        private void ChkSelectAll_Checked(object sender, RoutedEventArgs e) => SetAllChecks(true);
        private void ChkSelectAll_Unchecked(object sender, RoutedEventArgs e) => SetAllChecks(false);

        private void SetAllChecks(bool value)
        {
            if (ChkTemp == null) return;
            ChkTemp.IsChecked = value;
            ChkDisk.IsChecked = value;
            ChkRecycleBin.IsChecked = value;
            ChkStartup.IsChecked = value;
            ChkServices.IsChecked = value;
            ChkNetwork.IsChecked = value;
            ChkRegistry.IsChecked = value;
            ChkCortana.IsChecked = value;
            ChkDefrag.IsChecked = value;
            // Otimizações seguras de desempenho/privacidade/disco
            ChkPowerPlan.IsChecked = value;
            ChkVisualEffects.IsChecked = value;
            ChkBackgroundApps.IsChecked = value;
            ChkStandbyRam.IsChecked = value;
            ChkGpuScheduling.IsChecked = value;
            ChkTelemetry.IsChecked = value;
            ChkGameBar.IsChecked = value;
            ChkSsdTrim.IsChecked = value;
            ChkWinUpdateCache.IsChecked = value;
            ChkThumbnails.IsChecked = value;
            // Nota: Hibernação, Inicialização Rápida, Reparo e Bloatware ficam de fora
            // do "selecionar tudo" por serem opcionais/mais demorados — opt-in manual.
            UpdateSelectedCount();
        }

        private async void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning) return;
            _isRunning = true;
            BtnRun.IsEnabled = false;
            BtnRun.Content = "⏳ Executando...";

            var allChecks = new[] { ChkTemp, ChkDisk, ChkRecycleBin, ChkStartup, ChkServices,
                ChkNetwork, ChkRegistry, ChkCortana, ChkDefrag, ChkPowerPlan, ChkVisualEffects,
                ChkBackgroundApps, ChkStandbyRam, ChkGpuScheduling, ChkTelemetry, ChkGameBar,
                ChkSsdTrim, ChkWinUpdateCache, ChkThumbnails, ChkFastStartup, ChkHibernation,
                ChkSystemRepair, ChkBloatware };
            int totalSelected = 0;
            foreach (var c in allChecks) if (c.IsChecked == true) totalSelected++;

            int completedSteps = 0;
            var startTime = DateTime.Now;

            Progress.Visibility = Visibility.Visible;
            Progress.IsIndeterminate = false;
            Progress.Value = 0;
            TxtProgress.Visibility = Visibility.Visible;
            TxtProgress.Text = $"0 / {totalSelected} — 0%";
            TxtLog.Text = "Iniciando otimizações selecionadas...";

            long totalFreed = 0;
            int totalSteps = 0;

            try
            {
            if (ChkTemp.IsChecked == true)
            {
                Log("Limpando arquivos temporários...");
                StatusTemp.Text = "⏳";
                var (files, bytes) = await Task.Run(() => TempCleaner.Clean());
                totalFreed += bytes; totalSteps++;
                SetStatus(StatusTemp, "✅", true);
                Log($"✅ Temporários: {files} arquivos ({FormatBytes(bytes)})");
                completedSteps++; UpdateProgress(completedSteps, totalSelected, startTime);
            }

            if (ChkDisk.IsChecked == true)
            {
                Log("Limpando disco...");
                StatusDisk.Text = "⏳";
                var (files, bytes) = await Task.Run(() => DiskCleaner.Clean());
                totalFreed += bytes; totalSteps++;
                SetStatus(StatusDisk, "✅", true);
                Log($"✅ Disco: {files} arquivos ({FormatBytes(bytes)})");
                completedSteps++; UpdateProgress(completedSteps, totalSelected, startTime);
            }

            if (ChkRecycleBin.IsChecked == true)
            {
                Log("Esvaziando lixeira...");
                StatusRecycleBin.Text = "⏳";
                bool ok = await Task.Run(() => RecycleBinCleaner.Clean());
                totalSteps++;
                SetStatus(StatusRecycleBin, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Lixeira esvaziada" : "⚠️ Lixeira já estava vazia");
                completedSteps++; UpdateProgress(completedSteps, totalSelected, startTime);
            }

            if (ChkStartup.IsChecked == true)
            {
                Log("Abrindo gerenciador de inicialização...");
                StatusStartup.Text = "⏳";
                var startupWindow = new StartupWindow { Owner = this };
                if (startupWindow.ShowDialog() == true)
                {
                    totalSteps++;
                    SetStatus(StatusStartup, "✅", true);
                    Log($"✅ Inicialização: {startupWindow.ChangesApplied} alterações");
                }
                else
                {
                    SetStatus(StatusStartup, "⏭️", true);
                    Log("⏭️ Inicialização: ignorado");
                }
                completedSteps++; UpdateProgress(completedSteps, totalSelected, startTime);
            }

            if (ChkServices.IsChecked == true)
            {
                Log("Otimizando serviços...");
                StatusServices.Text = "⏳";
                int count = await Task.Run(() => ServiceOptimizer.Optimize());
                totalSteps++;
                SetStatus(StatusServices, "✅", true);
                Log($"✅ Serviços: {count} otimizados");
                completedSteps++; UpdateProgress(completedSteps, totalSelected, startTime);
            }

            if (ChkNetwork.IsChecked == true)
            {
                Log("Otimizando rede...");
                StatusNetwork.Text = "⏳";
                int steps = await Task.Run(() => NetworkOptimizer.Optimize());
                totalSteps++;
                SetStatus(StatusNetwork, "✅", true);
                Log($"✅ Rede: {steps} otimizações");
                completedSteps++; UpdateProgress(completedSteps, totalSelected, startTime);
            }

            if (ChkRegistry.IsChecked == true)
            {
                Log("Aplicando tweaks no registro...");
                StatusRegistry.Text = "⏳";
                int tweaks = await Task.Run(() => RegistryOptimizer.Optimize());
                totalSteps++;
                SetStatus(StatusRegistry, "✅", true);
                Log($"✅ Registro: {tweaks} tweaks");
                completedSteps++; UpdateProgress(completedSteps, totalSelected, startTime);
            }

            if (ChkCortana.IsChecked == true)
            {
                Log("Desativando Cortana...");
                StatusCortana.Text = "⏳";
                bool ok = await Task.Run(() => CortanaDisabler.Disable());
                totalSteps++;
                SetStatus(StatusCortana, ok ? "✅" : "❌", ok);
                Log(ok ? "✅ Cortana desativada" : "❌ Erro ao desativar Cortana");
                completedSteps++; UpdateProgress(completedSteps, totalSelected, startTime);
            }

            if (ChkDefrag.IsChecked == true)
            {
                Log("Desfragmentando disco C: (pode demorar)...");
                StatusDefrag.Text = "⏳";
                bool ok = await Task.Run(() => DefragService.Optimize());
                totalSteps++;
                SetStatus(StatusDefrag, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Disco desfragmentado" : "⚠️ Desfragmentação parcial ou SSD");
                completedSteps++; UpdateProgress(completedSteps, totalSelected, startTime);
            }

            if (ChkPowerPlan.IsChecked == true)
            {
                Log("Ativando plano de Alto Desempenho...");
                StatusPowerPlan.Text = "⏳";
                bool ok = await Task.Run(() => PowerPlanService.Apply());
                totalSteps++;
                SetStatus(StatusPowerPlan, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Plano de energia: Alto Desempenho ativado" : "⚠️ Plano de energia: requer admin");
                completedSteps++; UpdateProgress(completedSteps, totalSelected, startTime);
            }

            if (ChkVisualEffects.IsChecked == true)
            {
                Log("Otimizando efeitos visuais...");
                StatusVisualEffects.Text = "⏳";
                bool ok = await Task.Run(() => VisualEffectsService.OptimizeForPerformance());
                totalSteps++;
                SetStatus(StatusVisualEffects, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Efeitos visuais otimizados" : "⚠️ Efeitos visuais: falhou");
                completedSteps++; UpdateProgress(completedSteps, totalSelected, startTime);
            }

            if (ChkBackgroundApps.IsChecked == true)
            {
                Log("Desativando apps em segundo plano...");
                StatusBackgroundApps.Text = "⏳";
                bool ok = await Task.Run(() => BackgroundAppsService.Disable());
                totalSteps++;
                SetStatus(StatusBackgroundApps, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Apps em segundo plano desativados" : "⚠️ Apps em segundo plano: falhou");
                completedSteps++; UpdateProgress(completedSteps, totalSelected, startTime);
            }

            if (ChkStandbyRam.IsChecked == true)
            {
                Log("Liberando memória RAM...");
                StatusStandbyRam.Text = "⏳";
                int count = await Task.Run(() => MemoryService.ClearStandby());
                totalSteps++;
                SetStatus(StatusStandbyRam, "✅", true);
                Log($"✅ Memória liberada em {count} processos");
                completedSteps++; UpdateProgress(completedSteps, totalSelected, startTime);
            }

            if (ChkGpuScheduling.IsChecked == true)
            {
                Log("Ativando agendamento de GPU por hardware...");
                StatusGpuScheduling.Text = "⏳";
                bool ok = await Task.Run(() => GpuSchedulingService.Enable());
                totalSteps++;
                SetStatus(StatusGpuScheduling, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Agendamento de GPU ativado (reinicie o PC)" : "⚠️ Agendamento de GPU: requer admin");
                completedSteps++; UpdateProgress(completedSteps, totalSelected, startTime);
            }

            if (ChkTelemetry.IsChecked == true)
            {
                Log("Desativando telemetria...");
                StatusTelemetry.Text = "⏳";
                bool ok = await Task.Run(() => TelemetryService.Disable());
                totalSteps++;
                SetStatus(StatusTelemetry, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Telemetria desativada" : "⚠️ Telemetria: requer admin");
                completedSteps++; UpdateProgress(completedSteps, totalSelected, startTime);
            }

            if (ChkGameBar.IsChecked == true)
            {
                Log("Desativando Xbox Game Bar...");
                StatusGameBar.Text = "⏳";
                bool ok = await Task.Run(() => GameBarService.Disable());
                totalSteps++;
                SetStatus(StatusGameBar, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Xbox Game Bar / DVR desativado" : "⚠️ Game Bar: falhou");
                completedSteps++; UpdateProgress(completedSteps, totalSelected, startTime);
            }

            if (ChkSsdTrim.IsChecked == true)
            {
                Log("Otimizando SSD (TRIM)...");
                StatusSsdTrim.Text = "⏳";
                bool ok = await Task.Run(() => SsdTrimService.Trim());
                totalSteps++;
                SetStatus(StatusSsdTrim, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ SSD otimizado (TRIM)" : "⚠️ TRIM: falhou ou não aplicável");
                completedSteps++; UpdateProgress(completedSteps, totalSelected, startTime);
            }

            if (ChkWinUpdateCache.IsChecked == true)
            {
                Log("Limpando cache do Windows Update...");
                StatusWinUpdateCache.Text = "⏳";
                long bytes = await Task.Run(() => WindowsUpdateCacheService.Clean());
                totalFreed += bytes; totalSteps++;
                SetStatus(StatusWinUpdateCache, "✅", true);
                Log($"✅ Cache do Windows Update: {FormatBytes(bytes)} liberados");
                completedSteps++; UpdateProgress(completedSteps, totalSelected, startTime);
            }

            if (ChkThumbnails.IsChecked == true)
            {
                Log("Limpando cache de miniaturas...");
                StatusThumbnails.Text = "⏳";
                long bytes = await Task.Run(() => ThumbnailCacheService.Clean());
                totalFreed += bytes; totalSteps++;
                SetStatus(StatusThumbnails, "✅", true);
                Log($"✅ Cache de miniaturas: {FormatBytes(bytes)} liberados");
                completedSteps++; UpdateProgress(completedSteps, totalSelected, startTime);
            }

            if (ChkFastStartup.IsChecked == true)
            {
                Log("Desativando Inicialização Rápida...");
                StatusFastStartup.Text = "⏳";
                bool ok = await Task.Run(() => FastStartupService.Disable());
                totalSteps++;
                SetStatus(StatusFastStartup, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Inicialização Rápida desativada" : "⚠️ Inicialização Rápida: requer admin");
                completedSteps++; UpdateProgress(completedSteps, totalSelected, startTime);
            }

            if (ChkHibernation.IsChecked == true)
            {
                Log("Desativando hibernação...");
                StatusHibernation.Text = "⏳";
                bool ok = await Task.Run(() => HibernationService.Disable());
                totalSteps++;
                SetStatus(StatusHibernation, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Hibernação desativada (hiberfil.sys removido)" : "⚠️ Hibernação: requer admin");
                completedSteps++; UpdateProgress(completedSteps, totalSelected, startTime);
            }

            if (ChkSystemRepair.IsChecked == true)
            {
                Log("Reparando arquivos do sistema (pode demorar vários minutos)...");
                StatusSystemRepair.Text = "⏳";
                bool ok = await Task.Run(() => SystemRepairService.Repair());
                totalSteps++;
                SetStatus(StatusSystemRepair, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Verificação de arquivos do sistema concluída" : "⚠️ Reparo: requer admin ou houve erro");
                completedSteps++; UpdateProgress(completedSteps, totalSelected, startTime);
            }

            if (ChkBloatware.IsChecked == true)
            {
                Log("Removendo bloatware...");
                StatusBloatware.Text = "⏳";
                int count = await Task.Run(() => BloatwareRemover.Remove());
                totalSteps++;
                SetStatus(StatusBloatware, "✅", true);
                Log($"✅ Bloatware: {count} apps processados");
                completedSteps++; UpdateProgress(completedSteps, totalSelected, startTime);
            }

                Log($"\n🎉 Concluído! {totalSteps} otimizações. Espaço liberado: {FormatBytes(totalFreed)}");

                MessageBox.Show(
                    $"Otimização concluída!\n\n" +
                    $"✅ {totalSteps} otimizações executadas\n" +
                    $"💾 Espaço liberado: {FormatBytes(totalFreed)}",
                    "PC Optimizer", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log($"\n❌ Erro inesperado durante a otimização: {ex.Message}");
                MessageBox.Show(
                    $"Ocorreu um erro durante a otimização:\n\n{ex.Message}\n\n" +
                    "As otimizações concluídas antes do erro foram aplicadas normalmente.",
                    "PC Optimizer", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                Progress.Value = 100;
                TxtProgress.Visibility = Visibility.Collapsed;
                BtnRun.Content = "⚡ Executar Otimizações";
                BtnRun.IsEnabled = true;
                _isRunning = false;
            }
        }

        private async void BtnScreenshot_Click(object sender, RoutedEventArgs e)
        {
            var overlay = new ScreenshotOverlayWindow();
            bool? result = overlay.ShowDialog();
            if (result != true || overlay.CaptureRegion is not { } region) return;

            // Small delay to let the overlay finish closing before capturing
            await Task.Delay(120);

            try
            {
                using var bmp = new System.Drawing.Bitmap(region.Width, region.Height);
                using var g = System.Drawing.Graphics.FromImage(bmp);
                g.CopyFromScreen(region.Location, System.Drawing.Point.Empty, region.Size);

                string folder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                string fileName = $"Captura_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png";
                string path = Path.Combine(folder, fileName);
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);

                Log($"📸 Captura salva: {path}");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log($"❌ Erro ao capturar tela: {ex.Message}");
            }
        }

        private void Card_Brightness(object sender, MouseButtonEventArgs e)
        {
            ((App)Application.Current).ToggleBrightnessWindow();
        }

        private void BtnBrightnessHeader_Click(object sender, RoutedEventArgs e)
        {
            ((App)Application.Current).ToggleBrightnessWindow();
        }

        private void BtnThemeToggle_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.Toggle();
            Log($"🎨 Tema alterado para {(ThemeManager.Current == AppTheme.Dark ? "escuro" : "claro")}");
        }
    }
}
