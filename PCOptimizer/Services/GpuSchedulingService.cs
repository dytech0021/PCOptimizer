using Microsoft.Win32;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Ativa o Agendamento de GPU por Hardware (HAGS). Aplica apos reiniciar.
    /// </summary>
    public static class GpuSchedulingService
    {
        public static bool Enable()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", writable: true);
                if (key == null) return false;

                // HwSchMode: 2 = ativado
                key.SetValue("HwSchMode", 2, RegistryValueKind.DWord);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
