using System.Diagnostics;

namespace PCOptimizer.Services
{
    public static class ServiceOptimizer
    {
        private static readonly string[] UnnecessaryServices =
        {
            "DiagTrack",
            "dmwappushservice",
            "SysMain",
            "WSearch",
            "Fax",
            "PrintNotify",
            "RemoteRegistry",
            "lfsvc",
            "MapsBroker",
            "RetailDemo",
            "wisvc",
        };

        public static int Optimize()
        {
            int optimized = 0;

            foreach (var serviceName in UnnecessaryServices)
            {
                if (RunSc($"stop {serviceName}") && RunSc($"config {serviceName} start= disabled"))
                    optimized++;
            }

            return optimized;
        }

        private static bool RunSc(string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = arguments,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                using var process = Process.Start(psi);
                process?.WaitForExit(10000);
                return process?.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
