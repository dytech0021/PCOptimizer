using System.Diagnostics;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Repara arquivos de sistema corrompidos com DISM e SFC. Pode demorar varios minutos.
    /// </summary>
    public static class SystemRepairService
    {
        public static bool Repair()
        {
            bool dism = Run("DISM.exe", "/Online /Cleanup-Image /RestoreHealth");
            bool sfc = Run("sfc.exe", "/scannow");
            return dism || sfc;
        }

        private static bool Run(string file, string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(600000); // ate 10 minutos
                return p?.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
