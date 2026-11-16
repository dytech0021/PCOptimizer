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
    // Parte da janela de brilho: modo 16:9 com barras pretas em monitor ultrawide.
    public partial class BrightnessWindow
    {
        /// <summary>
        /// Modo 16:9 com barras pretas para jogos competitivos (CS, R6…) em
        /// monitores ultrawide: troca a resolução para o melhor modo 16:9 e pede
        /// ao monitor, por DDC/CI, para NÃO esticar a imagem — o que sobra das
        /// laterais fica preto. Reversível pelo mesmo botão.
        /// </summary>
        /// <summary>
        /// Acha a chave salva desse monitor. Além da correspondência exata, aceita
        /// a mesma base antes do "#": o sufixo de desempate de monitores iguais
        /// mudou entre versões, e sem isso o estado salvo ficava órfão — o botão
        /// tentava aplicar de novo em vez de reverter.
        /// </summary>
        private static string? FindGameArKey(string hardwareId)
        {
            var d = SettingsService.Current.GameArPrevMode;
            if (string.IsNullOrEmpty(hardwareId)) return null;
            if (d.ContainsKey(hardwareId)) return hardwareId;

            string baseId = hardwareId.Split('#')[0];
            foreach (var k in d.Keys)
                if (k.Split('#')[0].Equals(baseId, StringComparison.OrdinalIgnoreCase))
                    return k;

            // Última tentativa: o discriminador dentro do id mudou de formato
            // entre versões, então nem o prefixo antes do "#" bate. Compara pela
            // base (modelo) e só aceita se UMA chave salva corresponder — com dois
            // monitores iguais, um reivindicaria o estado do outro.
            string wanted = MonitorIdentity.SavedStateBase(hardwareId);
            if (wanted.Length == 0) return null;
            string? only = null;
            foreach (var k in d.Keys)
                if (MonitorIdentity.SavedStateBase(k) == wanted)
                {
                    if (only != null) return null;   // ambíguo: melhor não adivinhar
                    only = k;
                }
            return only;
        }

        private Button MakeGameArButton(MonitorControl mc, GameArAction action)
        {
            var btn = new Button
            {
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 6, 0, 2),
                Padding = new Thickness(12, 9, 12, 9),
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ToolTip = "Joga em 16:9 com barras pretas nas laterais (CS, R6…)"
            };

            ApplyGameArButtonStyle(btn, GameArPolicy.IsRevert(action));

            btn.Click += async (_, _) =>
            {
                btn.IsEnabled = false;
                try
                {
                    // Trocar de resolução faz o Windows reaplicar a configuração
                    // daquele modo e pode derrubar o HDR. Anota o que estava ligado
                    // para devolver no fim — o 16:9 é sobre proporção, não sobre cor.
                    var hdrWasOn = HdrService.SnapshotHdrOnTargets();
                    RemoteAccessService.SuppressAutoFixFor(15);

                    var s = SettingsService.Current;
                    string? savedKey = FindGameArKey(mc.HardwareId);

                    // Reavalia pela MESMA regra que montou o botão, para o rótulo
                    // e a ação nunca divergirem — a tela pode ter mudado desde que
                    // a janela abriu.
                    var decision = DisplayResolutionService.DecideGameAr(
                        mc.GdiDevice, savedKey != null);

                    // Ultrawide já em 16:9 e sem registro salvo: o estado se perdeu
                    // (o formato do id mudou entre versões). Aplicar de novo
                    // gravaria o próprio 16:9 como "resolução anterior" e prenderia
                    // o monitor nele — então a saída é voltar para a nativa.
                    if (decision == GameArAction.OfferRevertToNative)
                    {
                        var nat0 = DisplayResolutionService.GetNative(mc.GdiDevice);
                        if (nat0 == null)
                        {
                            TxtStatus.Text = "Não consegui ler a resolução nativa";
                            return;
                        }
                        TxtStatus.Text = "Voltando ao ultrawide...";
                        bool okNat = await Task.Run(() => DisplayResolutionService.SetFor(
                            mc.GdiDevice, nat0.Value.W, nat0.Value.H));
                        await Task.Run(() => MonitorService.SetAspectScaling(mc.Index, false));
                        TxtStatus.Text = okNat
                            ? "Resolução ultrawide restaurada"
                            : "⚠ Não consegui restaurar — use Configurações → Vídeo";
                        ApplyGameArButtonStyle(btn, false);
                    }
                    else if (decision == GameArAction.OfferApply)
                    {
                        var target = DisplayResolutionService.FindBest169(mc.GdiDevice);
                        if (target == null)
                        {
                            TxtStatus.Text = "Este monitor não oferece um modo 16:9";
                            return;
                        }
                        var cur = DisplayResolutionService.GetCurrentFor(mc.GdiDevice);
                        if (cur == null) { TxtStatus.Text = "Não consegui ler a resolução"; return; }

                        TxtStatus.Text = $"Aplicando {target.Value.W}×{target.Value.H}...";
                        bool ok = await Task.Run(() => DisplayResolutionService.SetFor(
                            mc.GdiDevice, target.Value.W, target.Value.H, cur.Value.Hz));
                        if (!ok) { TxtStatus.Text = "O monitor não aceitou o modo 16:9"; return; }

                        s.GameArPrevMode[mc.HardwareId] = $"{cur.Value.W}x{cur.Value.H}x{cur.Value.Hz}";
                        SettingsService.Save();

                        // Pede ao monitor para preservar a proporção (barras pretas).
                        bool ddc = await Task.Run(() => MonitorService.SetAspectScaling(mc.Index, true));
                        TxtStatus.Text = ddc
                            ? $"16:9 ({target.Value.W}×{target.Value.H}) com barras pretas"
                            : $"16:9 aplicado. Se esticar, ajuste a proporção no menu do " +
                              "monitor ou no painel da placa de vídeo";
                        ApplyGameArButtonStyle(btn, true);
                    }
                    else if (savedKey != null)
                    {
                        string prev = s.GameArPrevMode[savedKey];
                        var p = prev.Split('x');
                        bool ok = false;
                        if (p.Length == 3 && int.TryParse(p[0], out int w) &&
                            int.TryParse(p[1], out int h) && int.TryParse(p[2], out int hz))
                        {
                            TxtStatus.Text = "Voltando ao ultrawide...";
                            ok = await Task.Run(() => DisplayResolutionService.SetFor(mc.GdiDevice, w, h, hz));
                            await Task.Run(() => MonitorService.SetAspectScaling(mc.Index, false));
                        }
                        else
                        {
                            // Registro corrompido: volta para a resolução NATIVA
                            var nat = DisplayResolutionService.GetNative(mc.GdiDevice);
                            if (nat != null)
                                ok = await Task.Run(() => DisplayResolutionService.SetFor(
                                    mc.GdiDevice, nat.Value.W, nat.Value.H));
                        }

                        s.GameArPrevMode.Remove(savedKey);
                        SettingsService.Save();
                        TxtStatus.Text = ok
                            ? "Resolução ultrawide restaurada"
                            : "⚠ Não consegui restaurar — use Configurações → Vídeo";
                        ApplyGameArButtonStyle(btn, false);
                    }
                    else
                    {
                        // Não dá para ler o painel (device GDI inválido) — o guard
                        // do DisplayResolutionService já registrou o motivo no log.
                        TxtStatus.Text = "Não consegui ler este monitor";
                        return;
                    }

                    await Task.Delay(1500);

                    // Devolve o HDR que a troca de resolução tenha derrubado
                    int back = await Task.Run(() => HdrService.RestoreHdrOnTargets(hdrWasOn));
                    if (back > 0) await Task.Delay(1200);

                    await ReloadMonitorsAsync();
                }
                finally { btn.IsEnabled = true; }
            };
            return btn;
        }

        private static void ApplyGameArButtonStyle(Button btn, bool active)
        {
            btn.Content = active ? "🎮 Voltar ao ultrawide (21:9)"
                                 : "🎮 Jogar em 16:9 (barras pretas)";
            if (active)
            {
                // Ativo em laranja forte: é o estado em que o usuário precisa
                // achar o botão rápido para voltar ao normal.
                btn.Background  = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
                btn.Foreground  = new SolidColorBrush(Color.FromRgb(0x1A, 0x12, 0x06));
                btn.BorderBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
            }
            else
            {
                btn.Background  = new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B));
                btn.Foreground  = new SolidColorBrush(Color.FromRgb(0x93, 0xC5, 0xFD));
                btn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x4A, 0x6B));
            }
        }
    }
}
