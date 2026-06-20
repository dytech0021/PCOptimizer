using System.Collections.Generic;
using System.Linq;
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
            _selected         = adapter;
            TxtAdapterName.Text = adapter.Description;
            TxtMac.Text         = adapter.MacFormatted;
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

            bool ok = await Task.Run(() => WakeOnLanService.EnableWoL(_selected));

            BtnEnable.IsEnabled = true;
            RefreshStatus();

            if (ok)
                ShowMsg("✅ WoL ativado no Windows! Agora configure o BIOS (veja acima) e " +
                        "instale o app no celular com o MAC acima.", true);
            else
                ShowMsg("✅ Configuração aplicada. Caso não funcione, ative manualmente: " +
                        "Gerenciador de Dispositivos → sua placa → Gerenciamento de Energia → " +
                        "☑ Permitir que este dispositivo ative o computador.", true);
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

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
