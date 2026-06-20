using System.Windows;
using System.Windows.Media;
using PCOptimizer.Services;

namespace PCOptimizer.Views
{
    public partial class AutologonWindow : Window
    {
        public AutologonWindow()
        {
            InitializeComponent();
            TxtUsername.Text = WindowsAutologonService.GetConfiguredUser();
            RefreshStatus();
        }

        // Atualiza apenas o selo/botão de estado. NÃO mexe na mensagem (TxtMsg),
        // para não apagar um aviso de sucesso/erro recém-exibido.
        private void RefreshStatus()
        {
            bool enabled = WindowsAutologonService.IsEnabled();
            if (enabled)
            {
                StatusBadge.Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x4E, 0x2D));
                TxtStatusBadge.Text = "✅ Ativo";
                TxtStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(0xA6, 0xE3, 0xA1));
                BtnDisable.Visibility = Visibility.Visible;
                BtnEnable.Content = "🔓 Atualizar senha";
            }
            else
            {
                StatusBadge.Background = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x45));
                TxtStatusBadge.Text = "🔒 Desativado";
                TxtStatusBadge.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0xAA));
                BtnDisable.Visibility = Visibility.Collapsed;
                BtnEnable.Content = "🔓 Ativar auto-login";
            }
        }

        private void ShowMsg(string text, bool? success)
        {
            TxtMsg.Text = text;
            TxtMsg.Foreground = new SolidColorBrush(success switch
            {
                true  => Color.FromRgb(0xA6, 0xE3, 0xA1),
                false => Color.FromRgb(0xF3, 0x8B, 0xA8),
                null  => Color.FromRgb(0xF9, 0xE2, 0xAF),
            });
        }

        private async void BtnEnable_Click(object sender, RoutedEventArgs e)
        {
            string username = TxtUsername.Text.Trim();
            string password = TxtPassword.Password;

            if (string.IsNullOrEmpty(username))
            {
                ShowMsg("⚠ Preencha o nome de usuário (e-mail da conta Microsoft).", false);
                return;
            }

            BtnEnable.IsEnabled = false;
            ShowMsg("Aplicando...", null);

            // Validação é só informativa — NÃO bloqueia. Conta Microsoft
            // normalmente não valida offline, então gravamos de qualquer forma.
            // Roda fora da thread de UI (LogonUser pode demorar alguns instantes).
            var (verified, ok) = await System.Threading.Tasks.Task.Run(() =>
            {
                bool v = WindowsAutologonService.ValidateCredentials(username, password);
                bool o = WindowsAutologonService.Enable(username, password);
                return (v, o);
            });

            BtnEnable.IsEnabled = true;

            if (!ok)
            {
                ShowMsg("⚠ Não foi possível gravar. Feche e abra o app como Administrador " +
                        "(clique direito → Executar como administrador) e tente de novo.", false);
                return;
            }

            TxtPassword.Clear();
            RefreshStatus(); // atualiza o selo ANTES de mostrar a mensagem

            if (verified)
            {
                ShowMsg("✅ Tudo pronto! Senha verificada e gravada no cofre do Windows. " +
                        "Reinicie o PC: ele deve entrar direto na área de trabalho, sem pedir senha.", true);
            }
            else
            {
                ShowMsg("✅ Auto-login ativado e senha gravada no cofre do Windows (LSA). " +
                        "Não deu para verificar a senha aqui — isso é normal em conta Microsoft e " +
                        "não quer dizer que está errada. Reinicie para testar. Se pedir senha, " +
                        "reabra aqui e confira: Usuário = e-mail completo, Senha = a da conta " +
                        "Microsoft (NÃO o PIN).", true);
            }
        }

        private void BtnDisable_Click(object sender, RoutedEventArgs e)
        {
            bool ok = WindowsAutologonService.Disable();
            RefreshStatus();
            if (ok)
                ShowMsg("🔒 Auto-login desativado. A senha será pedida novamente ao iniciar o Windows.", true);
            else
                ShowMsg("⚠ Não foi possível desativar. O app precisa ser executado como Administrador.", false);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
