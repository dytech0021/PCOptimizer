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
        public MainWindow()
        {
            InitializeComponent();
            Loaded += (_, _) => ((App)Application.Current).InitHotkey(this);
        }

        private void Log(string message)
        {
            TxtLog.Text += $"\n[{DateTime.Now:HH:mm:ss}] {message}";
        }

        private string FormatBytes(long bytes)
        {
            if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F2} GB";
            if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F2} MB";
            if (bytes >= 1024) return $"{bytes / 1024.0:F2} KB";
            return $"{bytes} bytes";
        }

        private async void BtnOptimizeAll_Click(object sender, RoutedEventArgs e)
        {
            BtnOptimizeAll.IsEnabled = false;
            Progress.Visibility = Visibility.Visible;
            Progress.IsIndeterminate = true;
            TxtLog.Text = "Iniciando otimização completa...";

            // 1. Limpar temporários
            Log("Limpando arquivos temporários...");
            var (tempFiles, tempBytes) = await Task.Run(() => TempCleaner.Clean());
            Log($"✅ Temporários: {tempFiles} arquivos removidos ({FormatBytes(tempBytes)} liberados)");

            // 2. Limpar disco
            Log("Limpando disco...");
            var (diskFiles, diskBytes) = await Task.Run(() => DiskCleaner.Clean());
            Log($"✅ Disco: {diskFiles} arquivos removidos ({FormatBytes(diskBytes)} liberados)");

            // 3. Inicialização - abre janela para o usuário escolher
            Log("Abrindo gerenciador de inicialização...");
            var startupWindow = new StartupWindow { Owner = this };
            if (startupWindow.ShowDialog() == true)
            {
                Log($"✅ Inicialização: {startupWindow.ChangesApplied} alterações aplicadas");
            }
            else
            {
                Log("⏭️ Inicialização: ignorado pelo usuário");
            }

            // 4. Serviços
            Log("Otimizando serviços do Windows...");
            int services = await Task.Run(() => ServiceOptimizer.Optimize());
            Log($"✅ Serviços: {services} serviços otimizados");

            // 5. Rede
            Log("Otimizando rede...");
            int netSteps = await Task.Run(() => NetworkOptimizer.Optimize());
            Log($"✅ Rede: {netSteps} otimizações aplicadas");

            // 6. Registro
            Log("Aplicando tweaks no registro...");
            int regTweaks = await Task.Run(() => RegistryOptimizer.Optimize());
            Log($"✅ Registro: {regTweaks} tweaks aplicados");

            long totalFreed = tempBytes + diskBytes;
            Log($"\n🎉 Otimização concluída! Total liberado: {FormatBytes(totalFreed)}");

            Progress.IsIndeterminate = false;
            Progress.Value = 100;
            BtnOptimizeAll.IsEnabled = true;

            MessageBox.Show(
                $"Otimização concluída!\n\n" +
                $"📁 Arquivos removidos: {tempFiles + diskFiles}\n" +
                $"💾 Espaço liberado: {FormatBytes(totalFreed)}\n" +
                $"🛡️ Serviços otimizados: {services}\n" +
                $"🌐 Otimizações de rede: {netSteps}\n" +
                $"🔧 Tweaks de registro: {regTweaks}",
                "PC Optimizer", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void Card_TempCleaner(object sender, MouseButtonEventArgs e)
        {
            TxtLog.Text = "Limpando arquivos temporários...";
            var (files, bytes) = await Task.Run(() => TempCleaner.Clean());
            Log($"✅ {files} arquivos removidos ({FormatBytes(bytes)} liberados)");
        }

        private async void Card_DiskCleaner(object sender, MouseButtonEventArgs e)
        {
            TxtLog.Text = "Limpando disco...";
            var (files, bytes) = await Task.Run(() => DiskCleaner.Clean());
            Log($"✅ {files} arquivos removidos ({FormatBytes(bytes)} liberados)");
        }

        private void Card_Startup(object sender, MouseButtonEventArgs e)
        {
            var startupWindow = new StartupWindow { Owner = this };
            if (startupWindow.ShowDialog() == true)
            {
                Log($"✅ Inicialização: {startupWindow.ChangesApplied} alterações aplicadas");
            }
        }

        private async void Card_Services(object sender, MouseButtonEventArgs e)
        {
            TxtLog.Text = "Otimizando serviços...";
            int count = await Task.Run(() => ServiceOptimizer.Optimize());
            Log($"✅ {count} serviços otimizados");
        }

        private async void Card_Network(object sender, MouseButtonEventArgs e)
        {
            TxtLog.Text = "Otimizando rede...";
            int steps = await Task.Run(() => NetworkOptimizer.Optimize());
            Log($"✅ {steps} otimizações de rede aplicadas");
        }

        private async void Card_Registry(object sender, MouseButtonEventArgs e)
        {
            TxtLog.Text = "Aplicando tweaks no registro...";
            int tweaks = await Task.Run(() => RegistryOptimizer.Optimize());
            Log($"✅ {tweaks} tweaks aplicados");
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
