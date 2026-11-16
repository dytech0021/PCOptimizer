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
    // Parte da janela principal: lista de otimizações, progresso, execução e predefinições.
    public partial class MainWindow
    {
        private void Log(string message)
        {
            // Append incremental via Inlines: "Text +=" reconcatena o log inteiro a
            // cada linha (custo O(n²)) e re-mede o TextBlock todo na thread de UI.
            TxtLog.Inlines.Add(new System.Windows.Documents.Run(
                $"\n[{DateTime.Now:HH:mm:ss}] {message}"));
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
            if (chk == ChkStandbyRam) return 3;
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
            // Cores do tema (seguem o violeta e a troca claro/escuro).
            status.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                success ? "StatusOkFg" : "StatusErrFg");
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
                ChkExpertMsi
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
            // Mas DESMARCAR tudo limpa também os opt-in: sem isso, itens perigosos
            // marcados por um preset (Bloatware, Hibernação…) sobreviviam ao
            // "desmarcar tudo" e executavam sem o usuário perceber.
            if (!value)
            {
                ChkFastStartup.IsChecked = false;
                ChkHibernation.IsChecked = false;
                ChkSystemRepair.IsChecked = false;
                ChkBloatware.IsChecked = false;
                ChkFullscreenOpt.IsChecked = false;
                ChkMousePrecision.IsChecked = false;
                ChkCoreIsolation.IsChecked = false;
                ChkExpertMsi.IsChecked = false;
            }
            UpdateSelectedCount();
        }

        private async void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning) return;

            var allChecks = new[] { ChkTemp, ChkDisk, ChkRecycleBin, ChkStartup, ChkServices,
                ChkNetwork, ChkRegistry, ChkCortana, ChkDefrag, ChkPowerPlan, ChkVisualEffects,
                ChkBackgroundApps, ChkStandbyRam, ChkGpuScheduling, ChkTelemetry, ChkGameBar,
                ChkSsdTrim, ChkWinUpdateCache, ChkThumbnails, ChkShaderCache, ChkFastStartup,
                ChkHibernation, ChkSystemRepair, ChkFixDate, ChkBloatware, ChkGameMode, ChkGamePriority,
                ChkGameNetwork, ChkPowerThrottling, ChkFullscreenOpt, ChkMousePrecision,
                ChkCoreIsolation, ChkBootProcessors, ChkExpertMsi };

            // Congela a seleção no início: marcar/desmarcar caixas ou clicar num
            // preset DURANTE a execução não altera mais o que roda nem estraga a
            // contagem/previsão (antes, cada passo relia IsChecked ao vivo).
            var sel = new HashSet<System.Windows.Controls.CheckBox>(
                allChecks.Where(c => c.IsChecked == true));

            if (sel.Count == 0)
            {
                MessageBox.Show("Selecione ao menos uma otimização antes de executar.",
                    "PC Optimizer", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _isRunning = true;
            RefreshMemoryCard();
            // O Content do XAML é um StackPanel estilizado — guarda para restaurar
            // no final (uma string fixa descartaria o visual original para sempre).
            object originalRunContent = BtnRun.Content;
            BtnRun.IsEnabled = false;
            BtnRun.Content = "⏳ Executando...";

            _totalSelectedSteps = sel.Count;
            _totalWeight = 0;
            foreach (var c in sel) _totalWeight += GetWeight(c);
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
            if (sel.Contains(ChkTemp))
            {
                Log("Limpando arquivos temporários...");
                StatusTemp.Text = "⏳";
                var (files, bytes) = await Task.Run(() => TempCleaner.Clean());
                totalFreed += bytes; totalSteps++;
                SetStatus(StatusTemp, "✅", true);
                Log($"✅ Temporários: {files} arquivos ({FormatBytes(bytes)})");
                StepDone(ChkTemp);
            }

            if (sel.Contains(ChkDisk))
            {
                Log("Limpando disco...");
                StatusDisk.Text = "⏳";
                var (files, bytes) = await Task.Run(() => DiskCleaner.Clean());
                totalFreed += bytes; totalSteps++;
                SetStatus(StatusDisk, "✅", true);
                Log($"✅ Disco: {files} arquivos ({FormatBytes(bytes)})");
                StepDone(ChkDisk);
            }

            if (sel.Contains(ChkRecycleBin))
            {
                Log("Esvaziando lixeira...");
                StatusRecycleBin.Text = "⏳";
                bool ok = await Task.Run(() => RecycleBinCleaner.Clean());
                totalSteps++;
                SetStatus(StatusRecycleBin, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Lixeira esvaziada" : "⚠️ Lixeira já estava vazia");
                StepDone(ChkRecycleBin);
            }

            if (sel.Contains(ChkStartup))
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

            if (sel.Contains(ChkServices))
            {
                Log("Otimizando serviços...");
                StatusServices.Text = "⏳";
                int count = await Task.Run(() => ServiceOptimizer.Optimize());
                totalSteps++;
                SetStatus(StatusServices, "✅", true);
                Log($"✅ Serviços: {count} otimizados");
                StepDone(ChkServices);
            }

            if (sel.Contains(ChkNetwork))
            {
                Log("Otimizando rede...");
                StatusNetwork.Text = "⏳";
                int steps = await Task.Run(() => NetworkOptimizer.Optimize());
                totalSteps++;
                SetStatus(StatusNetwork, "✅", true);
                Log($"✅ Rede: {steps} otimizações");
                StepDone(ChkNetwork);
            }

            if (sel.Contains(ChkRegistry))
            {
                Log("Aplicando tweaks no registro...");
                StatusRegistry.Text = "⏳";
                int tweaks = await Task.Run(() => RegistryOptimizer.Optimize());
                totalSteps++;
                SetStatus(StatusRegistry, "✅", true);
                Log($"✅ Registro: {tweaks} tweaks");
                StepDone(ChkRegistry);
                RefreshMouseMenuCard();
            }

            if (sel.Contains(ChkCortana))
            {
                Log("Desativando Cortana...");
                StatusCortana.Text = "⏳";
                bool ok = await Task.Run(() => CortanaDisabler.Disable());
                totalSteps++;
                SetStatus(StatusCortana, ok ? "✅" : "❌", ok);
                Log(ok ? "✅ Cortana desativada" : "❌ Erro ao desativar Cortana");
                StepDone(ChkCortana);
            }

            if (sel.Contains(ChkDefrag))
            {
                Log("Desfragmentando disco C: (pode demorar)...");
                StatusDefrag.Text = "⏳";
                bool ok = await Task.Run(() => DefragService.Optimize());
                totalSteps++;
                SetStatus(StatusDefrag, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Disco desfragmentado" : "⚠️ Desfragmentação parcial ou SSD");
                StepDone(ChkDefrag);
            }

            if (sel.Contains(ChkPowerPlan) && CpuTuningService.IsActive)
            {
                // Trocar o plano aqui derrubaria o perfil do painel de CPU em
                // silêncio. O painel manda: este passo sai de cena e avisa.
                Log("↷ Plano de energia: pulado — o Controle de CPU está no comando");
                SetStatus(StatusPowerPlan, "↷", true);
                StepDone(ChkPowerPlan);
            }
            else if (sel.Contains(ChkPowerPlan))
            {
                Log("Ativando plano de energia de desempenho...");
                StatusPowerPlan.Text = "⏳";
                bool ok = await Task.Run(() => PowerPlanService.Apply());
                totalSteps++;
                SetStatus(StatusPowerPlan, ok ? "✅" : "⚠️", ok);
                Log(ok ? $"✅ Plano de energia: {PowerPlanService.LastAppliedPlan} ativado"
                       : "⚠️ Plano de energia: requer admin");
                StepDone(ChkPowerPlan);
            }

            if (sel.Contains(ChkVisualEffects))
            {
                Log("Otimizando efeitos visuais...");
                StatusVisualEffects.Text = "⏳";
                bool ok = await Task.Run(() => VisualEffectsService.OptimizeForPerformance());
                totalSteps++;
                SetStatus(StatusVisualEffects, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Efeitos visuais otimizados" : "⚠️ Efeitos visuais: falhou");
                StepDone(ChkVisualEffects);
            }

            if (sel.Contains(ChkBackgroundApps))
            {
                Log("Desativando apps em segundo plano...");
                StatusBackgroundApps.Text = "⏳";
                bool ok = await Task.Run(() => BackgroundAppsService.Disable());
                totalSteps++;
                SetStatus(StatusBackgroundApps, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Apps em segundo plano desativados" : "⚠️ Apps em segundo plano: falhou");
                StepDone(ChkBackgroundApps);
            }

            if (sel.Contains(ChkStandbyRam))
            {
                // Nome histórico: roda o otimizador de memória com as áreas do card,
                // sem as manuais (cache completo e memória modificada) e nunca com
                // jogo aberto.
                Log("Liberando memória RAM (leve)...");
                StatusStandbyRam.Text = "⏳";
                var mem = await MemoryOptimizerService.OptimizeAsync(
                    SettingsService.Current.MemoryOps, MemoryTrigger.Batch);
                bool memSkipped = mem.Outcome is MemoryRunOutcome.Paused or MemoryRunOutcome.NothingToDo
                                  or MemoryRunOutcome.Busy;
                if (!memSkipped) totalSteps++;
                bool memOk = mem.Outcome == MemoryRunOutcome.Done || memSkipped;
                SetStatus(StatusStandbyRam, mem.Outcome switch
                {
                    MemoryRunOutcome.Done       => "✅",
                    MemoryRunOutcome.Paused     => "⏸️",
                    MemoryRunOutcome.NeedsAdmin => "🔒",
                    _ when memSkipped           => "↷",
                    _                           => "⚠️",
                }, memOk);
                Log((memOk ? "✅ Memória: " : "⚠️ Memória: ") + mem.Text);
                StepDone(ChkStandbyRam);
                RefreshMemoryReadout();
            }

            if (sel.Contains(ChkGpuScheduling))
            {
                Log("Ativando agendamento de GPU por hardware...");
                StatusGpuScheduling.Text = "⏳";
                bool ok = await Task.Run(() => GpuSchedulingService.Enable());
                totalSteps++;
                SetStatus(StatusGpuScheduling, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Agendamento de GPU ativado (reinicie o PC)" : "⚠️ Agendamento de GPU: requer admin");
                StepDone(ChkGpuScheduling);
            }

            if (sel.Contains(ChkTelemetry))
            {
                Log("Desativando telemetria...");
                StatusTelemetry.Text = "⏳";
                bool ok = await Task.Run(() => TelemetryService.Disable());
                totalSteps++;
                SetStatus(StatusTelemetry, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Telemetria desativada" : "⚠️ Telemetria: requer admin");
                StepDone(ChkTelemetry);
            }

            if (sel.Contains(ChkGameBar))
            {
                Log("Desativando Xbox Game Bar...");
                StatusGameBar.Text = "⏳";
                bool ok = await Task.Run(() => GameBarService.Disable());
                totalSteps++;
                SetStatus(StatusGameBar, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Xbox Game Bar / DVR desativado" : "⚠️ Game Bar: falhou");
                StepDone(ChkGameBar);
            }

            if (sel.Contains(ChkSsdTrim))
            {
                Log("Otimizando SSD (TRIM)...");
                StatusSsdTrim.Text = "⏳";
                bool ok = await Task.Run(() => SsdTrimService.Trim());
                totalSteps++;
                SetStatus(StatusSsdTrim, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ SSD otimizado (TRIM)" : "⚠️ TRIM: falhou ou não aplicável");
                StepDone(ChkSsdTrim);
            }

            if (sel.Contains(ChkWinUpdateCache))
            {
                Log("Limpando cache do Windows Update...");
                StatusWinUpdateCache.Text = "⏳";
                long bytes = await Task.Run(() => WindowsUpdateCacheService.Clean());
                totalFreed += bytes; totalSteps++;
                SetStatus(StatusWinUpdateCache, "✅", true);
                Log($"✅ Cache do Windows Update: {FormatBytes(bytes)} liberados");
                StepDone(ChkWinUpdateCache);
            }

            if (sel.Contains(ChkThumbnails))
            {
                Log("Limpando cache de miniaturas...");
                StatusThumbnails.Text = "⏳";
                long bytes = await Task.Run(() => ThumbnailCacheService.Clean());
                totalFreed += bytes; totalSteps++;
                SetStatus(StatusThumbnails, "✅", true);
                Log($"✅ Cache de miniaturas: {FormatBytes(bytes)} liberados");
                StepDone(ChkThumbnails);
            }

            if (sel.Contains(ChkFastStartup))
            {
                Log("Desativando Inicialização Rápida...");
                StatusFastStartup.Text = "⏳";
                bool ok = await Task.Run(() => FastStartupService.Disable());
                totalSteps++;
                SetStatus(StatusFastStartup, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Inicialização Rápida desativada" : "⚠️ Inicialização Rápida: requer admin");
                StepDone(ChkFastStartup);
            }

            if (sel.Contains(ChkHibernation))
            {
                Log("Desativando hibernação...");
                StatusHibernation.Text = "⏳";
                bool ok = await Task.Run(() => HibernationService.Disable());
                totalSteps++;
                SetStatus(StatusHibernation, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Hibernação desativada (hiberfil.sys removido)" : "⚠️ Hibernação: requer admin");
                StepDone(ChkHibernation);
            }

            if (sel.Contains(ChkSystemRepair))
            {
                Log("Reparando arquivos do sistema (pode demorar vários minutos)...");
                StatusSystemRepair.Text = "⏳";
                bool ok = await Task.Run(() => SystemRepairService.Repair());
                totalSteps++;
                SetStatus(StatusSystemRepair, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Verificação de arquivos do sistema concluída" : "⚠️ Reparo: requer admin ou houve erro");
                StepDone(ChkSystemRepair);
            }

            if (sel.Contains(ChkFixDate))
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

            if (sel.Contains(ChkBloatware))
            {
                Log("Removendo bloatware...");
                StatusBloatware.Text = "⏳";
                int count = await Task.Run(() => BloatwareRemover.Remove());
                totalSteps++;
                SetStatus(StatusBloatware, "✅", true);
                Log($"✅ Bloatware: {count} apps processados");
                StepDone(ChkBloatware);
            }

            if (sel.Contains(ChkShaderCache))
            {
                Log("Limpando cache de shaders...");
                StatusShaderCache.Text = "⏳";
                long bytes = await Task.Run(() => ShaderCacheService.Clean());
                totalFreed += bytes; totalSteps++;
                SetStatus(StatusShaderCache, "✅", true);
                Log($"✅ Cache de shaders: {FormatBytes(bytes)} liberados");
                StepDone(ChkShaderCache);
            }

            if (sel.Contains(ChkGameMode))
            {
                Log("Ativando Modo de Jogo do Windows...");
                StatusGameMode.Text = "⏳";
                bool ok = await Task.Run(() => GameModeService.Enable());
                totalSteps++;
                SetStatus(StatusGameMode, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Modo de Jogo ativado" : "⚠️ Modo de Jogo: falhou");
                StepDone(ChkGameMode);
            }

            if (sel.Contains(ChkGamePriority))
            {
                Log("Aplicando prioridade máxima para jogos...");
                StatusGamePriority.Text = "⏳";
                bool ok = await Task.Run(() => GamePriorityService.Apply());
                totalSteps++;
                SetStatus(StatusGamePriority, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Prioridade de jogos: CPU/GPU em modo High" : "⚠️ Prioridade de jogos: requer admin");
                StepDone(ChkGamePriority);
            }

            if (sel.Contains(ChkGameNetwork))
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

            if (sel.Contains(ChkPowerThrottling))
            {
                Log("Desativando Power Throttling...");
                StatusPowerThrottling.Text = "⏳";
                bool ok = await Task.Run(() => PowerThrottlingService.Disable());
                totalSteps++;
                SetStatus(StatusPowerThrottling, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Power Throttling desativado" : "⚠️ Power Throttling: requer admin");
                StepDone(ChkPowerThrottling);
            }

            if (sel.Contains(ChkFullscreenOpt))
            {
                Log("Desativando Otimizações de Tela Cheia...");
                StatusFullscreenOpt.Text = "⏳";
                bool ok = await Task.Run(() => FullscreenOptimizationsService.Disable());
                totalSteps++;
                SetStatus(StatusFullscreenOpt, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Otimizações de Tela Cheia desativadas" : "⚠️ Tela Cheia: falhou");
                StepDone(ChkFullscreenOpt);
            }

            if (sel.Contains(ChkMousePrecision))
            {
                Log("Desativando precisão aprimorada do ponteiro...");
                StatusMousePrecision.Text = "⏳";
                bool ok = await Task.Run(() => MousePrecisionService.Disable());
                totalSteps++;
                SetStatus(StatusMousePrecision, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Aceleração do mouse desativada" : "⚠️ Precisão do ponteiro: falhou");
                StepDone(ChkMousePrecision);
                RefreshMouseMenuCard();
            }

            if (sel.Contains(ChkCoreIsolation))
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

            if (sel.Contains(ChkBootProcessors))
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



            if (sel.Contains(ChkExpertMsi))
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
                // Recolhe a barra (deixá-la visível em 100% sugeria conclusão total
                // mesmo quando a run parou no meio por erro).
                Progress.Visibility = Visibility.Collapsed;
                TxtProgress.Visibility = Visibility.Collapsed;
                BtnRun.Content = originalRunContent;
                BtnRun.IsEnabled = true;
                _isRunning = false;
                RefreshMemoryCard();
            }
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
                "Esta opção marcará TODAS as 33 otimizações sem exceção, incluindo:\n\n" +
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
    }
}
