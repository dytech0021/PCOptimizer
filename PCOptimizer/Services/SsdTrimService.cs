using System.Diagnostics;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Executa o TRIM (retrim) no SSD para manter a performance de escrita.
    /// </summary>
    public static class SsdTrimService
    {
        public static bool Trim(string drive = "C:")
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "defrag.exe",
                    Arguments = $"{drive} /L", // /L = retrim (TRIM)
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(120000);
                return p?.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
