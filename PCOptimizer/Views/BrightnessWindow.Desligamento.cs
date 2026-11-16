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
    // Parte da janela de brilho: timer de desligamento.
    public partial class BrightnessWindow
    {
        // ── Timer de desligamento ─────────────────────────────────────────────

        private System.Windows.Threading.DispatcherTimer? _countdownTimer;

        private void BtnTimerToggle_Click(object sender, RoutedEventArgs e)
        {
            TimerPanel.Visibility = TimerPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        }

        private void TimerPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && int.TryParse(fe.Tag?.ToString(), out int minutes))
                ScheduleShutdown(minutes);
        }

        private void TimerCustom_Click(object sender, RoutedEventArgs e)
        {
            // Limite de 30 dias: acima disso minutes*60 estoura o int no
            // ShutdownService e o shutdown.exe rejeita o /t, mas a UI mostraria
            // uma contagem regressiva de um desligamento que nunca foi agendado.
            if (int.TryParse(TxtTimerCustom.Text.Trim(), out int minutes)
                && minutes > 0 && minutes <= 43_200)
                ScheduleShutdown(minutes);
            else
                TxtStatus.Text = "Digite minutos entre 1 e 43200 (30 dias)";
        }

        private void TimerCancel_Click(object sender, RoutedEventArgs e)
        {
            if (ShutdownService.Cancel())
            {
                StopCountdown();
                TxtStatus.Text = "Desligamento cancelado";
            }
        }

        private async void ScheduleShutdown(int minutes)
        {
            // Fora da thread de UI: Schedule espera o shutdown.exe /a terminar
            // (até 5 s), e isso congelava a janela ao clicar num preset de timer.
            TxtStatus.Text = "Agendando...";
            bool ok = await Task.Run(() => ShutdownService.Schedule(minutes));
            if (ok)
            {
                BtnTimerCancel.Visibility = Visibility.Visible;
                StartCountdown();
                TxtStatus.Text = $"PC desligará em {minutes} min";
            }
            else
            {
                TxtStatus.Text = "Não foi possível agendar o desligamento";
            }
        }

        private void StartCountdown()
        {
            _countdownTimer ??= new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _countdownTimer.Tick -= Countdown_Tick;
            _countdownTimer.Tick += Countdown_Tick;
            _countdownTimer.Start();
            UpdateCountdownText();
        }

        private void StopCountdown()
        {
            _countdownTimer?.Stop();
            TxtTimerStatus.Text = "";
            BtnTimerCancel.Visibility = Visibility.Collapsed;
        }

        private void Countdown_Tick(object? sender, EventArgs e) => UpdateCountdownText();

        private void UpdateCountdownText()
        {
            if (ShutdownService.ScheduledAt is not { } at) { StopCountdown(); return; }
            var remaining = at - DateTime.Now;
            if (remaining <= TimeSpan.Zero) { TxtTimerStatus.Text = "00:00"; _countdownTimer?.Stop(); return; }
            TxtTimerStatus.Text = remaining.TotalHours >= 1
                ? $"{(int)remaining.TotalHours}:{remaining.Minutes:D2}:{remaining.Seconds:D2}"
                : $"{remaining.Minutes:D2}:{remaining.Seconds:D2}";
        }
    }
}
