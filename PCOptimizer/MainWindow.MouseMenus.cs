using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using PCOptimizer.Services;
using PCOptimizer.Views;

namespace PCOptimizer
{
    // Parte da janela principal: card "Mouse e menus".
    public partial class MainWindow
    {
        // ── Mouse e menus ─────────────────────────────────────────────────────

        private void RefreshMouseMenuCard()
        {
            bool? prec = MousePrecisionService.IsEnabled();
            TxtMousePrecisionState.Text = prec switch
            {
                true  => "Ligada agora (aceleração do Windows)",
                false => "Desligada agora (movimento 1:1)",
                _     => "Não deu para ler o estado"
            };
            BtnMousePrecisionToggle.Content = prec == true ? "Desativar" : "Ativar";

            int ms = MenuDelayService.GetDelayMs();
            TxtMenuDelayState.Text = ms switch
            {
                < 0  => "Não deu para ler o estado",
                0    => "Desligado agora — os menus abrem na hora",
                _    => $"Ligado agora ({ms} ms)"
            };
            BtnMenuDelayToggle.Content = ms > 0 ? "Desativar" : "Ativar";
        }

        private async void BtnMousePrecisionToggle_Click(object sender, RoutedEventArgs e)
        {
            bool enable = MousePrecisionService.IsEnabled() != true;
            BtnMousePrecisionToggle.IsEnabled = false;
            try
            {
                bool ok = await Task.Run(() => MousePrecisionService.Set(enable));
                Log(ok ? (enable ? "✅ Precisão do mouse ativada" : "✅ Precisão do mouse desativada")
                       : "⚠️ Precisão do mouse: não consegui alterar");
            }
            finally { BtnMousePrecisionToggle.IsEnabled = true; RefreshMouseMenuCard(); }
        }

        private async void BtnMenuDelayToggle_Click(object sender, RoutedEventArgs e)
        {
            bool enable = MenuDelayService.GetDelayMs() <= 0;
            BtnMenuDelayToggle.IsEnabled = false;
            try
            {
                bool ok = await Task.Run(() => MenuDelayService.SetEnabled(enable));
                if (ok)
                {
                    // Religou: a otimização "Registro" não zera mais. Desligou: volta a valer.
                    SettingsService.Current.KeepMenuShowDelay = enable;
                    SettingsService.Save();
                }
                Log(ok ? (enable ? "✅ Atraso dos menus ligado (400 ms)" : "✅ Atraso dos menus desligado (instantâneo)")
                       : "⚠️ Atraso dos menus: não consegui alterar");
            }
            finally { BtnMenuDelayToggle.IsEnabled = true; RefreshMouseMenuCard(); }
        }
    }
}
