namespace PCOptimizer.Services
{
    /// <summary>
    /// Desativa a hibernacao e remove o arquivo hiberfil.sys, liberando vários GB.
    /// </summary>
    public static class HibernationService
    {
        public static bool Disable()
        {
            return ProcessRunner.Run("powercfg.exe", "-h off", 15000);
        }
    }
}
