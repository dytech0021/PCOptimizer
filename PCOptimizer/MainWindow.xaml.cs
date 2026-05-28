using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using PCOptimizer.Services;
using PCOptimizer.Views;

namespace PCOptimizer
{
    public partial class MainWindow : Window
    {
        private bool _isRunning;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += (_, _) => ((App)Application.Current).InitHotkey(this);
            UpdateSelectedCount();
        }

        private void Log(string message)
        {
            TxtLog.Text += $"\n[{DateTime.Now:HH:mm:ss}] {message}";
            LogScroller.ScrollToEnd();
        }

        private string FormatBytes(long bytes)
        {
            if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F2} GB";
            if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F2} MB";
            if (bytes >= 1024) return $"{bytes / 1024.0:F2} KB";
            return $"{bytes} bytes";
        }

        private void SetStatus(System.Windows.Controls.TextBlock status, string text, bool success)
        {
            status.Text = text;
            status.Foreground = new System.Windows.Media.SolidColorBrush(
                success ? System.Windows.Media.Color.FromRgb(166, 227, 161)
                        : System.Windows.Media.Color.FromRgb(243, 139, 168));
        }

        private void UpdateSelectedCount()
        {
            int total = 9;
            int selected = 0;
            if (ChkTemp.IsChecked == true) selected++;
            if (ChkDisk.IsChecked == true) selected++;
            if (ChkRecycleBin.IsChecked == true) selected++;
            if (ChkStartup.IsChecked == true) selected++;
            if (ChkServices.IsChecked == true) selected++;
            if (ChkNetwork.IsChecked == true) selected++;
            if (ChkRegistry.IsChecked == true) selected++;
            if (ChkCortana.IsChecked == true) selected++;
            if (ChkDefrag.IsChecked == true) selected++;
            TxtSelected.Text = $"{selected} de {total} selecionados";
        }

        private void OptCheckChanged(object sender, RoutedEventArgs e)
        {
            if (TxtSelected != null) UpdateSelectedCount();
        }

        private void ChkSelectAll_Checked(object sender, RoutedEventArgs e) => SetAllChecks(true);
        private void ChkSelectAll_Unchecked(object sender, RoutedEventArgs e) => SetAllChecks(false);

        private void SetAllChecks(bool value)
        {
            ChkTemp.IsChecked = value;
            ChkDisk.IsChecked = value;
            ChkRecycleBin.IsChecked = value;
            ChkStartup.IsChecked = value;
            ChkServices.IsChecked = value;
            ChkNetwork.IsChecked = value;
            ChkRegistry.IsChecked = value;
            ChkCortana.IsChecked = value;
            ChkDefrag.IsChecked = value;
            UpdateSelectedCount();
        }

        private async void BtnRun_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning) return;
            _isRunning = true;
            BtnRun.IsEnabled = false;
            BtnRun.Content = "⏳ Executando...";
            Progress.Visibility = Visibility.Visible;
            Progress.IsIndeterminate = true;
            TxtLog.Text = "Iniciando otimizações selecionadas...";

            long totalFreed = 0;
            int totalSteps = 0;

            if (ChkTemp.IsChecked == true)
            {
                Log("Limpando arquivos temporários...");
                StatusTemp.Text = "⏳";
                var (files, bytes) = await Task.Run(() => TempCleaner.Clean());
                totalFreed += bytes; totalSteps++;
                SetStatus(StatusTemp, "✅", true);
                Log($"✅ Temporários: {files} arquivos ({FormatBytes(bytes)})");
            }

            if (ChkDisk.IsChecked == true)
            {
                Log("Limpando disco...");
                StatusDisk.Text = "⏳";
                var (files, bytes) = await Task.Run(() => DiskCleaner.Clean());
                totalFreed += bytes; totalSteps++;
                SetStatus(StatusDisk, "✅", true);
                Log($"✅ Disco: {files} arquivos ({FormatBytes(bytes)})");
            }

            if (ChkRecycleBin.IsChecked == true)
            {
                Log("Esvaziando lixeira...");
                StatusRecycleBin.Text = "⏳";
                bool ok = await Task.Run(() => RecycleBinCleaner.Clean());
                totalSteps++;
                SetStatus(StatusRecycleBin, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Lixeira esvaziada" : "⚠️ Lixeira já estava vazia");
            }

            if (ChkStartup.IsChecked == true)
            {
                Log("Abrindo gerenciador de inicialização...");
                StatusStartup.Text = "⏳";
                var startupWindow = new StartupWindow { Owner = this };
                if (startupWindow.ShowDialog() == true)
                {
                    totalSteps++;
                    SetStatus(StatusStartup, "✅", true);
                    Log($"✅ Inicialização: {startupWindow.ChangesApplied} alterações");
                }
                else
                {
                    SetStatus(StatusStartup, "⏭️", true);
                    Log("⏭️ Inicialização: ignorado");
                }
            }

            if (ChkServices.IsChecked == true)
            {
                Log("Otimizando serviços...");
                StatusServices.Text = "⏳";
                int count = await Task.Run(() => ServiceOptimizer.Optimize());
                totalSteps++;
                SetStatus(StatusServices, "✅", true);
                Log($"✅ Serviços: {count} otimizados");
            }

            if (ChkNetwork.IsChecked == true)
            {
                Log("Otimizando rede...");
                StatusNetwork.Text = "⏳";
                int steps = await Task.Run(() => NetworkOptimizer.Optimize());
                totalSteps++;
                SetStatus(StatusNetwork, "✅", true);
                Log($"✅ Rede: {steps} otimizações");
            }

            if (ChkRegistry.IsChecked == true)
            {
                Log("Aplicando tweaks no registro...");
                StatusRegistry.Text = "⏳";
                int tweaks = await Task.Run(() => RegistryOptimizer.Optimize());
                totalSteps++;
                SetStatus(StatusRegistry, "✅", true);
                Log($"✅ Registro: {tweaks} tweaks");
            }

            if (ChkCortana.IsChecked == true)
            {
                Log("Desativando Cortana...");
                StatusCortana.Text = "⏳";
                bool ok = await Task.Run(() => CortanaDisabler.Disable());
                totalSteps++;
                SetStatus(StatusCortana, ok ? "✅" : "❌", ok);
                Log(ok ? "✅ Cortana desativada" : "❌ Erro ao desativar Cortana");
            }

            if (ChkDefrag.IsChecked == true)
            {
                Log("Desfragmentando disco C: (pode demorar)...");
                StatusDefrag.Text = "⏳";
                bool ok = await Task.Run(() => DefragService.Optimize());
                totalSteps++;
                SetStatus(StatusDefrag, ok ? "✅" : "⚠️", ok);
                Log(ok ? "✅ Disco desfragmentado" : "⚠️ Desfragmentação parcial ou SSD");
            }

            Log($"\n🎉 Concluído! {totalSteps} otimizações. Espaço liberado: {FormatBytes(totalFreed)}");

            Progress.IsIndeterminate = false;
            Progress.Value = 100;
            BtnRun.Content = "⚡ Executar Otimizações";
            BtnRun.IsEnabled = true;
            _isRunning = false;

            MessageBox.Show(
                $"Otimização concluída!\n\n" +
                $"✅ {totalSteps} otimizações executadas\n" +
                $"💾 Espaço liberado: {FormatBytes(totalFreed)}",
                "PC Optimizer", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Card_Brightness(object sender, MouseButtonEventArgs e)
        {
            ((App)Application.Current).ToggleBrightnessWindow();
        }

        private void BtnThemeToggle_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.Toggle();
            Log($"🎨 Tema alterado para {(ThemeManager.Current == AppTheme.Dark ? "escuro" : "claro")}");
        }
    }
}
