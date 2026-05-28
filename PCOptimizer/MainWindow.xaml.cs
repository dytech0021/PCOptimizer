using System;
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
            };
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
            Progress.Visibility = Visibility.Visible;
            Progress.IsIndeterminate = true;
            TxtLog.Text = "Iniciando otimizações selecionadas...";

            long totalFreed = 0;
            int totalSteps = 0;

            if (ChkTemp.IsChecked == true)
            {
                Log("Limpando arquivos temporários...");
                StatusTemp.Text = "⏳";
                var (files, bytes) = await Task.Run(() => TempCleaner.Clean());
                totalFreed += bytes; totalSteps++;
                SetStatus(StatusTemp, "✅", true);
                Log($"✅ Temporários: {files} arquivos ({FormatBytes(bytes)})");
            }

            if (ChkDisk.IsChecked == true)
            {
                Log("Limpando disco...");
                StatusDisk.Text = "⏳";
                var (files, bytes) = await Task.Run(() => DiskCleaner.Clean());
                totalFreed += bytes; totalSteps++;
                SetStatus(StatusDisk, "✅", true);
                Log($"✅ Disco: {files} arquivos ({FormatBytes(bytes)})");
            }

            if (ChkRecycleBin.IsChecked == true)
            {
                Log("Esvaziando lixeira...");
                StatusRecycleBin.Text = "⏳";
                bool ok = await Task.Run(() => RecycleBinCleaner.Clean());
                totalSteps++;
                SetStatus(StatusRecycleBin, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Lixeira esvaziada" : "⚠️ Lixeira já estava vazia");
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
            }

            if (ChkServices.IsChecked == true)
            {
                Log("Otimizando serviços...");
                StatusServices.Text = "⏳";
                int count = await Task.Run(() => ServiceOptimizer.Optimize());
                totalSteps++;
                SetStatus(StatusServices, "✅", true);
                Log($"✅ Serviços: {count} otimizados");
            }

            if (ChkNetwork.IsChecked == true)
            {
                Log("Otimizando rede...");
                StatusNetwork.Text = "⏳";
                int steps = await Task.Run(() => NetworkOptimizer.Optimize());
                totalSteps++;
                SetStatus(StatusNetwork, "✅", true);
                Log($"✅ Rede: {steps} otimizações");
            }

            if (ChkRegistry.IsChecked == true)
            {
                Log("Aplicando tweaks no registro...");
                StatusRegistry.Text = "⏳";
                int tweaks = await Task.Run(() => RegistryOptimizer.Optimize());
                totalSteps++;
                SetStatus(StatusRegistry, "✅", true);
                Log($"✅ Registro: {tweaks} tweaks");
            }

            if (ChkCortana.IsChecked == true)
            {
                Log("Desativando Cortana...");
                StatusCortana.Text = "⏳";
                bool ok = await Task.Run(() => CortanaDisabler.Disable());
                totalSteps++;
                SetStatus(StatusCortana, ok ? "✅" : "❌", ok);
                Log(ok ? "✅ Cortana desativada" : "❌ Erro ao desativar Cortana");
            }

            if (ChkDefrag.IsChecked == true)
            {
                Log("Desfragmentando disco C: (pode demorar)...");
                StatusDefrag.Text = "⏳";
                bool ok = await Task.Run(() => DefragService.Optimize());
                totalSteps++;
                SetStatus(StatusDefrag, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Disco desfragmentado" : "⚠️ Desfragmentação parcial ou SSD");
            }

            if (ChkPowerPlan.IsChecked == true)
            {
                Log("Ativando plano de Alto Desempenho...");
                StatusPowerPlan.Text = "⏳";
                bool ok = await Task.Run(() => PowerPlanService.Apply());
                totalSteps++;
                SetStatus(StatusPowerPlan, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Plano de energia: Alto Desempenho ativado" : "⚠️ Plano de energia: requer admin");
            }

            if (ChkVisualEffects.IsChecked == true)
            {
                Log("Otimizando efeitos visuais...");
                StatusVisualEffects.Text = "⏳";
                bool ok = await Task.Run(() => VisualEffectsService.OptimizeForPerformance());
                totalSteps++;
                SetStatus(StatusVisualEffects, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Efeitos visuais otimizados" : "⚠️ Efeitos visuais: falhou");
            }

            if (ChkBackgroundApps.IsChecked == true)
            {
                Log("Desativando apps em segundo plano...");
                StatusBackgroundApps.Text = "⏳";
                bool ok = await Task.Run(() => BackgroundAppsService.Disable());
                totalSteps++;
                SetStatus(StatusBackgroundApps, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Apps em segundo plano desativados" : "⚠️ Apps em segundo plano: falhou");
            }

            if (ChkStandbyRam.IsChecked == true)
            {
                Log("Liberando memória RAM...");
                StatusStandbyRam.Text = "⏳";
                int count = await Task.Run(() => MemoryService.ClearStandby());
                totalSteps++;
                SetStatus(StatusStandbyRam, "✅", true);
                Log($"✅ Memória liberada em {count} processos");
            }

            if (ChkGpuScheduling.IsChecked == true)
            {
                Log("Ativando agendamento de GPU por hardware...");
                StatusGpuScheduling.Text = "⏳";
                bool ok = await Task.Run(() => GpuSchedulingService.Enable());
                totalSteps++;
                SetStatus(StatusGpuScheduling, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Agendamento de GPU ativado (reinicie o PC)" : "⚠️ Agendamento de GPU: requer admin");
            }

            if (ChkTelemetry.IsChecked == true)
            {
                Log("Desativando telemetria...");
                StatusTelemetry.Text = "⏳";
                bool ok = await Task.Run(() => TelemetryService.Disable());
                totalSteps++;
                SetStatus(StatusTelemetry, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Telemetria desativada" : "⚠️ Telemetria: requer admin");
            }

            if (ChkGameBar.IsChecked == true)
            {
                Log("Desativando Xbox Game Bar...");
                StatusGameBar.Text = "⏳";
                bool ok = await Task.Run(() => GameBarService.Disable());
                totalSteps++;
                SetStatus(StatusGameBar, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Xbox Game Bar / DVR desativado" : "⚠️ Game Bar: falhou");
            }

            if (ChkSsdTrim.IsChecked == true)
            {
                Log("Otimizando SSD (TRIM)...");
                StatusSsdTrim.Text = "⏳";
                bool ok = await Task.Run(() => SsdTrimService.Trim());
                totalSteps++;
                SetStatus(StatusSsdTrim, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ SSD otimizado (TRIM)" : "⚠️ TRIM: falhou ou não aplicável");
            }

            if (ChkWinUpdateCache.IsChecked == true)
            {
                Log("Limpando cache do Windows Update...");
                StatusWinUpdateCache.Text = "⏳";
                long bytes = await Task.Run(() => WindowsUpdateCacheService.Clean());
                totalFreed += bytes; totalSteps++;
                SetStatus(StatusWinUpdateCache, "✅", true);
                Log($"✅ Cache do Windows Update: {FormatBytes(bytes)} liberados");
            }

            if (ChkThumbnails.IsChecked == true)
            {
                Log("Limpando cache de miniaturas...");
                StatusThumbnails.Text = "⏳";
                long bytes = await Task.Run(() => ThumbnailCacheService.Clean());
                totalFreed += bytes; totalSteps++;
                SetStatus(StatusThumbnails, "✅", true);
                Log($"✅ Cache de miniaturas: {FormatBytes(bytes)} liberados");
            }

            if (ChkFastStartup.IsChecked == true)
            {
                Log("Desativando Inicialização Rápida...");
                StatusFastStartup.Text = "⏳";
                bool ok = await Task.Run(() => FastStartupService.Disable());
                totalSteps++;
                SetStatus(StatusFastStartup, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Inicialização Rápida desativada" : "⚠️ Inicialização Rápida: requer admin");
            }

            if (ChkHibernation.IsChecked == true)
            {
                Log("Desativando hibernação...");
                StatusHibernation.Text = "⏳";
                bool ok = await Task.Run(() => HibernationService.Disable());
                totalSteps++;
                SetStatus(StatusHibernation, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Hibernação desativada (hiberfil.sys removido)" : "⚠️ Hibernação: requer admin");
            }

            if (ChkSystemRepair.IsChecked == true)
            {
                Log("Reparando arquivos do sistema (pode demorar vários minutos)...");
                StatusSystemRepair.Text = "⏳";
                bool ok = await Task.Run(() => SystemRepairService.Repair());
                totalSteps++;
                SetStatus(StatusSystemRepair, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Verificação de arquivos do sistema concluída" : "⚠️ Reparo: requer admin ou houve erro");
            }

            if (ChkBloatware.IsChecked == true)
            {
                Log("Removendo bloatware...");
                StatusBloatware.Text = "⏳";
                int count = await Task.Run(() => BloatwareRemover.Remove());
                totalSteps++;
                SetStatus(StatusBloatware, "✅", true);
                Log($"✅ Bloatware: {count} apps processados");
            }

            Log($"\n🎉 Concluído! {totalSteps} otimizações. Espaço liberado: {FormatBytes(totalFreed)}");

            Progress.IsIndeterminate = false;
            Progress.Value = 100;
            BtnRun.Content = "⚡ Executar Otimizações";
            BtnRun.IsEnabled = true;
            _isRunning = false;

            MessageBox.Show(
                $"Otimização concluída!\n\n" +
                $"✅ {totalSteps} otimizações executadas\n" +
                $"💾 Espaço liberado: {FormatBytes(totalFreed)}",
                "PC Optimizer", MessageBoxButton.OK, MessageBoxImage.Information);
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
