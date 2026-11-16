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
    // Parte da janela principal: card "Idioma do teclado".
    public partial class MainWindow
    {
        // ── Idioma do teclado ─────────────────────────────────────────────────

        private bool _kbdLoaded, _kbdBusy;

        /// <summary>Abre o app na aba Sistema (pedido da bandeja: "Adicionar teclado…").</summary>
        public void ShowKeyboardSection()
        {
            OptList.SelectedItem = TabSistema;
            EnsureKeyboardsLoaded();
        }

        private void EnsureKeyboardsLoaded()
        {
            if (_kbdLoaded || _kbdBusy) return;
            _kbdLoaded = true;
            _ = ReloadKeyboardsAsync();
        }

        private async Task ReloadKeyboardsAsync()
        {
            TxtKbdStatus.Text = "Lendo os teclados do Windows…";
            await Task.Run(KeyboardLayoutService.Refresh);
            TxtKbdStatus.Text = "";
        }

        private void RefreshKeyboardCard()
        {
            try
            {
                var installed = KeyboardLayoutService.Installed;
                string? current = KeyboardLayoutService.CurrentTip();
                var active = installed.FirstOrDefault(e => KeyboardLayoutPolicy.SameTip(e.Tip, current));
                TxtKbdCurrent.Text = active != null ? "em uso: " + active.DisplayName : "";

                PnlKbdLayouts.Children.Clear();
                bool canRemove = installed.Count > 1;
                foreach (var entry in installed)
                {
                    bool isCurrent = ReferenceEquals(entry, active);
                    var row = new System.Windows.Controls.StackPanel
                    {
                        Orientation = System.Windows.Controls.Orientation.Horizontal,
                        Margin = new Thickness(0, 0, 10, 8)
                    };
                    var btn = new System.Windows.Controls.Button
                    {
                        Content = entry.DisplayName,
                        Style = (Style)FindResource(isCurrent ? "PrimaryButtonStyle" : "ActionButtonStyle"),
                        MinWidth = 0,
                        Padding = new Thickness(14, 0, 14, 0),
                        ToolTip = $"Usar este teclado ({entry.Tip})",
                        IsEnabled = !_kbdBusy,
                    };
                    btn.Click += (_, _) => SwitchKeyboard(entry);
                    row.Children.Add(btn);

                    var remove = new System.Windows.Controls.Button
                    {
                        Content = "✕",
                        Style = (Style)FindResource("ActionButtonStyle"),
                        MinWidth = 0,
                        Width = 36,
                        Margin = new Thickness(4, 0, 0, 0),
                        ToolTip = canRemove ? "Remover este teclado do Windows" : "É preciso manter pelo menos um teclado",
                        IsEnabled = canRemove && !_kbdBusy,
                    };
                    remove.Click += async (_, _) => await RemoveKeyboardAsync(entry);
                    row.Children.Add(remove);
                    PnlKbdLayouts.Children.Add(row);
                }

                var options = KeyboardLayoutPolicy.AvailableToAdd(installed);
                CmbKbdAdd.ItemsSource = options;
                CmbKbdAdd.DisplayMemberPath = nameof(KeyboardOption.Label);
                if (options.Count > 0 && CmbKbdAdd.SelectedIndex < 0) CmbKbdAdd.SelectedIndex = 0;
                BtnKbdAdd.IsEnabled = options.Count > 0 && !_kbdBusy;
            }
            catch (Exception ex) { Logger.Error(ex, "RefreshKeyboardCard"); }
        }

        private void SwitchKeyboard(KeyboardEntry entry)
        {
            bool ok = KeyboardLayoutService.Switch(entry);
            TxtKbdStatus.Text = ok ? $"Teclado: {entry.DisplayName}" : "Não consegui trocar o teclado — veja o log";
            RefreshKeyboardCard();
        }

        private async void BtnKbdAdd_Click(object sender, RoutedEventArgs e)
        {
            if (_kbdBusy || CmbKbdAdd.SelectedItem is not KeyboardOption option) return;
            _kbdBusy = true;
            RefreshKeyboardCard();
            TxtKbdStatus.Text = $"Adicionando {option.Label}…";
            try
            {
                string result = await Task.Run(() => KeyboardLayoutService.Add(option));
                TxtKbdStatus.Text = result;
                Log("⌨️ " + result);
            }
            finally
            {
                _kbdBusy = false;
                RefreshKeyboardCard();
            }
        }

        private async Task RemoveKeyboardAsync(KeyboardEntry entry)
        {
            if (_kbdBusy) return;
            var answer = MessageBox.Show($"Remover o teclado \"{entry.DisplayName}\" do Windows?\n\n" +
                                         "Dá para adicionar de novo depois, aqui mesmo.",
                                         "PC Optimizer", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            _kbdBusy = true;
            RefreshKeyboardCard();
            TxtKbdStatus.Text = $"Removendo {entry.DisplayName}…";
            try
            {
                string result = await Task.Run(() => KeyboardLayoutService.Remove(entry));
                TxtKbdStatus.Text = result;
                Log("⌨️ " + result);
            }
            finally
            {
                _kbdBusy = false;
                RefreshKeyboardCard();
            }
        }
    }
}
