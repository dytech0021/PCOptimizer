using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCOptimizer.Services;

namespace PCOptimizer.Views
{
    // Parte da janela de brilho: Modo Acesso Remoto.
    public partial class BrightnessWindow
    {
        // ── Modo Acesso Remoto: 1 tela + 1080p + sem HDR, num botão só ───────

        private void UpdateRemoteModeButton()
        {
            if (RemoteAccessService.IsActive)
            {
                // Enquanto ativo, o botão de sair NUNCA some — inclusive após
                // reiniciar o PC no modo remoto.
                RemoteRow.Visibility = Visibility.Visible;
                BtnRemoteMode.Content = "↩ Sair do Modo Acesso Remoto";
                return;
            }

            // Só aparece quando há o que preparar: mais de uma tela ativa ou
            // resolução diferente de 1080p (no PC de acesso comum, fica oculto).
            var cur = DisplayResolutionService.GetCurrent();
            bool hasWork = MonitorTopologyService.ActiveScreenCount() > 1
                        || (cur != null && (cur.Value.W != 1920 || cur.Value.H != 1080));
            RemoteRow.Visibility = hasWork ? Visibility.Visible : Visibility.Collapsed;
            if (hasWork)
                BtnRemoteMode.Content = "🖥 Modo Acesso Remoto (1 tela · 1080p · sem HDR)";
        }

        private void BtnRemoteTune_Click(object sender, RoutedEventArgs e)
        {
            var win = new RemoteTuneWindow { Owner = this };
            win.ShowDialog();
            UpdateRemoteModeButton();
        }

        private async void BtnRemoteMode_Click(object sender, RoutedEventArgs e)
        {
            BtnRemoteMode.IsEnabled = false;
            try
            {
                if (!RemoteAccessService.IsActive)
                {
                    var c = MessageBox.Show(
                        "Ativar o Modo Acesso Remoto?\n\n" +
                        "Em todos os casos: fica só a tela principal, resolução " +
                        "1920×1080 (16:9) e as cores automáticas do Windows são " +
                        "desligadas (é o que deixa a imagem remota saturada).\n\n" +
                        "SIM = também desliga o HDR (religa na saída)\n" +
                        "NÃO = mantém o HDR ligado — escolha esta se a imagem no " +
                        "AnyDesk ficar saturada ou escura com o HDR desligado\n" +
                        "CANCELAR = não fazer nada\n\n" +
                        "Tudo é revertido por este mesmo botão. Emergência: Win+P.",
                        "PC Optimizer", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                    if (c == MessageBoxResult.Cancel) return;

                    TxtStatus.Text = "Ativando modo acesso remoto...";
                    TxtStatus.Text = await RemoteAccessService.EnterAsync(
                        keepHdr: c == MessageBoxResult.No);
                }
                else
                {
                    TxtStatus.Text = "Restaurando a configuração normal...";
                    TxtStatus.Text = await RemoteAccessService.ExitAsync();
                }

                // Dá um instante para o vídeo assentar e re-enumera — painel e
                // overlays passam a refletir a configuração real.
                await Task.Delay(1500);
                await ReloadMonitorsAsync();
            }
            finally
            {
                BtnRemoteMode.IsEnabled = true;
                UpdateRemoteModeButton();
            }
        }
    }
}
