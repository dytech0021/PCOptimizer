using Microsoft.Win32;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Desativa a coleta de dados/telemetria da Microsoft.
    /// </summary>
    public static class TelemetryService
    {
        public static bool Disable()
        {
            try
            {
                using (var policy = Registry.LocalMachine.CreateSubKey(
                    @"SOFTWARE\Policies\Microsoft\Windows\DataCollection"))
                {
                    policy?.SetValue("AllowTelemetry", 0, RegistryValueKind.DWord);
                }

                // Desativa os servicos de telemetria (Start = 4 = desabilitado)
                SetServiceDisabled("DiagTrack");
                SetServiceDisabled("dmwappushservice");

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void SetServiceDisabled(string serviceName)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{serviceName}", writable: true);
                key?.SetValue("Start", 4, RegistryValueKind.DWord);
            }
            catch { }
        }
    }
}
