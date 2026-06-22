using System;
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
    public partial class MainWindow : Window
    {
        private bool _isRunning;

        // Progresso ponderado: cada otimização tem uma duração típica estimada,
        // então a previsão é do PROCESSO TOTAL (não passo a passo) e a barra
        // avança proporcional ao trabalho real, não à contagem de passos.
        private int _completedSteps, _totalSelectedSteps;
        private double _doneWeight, _totalWeight;
        private DateTime _runStart;

        // Modo Expert: aviso único por sessão + dados da GPU/ferramenta detectadas
        private bool _expertWarned;
        private GpuTuningService.GpuInfo? _gpuInfo;
        private CpuUndervoltToolService.ToolInfo? _cpuUvTool;
        private System.Collections.Generic.List<CpuTuningDetectionService.DetectedTool>? _detectedTools;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                ((App)Application.Current).InitHotkey(this);
                ApplyTitleBarColor();
                TxtVersion.Text = "v" + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?");
                ChkSelectAll.IsChecked = true;
                UpdateSelectedCount();
                _ = CheckForUpdatesAsync();
                _ = InitExpertAsync();
                RefreshAutologonStatus();
                _ = RefreshWoLStatusAsync();
            };
        }

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

        // Duração típica estimada de cada otimização, em segundos — base da
        // previsão de tempo total, refinada pelo tempo real durante a execução.
        private double GetWeight(System.Windows.Controls.CheckBox chk)
        {
            if (chk == ChkTemp) return 6;
            if (chk == ChkDisk) return 8;
            if (chk == ChkRecycleBin) return 3;
            if (chk == ChkStartup) return 20;       // interativo (janela do usuário)
            if (chk == ChkServices) return 8;
            if (chk == ChkNetwork) return 10;
            if (chk == ChkRegistry) return 2;
            if (chk == ChkCortana) return 2;
            if (chk == ChkDefrag) return 240;
            if (chk == ChkPowerPlan) return 5;
            if (chk == ChkVisualEffects) return 2;
            if (chk == ChkBackgroundApps) return 2;
            if (chk == ChkStandbyRam) return 6;
            if (chk == ChkGpuScheduling) return 1;
            if (chk == ChkTelemetry) return 4;
            if (chk == ChkGameBar) return 1;
            if (chk == ChkSsdTrim) return 20;
            if (chk == ChkWinUpdateCache) return 12;
            if (chk == ChkThumbnails) return 2;
            if (chk == ChkShaderCache) return 4;
            if (chk == ChkFastStartup) return 3;
            if (chk == ChkHibernation) return 4;
            if (chk == ChkSystemRepair) return 420;
            if (chk == ChkFixDate) return 6;
            if (chk == ChkBloatware) return 60;
            if (chk == ChkGameMode) return 1;
            if (chk == ChkGamePriority) return 1;
            if (chk == ChkGameNetwork) return 2;
            if (chk == ChkPowerThrottling) return 1;
            if (chk == ChkFullscreenOpt) return 1;
            if (chk == ChkMousePrecision) return 1;
            if (chk == ChkCoreIsolation) return 1;
            if (chk == ChkBootProcessors) return 2;
            if (chk == ChkExpertCpuMax) return 4;
            if (chk == ChkExpertTimer) return 1;
            if (chk == ChkExpertMsi) return 3;
            return 5;
        }

        private void StepDone(System.Windows.Controls.CheckBox chk)
        {
            _completedSteps++;
            _doneWeight += GetWeight(chk);
            UpdateProgressDisplay();
        }

        private void UpdateProgressDisplay()
        {
            if (_totalWeight <= 0) return;
            double pct = Math.Min(100, _doneWeight * 100.0 / _totalWeight);
            Progress.Value = pct;

            double elapsed = (DateTime.Now - _runStart).TotalSeconds;
            string timeText = "";
            if (_doneWeight < _totalWeight)
            {
                // Fator medido (segundos reais por unidade de peso), suavizado
                // com o nominal enquanto pouca coisa rodou — evita previsões
                // erráticas se o primeiro passo for atipicamente rápido/lento.
                double measured = _doneWeight > 0 ? elapsed / _doneWeight : 1.0;
                double blend = Math.Min(1.0, _doneWeight / Math.Max(1.0, _totalWeight * 0.2));
                double factor = measured * blend + (1.0 - blend);
                double remaining = factor * (_totalWeight - _doneWeight);
                timeText = $" • restam ~{FormatDuration(remaining)} de ~{FormatDuration(elapsed + remaining)}";
            }

            TxtProgress.Text = $"{_completedSteps} / {_totalSelectedSteps} otimizações — {pct:F0}%{timeText}";
        }

        private static string FormatDuration(double seconds)
        {
            if (seconds < 5) return "5s";
            if (seconds < 60) return $"{(int)Math.Ceiling(seconds / 5) * 5}s";
            int m = (int)(seconds / 60);
            int s = (int)Math.Round(seconds % 60 / 10) * 10;
            if (s == 60) { m++; s = 0; }
            return s > 0 ? $"{m}min {s}s" : $"{m}min";
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
                ChkSsdTrim, ChkWinUpdateCache, ChkThumbnails, ChkShaderCache,
                ChkHibernation, ChkSystemRepair, ChkFixDate, ChkBloatware,
                ChkGameMode, ChkGamePriority, ChkGameNetwork, ChkPowerThrottling,
                ChkFullscreenOpt, ChkMousePrecision, ChkCoreIsolation, ChkBootProcessors,
                ChkExpertCpuMax, ChkExpertTimer, ChkExpertMsi
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
            ChkShaderCache.IsChecked = value;
            // Games (seguras)
            ChkGameMode.IsChecked = value;
            ChkGamePriority.IsChecked = value;
            ChkGameNetwork.IsChecked = value;
            ChkPowerThrottling.IsChecked = value;
            ChkBootProcessors.IsChecked = value;
            // Correção de data/hora: segura e rápida, entra no "selecionar tudo".
            ChkFixDate.IsChecked = value;
            // Nota: Hibernação, Inicialização Rápida, Reparo, Bloatware e as opções
            // de games com trade-off (Tela Cheia, Ponteiro, Isolamento de Núcleo)
            // ficam de fora do "selecionar tudo" — opt-in manual.
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
                ChkSsdTrim, ChkWinUpdateCache, ChkThumbnails, ChkShaderCache, ChkFastStartup,
                ChkHibernation, ChkSystemRepair, ChkFixDate, ChkBloatware, ChkGameMode, ChkGamePriority,
                ChkGameNetwork, ChkPowerThrottling, ChkFullscreenOpt, ChkMousePrecision,
                ChkCoreIsolation, ChkBootProcessors, ChkExpertCpuMax, ChkExpertTimer,
                ChkExpertMsi };
            _totalSelectedSteps = 0;
            _totalWeight = 0;
            foreach (var c in allChecks)
                if (c.IsChecked == true) { _totalSelectedSteps++; _totalWeight += GetWeight(c); }
            _completedSteps = 0;
            _doneWeight = 0;
            _runStart = DateTime.Now;

            Progress.Visibility = Visibility.Visible;
            Progress.IsIndeterminate = false;
            Progress.Value = 0;
            TxtProgress.Visibility = Visibility.Visible;
            TxtProgress.Text = $"0 / {_totalSelectedSteps} otimizações — previsão total: ~{FormatDuration(_totalWeight)}";
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
                StepDone(ChkTemp);
            }

            if (ChkDisk.IsChecked == true)
            {
                Log("Limpando disco...");
                StatusDisk.Text = "⏳";
                var (files, bytes) = await Task.Run(() => DiskCleaner.Clean());
                totalFreed += bytes; totalSteps++;
                SetStatus(StatusDisk, "✅", true);
                Log($"✅ Disco: {files} arquivos ({FormatBytes(bytes)})");
                StepDone(ChkDisk);
            }

            if (ChkRecycleBin.IsChecked == true)
            {
                Log("Esvaziando lixeira...");
                StatusRecycleBin.Text = "⏳";
                bool ok = await Task.Run(() => RecycleBinCleaner.Clean());
                totalSteps++;
                SetStatus(StatusRecycleBin, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Lixeira esvaziada" : "⚠️ Lixeira já estava vazia");
                StepDone(ChkRecycleBin);
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
                StepDone(ChkStartup);
            }

            if (ChkServices.IsChecked == true)
            {
                Log("Otimizando serviços...");
                StatusServices.Text = "⏳";
                int count = await Task.Run(() => ServiceOptimizer.Optimize());
                totalSteps++;
                SetStatus(StatusServices, "✅", true);
                Log($"✅ Serviços: {count} otimizados");
                StepDone(ChkServices);
            }

            if (ChkNetwork.IsChecked == true)
            {
                Log("Otimizando rede...");
                StatusNetwork.Text = "⏳";
                int steps = await Task.Run(() => NetworkOptimizer.Optimize());
                totalSteps++;
                SetStatus(StatusNetwork, "✅", true);
                Log($"✅ Rede: {steps} otimizações");
                StepDone(ChkNetwork);
            }

            if (ChkRegistry.IsChecked == true)
            {
                Log("Aplicando tweaks no registro...");
                StatusRegistry.Text = "⏳";
                int tweaks = await Task.Run(() => RegistryOptimizer.Optimize());
                totalSteps++;
                SetStatus(StatusRegistry, "✅", true);
                Log($"✅ Registro: {tweaks} tweaks");
                StepDone(ChkRegistry);
            }

            if (ChkCortana.IsChecked == true)
            {
                Log("Desativando Cortana...");
                StatusCortana.Text = "⏳";
                bool ok = await Task.Run(() => CortanaDisabler.Disable());
                totalSteps++;
                SetStatus(StatusCortana, ok ? "✅" : "❌", ok);
                Log(ok ? "✅ Cortana desativada" : "❌ Erro ao desativar Cortana");
                StepDone(ChkCortana);
            }

            if (ChkDefrag.IsChecked == true)
            {
                Log("Desfragmentando disco C: (pode demorar)...");
                StatusDefrag.Text = "⏳";
                bool ok = await Task.Run(() => DefragService.Optimize());
                totalSteps++;
                SetStatus(StatusDefrag, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Disco desfragmentado" : "⚠️ Desfragmentação parcial ou SSD");
                StepDone(ChkDefrag);
            }

            if (ChkPowerPlan.IsChecked == true)
            {
                Log("Ativando plano de Desempenho Máximo...");
                StatusPowerPlan.Text = "⏳";
                bool ok = await Task.Run(() => PowerPlanService.Apply());
                totalSteps++;
                SetStatus(StatusPowerPlan, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Plano de energia: Desempenho Máximo ativado" : "⚠️ Plano de energia: requer admin");
                StepDone(ChkPowerPlan);
            }

            if (ChkVisualEffects.IsChecked == true)
            {
                Log("Otimizando efeitos visuais...");
                StatusVisualEffects.Text = "⏳";
                bool ok = await Task.Run(() => VisualEffectsService.OptimizeForPerformance());
                totalSteps++;
                SetStatus(StatusVisualEffects, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Efeitos visuais otimizados" : "⚠️ Efeitos visuais: falhou");
                StepDone(ChkVisualEffects);
            }

            if (ChkBackgroundApps.IsChecked == true)
            {
                Log("Desativando apps em segundo plano...");
                StatusBackgroundApps.Text = "⏳";
                bool ok = await Task.Run(() => BackgroundAppsService.Disable());
                totalSteps++;
                SetStatus(StatusBackgroundApps, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Apps em segundo plano desativados" : "⚠️ Apps em segundo plano: falhou");
                StepDone(ChkBackgroundApps);
            }

            if (ChkStandbyRam.IsChecked == true)
            {
                Log("Liberando memória RAM...");
                StatusStandbyRam.Text = "⏳";
                int count = await Task.Run(() => MemoryService.ClearStandby());
                totalSteps++;
                SetStatus(StatusStandbyRam, "✅", true);
                Log($"✅ Memória liberada em {count} processos");
                StepDone(ChkStandbyRam);
            }

            if (ChkGpuScheduling.IsChecked == true)
            {
                Log("Ativando agendamento de GPU por hardware...");
                StatusGpuScheduling.Text = "⏳";
                bool ok = await Task.Run(() => GpuSchedulingService.Enable());
                totalSteps++;
                SetStatus(StatusGpuScheduling, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Agendamento de GPU ativado (reinicie o PC)" : "⚠️ Agendamento de GPU: requer admin");
                StepDone(ChkGpuScheduling);
            }

            if (ChkTelemetry.IsChecked == true)
            {
                Log("Desativando telemetria...");
                StatusTelemetry.Text = "⏳";
                bool ok = await Task.Run(() => TelemetryService.Disable());
                totalSteps++;
                SetStatus(StatusTelemetry, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Telemetria desativada" : "⚠️ Telemetria: requer admin");
                StepDone(ChkTelemetry);
            }

            if (ChkGameBar.IsChecked == true)
            {
                Log("Desativando Xbox Game Bar...");
                StatusGameBar.Text = "⏳";
                bool ok = await Task.Run(() => GameBarService.Disable());
                totalSteps++;
                SetStatus(StatusGameBar, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Xbox Game Bar / DVR desativado" : "⚠️ Game Bar: falhou");
                StepDone(ChkGameBar);
            }

            if (ChkSsdTrim.IsChecked == true)
            {
                Log("Otimizando SSD (TRIM)...");
                StatusSsdTrim.Text = "⏳";
                bool ok = await Task.Run(() => SsdTrimService.Trim());
                totalSteps++;
                SetStatus(StatusSsdTrim, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ SSD otimizado (TRIM)" : "⚠️ TRIM: falhou ou não aplicável");
                StepDone(ChkSsdTrim);
            }

            if (ChkWinUpdateCache.IsChecked == true)
            {
                Log("Limpando cache do Windows Update...");
                StatusWinUpdateCache.Text = "⏳";
                long bytes = await Task.Run(() => WindowsUpdateCacheService.Clean());
                totalFreed += bytes; totalSteps++;
                SetStatus(StatusWinUpdateCache, "✅", true);
                Log($"✅ Cache do Windows Update: {FormatBytes(bytes)} liberados");
                StepDone(ChkWinUpdateCache);
            }

            if (ChkThumbnails.IsChecked == true)
            {
                Log("Limpando cache de miniaturas...");
                StatusThumbnails.Text = "⏳";
                long bytes = await Task.Run(() => ThumbnailCacheService.Clean());
                totalFreed += bytes; totalSteps++;
                SetStatus(StatusThumbnails, "✅", true);
                Log($"✅ Cache de miniaturas: {FormatBytes(bytes)} liberados");
                StepDone(ChkThumbnails);
            }

            if (ChkFastStartup.IsChecked == true)
            {
                Log("Desativando Inicialização Rápida...");
                StatusFastStartup.Text = "⏳";
                bool ok = await Task.Run(() => FastStartupService.Disable());
                totalSteps++;
                SetStatus(StatusFastStartup, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Inicialização Rápida desativada" : "⚠️ Inicialização Rápida: requer admin");
                StepDone(ChkFastStartup);
            }

            if (ChkHibernation.IsChecked == true)
            {
                Log("Desativando hibernação...");
                StatusHibernation.Text = "⏳";
                bool ok = await Task.Run(() => HibernationService.Disable());
                totalSteps++;
                SetStatus(StatusHibernation, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Hibernação desativada (hiberfil.sys removido)" : "⚠️ Hibernação: requer admin");
                StepDone(ChkHibernation);
            }

            if (ChkSystemRepair.IsChecked == true)
            {
                Log("Reparando arquivos do sistema (pode demorar vários minutos)...");
                StatusSystemRepair.Text = "⏳";
                bool ok = await Task.Run(() => SystemRepairService.Repair());
                totalSteps++;
                SetStatus(StatusSystemRepair, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Verificação de arquivos do sistema concluída" : "⚠️ Reparo: requer admin ou houve erro");
                StepDone(ChkSystemRepair);
            }

            if (ChkFixDate.IsChecked == true)
            {
                Log("Corrigindo data e hora (sincronizando com a internet)...");
                StatusFixDate.Text = "⏳";
                bool ok = await Task.Run(() => WindowsDateService.Fix());
                totalSteps++;
                SetStatus(StatusFixDate, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Data e hora sincronizadas com a internet (NTP)"
                       : "⚠️ Data/hora: requer admin ou sem conexão com a internet");
                StepDone(ChkFixDate);
            }

            if (ChkBloatware.IsChecked == true)
            {
                Log("Removendo bloatware...");
                StatusBloatware.Text = "⏳";
                int count = await Task.Run(() => BloatwareRemover.Remove());
                totalSteps++;
                SetStatus(StatusBloatware, "✅", true);
                Log($"✅ Bloatware: {count} apps processados");
                StepDone(ChkBloatware);
            }

            if (ChkShaderCache.IsChecked == true)
            {
                Log("Limpando cache de shaders...");
                StatusShaderCache.Text = "⏳";
                long bytes = await Task.Run(() => ShaderCacheService.Clean());
                totalFreed += bytes; totalSteps++;
                SetStatus(StatusShaderCache, "✅", true);
                Log($"✅ Cache de shaders: {FormatBytes(bytes)} liberados");
                StepDone(ChkShaderCache);
            }

            if (ChkGameMode.IsChecked == true)
            {
                Log("Ativando Modo de Jogo do Windows...");
                StatusGameMode.Text = "⏳";
                bool ok = await Task.Run(() => GameModeService.Enable());
                totalSteps++;
                SetStatus(StatusGameMode, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Modo de Jogo ativado" : "⚠️ Modo de Jogo: falhou");
                StepDone(ChkGameMode);
            }

            if (ChkGamePriority.IsChecked == true)
            {
                Log("Aplicando prioridade máxima para jogos...");
                StatusGamePriority.Text = "⏳";
                bool ok = await Task.Run(() => GamePriorityService.Apply());
                totalSteps++;
                SetStatus(StatusGamePriority, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Prioridade de jogos: CPU/GPU em modo High" : "⚠️ Prioridade de jogos: requer admin");
                StepDone(ChkGamePriority);
            }

            if (ChkGameNetwork.IsChecked == true)
            {
                Log("Reduzindo latência de rede para jogos...");
                StatusGameNetwork.Text = "⏳";
                int tweaks = await Task.Run(() => GameNetworkService.Apply());
                totalSteps++;
                bool ok = tweaks > 0;
                SetStatus(StatusGameNetwork, ok ? "✅" : "⚠️", ok);
                Log(ok ? $"✅ Latência de rede: {tweaks} ajustes aplicados" : "⚠️ Latência de rede: requer admin");
                StepDone(ChkGameNetwork);
            }

            if (ChkPowerThrottling.IsChecked == true)
            {
                Log("Desativando Power Throttling...");
                StatusPowerThrottling.Text = "⏳";
                bool ok = await Task.Run(() => PowerThrottlingService.Disable());
                totalSteps++;
                SetStatus(StatusPowerThrottling, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Power Throttling desativado" : "⚠️ Power Throttling: requer admin");
                StepDone(ChkPowerThrottling);
            }

            if (ChkFullscreenOpt.IsChecked == true)
            {
                Log("Desativando Otimizações de Tela Cheia...");
                StatusFullscreenOpt.Text = "⏳";
                bool ok = await Task.Run(() => FullscreenOptimizationsService.Disable());
                totalSteps++;
                SetStatus(StatusFullscreenOpt, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Otimizações de Tela Cheia desativadas" : "⚠️ Tela Cheia: falhou");
                StepDone(ChkFullscreenOpt);
            }

            if (ChkMousePrecision.IsChecked == true)
            {
                Log("Desativando precisão aprimorada do ponteiro...");
                StatusMousePrecision.Text = "⏳";
                bool ok = await Task.Run(() => MousePrecisionService.Disable());
                totalSteps++;
                SetStatus(StatusMousePrecision, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Aceleração do mouse desativada" : "⚠️ Precisão do ponteiro: falhou");
                StepDone(ChkMousePrecision);
            }

            if (ChkCoreIsolation.IsChecked == true)
            {
                Log("Desativando Isolamento de Núcleo (HVCI)...");
                StatusCoreIsolation.Text = "⏳";
                bool ok = await Task.Run(() => CoreIsolationService.Disable());
                totalSteps++;
                SetStatus(StatusCoreIsolation, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Isolamento de Núcleo desativado (reinicie o PC para aplicar)"
                       : "⚠️ Isolamento de Núcleo: requer admin");
                StepDone(ChkCoreIsolation);
            }

            if (ChkBootProcessors.IsChecked == true)
            {
                Log($"Maximizando núcleos de CPU no boot ({Environment.ProcessorCount} núcleos)...");
                StatusBootProcessors.Text = "⏳";
                bool ok = await Task.Run(() => BootConfigService.SetMaxProcessors());
                totalSteps++;
                SetStatus(StatusBootProcessors, ok ? "✅" : "⚠️", ok);
                Log(ok ? $"✅ Boot: {Environment.ProcessorCount} núcleos configurados via bcdedit"
                       : "⚠️ Boot processors: requer admin");
                StepDone(ChkBootProcessors);
            }

            if (ChkExpertCpuMax.IsChecked == true)
            {
                Log("⚠️ Expert: travando CPU no clock máximo (C-States off)...");
                StatusExpertCpuMax.Text = "⏳";
                bool ok = await Task.Run(() => CpuMaxPerformanceService.Apply());
                totalSteps++;
                SetStatus(StatusExpertCpuMax, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ CPU travada no clock máximo — consumo/temperatura ficarão altos"
                       : "⚠️ CPU clock máximo: requer admin");
                StepDone(ChkExpertCpuMax);
            }

            if (ChkExpertTimer.IsChecked == true)
            {
                Log("⚠️ Expert: aplicando Timer Resolution 0.5ms...");
                StatusExpertTimer.Text = "⏳";
                bool ok = await Task.Run(() => TimerResolutionService.Apply());
                totalSteps++;
                SetStatus(StatusExpertTimer, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Timer em 0.5ms — válido enquanto o PC Optimizer estiver aberto"
                       : "⚠️ Timer Resolution: falhou");
                StepDone(ChkExpertTimer);
            }

            if (ChkExpertMsi.IsChecked == true)
            {
                Log("⚠️ Expert: ativando MSI Mode na GPU/rede...");
                StatusExpertMsi.Text = "⏳";
                int count = await Task.Run(() => MsiModeService.Apply());
                totalSteps++;
                bool ok = count > 0;
                SetStatus(StatusExpertMsi, ok ? "✅" : "⚠️", ok);
                Log(ok ? $"✅ MSI Mode: {count} ajustes aplicados (reinicie o PC)"
                       : "⚠️ MSI Mode: requer admin");
                StepDone(ChkExpertMsi);
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
            try
            {
                string? path = await ScreenshotService.CaptureAreaAsync(this);
                if (path != null) Log($"📸 Captura salva: {path}");
            }
            catch (Exception ex)
            {
                Log($"❌ Erro ao capturar tela: {ex.Message}");
            }
        }

        private void BtnAutologon_Click(object sender, RoutedEventArgs e)
        {
            var win = new Views.AutologonWindow { Owner = this };
            win.ShowDialog();
            RefreshAutologonStatus();
        }

        private void RefreshAutologonStatus()
        {
            if (TxtAutologonStatus == null) return;
            bool enabled = Services.WindowsAutologonService.IsEnabled();
            TxtAutologonStatus.Text = enabled
                ? $"✅ Ativo — entrará como {Services.WindowsAutologonService.GetConfiguredUser()}"
                : "🔒 Desativado — senha pedida ao iniciar";
        }

        private void BtnWoL_Click(object sender, RoutedEventArgs e)
        {
            var win = new Views.WakeOnLanWindow { Owner = this };
            win.ShowDialog();
            _ = RefreshWoLStatusAsync();
        }

        private void BtnMalware_Click(object sender, RoutedEventArgs e)
        {
            var win = new Views.MalwareScanWindow { Owner = this };
            win.ShowDialog();
        }

        private async Task RefreshWoLStatusAsync()
        {
            if (TxtWoLStatus == null) return;
            (bool enabled, string mac)? result = await Task.Run(() =>
            {
                var adapters = Services.WakeOnLanService.GetPhysicalAdapters();
                if (adapters.Count == 0) return ((bool enabled, string mac)?)null;
                var a = adapters[0];
                return (Services.WakeOnLanService.IsWoLEnabled(a), a.MacFormatted);
                // tupla (bool, string) converte implicitamente p/ (bool enabled, string mac)?
            });

            if (result == null)
                TxtWoLStatus.Text = "Nenhuma placa Ethernet encontrada";
            else
                TxtWoLStatus.Text = result.Value.enabled
                    ? $"✅ Ativo — {result.Value.mac}"
                    : $"Desativado — {result.Value.mac}";
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

        private void Card_Brightness(object sender, MouseButtonEventArgs e)
        {
            ((App)Application.Current).ToggleBrightnessWindow();
        }

        private void BtnBrightnessHeader_Click(object sender, RoutedEventArgs e)
        {
            ((App)Application.Current).ToggleBrightnessWindow();
        }

        private void SetAllChecksForPreset(bool value)
        {
            foreach (var chk in new[] { ChkTemp, ChkDisk, ChkRecycleBin, ChkStartup, ChkServices,
                ChkNetwork, ChkRegistry, ChkCortana, ChkDefrag, ChkPowerPlan, ChkVisualEffects,
                ChkBackgroundApps, ChkStandbyRam, ChkGpuScheduling, ChkTelemetry, ChkGameBar,
                ChkSsdTrim, ChkWinUpdateCache, ChkThumbnails, ChkShaderCache, ChkFastStartup,
                ChkHibernation, ChkSystemRepair, ChkFixDate, ChkBloatware, ChkGameMode, ChkGamePriority,
                ChkGameNetwork, ChkPowerThrottling, ChkFullscreenOpt, ChkMousePrecision,
                ChkCoreIsolation, ChkBootProcessors })
                chk.IsChecked = value;
        }

        private void BtnPresetBasica_Click(object sender, RoutedEventArgs e)
        {
            SetAllChecksForPreset(false);
            ChkTemp.IsChecked = true;
            ChkDisk.IsChecked = true;
            ChkRecycleBin.IsChecked = true;
            ChkThumbnails.IsChecked = true;
            ChkStandbyRam.IsChecked = true;
            ChkRegistry.IsChecked = true;
            ChkNetwork.IsChecked = true;
            ChkGameBar.IsChecked = true;
            ChkBootProcessors.IsChecked = true;
            UpdateSelectedCount();
        }

        private void BtnPresetIntermediaria_Click(object sender, RoutedEventArgs e)
        {
            SetAllChecksForPreset(false);
            ChkTemp.IsChecked = true;
            ChkDisk.IsChecked = true;
            ChkRecycleBin.IsChecked = true;
            ChkThumbnails.IsChecked = true;
            ChkStandbyRam.IsChecked = true;
            ChkRegistry.IsChecked = true;
            ChkNetwork.IsChecked = true;
            ChkGameBar.IsChecked = true;
            ChkWinUpdateCache.IsChecked = true;
            ChkShaderCache.IsChecked = true;
            ChkServices.IsChecked = true;
            ChkCortana.IsChecked = true;
            ChkBackgroundApps.IsChecked = true;
            ChkPowerPlan.IsChecked = true;
            ChkTelemetry.IsChecked = true;
            ChkGameMode.IsChecked = true;
            ChkGameNetwork.IsChecked = true;
            ChkBootProcessors.IsChecked = true;
            UpdateSelectedCount();
        }

        private void BtnPresetAvancada_Click(object sender, RoutedEventArgs e)
        {
            SetAllChecksForPreset(false);
            ChkTemp.IsChecked = true;
            ChkDisk.IsChecked = true;
            ChkRecycleBin.IsChecked = true;
            ChkThumbnails.IsChecked = true;
            ChkStandbyRam.IsChecked = true;
            ChkRegistry.IsChecked = true;
            ChkNetwork.IsChecked = true;
            ChkGameBar.IsChecked = true;
            ChkWinUpdateCache.IsChecked = true;
            ChkShaderCache.IsChecked = true;
            ChkServices.IsChecked = true;
            ChkCortana.IsChecked = true;
            ChkBackgroundApps.IsChecked = true;
            ChkPowerPlan.IsChecked = true;
            ChkTelemetry.IsChecked = true;
            ChkGameMode.IsChecked = true;
            ChkGameNetwork.IsChecked = true;
            ChkStartup.IsChecked = true;
            ChkSsdTrim.IsChecked = true;
            ChkVisualEffects.IsChecked = true;
            ChkGpuScheduling.IsChecked = true;
            ChkGamePriority.IsChecked = true;
            ChkPowerThrottling.IsChecked = true;
            ChkFastStartup.IsChecked = true;
            ChkBootProcessors.IsChecked = true;
            UpdateSelectedCount();
        }

        private void BtnPresetFull_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "⚠️ ATENÇÃO — Otimização Full\n\n" +
                "Esta opção marcará TODAS as 32 otimizações sem exceção, incluindo:\n\n" +
                "• Desfragmentação de disco (pode demorar horas em HDDs)\n" +
                "• Reparo do sistema SFC + DISM (~5-10 minutos)\n" +
                "• Remoção de bloatware (apps Microsoft serão desinstalados)\n" +
                "• Desativar Hibernação (remove o hiberfil.sys)\n" +
                "• Desativar Isolamento de Núcleo — reduz segurança do sistema\n" +
                "• Desativar Precisão do Ponteiro — muda a sensação do mouse\n\n" +
                "O grupo Expert ⚠️ (overclock/undervolt) NÃO é incluído — ele é 100% manual.\n\n" +
                "Deseja continuar e marcar tudo?",
                "PC Optimizer — Modo Full",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            SetAllChecksForPreset(true);
            UpdateSelectedCount();
        }

        // ════════════════ Modo Expert ════════════════

        private async Task InitExpertAsync()
        {
            _gpuInfo = await Task.Run(GpuTuningService.Detect);
            if (_gpuInfo == null)
            {
                TxtGpuName.Text = "GPU NVIDIA não detectada — OC/undervolt indisponíveis";
            }
            else
            {
                TxtGpuName.Text = $"{_gpuInfo.Name} — boost {_gpuInfo.BoostMhz} MHz, " +
                    $"power limit {_gpuInfo.DefaultPowerW}W (máx {_gpuInfo.MaxPowerW}W)";
                bool nvapi = await Task.Run(NvapiService.IsAvailable);
                BtnGpuOcApply.IsEnabled = nvapi;
                BtnGpuPowerMax.IsEnabled = _gpuInfo.MaxPowerW > 0;
                BtnGpuRevert.IsEnabled = true;
                bool uv = nvapi && _gpuInfo.SupportsLock;
                BtnUvLeve.IsEnabled = uv;
                BtnUvMedio.IsEnabled = uv;
                BtnUvAgressivo.IsEnabled = uv;
                if (!uv)
                    TxtGpuUvStatus.Text = nvapi
                        ? "Esta GPU não suporta trava de clock (precisa ser GTX 16xx / RTX ou mais nova)."
                        : "NVAPI indisponível — undervolt desativado.";
            }

            _cpuUvTool = await Task.Run(CpuUndervoltToolService.Detect);
            _detectedTools = await Task.Run(CpuTuningDetectionService.Detect);

            // Aproveita o que já está instalado: se houver uma ferramenta de undervolt
            // dedicada no PC (mesmo que para a outra marca), abre direto sem baixar nada.
            var installedUv = CpuTuningDetectionService.BestInstalledUndervoltTool(_detectedTools);
            UpdateCpuUvStatus(installedUv);
        }

        private void UpdateCpuUvStatus(CpuTuningDetectionService.DetectedTool? installedUv)
        {
            if (_cpuUvTool == null) return;

            // Monitores detectados (HWiNFO/CPU-Z/AIDA64/Afterburner) — úteis para
            // acompanhar tensão e temperatura enquanto se faz o undervolt.
            var monitors = _detectedTools?
                .Where(t => t.Kind == CpuTuningDetectionService.ToolKind.Monitor)
                .Select(t => t.Name + (t.Running ? " (aberto)" : ""))
                .ToList() ?? new System.Collections.Generic.List<string>();
            string monitorNote = monitors.Count > 0
                ? $" • Monitor detectado: {string.Join(", ", monitors)}"
                : "";

            if (installedUv != null)
            {
                // Ferramenta de undervolt já instalada — leverage direto
                _cpuUvToolToOpen = installedUv.InstalledPath;
                TxtCpuUvStatus.Text = installedUv.Running
                    ? $"✅ {installedUv.Name} já está aberto — clique para focar e fazer o undervolt{monitorNote}"
                    : $"✅ {installedUv.Name} instalado — clique para abrir e fazer o undervolt{monitorNote}";
                BtnCpuUvTool.Content = "Abrir";
            }
            else if (_cpuUvTool.InstalledPath != null)
            {
                _cpuUvToolToOpen = _cpuUvTool.InstalledPath;
                TxtCpuUvStatus.Text = $"✅ CPU {_cpuUvTool.CpuVendor} — {_cpuUvTool.ToolName} instalado, clique para abrir{monitorNote}";
                BtnCpuUvTool.Content = "Abrir";
            }
            else
            {
                _cpuUvToolToOpen = null;
                TxtCpuUvStatus.Text = $"CPU {_cpuUvTool.CpuVendor} — requer ferramenta dedicada; recomendamos o {_cpuUvTool.ToolName} (clique para baixar){monitorNote}";
                BtnCpuUvTool.Content = "Baixar";
            }
        }

        private string? _cpuUvToolToOpen;

        /// <summary>Aviso único por sessão antes da primeira ação Expert. Retorna true se o usuário aceitou.</summary>
        private bool ConfirmExpertOnce()
        {
            if (_expertWarned) return true;
            var result = MessageBox.Show(
                "⚠️ MODO EXPERT — Usuários experientes\n\n" +
                "As opções deste grupo mexem em overclock, tensão e comportamento " +
                "interno do Windows. Podem causar instabilidade (tela preta, crash de " +
                "jogo/driver) até você encontrar os valores certos para o seu hardware.\n\n" +
                "Nada danifica o hardware permanentemente e tudo pode ser revertido " +
                "(botão Stock / reiniciar o PC).\n\n" +
                "Deseja continuar?",
                "PC Optimizer — Modo Expert",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            _expertWarned = result == MessageBoxResult.Yes;
            return _expertWarned;
        }

        private void ExpertCheckChanged(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.CheckBox chk && !ConfirmExpertOnce())
            {
                chk.IsChecked = false;
                return;
            }
            OptCheckChanged(sender, e);
        }

        private async void BtnGpuOcApply_Click(object sender, RoutedEventArgs e)
        {
            if (!ConfirmExpertOnce()) return;
            int core = (int)SldGpuCore.Value;
            int mem = (int)SldGpuMem.Value;
            TxtGpuOcStatus.Text = "Aplicando...";
            var (okCore, okMem) = await Task.Run(() => GpuTuningService.ApplyOverclock(core, mem));
            TxtGpuOcStatus.Text = okCore && okMem ? $"✅ Core +{core} / Mem +{mem}"
                                : okCore ? $"✅ Core +{core} • ⚠️ memória falhou (NVAPI {NvapiService.LastStatus})"
                                : $"⚠️ Falhou — driver não aceitou o offset (NVAPI {NvapiService.LastStatus})";
            Log(okCore ? $"📈 OC GPU aplicado: core +{core} MHz, memória +{mem} MHz"
                       : $"⚠️ OC GPU: o driver rejeitou o offset (NVAPI {NvapiService.LastStatus})");
        }

        private async void BtnGpuPowerMax_Click(object sender, RoutedEventArgs e)
        {
            if (!ConfirmExpertOnce() || _gpuInfo is not { } gpu) return;
            bool ok = await Task.Run(() => GpuTuningService.ApplyMaxPowerLimit(gpu));
            TxtGpuOcStatus.Text = ok ? $"✅ Power limit: {gpu.MaxPowerW}W"
                                     : "⚠️ Esta placa não permite alterar o power limit";
            Log(ok ? $"⚡ Power limit da GPU elevado para {gpu.MaxPowerW}W"
                   : "⚠️ Power limit: não suportado nesta placa");
        }

        private async void ApplyUndervolt(double lockFactor, int offsetMhz, string nome)
        {
            if (!ConfirmExpertOnce() || _gpuInfo is not { } gpu) return;
            TxtGpuUvStatus.Text = "Aplicando undervolt...";
            var (ok, lockMhz, falha) = await Task.Run(() =>
                GpuTuningService.ApplyUndervolt(gpu, lockFactor, offsetMhz));
            TxtGpuUvStatus.Text = ok
                ? $"✅ Undervolt {nome}: clock travado em {lockMhz} MHz com offset +{offsetMhz} — teste num jogo"
                : $"⚠️ Falhou: {falha}";
            Log(ok ? $"🌡️ Undervolt {nome} aplicado: trava {lockMhz} MHz, offset +{offsetMhz} MHz"
                   : $"⚠️ Undervolt: {falha}");
        }

        private void BtnUvLeve_Click(object sender, RoutedEventArgs e) => ApplyUndervolt(0.95, 120, "leve");
        private void BtnUvMedio_Click(object sender, RoutedEventArgs e) => ApplyUndervolt(0.90, 150, "médio");
        private void BtnUvAgressivo_Click(object sender, RoutedEventArgs e) => ApplyUndervolt(0.85, 180, "agressivo");

        private async void BtnGpuRevert_Click(object sender, RoutedEventArgs e)
        {
            TxtGpuUvStatus.Text = "Revertendo...";
            bool ok = await Task.Run(() => GpuTuningService.RevertAll(_gpuInfo));
            TxtGpuUvStatus.Text = ok ? "✅ GPU revertida para o padrão de fábrica"
                                     : "⚠️ Reversão parcial — reinicie o PC para garantir";
            TxtGpuOcStatus.Text = "";
            Log(ok ? "↩️ GPU revertida para stock (offsets 0, sem trava, power limit padrão)"
                   : "⚠️ Reversão de GPU parcial — reinicie o PC");
        }

        private async void BtnCpuUvTool_Click(object sender, RoutedEventArgs e)
        {
            if (_cpuUvTool == null || !ConfirmExpertOnce()) return;

            // Re-detecta na hora do clique: se o usuário instalou a ferramenta
            // com o app já aberto (ex.: acabou de instalar o Ryzen Master), a
            // detecção do startup está desatualizada. Atualiza antes de decidir.
            _cpuUvTool = await Task.Run(CpuUndervoltToolService.Detect);
            _detectedTools = await Task.Run(CpuTuningDetectionService.Detect);
            UpdateCpuUvStatus(CpuTuningDetectionService.BestInstalledUndervoltTool(_detectedTools));

            // Ferramenta de undervolt já instalada (detectada no PC): abre direto,
            // sem baixar nada nem barra de progresso.
            if (_cpuUvToolToOpen != null)
            {
                try
                {
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(_cpuUvToolToOpen) { UseShellExecute = true });
                    Log($"🧬 Ferramenta de undervolt aberta: {System.IO.Path.GetFileNameWithoutExtension(_cpuUvToolToOpen)}");
                }
                catch (Exception ex)
                {
                    Log($"⚠️ Falha ao abrir a ferramenta de undervolt: {ex.Message}");
                }
                return;
            }

            BtnCpuUvTool.IsEnabled = false;
            TxtCpuUvStatus.Text = $"Baixando {_cpuUvTool.ToolName}...";
            Progress.Visibility = Visibility.Visible;
            Progress.IsIndeterminate = false;
            Progress.Value = 0;
            TxtProgress.Visibility = Visibility.Visible;
            TxtProgress.Text = $"Baixando {_cpuUvTool.ToolName}... 0%";

            var progress = new Progress<double>(p =>
            {
                Progress.Value = p * 100;
                TxtProgress.Text = $"Baixando {_cpuUvTool.ToolName}... {p * 100:F0}%";
            });

            var result = await CpuUndervoltToolService.AcquireAsync(_cpuUvTool, progress);

            Progress.Visibility = Visibility.Collapsed;
            TxtProgress.Visibility = Visibility.Collapsed;
            BtnCpuUvTool.IsEnabled = true;

            switch (result)
            {
                case CpuUndervoltToolService.AcquireResult.Downloaded:
                    TxtCpuUvStatus.Text = $"✅ {_cpuUvTool.ToolName} baixado e iniciado — siga a instalação";
                    Log($"🧬 {_cpuUvTool.ToolName} baixado na pasta Downloads e instalador aberto");
                    break;
                case CpuUndervoltToolService.AcquireResult.OpenedPage:
                    TxtCpuUvStatus.Text = $"🌐 Página do {_cpuUvTool.ToolName} aberta — baixe por lá";
                    Log($"🌐 Download direto indisponível; página oficial do {_cpuUvTool.ToolName} aberta no navegador");
                    break;
                default:
                    TxtCpuUvStatus.Text = $"⚠️ Não foi possível abrir o {_cpuUvTool.ToolName}";
                    Log($"⚠️ Falha ao baixar/abrir o {_cpuUvTool.ToolName}");
                    break;
            }
        }

        private void BtnThemeToggle_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.Toggle();
            ApplyTitleBarColor();
            Log($"🎨 Tema alterado para {(ThemeManager.Current == AppTheme.Dark ? "escuro" : "claro")}");
        }

        // ── Barra de título nativa acompanha a cor do tema ─────────────────
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20; // Win10 1809+
        private const int DWMWA_CAPTION_COLOR = 35;           // Win11 — COLORREF 0x00BBGGRR

        private void ApplyTitleBarColor()
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                bool dark = ThemeManager.Current == AppTheme.Dark;
                int darkMode = dark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

                // Mesma cor do WindowBg de cada tema (formato COLORREF: 0x00BBGGRR)
                int caption = dark ? 0x1E1111 : 0xF8F2F0;
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            }
            catch { /* DWM indisponível (Win10 antigo) — barra fica padrão */ }
        }
    }
}
