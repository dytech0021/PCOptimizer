using System.Diagnostics;

namespace PCOptimizer.Services
{
    public static class DefragService
    {
        public static bool Optimize(string drive = "C:")
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "defrag.exe",
                    Arguments = $"{drive} /O",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                using var process = Process.Start(psi);
                process?.WaitForExit(300000);
                return process?.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
