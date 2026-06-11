namespace PCOptimizer.Services
{
    /// <summary>
    /// Modo Expert: trava a CPU no clock máximo desativando os estados ociosos
    /// (C-States) e fixando o throttle em 100%. Latência mínima, mas consumo e
    /// temperatura ficam permanentemente altos — pensado para desktops gamer.
    /// Reversível trocando o plano de energia ou refazendo com IDLEDISABLE 0.
    /// </summary>
    public static class CpuMaxPerformanceService
    {
        public static bool Apply()
        {
            int ok = 0;
            // IDLEDISABLE 1 = núcleos nunca entram em estado ocioso (C-States off)
            if (ProcessRunner.Run("powercfg",
                "/setacvalueindex scheme_current sub_processor IDLEDISABLE 1", 10000)) ok++;
            // Boost agressivo + clock mínimo e máximo em 100%
            if (ProcessRunner.Run("powercfg",
                "/setacvalueindex scheme_current sub_processor PERFBOOSTMODE 2", 10000)) ok++;
            if (ProcessRunner.Run("powercfg",
                "/setacvalueindex scheme_current sub_processor PROCTHROTTLEMIN 100", 10000)) ok++;
            if (ProcessRunner.Run("powercfg",
                "/setacvalueindex scheme_current sub_processor PROCTHROTTLEMAX 100", 10000)) ok++;

            ProcessRunner.Run("powercfg", "/setactive scheme_current", 10000);
            return ok >= 3;
        }
    }
}
