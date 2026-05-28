using Microsoft.Win32;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Desativa a Inicializacao Rapida (Fast Startup). Evita o "desligamento falso"
    /// que mantem o kernel hibernado e pode causar travamentos e drivers desatualizados.
    /// </summary>
    public static class FastStartupService
    {
        public static bool Disable()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", writable: true);
                if (key == null) return false;

                key.SetValue("HiberbootEnabled", 0, RegistryValueKind.DWord);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
