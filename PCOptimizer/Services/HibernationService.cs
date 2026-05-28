using System.Diagnostics;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Desativa a hibernacao e remove o arquivo hiberfil.sys, liberando vários GB.
    /// </summary>
    public static class HibernationService
    {
        public static bool Disable()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powercfg.exe",
                    Arguments = "-h off",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(15000);
                return p?.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
