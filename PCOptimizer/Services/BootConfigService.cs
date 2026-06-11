using System;
using System.Diagnostics;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Define o número de processadores lógicos nas opções avançadas de boot
    /// (equivalente ao MSCONFIG → Boot → Opções Avançadas → Número de Processadores).
    /// Usa bcdedit para gravar no BCD o valor máximo disponível no sistema.
    /// </summary>
    public static class BootConfigService
    {
        public static bool SetMaxProcessors()
        {
            try
            {
                int cores = Environment.ProcessorCount;
                var psi = new ProcessStartInfo("bcdedit.exe",
                    $"/set {{current}} numproc {cores}")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                p.WaitForExit(10_000);
                return p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
