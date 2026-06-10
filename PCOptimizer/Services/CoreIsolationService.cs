using Microsoft.Win32;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Desativa o Isolamento de Núcleo / Integridade de Memória (HVCI).
    /// É a otimização com maior ganho real de FPS (5–15% em CPUs mais antigas),
    /// mas REDUZ a segurança do sistema e exige reiniciar o PC.
    /// </summary>
    public static class CoreIsolationService
    {
        public static bool Disable()
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(
                    @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
                if (key == null) return false;
                key.SetValue("Enabled", 0, RegistryValueKind.DWord);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
