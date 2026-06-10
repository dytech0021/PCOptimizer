using Microsoft.Win32;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Desativa o Power Throttling — o Windows reduz o clock de processos que
    /// julga "inativos", o que pode afetar jogos e apps em segundo plano
    /// (Discord, OBS) enquanto você joga.
    /// </summary>
    public static class PowerThrottlingService
    {
        public static bool Disable()
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling");
                if (key == null) return false;
                key.SetValue("PowerThrottlingOff", 1, RegistryValueKind.DWord);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
