using System;
using System.Management;

namespace PCOptimizer.Services
{
    public static class PowerPlanService
    {
        // GUID do plano oculto "Desempenho Máximo" (Ultimate Performance)
        private const string Ultimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
        // GUID fixo para a nossa cópia do Ultimate (idempotente entre execuções)
        private const string UltimateCopy = "11111111-eeee-4444-aaaa-999999999999";
        // GUID fixo e universal do plano "Alto Desempenho" do Windows
        private const string HighPerf = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

        /// <summary>Nome do plano efetivamente ativado na última chamada de Apply.</summary>
        public static string LastAppliedPlan { get; private set; } = "";

        /// <summary>
        /// Ativa o plano de energia de desempenho. Por padrão usa "Alto Desempenho";
        /// em CPUs de 4 núcleos FÍSICOS ou menos usa o "Desempenho Máximo" (Ultimate),
        /// que ajuda mais em processadores fracos (mantém clocks/latência sob controle).
        /// </summary>
        public static bool Apply()
        {
            try
            {
                int cores = GetPhysicalCoreCount();
                bool preferUltimate = cores <= 4;
                Logger.Info($"PowerPlan: {cores} núcleos físicos → " +
                            (preferUltimate ? "Desempenho Máximo" : "Alto Desempenho"));

                // Escolhe o plano-alvo; se ele falhar, cai no outro como fallback.
                return preferUltimate
                    ? (ApplyUltimate() || ApplyHighPerformance())
                    : (ApplyHighPerformance() || ApplyUltimate());
            }
            catch (Exception ex) { Logger.Error(ex, "PowerPlanService.Apply"); return false; }
        }

        private static bool ApplyHighPerformance()
        {
            // "Alto Desempenho" tem GUID fixo e existe em todas as edições — o
            // -setactive funciona mesmo quando o plano está oculto na interface.
            if (ProcessRunner.Run("powercfg.exe", $"-setactive {HighPerf}", 15000))
            {
                LastAppliedPlan = "Alto Desempenho";
                return true;
            }
            // Edição rara sem o plano exposto: duplica e ativa.
            ProcessRunner.Run("powercfg.exe", $"-duplicatescheme {HighPerf}", 15000);
            if (ProcessRunner.Run("powercfg.exe", $"-setactive {HighPerf}", 15000))
            {
                LastAppliedPlan = "Alto Desempenho";
                return true;
            }
            return false;
        }

        private static bool ApplyUltimate()
        {
            // 1º: ativa o Ultimate direto, se a edição do Windows o expõe
            if (ProcessRunner.Run("powercfg.exe", $"-setactive {Ultimate}", 15000))
            {
                LastAppliedPlan = "Desempenho Máximo";
                return true;
            }
            // 2º: duplica o Ultimate para um GUID fixo nosso e ativa a cópia
            ProcessRunner.Run("powercfg.exe", $"-duplicatescheme {Ultimate} {UltimateCopy}", 15000);
            if (ProcessRunner.Run("powercfg.exe", $"-setactive {UltimateCopy}", 15000))
            {
                LastAppliedPlan = "Desempenho Máximo";
                return true;
            }
            return false;
        }

        /// <summary>
        /// Conta os núcleos FÍSICOS (soma entre sockets). Environment.ProcessorCount
        /// devolve os LÓGICOS (o dobro com Hyper-Threading), que não servem aqui.
        /// </summary>
        private static int GetPhysicalCoreCount()
        {
            try
            {
                int cores = 0;
                using var searcher = new ManagementObjectSearcher(
                    "SELECT NumberOfCores FROM Win32_Processor");
                using var results = searcher.Get();
                foreach (ManagementObject obj in results)
                    using (obj)
                        cores += Convert.ToInt32(obj["NumberOfCores"]);
                if (cores > 0) return cores;
            }
            catch (Exception ex) { Logger.Error(ex, "GetPhysicalCoreCount"); }

            // Fallback: assume Hyper-Threading e estima os físicos como metade dos
            // lógicos (mínimo 1) — evita classificar uma CPU forte como "fraca".
            return Math.Max(1, Environment.ProcessorCount / 2);
        }
    }
}
