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
    // Parte da janela de brilho: ligar e desligar saídas de vídeo.
    public partial class BrightnessWindow
    {
        private void Displays_Toggle(object sender, MouseButtonEventArgs e)
        {
            bool show = DisplaysPanel.Visibility != Visibility.Visible;
            DisplaysPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            TxtDisplaysArrow.Text = show ? "▾" : "▸";
            if (show) BuildDisplayToggles();
        }

        /// <summary>
        /// Uma linha por saída de vídeo, com botão de ligar/desligar. Usa a lista
        /// que enxerga também as telas desanexadas — senão não haveria como
        /// reativar o que foi desligado.
        /// </summary>
        private void BuildDisplayToggles()
        {
            PnlDisplayToggles.Children.Clear();
            var devices = MonitorEnableService.ListAll();

            foreach (var d in devices)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var name = new TextBlock
                {
                    Text = d.Description + (d.Primary ? "  (principal)" : ""),
                    FontSize = 11, FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 175
                };
                name.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
                var sub = new TextBlock
                {
                    Text = d.Attached ? "Ativa" : "Desativada",
                    FontSize = 9, Opacity = 0.7
                };
                sub.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                label.Children.Add(name);
                label.Children.Add(sub);
                row.Children.Add(label);

                var btn = new Button
                {
                    Content = d.Attached ? "Desativar" : "Ativar",
                    FontSize = 10, Padding = new Thickness(12, 5, 12, 5),
                    Cursor = Cursors.Hand, BorderThickness = new Thickness(1),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(btn, 1);
                if (d.Attached)
                {
                    btn.Background  = Brushes.Transparent;
                    btn.Foreground  = new SolidColorBrush(Color.FromRgb(0xF3, 0x8B, 0xA8));
                    btn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x5A, 0x2A, 0x35));
                }
                else
                {
                    btn.SetResourceReference(Control.BackgroundProperty, "AccentBoxBg");
                    btn.SetResourceReference(Control.ForegroundProperty, "AccentBoxFg");
                    btn.SetResourceReference(Control.BorderBrushProperty, "AccentBoxBorder");
                }

                string dev = d.Device;
                bool attached = d.Attached;
                btn.Click += async (_, _) =>
                {
                    btn.IsEnabled = false;
                    TxtStatus.Text = attached ? "Desativando monitor..." : "Reativando monitor...";

                    // Anexar/desanexar mexe no vídeo: preserva o HDR das outras
                    // telas e cala a correção automática de cor.
                    var hdrWasOn = HdrService.SnapshotHdrOnTargets();
                    RemoteAccessService.SuppressAutoFixFor(20);

                    var r = await Task.Run(() => attached
                        ? MonitorEnableService.Disable(dev)
                        : MonitorEnableService.Enable(dev));
                    TxtStatus.Text = r.Message;

                    await Task.Delay(1500);      // o vídeo precisa assentar
                    await Task.Run(() => HdrService.RestoreHdrOnTargets(hdrWasOn));
                    BuildDisplayToggles();
                    await ReloadMonitorsAsync();
                    btn.IsEnabled = true;
                };
                row.Children.Add(btn);
                PnlDisplayToggles.Children.Add(row);
            }
        }
    }
}
