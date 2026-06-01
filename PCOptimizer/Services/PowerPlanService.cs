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
                ProcessRunner.Run("powercfg.exe", $"-duplicatescheme {HighPerf}", 15000);
                return ProcessRunner.Run("powercfg.exe", $"-setactive {HighPerf}", 15000);
            }
            catch
            {
                return false;
            }
        }
    }
}
