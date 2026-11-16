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
    // Parte da janela de brilho: HDR do Windows e HDR do menu do monitor.
    public partial class BrightnessWindow
    {
        // ── HDR toggle ────────────────────────────────────────────────────────

        private Button MakeHdrButton(MonitorControl mc)
        {
            var btn = new Button
            {
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0),
                Padding = new Thickness(12, 5, 12, 5),
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ToolTip = "Alternar HDR (High Dynamic Range)"
            };
            ApplyHdrButtonStyle(btn, mc.HdrEnabled);

            btn.Click += async (_, _) =>
            {
                bool newState = !mc.HdrEnabled;
                btn.IsEnabled = false;
                bool ok;
                // HDR do menu do monitor, se o usuário ensinou: ao LIGAR vai
                // antes (monitor com o HDR do menu desligado pode recusar o sinal
                // HDR), ao DESLIGAR vai depois do Windows.
                var osd = MonitorHdrDdcService.Get(mc.HardwareId);
                bool? osdOk = null;
                try
                {
                    if (osd.Count > 0 && newState)
                    {
                        TxtStatus.Text = "Ligando o HDR do monitor...";
                        osdOk = await Task.Run(() => MonitorHdrDdcService.Apply(
                            MonitorHdrDdcService.ResolveIndex(mc.HardwareId, mc.Index), osd, true));
                        await Task.Delay(1500);
                    }

                    ok = await HdrService.SetHdrEnabledVerifiedAsync(
                        mc.HdrAdapterIdLow, mc.HdrAdapterIdHigh, mc.HdrTargetId, newState);

                    if (osd.Count > 0 && (!newState ? ok : !ok))
                    {
                        // Desligou no Windows → desliga no monitor. Ou: o Windows
                        // não ligou → devolve o monitor como estava.
                        if (!newState) TxtStatus.Text = "Desligando o HDR do monitor...";
                        await Task.Delay(1500);   // o monitor ressincroniza a imagem
                        bool r = await Task.Run(() => MonitorHdrDdcService.Apply(
                            MonitorHdrDdcService.ResolveIndex(mc.HardwareId, mc.Index), osd, false));
                        if (!newState) osdOk = r;
                    }
                }
                finally { btn.IsEnabled = true; }
                if (ok)
                {
                    mc.HdrEnabled = newState;
                    ApplyHdrButtonStyle(btn, newState);
                    TxtStatus.Text = (newState ? "HDR ativado" : "HDR desativado") + osdOk switch
                    {
                        true  => " (Windows e monitor)",
                        false => " no Windows — o monitor não aceitou o comando",
                        _     => ""
                    };

                    // O modo de controle de brilho muda junto com o HDR (SDR white
                    // level vs DDC) — espera a troca de modo assentar e reconstrói
                    // as linhas para o slider partir do valor certo.
                    _ = ReloadMonitorsAsync();
                }
                else
                {
                    TxtStatus.Text = "Não foi possível alterar o HDR";
                }
            };
            return btn;
        }

        /// <summary>
        /// Botão do HDR do MENU do monitor (OSD). Cada fabricante guarda esse HDR
        /// num código DDC/CI diferente, então o app aprende com o usuário: lê o
        /// monitor com o HDR do menu ligado, depois desligado, e guarda o que mudou.
        /// </summary>
        private Button MakeOsdHdrButton(MonitorControl mc)
        {
            var btn = new Button
            {
                FontSize = 10,
                Margin = new Thickness(0, 3, 0, 0),
                Padding = new Thickness(10, 4, 10, 4),
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            btn.SetResourceReference(Control.BackgroundProperty, "ButtonSecondaryBg");
            btn.SetResourceReference(Control.ForegroundProperty, "TextSecondary");
            btn.SetResourceReference(Control.BorderBrushProperty, "BorderStrong");

            void Refresh()
            {
                bool on = MonitorHdrDdcService.IsConfigured(mc.HardwareId);
                btn.Content = on
                    ? "✓ HDR do menu do monitor acompanha o Windows"
                    : "Desligar também o HDR do menu do monitor…";
                btn.ToolTip = on
                    ? "O botão de HDR acima liga e desliga também o HDR do próprio monitor " +
                      "(DDC/CI). Clique para refazer ou esquecer."
                    : "Ensina o app a ligar e desligar o HDR do menu (OSD) deste monitor " +
                      "junto com o HDR do Windows";
            }
            Refresh();

            btn.Click += async (_, _) =>
            {
                if (MonitorHdrDdcService.IsConfigured(mc.HardwareId))
                {
                    var r = MessageBox.Show(
                        "O HDR do menu deste monitor já acompanha o do Windows.\n\n" +
                        "SIM = refazer o aprendizado\n" +
                        "NÃO = esquecer (o botão de HDR volta a mexer só no Windows)\n" +
                        "CANCELAR = deixar como está",
                        "PC Optimizer", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                    if (r == MessageBoxResult.Cancel) return;
                    if (r == MessageBoxResult.No)
                    {
                        MonitorHdrDdcService.Forget(mc.HardwareId);
                        Refresh();
                        TxtStatus.Text = "HDR do monitor: esquecido";
                        return;
                    }
                }

                btn.IsEnabled = false;
                try { await LearnOsdHdrAsync(mc); }
                finally { btn.IsEnabled = true; Refresh(); }
            };
            return btn;
        }

        private async Task LearnOsdHdrAsync(MonitorControl mc)
        {
            if (MessageBox.Show(
                    "Vamos ensinar o app a desligar o HDR do próprio monitor.\n\n" +
                    "PASSO 1 de 2: pelos botões do monitor, abra o menu e LIGUE o HDR.\n" +
                    "Feche o menu do monitor e clique em OK.\n\n" +
                    "(O DDC/CI precisa estar ligado no menu do monitor.)",
                    "PC Optimizer — HDR do monitor", MessageBoxButton.OKCancel,
                    MessageBoxImage.Information) != MessageBoxResult.OK) return;

            TxtStatus.Text = "Lendo o monitor (HDR ligado)...";
            int idx = mc.Index;
            var (codes, on1, on2) = await Task.Run(() =>
            {
                var c = MonitorHdrDdcService.CodesFor(idx);
                return (c, MonitorHdrDdcService.Snapshot(idx, c), MonitorHdrDdcService.Snapshot(idx, c));
            });
            if (on1.Count == 0)
            {
                TxtStatus.Text = "O monitor não respondeu pelo DDC/CI";
                MessageBox.Show(
                    "O monitor não respondeu pelo DDC/CI.\n\nLigue a opção DDC/CI no menu do " +
                    "monitor e tente de novo.",
                    "PC Optimizer — HDR do monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var unstable = MonitorHdrDdcPolicy.Unstable(on1, on2);

            if (MessageBox.Show(
                    "PASSO 2 de 2: agora, pelo menu do monitor, DESLIGUE o HDR.\n" +
                    "Feche o menu do monitor e clique em OK.",
                    "PC Optimizer — HDR do monitor", MessageBoxButton.OKCancel,
                    MessageBoxImage.Information) != MessageBoxResult.OK)
            {
                TxtStatus.Text = "";
                return;
            }

            TxtStatus.Text = "Lendo o monitor (HDR desligado)...";
            var off = await Task.Run(() => MonitorHdrDdcService.Snapshot(idx, codes));
            var learned = MonitorHdrDdcPolicy.Learn(on1, off, unstable);
            if (learned.Count == 0)
            {
                TxtStatus.Text = "HDR do monitor: nada mudou pelo DDC/CI";
                MessageBox.Show(
                    "Não encontrei o HDR do menu entre os ajustes que o monitor mostra pelo " +
                    "DDC/CI — nada mudou entre ligado e desligado (ou mudou coisa demais).\n\n" +
                    "Confira se o HDR do menu realmente mudou entre os dois passos e tente de " +
                    "novo. Alguns monitores não deixam controlar o HDR pelo cabo.",
                    "PC Optimizer — HDR do monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Prova real: liga e desliga pelo app, e o usuário confirma na tela.
            TxtStatus.Text = "Testando: ligando o HDR do monitor...";
            await Task.Run(() => MonitorHdrDdcService.Apply(idx, learned, true));
            var seen = MessageBox.Show(
                "Teste: o app acabou de LIGAR o HDR do monitor pelo cabo.\n\n" +
                "O HDR do menu do monitor LIGOU? (confira no menu do monitor, se precisar)",
                "PC Optimizer — HDR do monitor", MessageBoxButton.YesNo, MessageBoxImage.Question);
            TxtStatus.Text = "Testando: desligando o HDR do monitor...";
            await Task.Run(() => MonitorHdrDdcService.Apply(idx, learned, false));

            if (seen != MessageBoxResult.Yes)
            {
                TxtStatus.Text = "HDR do monitor: não salvo";
                MessageBox.Show(
                    "Não salvei nada: o monitor informa o ajuste pelo cabo " +
                    $"({MonitorHdrDdcPolicy.Describe(learned)}), mas não obedeceu ao comando " +
                    "do app. Este monitor só deixa mudar o HDR pelo próprio menu.",
                    "PC Optimizer — HDR do monitor", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            MonitorHdrDdcService.Save(mc.HardwareId, learned);
            TxtStatus.Text = "HDR do monitor: pronto — acompanha o botão de HDR";
            MessageBox.Show(
                "Pronto! Agora, ao desligar o HDR deste monitor pelo botão de HDR do app, o HDR " +
                "do menu do monitor desliga junto (e liga junto ao ligar).\n\n" +
                $"Ajuste aprendido: {MonitorHdrDdcPolicy.Describe(learned)}",
                "PC Optimizer — HDR do monitor", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static void ApplyHdrButtonStyle(Button btn, bool enabled)
        {
            btn.Content     = enabled ? "HDR: Ligado" : "HDR: Desligado";
            if (enabled)
            {
                // Ligado segue o destaque do tema (violeta).
                btn.SetResourceReference(Control.BackgroundProperty, "AccentBoxBg");
                btn.SetResourceReference(Control.ForegroundProperty, "AccentBoxFg");
                btn.SetResourceReference(Control.BorderBrushProperty, "AccentBoxBorder");
                return;
            }
            btn.Background  = new SolidColorBrush(Color.FromRgb(0x1B, 0x3A, 0x4E));
            btn.Foreground  = new SolidColorBrush(Color.FromRgb(0x89, 0xB4, 0xFA));
            btn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x4A, 0x5E));
        }
    }
}
