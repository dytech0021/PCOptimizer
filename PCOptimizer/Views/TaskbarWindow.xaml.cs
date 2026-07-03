using System;
using System.Windows;
using System.Windows.Input;
using PCOptimizer.Services;

namespace PCOptimizer.Views
{
    public partial class TaskbarWindow : Window
    {
        // Evita disparar as ações enquanto a janela ainda está sendo montada.
        // Começa TRUE: no XAML compilado os eventos podem disparar durante o
        // próprio InitializeComponent, antes do construtor setar a flag.
        private bool _initializing = true;

        public TaskbarWindow()
        {
            InitializeComponent();

            _initializing = true;
            var s = SettingsService.Current;

            // O slider opera em unidades de alpha (0-255); o rótulo mostra %.
            // Evita a conversão assimétrica que fazia o valor salvo "derrapar".
            int alpha = Math.Clamp(s.TaskbarTintAlpha, 0, 255);
            SldTint.Value = alpha;
            TxtTintValue.Text = $"{PercentOf(alpha)}%";

            var mode = s.TaskbarTransparencyEnabled
                ? TaskbarTransparencyService.ParseMode(s.TaskbarMode)
                : TaskbarMode.Off;

            (mode switch
            {
                TaskbarMode.Transparent => RbTransparent,
                TaskbarMode.Blur        => RbBlur,
                TaskbarMode.Acrylic     => RbAcrylic,
                TaskbarMode.WholeBar    => RbWholeBar,
                _                       => RbOff
            }).IsChecked = true;

            _initializing = false;

            // No Windows 11 o efeito pode não pegar de primeira — mostra o aviso e
            // o atalho para o TranslucentTB oficial como plano B.
            if (TaskbarTransparencyService.IsWindows11)
                Win11Warn.Visibility = Visibility.Visible;

            UpdateVariationLabel();
            UpdateMsg();
        }

        private void UpdateVariationLabel()
        {
            var vars = TaskbarTransparencyService.Variations;
            int i = Math.Clamp(SettingsService.Current.TaskbarVariation, 0, vars.Length - 1);
            TxtVariation.Text = $"Variação {vars[i].Label}";
        }

        private void BtnVariation_Click(object sender, RoutedEventArgs e)
        {
            var s = SettingsService.Current;
            s.TaskbarVariation = (s.TaskbarVariation + 1) % TaskbarTransparencyService.Variations.Length;
            SettingsService.Save();
            UpdateVariationLabel();

            // Reaplica na hora com a nova variação (se algum estilo estiver ativo).
            var mode = SelectedMode;
            if (mode != TaskbarMode.Off)
            {
                TaskbarTransparencyService.Update(mode, SelectedAlpha);
                TxtTbMsg.Text = "Variação aplicada — olhe a barra agora. Se não mudou/ficou preta, teste a próxima.";
            }
            else
            {
                TxtTbMsg.Text = "Variação salva. Escolha um estilo acima para ver o efeito.";
            }
        }

        private TaskbarMode SelectedMode =>
            RbTransparent.IsChecked == true ? TaskbarMode.Transparent :
            RbBlur.IsChecked        == true ? TaskbarMode.Blur :
            RbAcrylic.IsChecked     == true ? TaskbarMode.Acrylic :
            RbWholeBar.IsChecked    == true ? TaskbarMode.WholeBar :
                                              TaskbarMode.Off;

        private int SelectedAlpha => (int)Math.Round(SldTint.Value);

        private static int PercentOf(int alpha) => (int)Math.Round(alpha / 255.0 * 100);

        private void Mode_Checked(object sender, RoutedEventArgs e)
        {
            if (!_initializing) ApplyAndSave();
        }

        private void SldTint_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtTintValue != null) TxtTintValue.Text = $"{PercentOf((int)Math.Round(e.NewValue))}%";
            if (!_initializing) ApplyAndSave();
        }

        private void ApplyAndSave()
        {
            var mode  = SelectedMode;
            int alpha = SelectedAlpha;

            // Ao ligar o modo barra-inteira com o slider quase em zero, sobe para
            // um valor com efeito bem visível — senão parece que "não fez nada".
            if (mode == TaskbarMode.WholeBar && alpha < 40) alpha = 100;

            TaskbarTransparencyService.Update(mode, alpha); // Off → reverte para o padrão

            // O serviço pode elevar o alpha (piso do acrílico). Usa o valor EFETIVO
            // para que slider, rótulo e configuração reflitam o que está na tela.
            int effective = TaskbarTransparencyService.TintAlpha;
            if (effective != alpha)
            {
                _initializing = true;
                SldTint.Value     = effective;
                TxtTintValue.Text = $"{PercentOf(effective)}%";
                _initializing = false;
            }

            var s = SettingsService.Current;
            s.TaskbarTransparencyEnabled = mode != TaskbarMode.Off;
            s.TaskbarMode      = mode.ToString();
            s.TaskbarTintAlpha = effective;
            SettingsService.Save();

            UpdateMsg();
        }

        private void UpdateMsg()
        {
            TxtTbMsg.Text = SelectedMode == TaskbarMode.Off
                ? "A barra está com a aparência padrão do Windows."
                : "✅ Aplicado. Mantenha o PC Optimizer aberto (pode minimizar para a bandeja) " +
                  "para o efeito continuar.";
        }

        private void BtnDiag_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(TaskbarTransparencyService.Diagnose(),
                "Diagnóstico — Barra de Tarefas", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnTtb_Click(object sender, RoutedEventArgs e)
        {
            bool ok = TaskbarTransparencyService.OpenTranslucentTB();
            TxtTbMsg.Text = ok
                ? "Abrindo a loja para instalar o TranslucentTB. Depois de instalar, abra-o — ele " +
                  "deixa a barra transparente de forma confiável no Windows 11."
                : "Não foi possível abrir a loja. Procure por \"TranslucentTB\" na Microsoft Store.";
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
