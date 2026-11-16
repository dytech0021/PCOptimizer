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
    // Parte da janela de brilho: uma linha de controles por monitor (brilho, contraste e botões).
    public partial class BrightnessWindow
    {
        private void BuildMonitorPanels(List<MonitorEntry> entries)
        {
            PnlMonitors.Children.Clear();
            _monitorControls.Clear();

            for (int i = 0; i < entries.Count; i++)
                PnlMonitors.Children.Add(CreateMonitorRow(entries[i], i > 0));
        }

        private FrameworkElement CreateMonitorRow(MonitorEntry entry, bool addSeparator)
        {
            var container = new StackPanel();

            if (addSeparator)
            {
                var sep = new Border { Height = 1, Margin = new Thickness(0, 6, 0, 8) };
                sep.SetResourceReference(Border.BackgroundProperty, "BorderColor");
                container.Children.Add(sep);
            }

            // Monitor name — editable TextBox styled as label (double-click to rename)
            string displayName = SettingsService.Current.MonitorAliases.TryGetValue(entry.HardwareId, out var alias)
                ? alias : entry.Name;

            var nameEdit = new TextBox
            {
                Text = displayName,
                FontSize = 11, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                IsReadOnly = true,
                Cursor = Cursors.Arrow,
                Padding = new Thickness(1, 0, 1, 0),
                MaxWidth = 150,
                ToolTip = "Duplo clique para renomear"
            };
            nameEdit.SetResourceReference(TextBox.ForegroundProperty, "TextPrimary");

            string hwId = entry.HardwareId;
            string originalName = entry.Name;

            nameEdit.MouseDoubleClick += (_, _) =>
            {
                nameEdit.IsReadOnly = false;
                nameEdit.Cursor = Cursors.IBeam;
                nameEdit.SetResourceReference(TextBox.BorderBrushProperty, "BorderColor");
                nameEdit.BorderThickness = new Thickness(0, 0, 0, 1);
                nameEdit.SelectAll();
                nameEdit.Focus();
            };

            void CommitRename()
            {
                string newName = nameEdit.Text.Trim();
                if (string.IsNullOrEmpty(newName)) newName = originalName;
                nameEdit.Text = newName;
                nameEdit.IsReadOnly = true;
                nameEdit.Cursor = Cursors.Arrow;
                nameEdit.BorderThickness = new Thickness(0);
                if (!string.IsNullOrEmpty(hwId))
                {
                    SettingsService.Current.MonitorAliases[hwId] = newName;
                    SettingsService.Save();
                }
            }

            void CancelRename()
            {
                nameEdit.Text = SettingsService.Current.MonitorAliases.TryGetValue(hwId, out var a)
                    ? a : originalName;
                nameEdit.IsReadOnly = true;
                nameEdit.Cursor = Cursors.Arrow;
                nameEdit.BorderThickness = new Thickness(0);
            }

            nameEdit.LostFocus  += (_, _) => CommitRename();
            nameEdit.KeyDown    += (_, ke) =>
            {
                if (ke.Key == Key.Enter)  { CommitRename(); Keyboard.ClearFocus(); ke.Handled = true; }
                if (ke.Key == Key.Escape) { CancelRename(); Keyboard.ClearFocus(); ke.Handled = true; }
            };

            var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 5) };
            var nameIcon = new TextBlock { Text = "🖥", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            nameRow.Children.Add(nameIcon);
            nameRow.Children.Add(nameEdit);
            container.Children.Add(nameRow);

            // Com HDR ativo, o monitor ignora brilho DDC/CI e gamma ramp — o controle
            // real é o "brilho do conteúdo SDR" do Windows. Lê o valor atual para o
            // slider partir do ponto certo.
            int hdrSdrPct = entry.HdrSdrBrightness;

            // Brightness slider
            var sliderB = new Slider
            {
                Minimum = 0, Maximum = 100,
                Value = hdrSdrPct >= 0 ? hdrSdrPct : entry.Brightness,
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = entry.SupportsBrightness || hdrSdrPct >= 0
            };
            var txtB = new TextBlock
            {
                Text = $"{(hdrSdrPct >= 0 ? hdrSdrPct : entry.Brightness)}%",
                FontSize = 12, FontWeight = FontWeights.Bold, Width = 38,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            txtB.SetResourceReference(TextBlock.ForegroundProperty, "ButtonPrimaryBg");
            container.Children.Add(MakeSliderRow("☀️", sliderB, txtB, new Thickness(0, 0, 0, 6)));

            if (entry.HdrEnabled)
            {
                var hdrNote = new TextBlock
                {
                    Text = "HDR ativo — o brilho ajusta o conteúdo SDR (o mesmo controle " +
                           "das Configurações do Windows; o monitor ignora o brilho comum em HDR)",
                    FontSize = 9, Opacity = 0.85, Margin = new Thickness(0, 0, 0, 4),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x89, 0xB4, 0xFA))
                };
                container.Children.Add(hdrNote);
            }

            // Contrast slider (DDC/CI only) — não existe em WMI nem no modo software
            Slider? sliderC = null;
            TextBlock? txtC = null;

            if (!entry.IsWmi && !entry.IsSoftware)
            {
                sliderC = new Slider
                {
                    Minimum = 0, Maximum = 100,
                    Value = entry.SupportsContrast ? entry.Contrast : 50,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsEnabled = entry.SupportsContrast
                };
                txtC = new TextBlock
                {
                    Text = entry.SupportsContrast ? $"{entry.Contrast}%" : "N/A",
                    FontSize = 12, FontWeight = FontWeights.Bold, Width = 38,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                txtC.SetResourceReference(TextBlock.ForegroundProperty, "ButtonPrimaryBg");
                container.Children.Add(MakeSliderRow("🌗", sliderC, txtC, new Thickness(0, 0, 0, 4)));
            }
            else if (entry.IsWmi)
            {
                var note = new TextBlock
                {
                    Text = "Painel do notebook — somente brilho",
                    FontSize = 9, Opacity = 0.65, Margin = new Thickness(0, 0, 0, 4)
                };
                note.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                container.Children.Add(note);
            }

            // Monitor sem DDC/CI nem WMI: brilho por software (escurecimento via overlay).
            if (entry.IsSoftware)
            {
                var swNote = new TextBlock
                {
                    Text = "🖌 Brilho por software (escurece a imagem) — DDC/CI indisponível. " +
                           "Para brilho real do backlight, ative \"DDC/CI\" no menu (OSD) do monitor.",
                    FontSize = 9, Opacity = 0.85, Margin = new Thickness(0, 0, 0, 4),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xF9, 0xE2, 0xAF))
                };
                container.Children.Add(swNote);
            }

            var mc = new MonitorControl
            {
                Index = entry.Index, IsWmi = entry.IsWmi,
                IsSoftware   = entry.IsSoftware,
                OverlayKey   = entry.OverlayKey,
                GdiDevice    = entry.GdiDevice,
                HardwareId   = entry.HardwareId,
                ScreenLeft   = entry.ScreenLeft,
                ScreenTop    = entry.ScreenTop,
                ScreenWidth  = entry.ScreenWidth,
                ScreenHeight = entry.ScreenHeight,
                SliderBrightness = sliderB, TxtBrightness = txtB,
                SliderContrast   = sliderC, TxtContrast   = txtC,
                SupportsHdr      = entry.SupportsHdr,
                HdrEnabled       = entry.HdrEnabled,
                HdrAdapterIdLow  = entry.HdrAdapterIdLow,
                HdrAdapterIdHigh = entry.HdrAdapterIdHigh,
                HdrTargetId      = entry.HdrTargetId
            };
            _monitorControls.Add(mc);

            if (entry.SupportsHdr)
            {
                container.Children.Add(MakeHdrButton(mc));
                // HDR do menu do monitor: só por DDC/CI (painel de notebook e
                // monitor sem DDC não têm por onde receber o comando).
                if (!entry.IsWmi && !entry.IsSoftware)
                    container.Children.Add(MakeOsdHdrButton(mc));
            }

            // Ultrawide (21:9 e mais largos): oferece o modo 16:9 com barras
            // pretas, usado em CS/R6 e afins. A regra mora em GameArPolicy — ela
            // olha a resolução NATIVA do painel, porque pela atual o botão sumia
            // assim que o 16:9 era aplicado e não dava mais para reverter.
            var gameAr = DisplayResolutionService.DecideGameAr(
                mc.GdiDevice, FindGameArKey(mc.HardwareId) != null);
            if (GameArPolicy.ShouldOffer(gameAr))
                container.Children.Add(MakeGameArButton(mc, gameAr));

            // Events — aplica o primeiro valor NA HORA; durante o arraste, envia
            // sempre o valor mais recente assim que o anterior termina (sem debounce)
            sliderB.ValueChanged += async (_, ev) =>
            {
                if (!_initialized) return;
                int val = (int)ev.NewValue;
                txtB.Text = $"{val}%";
                mc.PendingBrightness = val;
                if (mc.BrightnessBusy) return;
                mc.BrightnessBusy = true;
                try
                {
                    while (mc.PendingBrightness >= 0)
                    {
                        int v = mc.PendingBrightness;
                        mc.PendingBrightness = -1;
                        // HDR ativo: ajusta o brilho do conteúdo SDR (DDC/gamma são
                        // ignorados pelo monitor em HDR). Se a chamada falhar
                        // (Windows antigo), cai nos caminhos normais abaixo.
                        if (mc.HdrEnabled && await Task.Run(() => HdrService.SetSdrBrightness(
                                mc.HdrAdapterIdLow, mc.HdrAdapterIdHigh, mc.HdrTargetId, v)))
                            continue;
                        if (mc.IsWmi)
                            await Task.Run(() => MonitorService.SetWmiBrightness(v));
                        else if (mc.IsSoftware)
                            // Overlay roda na thread de UI (operação instantânea)
                            SoftwareBrightnessService.SetBrightness(
                                mc.OverlayKey, mc.ScreenLeft, mc.ScreenTop,
                                mc.ScreenWidth, mc.ScreenHeight, v);
                        else
                            await Task.Run(() => MonitorService.SetBrightnessForIndex(mc.Index, v));
                    }
                }
                catch (Exception ex) { TxtStatus.Text = $"Erro brilho: {ex.Message}"; }
                finally { mc.BrightnessBusy = false; }
            };

            if (sliderC != null && txtC != null)
            {
                var capturedTxtC = txtC;
                sliderC.ValueChanged += async (_, ev) =>
                {
                    if (!_initialized || !entry.SupportsContrast) return;
                    int val = (int)ev.NewValue;
                    capturedTxtC.Text = $"{val}%";
                    mc.PendingContrast = val;
                    if (mc.ContrastBusy) return;
                    mc.ContrastBusy = true;
                    try
                    {
                        while (mc.PendingContrast >= 0)
                        {
                            int v = mc.PendingContrast;
                            mc.PendingContrast = -1;
                            await Task.Run(() => MonitorService.SetContrastForIndex(mc.Index, v));
                        }
                    }
                    catch (Exception ex) { TxtStatus.Text = $"Erro contraste: {ex.Message}"; }
                    finally { mc.ContrastBusy = false; }
                };
            }

            return container;
        }

        private static Grid MakeSliderRow(string icon, Slider slider, TextBlock txtValue, Thickness margin)
        {
            var grid = new Grid { Margin = margin };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });

            var ic = new TextBlock { Text = icon, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(ic, 0);
            Grid.SetColumn(slider, 1);
            Grid.SetColumn(txtValue, 2);

            grid.Children.Add(ic);
            grid.Children.Add(slider);
            grid.Children.Add(txtValue);
            return grid;
        }
    }
}
