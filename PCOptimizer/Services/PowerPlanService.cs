using System.Diagnostics;

namespace PCOptimizer.Services
{
    public static class PowerPlanService
    {
        // GUID do plano "Alto Desempenho" do Windows
        private const string HighPerf = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

        public static bool Apply()
        {
            try
            {
                // Garante que o plano existe (duplica se necessario) e ativa
                Run($"-duplicatescheme {HighPerf}");
                Run($"-setactive {HighPerf}");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void Run(string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powercfg.exe",
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(15000);
            }
            catch { }
        }
    }
}
