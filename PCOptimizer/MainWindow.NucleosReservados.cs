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
    // Parte da janela principal: Game Boost por núcleos reservados (CPU sem híbrido).
    public partial class MainWindow
    {
        // ── Game Boost por núcleos reservados ─────────────────────────────────

        private void InitCoreReserveCard()
        {
            ChkCoreReserveAuto.IsChecked = SettingsService.Current.CoreReserveAuto;
            ChkCoreReserveAuto.Checked   += (_, _) => SaveCoreReserveAuto();
            ChkCoreReserveAuto.Unchecked += (_, _) => SaveCoreReserveAuto();
            CoreReserveService.StatusChanged += () => Dispatcher.BeginInvoke(new Action(RefreshCoreReserveCard));
            RefreshCoreReserveCard();
        }

        private void SaveCoreReserveAuto()
        {
            SettingsService.Current.CoreReserveAuto = ChkCoreReserveAuto.IsChecked == true;
            SettingsService.Save();
            // O detector de jogo só roda se algum recurso precisar dele.
            if (SettingsService.Current.CoreReserveAuto) GameAwarenessService.Start();
        }

        private void RefreshCoreReserveCard()
        {
            try
            {
                bool supported = CoreReserveService.IsSupported;
                TxtCoreReserveStatus.Text = CoreReserveService.StatusText();
                BtnCoreReserve.IsEnabled = supported;
                ChkCoreReserveAuto.IsEnabled = supported;
                BtnCoreReserve.Content = CoreReserveService.IsActive ? "Desativar" : "Ativar agora";
                BtnCoreReserve.Style = (Style)FindResource(CoreReserveService.IsActive ? "PrimaryButtonStyle" : "ActionButtonStyle");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "RefreshCoreReserveCard");
                TxtCoreReserveStatus.Text = "Não consegui ler os núcleos do processador";
            }
        }

        private async void BtnCoreReserve_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning) return;
            BtnCoreReserve.IsEnabled = false;
            try
            {
                if (CoreReserveService.IsActive)
                {
                    TxtCoreReserveStatus.Text = await Task.Run(() => CoreReserveService.ReleaseAll("botão manual"));
                    return;
                }

                if (!SettingsService.Current.CoreReserveWarningShown)
                {
                    var r = MessageBox.Show(
                        "🚀 Game Boost — núcleos reservados\n\n" +
                        $"Enquanto o jogo roda, os outros programas (navegador, Discord…) ficam nos " +
                        $"{CoreReservePolicy.BackgroundCores} últimos núcleos do processador e o jogo fica " +
                        "com o resto livre — como jogar sem mais nada aberto.\n\n" +
                        "Eles ficam mais lentos nesse período. Tudo volta ao normal quando o jogo fecha " +
                        "ou você clica em Desativar.\n\n" +
                        "O app NÃO altera o processo do jogo, então não interfere com anticheat.\n\nContinuar?",
                        "PC Optimizer", MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (r != MessageBoxResult.Yes) return;
                    SettingsService.Current.CoreReserveWarningShown = true;
                    SettingsService.Save();
                }

                for (int i = 5; i > 0; i--)
                {
                    TxtCoreReserveStatus.Text = $"Clique na janela do jogo… {i}";
                    await Task.Delay(1000);
                }

                var target = GameTargetService.FromForegroundWindow();
                if (target == null)
                {
                    TxtCoreReserveStatus.Text = "Não consegui identificar o jogo — tente de novo";
                    return;
                }
                TxtCoreReserveStatus.Text = await Task.Run(() => CoreReserveService.ApplyTo(target, manual: true));
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "BtnCoreReserve_Click");
                TxtCoreReserveStatus.Text = "Erro: " + ex.Message;
            }
            finally { RefreshCoreReserveCard(); }
        }
    }
}
