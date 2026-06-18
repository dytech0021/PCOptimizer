using System.Windows;
using System.Windows.Media;
using PCOptimizer.Services;

namespace PCOptimizer.Views
{
    public partial class AutologonWindow : Window
    {
        // Quando a validação falha mas pode ser conta Microsoft, aguardamos
        // confirmação explícita do usuário antes de gravar mesmo assim.
        private bool _awaitingConfirm;

        public AutologonWindow()
        {
            InitializeComponent();
            TxtUsername.Text = WindowsAutologonService.GetConfiguredUser();
            TxtPassword.PasswordChanged += (_, _) => _awaitingConfirm = false;
            RefreshStatus();
        }

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
            TxtMsg.Text = "";
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

        private void BtnEnable_Click(object sender, RoutedEventArgs e)
        {
            string username = TxtUsername.Text.Trim();
            string password = TxtPassword.Password;

            if (string.IsNullOrEmpty(username))
            {
                ShowMsg("⚠ Preencha o nome de usuário.", false);
                return;
            }

            // Segunda tentativa após aviso de validação: aplica direto sem revalidar.
            if (_awaitingConfirm)
            {
                _awaitingConfirm = false;
                Apply(username, password);
                return;
            }

            BtnEnable.IsEnabled = false;
            ShowMsg("Verificando credenciais...", null);

            bool valid = WindowsAutologonService.ValidateCredentials(username, password);

            if (!valid)
            {
                // Pode ser conta Microsoft (LogonUser não valida contas online).
                // Permite confirmar na segunda tentativa.
                _awaitingConfirm = true;
                BtnEnable.IsEnabled = true;
                ShowMsg("Não foi possível verificar a senha — comum em contas Microsoft. " +
                        "Se a senha estiver correta, clique em \"Ativar auto-login\" novamente para confirmar.", null);
                return;
            }

            Apply(username, password);
        }

        private void Apply(string username, string password)
        {
            bool ok = WindowsAutologonService.Enable(username, password);
            BtnEnable.IsEnabled = true;

            if (ok)
            {
                TxtPassword.Clear();
                ShowMsg("✅ Auto-login ativado e a trava do Windows 11 (login só por Hello) foi " +
                        "desligada. Reinicie o PC para testar: ele deve ir direto à área de trabalho. " +
                        "Se ainda pedir senha, confira se o Usuário é o e-mail e a senha é a da conta " +
                        "Microsoft (não o PIN).", true);
                RefreshStatus();
            }
            else
            {
                ShowMsg("⚠ Não foi possível aplicar. Certifique-se de que o app está sendo " +
                        "executado como Administrador.", false);
            }
        }

        private void BtnDisable_Click(object sender, RoutedEventArgs e)
        {
            bool ok = WindowsAutologonService.Disable();
            if (ok)
            {
                ShowMsg("🔒 Auto-login desativado. A senha será pedida novamente ao iniciar o Windows.", true);
                RefreshStatus();
            }
            else
            {
                ShowMsg("⚠ Não foi possível desativar. O app precisa de privilégios de Administrador.", false);
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
