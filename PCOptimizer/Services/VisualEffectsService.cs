using Microsoft.Win32;

namespace PCOptimizer.Services
{
    public static class VisualEffectsService
    {
        public static bool OptimizeForPerformance()
        {
            try
            {
                // Define "Ajustar para melhor desempenho"
                using (var key = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects"))
                {
                    key?.SetValue("VisualFXSetting", 2, RegistryValueKind.DWord); // 2 = melhor desempenho
                }

                // Desativa animacoes de minimizar/maximizar
                using (var anim = Registry.CurrentUser.CreateSubKey(
                    @"Control Panel\Desktop\WindowMetrics"))
                {
                    anim?.SetValue("MinAnimate", "0", RegistryValueKind.String);
                }

                // Desativa transparencia
                using (var personalize = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    personalize?.SetValue("EnableTransparency", 0, RegistryValueKind.DWord);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
