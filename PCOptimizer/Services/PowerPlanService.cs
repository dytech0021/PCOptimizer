namespace PCOptimizer.Services
{
    public static class PowerPlanService
    {
        // GUID do plano oculto "Desempenho Máximo" (Ultimate Performance)
        private const string Ultimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
        // GUID fixo para a nossa cópia do Ultimate (idempotente entre execuções)
        private const string UltimateCopy = "11111111-eeee-4444-aaaa-999999999999";
        // GUID do plano "Alto Desempenho" do Windows (fallback)
        private const string HighPerf = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

        public static bool Apply()
        {
            try
            {
                // 1º: ativa o Ultimate direto, se a edição do Windows o expõe
                if (ProcessRunner.Run("powercfg.exe", $"-setactive {Ultimate}", 15000))
                    return true;

                // 2º: duplica o Ultimate para um GUID fixo nosso e ativa a cópia
                ProcessRunner.Run("powercfg.exe", $"-duplicatescheme {Ultimate} {UltimateCopy}", 15000);
                if (ProcessRunner.Run("powercfg.exe", $"-setactive {UltimateCopy}", 15000))
                    return true;

                // 3º: fallback — Alto Desempenho clássico
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
