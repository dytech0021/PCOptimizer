using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using PCOptimizer.Services;

namespace PCOptimizer.Views
{
    public partial class WakeOnLanWindow : Window
    {
        private List<AdapterInfo> _adapters = new();
        private AdapterInfo? _selected;

        public WakeOnLanWindow()
        {
            InitializeComponent();
            LoadAdapters();
        }

        private void LoadAdapters()
        {
            _adapters = WakeOnLanService.GetPhysicalAdapters();

            if (_adapters.Count == 0)
            {
                TxtAdapterName.Text = "Nenhuma placa Ethernet física encontrada.";
                TxtMac.Text = "—";
                BtnCopyMac.IsEnabled = false;
                BtnEnable.IsEnabled  = false;
                SetStatusBadge(false);
                ShowMsg("⚠ Conecte o PC por cabo Ethernet e reabra esta janela.", false);
                return;
            }

            if (_adapters.Count > 1)
            {
                MultiAdapterRow.Visibility = Visibility.Visible;
                CboAdapter.ItemsSource     = _adapters.Select(a => a.Description).ToList();
                CboAdapter.SelectedIndex   = 0;
            }
            else
            {
                MultiAdapterRow.Visibility = Visibility.Collapsed;
            }

            SelectAdapter(_adapters[0]);
        }

        private void SelectAdapter(AdapterInfo adapter)
        {
            _selected           = adapter;
            TxtAdapterName.Text = adapter.Description;
            TxtMac.Text         = adapter.MacFormatted;
            TxtIp.Text          = WakeOnLanService.GetAdapterIpAddress(adapter);
            RefreshStatus();
        }

        private void RefreshStatus()
        {
            if (_selected == null) return;
            bool enabled = WakeOnLanService.IsWoLEnabled(_selected);
            SetStatusBadge(enabled);
            BtnEnable.Content = enabled ? "🔄 Reativar" : "📡 Ativar no Windows";
        }

        private void SetStatusBadge(bool enabled)
        {
            if (enabled)
            {
                StatusBadge.Background     = new SolidColorBrush(Color.FromRgb(0x06, 0x2D, 0x1A));
                TxtStatusBadge.Text        = "✅ Ativo";
                TxtStatusBadge.Foreground  = new SolidColorBrush(Color.FromRgb(0x6E, 0xE7, 0xB7));
            }
            else
            {
                StatusBadge.Background     = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x45));
                TxtStatusBadge.Text        = "⭕ Desativado";
                TxtStatusBadge.Foreground  = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0xAA));
            }
        }

        private void ShowMsg(string text, bool? success)
        {
            TxtMsg.Text       = text;
            TxtMsg.Foreground = new SolidColorBrush(success switch
            {
                true  => Color.FromRgb(0xA6, 0xE3, 0xA1),
                false => Color.FromRgb(0xF3, 0x8B, 0xA8),
                null  => Color.FromRgb(0xF9, 0xE2, 0xAF),
            });
        }

        private void CboAdapter_SelectionChanged(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            int i = CboAdapter.SelectedIndex;
            if (i >= 0 && i < _adapters.Count)
                SelectAdapter(_adapters[i]);
        }

        private async void BtnEnable_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            BtnEnable.IsEnabled = false;
            ShowMsg("Ativando...", null);

            await Task.Run(() => WakeOnLanService.EnableWoL(_selected));

            BtnEnable.IsEnabled = true;
            RefreshStatus();

            // A verdade é o registro, não o exit code: roda o diagnóstico de novo.
            bool nowEnabled = WakeOnLanService.IsWoLEnabled(_selected);
            if (nowEnabled)
            {
                ShowMsg("✅ WoL ativado! Também desliguei a Inicialização Rápida e as economias " +
                        "de energia da placa. Para sobreviver a tirar da tomada, DESATIVE 'ErP/EuP' " +
                        "no BIOS (veja o aviso acima). Depois desligue o PC normal e teste pelo celular.", true);
            }
            else
            {
                // Mostra o que o driver aceitou/recusou para identificar o motivo.
                ShowMsg("⚠ Não consegui confirmar a ativação no driver. Veja o que a placa " +
                        "respondeu (mande isto pra mim):\n\n" + WakeOnLanService.LastEnableLog, false);
            }
        }

        private void BtnCopyMac_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            try
            {
                Clipboard.SetText(_selected.MacFormatted);
                ShowMsg("📋 MAC address copiado! Cole no app do celular.", true);
            }
            catch
            {
                ShowMsg("⚠ Não foi possível copiar. MAC: " + _selected.MacFormatted, null);
            }
        }

        private async void BtnDiagnose_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            BtnDiagnose.IsEnabled = false;
            ShowMsg("Verificando...", null);

            var r = await Task.Run(() =>
            {
                bool wol        = WakeOnLanService.IsWoLEnabled(_selected);
                bool fastStartup = FastStartupService.IsEnabled();
                string ip       = WakeOnLanService.GetAdapterIpAddress(_selected);
                string[] armed  = WakeOnLanService.GetWakeArmedDevices();
                bool adapterArmed = armed.Any(d =>
                    d.Contains(_selected.Description, StringComparison.OrdinalIgnoreCase) ||
                    d.Contains(_selected.Name,        StringComparison.OrdinalIgnoreCase));
                return (wol, fastStartup, ip, armed, adapterArmed);
            });

            BtnDiagnose.IsEnabled = true;

            var sb = new StringBuilder();
            sb.AppendLine(r.wol
                ? "✅ Registro: *WakeOnMagicPacket = 1"
                : "❌ Registro: *WakeOnMagicPacket ≠ 1 → clique Ativar!");
            sb.AppendLine(r.fastStartup
                ? "❌ Inicialização Rápida ATIVA → clique Ativar para desabilitar"
                : "✅ Inicialização Rápida desativada");
            sb.AppendLine(r.adapterArmed
                ? "✅ Placa armada (powercfg wake_armed)"
                : "❌ Placa NÃO armada (powercfg) → clique Ativar!");
            if (!r.adapterArmed && r.armed.Length > 0)
                sb.AppendLine("   Armados: " + string.Join(", ", r.armed));
            else if (!r.adapterArmed && r.armed.Length == 0)
                sb.AppendLine("   Nenhum dispositivo armado (execute como admin)");
            sb.AppendLine($"IP local: {r.ip}  ← use este no app do celular");

            bool allGood = r.wol && !r.fastStartup && r.adapterArmed;
            ShowMsg(sb.ToString().TrimEnd(), allGood ? (bool?)true : false);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
