using Microsoft.Win32;

namespace PCOptimizer.Services
{
    public static class RegistryOptimizer
    {
        public static int Optimize()
        {
            int tweaks = 0;

            // Desativar animações desnecessárias
            SetValue(Registry.CurrentUser, @"Control Panel\Desktop\WindowMetrics", "MinAnimate", "0", ref tweaks);

            // Reduzir delay do menu
            SetValue(Registry.CurrentUser, @"Control Panel\Desktop", "MenuShowDelay", "0", ref tweaks);

            // Desativar transparência
            SetValue(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0, ref tweaks);

            // Otimizar prioridade de processos em primeiro plano
            SetValue(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 38, ref tweaks);

            // Desativar last access timestamp para NTFS
            SetValue(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\FileSystem", "NtfsDisableLastAccessUpdate", 1, ref tweaks);

            // Aumentar buffer de rede
            SetValue(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", "Size", 3, ref tweaks);

            return tweaks;
        }

        private static void SetValue(RegistryKey root, string keyPath, string name, object value, ref int count)
        {
            try
            {
                using var key = root.OpenSubKey(keyPath, writable: true)
                                ?? root.CreateSubKey(keyPath);
                if (key != null)
                {
                    key.SetValue(name, value);
                    count++;
                }
            }
            catch { }
        }
    }
}
