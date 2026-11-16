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
    // Parte da janela principal: ferramentas avulsas (captura, autologon, Wake-on-LAN, varredura, tela, reparo, barra de tarefas, brilho).
    public partial class MainWindow
    {
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

        private async void BtnMaximizeDisplay_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning) return;

            BtnMaximizeDisplay.IsEnabled = false;
            TxtMaximizeStatus.Text = "Aplicando o melhor modo em cada monitor...";
            try
            {
                // Trocar de modo pode derrubar o HDR e dispara o mesmo evento de
                // uma reconexão remota — preserva o HDR e cala a correção automática.
                var hdrWasOn = HdrService.SnapshotHdrOnTargets();
                RemoteAccessService.SuppressAutoFixFor(20);

                var results = await Task.Run(DisplayResolutionService.MaximizeAll);
                if (results.Count == 0)
                {
                    TxtMaximizeStatus.Text = "Não consegui ler os monitores";
                    return;
                }

                foreach (var r in results)
                    Log(r.Ok
                        ? $"✅ {r.Device}: {r.W}×{r.H} @ {r.Hz}Hz"
                        : $"⚠️ {r.Device}: o Windows recusou o modo máximo");

                int ok = results.Count(r => r.Ok);
                var first = results[0];
                TxtMaximizeStatus.Text = ok == results.Count
                    ? (results.Count == 1
                        ? $"✅ {first.W}×{first.H} @ {first.Hz}Hz"
                        : $"✅ {ok} monitores no máximo — veja o log")
                    : $"⚠️ {ok} de {results.Count} monitores aplicados — veja o log";

                // Devolve o HDR que a troca de modo tenha derrubado
                await Task.Delay(1200);
                await Task.Run(() => HdrService.RestoreHdrOnTargets(hdrWasOn));

                // A troca de modo muda o layout: refaz o painel de brilho.
                MonitorService.MarkDisplaysChanged();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "BtnMaximizeDisplay_Click");
                TxtMaximizeStatus.Text = "Erro: " + ex.Message;
            }
            finally { BtnMaximizeDisplay.IsEnabled = true; }
        }

        private async void BtnDeepRepair_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning) return;

            var confirm = MessageBox.Show(
                "🔧 Reparo Profundo do Sistema\n\n" +
                "Vai executar, em sequência:\n" +
                "• DISM /RestoreHealth — repara a imagem do Windows\n" +
                "• SFC /scannow — verifica os arquivos do sistema\n" +
                "• CHKDSK /r — verifica o disco (AGENDADO para o próximo reinício)\n\n" +
                "DISM + SFC podem levar de 15 minutos a mais de 1 hora. Não desligue o PC. " +
                "A verificação do disco (CHKDSK) roda na próxima vez que você reiniciar.\n\n" +
                "Deseja continuar?",
                "PC Optimizer — Reparo Profundo",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            _isRunning = true;
            OptList.IsEnabled = false;
            ChkSelectAll.IsEnabled = false;
            BtnDeepRepair.IsEnabled = false;
            BtnRun.IsEnabled = false;
            Progress.Visibility = Visibility.Visible;
            Progress.IsIndeterminate = true;
            TxtProgress.Visibility = Visibility.Visible;
            TxtProgress.Text = "Reparo profundo em andamento — pode demorar bastante...";
            TxtLog.Text = "Iniciando reparo profundo do sistema...";

            try
            {
                var progress = new Progress<string>(step => Log("⏳ " + step));
                var r = await Task.Run(() => DeepRepairService.Run(
                    s => ((IProgress<string>)progress).Report(s)));

                Log(r.Dism ? "✅ DISM: imagem do Windows verificada/reparada"
                           : "⚠️ DISM: falhou ou requer admin");
                Log(r.Sfc ? "✅ SFC: arquivos do sistema verificados"
                          : "⚠️ SFC: falhou ou requer admin");
                Log(r.ChkdskScheduled
                    ? "✅ CHKDSK: agendado para o próximo reinício"
                    : "⚠️ CHKDSK: não foi possível agendar");

                MessageBox.Show(
                    $"Reparo profundo concluído.\n\n" +
                    $"{(r.Dism ? "✅" : "⚠️")} DISM (imagem do Windows)\n" +
                    $"{(r.Sfc ? "✅" : "⚠️")} SFC (arquivos do sistema)\n" +
                    $"{(r.ChkdskScheduled ? "✅" : "⚠️")} CHKDSK (verificação do disco)\n\n" +
                    (r.ChkdskScheduled
                        ? "A verificação do disco vai rodar na PRÓXIMA vez que você reiniciar o PC."
                        : "A verificação do disco não pôde ser agendada."),
                    "PC Optimizer", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log($"❌ Erro no reparo profundo: {ex.Message}");
                MessageBox.Show("Ocorreu um erro durante o reparo:\n\n" + ex.Message,
                    "PC Optimizer", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                Progress.Visibility = Visibility.Collapsed;
                TxtProgress.Visibility = Visibility.Collapsed;
                OptList.IsEnabled = true;
                ChkSelectAll.IsEnabled = true;
                BtnDeepRepair.IsEnabled = true;
                BtnRun.IsEnabled = true;
                _isRunning = false;
                RefreshMemoryCard();
            }
        }

        private void BtnTaskbar_Click(object sender, RoutedEventArgs e)
        {
            var win = new Views.TaskbarWindow { Owner = this };
            win.ShowDialog();
            RefreshTaskbarStatus();
        }

        private void RefreshTaskbarStatus()
        {
            if (TxtTaskbarStatus == null) return;
            TxtTaskbarStatus.Text = TaskbarTransparencyService.IsActive
                ? "✅ Ativa — " + TaskbarTransparencyService.StatusText()
                : "Desativada — clique para deixar a barra translúcida";
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

        private void BtnBrightnessHeader_Click(object sender, RoutedEventArgs e)
        {
            ((App)Application.Current).ToggleBrightnessWindow();
        }
    }
}
