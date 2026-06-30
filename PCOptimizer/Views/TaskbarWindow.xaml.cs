using System;
using System.Windows;
using System.Windows.Input;
using PCOptimizer.Services;

namespace PCOptimizer.Views
{
    public partial class TaskbarWindow : Window
    {
        // Evita disparar as ações enquanto a janela ainda está sendo montada.
        private bool _initializing;

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
                _                       => RbOff
            }).IsChecked = true;

            _initializing = false;
            UpdateMsg();
        }

        private TaskbarMode SelectedMode =>
            RbTransparent.IsChecked == true ? TaskbarMode.Transparent :
            RbBlur.IsChecked        == true ? TaskbarMode.Blur :
            RbAcrylic.IsChecked     == true ? TaskbarMode.Acrylic :
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

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}
