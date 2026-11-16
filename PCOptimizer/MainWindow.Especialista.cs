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
    // Parte da janela principal: Modo Expert (GPU e ferramenta de undervolt da CPU).
    public partial class MainWindow
    {
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
            // Compartilha a barra de progresso com a run — não misturar os dois.
            if (_isRunning || _cpuUvTool == null || !ConfirmExpertOnce()) return;

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
    }
}
