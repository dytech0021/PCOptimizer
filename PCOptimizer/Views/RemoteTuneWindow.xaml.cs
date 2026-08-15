using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using PCOptimizer.Services;

namespace PCOptimizer.Views
{
    /// <summary>
    /// Ajustes finos do Modo Acesso Remoto: cada etapa é um interruptor separado,
    /// aplicado na hora. Serve para ISOLAR qual delas afeta a imagem capturada
    /// pelo programa de acesso remoto (AnyDesk etc.).
    /// </summary>
    public partial class RemoteTuneWindow : Window
    {
        // Evita que o Refresh (que escreve nos checkboxes) dispare os handlers
        private bool _sync;

        public RemoteTuneWindow()
        {
            InitializeComponent();
            Loaded += (_, _) => Refresh();
        }

        private void Refresh()
        {
            _sync = true;
            var st = RemoteAccessService.ReadState();
            ChkSingle.IsChecked = st.Single;
            ChkRes.IsChecked    = st.Res1080;
            ChkHdrOff.IsChecked = !st.HdrOn;   // caixa = "desligar HDR"
            ChkAcmOff.IsChecked = !st.AcmOn;   // caixa = "desligar ACM"

            var cur = DisplayResolutionService.GetCurrent();
            if (cur != null)
                TxtResNote.Text = $"Agora: {cur.Value.W}×{cur.Value.H} @ {cur.Value.Hz}Hz";

            string mode = RemoteAccessService.DescribeColorMode();
            if (mode.Length > 0) TxtColorMode.Text = mode;

            ChkAutoFix.IsChecked = SettingsService.Current.AutoFixColorOnDisplayChange;
            _sync = false;
        }

        private void ChkAutoFix_Click(object sender, RoutedEventArgs e)
        {
            if (_sync) return;
            bool on = ChkAutoFix.IsChecked == true;
            SettingsService.Current.AutoFixColorOnDisplayChange = on;
            SettingsService.Save();
            RemoteAccessService.SetAutoFixHook(on);

            TxtTuneStatus.Text = on
                ? "Ligado — ao reconectar o acesso remoto, a cor se corrige sozinha"
                : "Desligado — use o botão acima quando precisar";
        }

        private void BtnDiagMonitors_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string txt = MonitorService.Diagnose();
                Clipboard.SetText(txt);
                TxtTuneStatus.Text = "Diagnóstico copiado — cole no chat do suporte";
            }
            catch (Exception ex) { TxtTuneStatus.Text = "Erro ao copiar: " + ex.Message; }
        }

        private async void BtnFixColor_Click(object sender, RoutedEventArgs e)
        {
            IsEnabled = false;
            TxtTuneStatus.Text = "Aplicando o ciclo de HDR — a tela vai piscar...";
            TxtTuneStatus.Text = await RemoteAccessService.FixColorNowAsync();
            Refresh();
            IsEnabled = true;
        }

        private async Task RunAsync(string busy, System.Func<Task<bool>> action, string doneMsg)
        {
            IsEnabled = false;
            TxtTuneStatus.Text = busy;
            bool ok = await action();
            await Task.Delay(1200); // o vídeo precisa assentar antes de reler
            TxtTuneStatus.Text = ok ? doneMsg : "⚠ O Windows não aceitou essa mudança";
            Refresh();
            IsEnabled = true;
        }

        private async void ChkSingle_Click(object sender, RoutedEventArgs e)
        {
            if (_sync) return;
            bool want = ChkSingle.IsChecked == true;
            await RunAsync(want ? "Deixando só a tela principal..." : "Reativando todas as telas...",
                () => RemoteAccessService.SetSingleScreenAsync(want),
                want ? "Somente a tela principal" : "Todas as telas ativas");
        }

        private async void ChkRes_Click(object sender, RoutedEventArgs e)
        {
            if (_sync) return;
            bool want = ChkRes.IsChecked == true;
            await RunAsync(want ? "Mudando para 1920×1080..." : "Restaurando a resolução nativa...",
                () => RemoteAccessService.Set1080Async(want),
                want ? "Resolução 1920×1080" : "Resolução nativa restaurada");
        }

        private async void ChkHdrOff_Click(object sender, RoutedEventArgs e)
        {
            if (_sync) return;
            bool off = ChkHdrOff.IsChecked == true;
            await RunAsync(off ? "Desligando o HDR..." : "Ligando o HDR...",
                () => RemoteAccessService.SetHdrAllAsync(!off),
                off ? "HDR desligado" : "HDR ligado");
        }

        private async void ChkAcmOff_Click(object sender, RoutedEventArgs e)
        {
            if (_sync) return;
            bool off = ChkAcmOff.IsChecked == true;
            await RunAsync(off ? "Desligando as cores automáticas..." : "Ligando as cores automáticas...",
                () => Task.FromResult(RemoteAccessService.SetAcmAll(!off)),
                off ? "Cores automáticas desligadas" : "Cores automáticas ligadas");
        }

        private async void BtnRevertAll_Click(object sender, RoutedEventArgs e)
        {
            IsEnabled = false;
            TxtTuneStatus.Text = "Revertendo tudo...";
            TxtTuneStatus.Text = await RemoteAccessService.ExitAsync();
            Refresh();
            IsEnabled = true;
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
