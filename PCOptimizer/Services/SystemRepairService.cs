namespace PCOptimizer.Services
{
    /// <summary>
    /// Repara arquivos de sistema corrompidos com DISM e SFC. Pode demorar varios minutos.
    /// </summary>
    public static class SystemRepairService
    {
        public static bool Repair()
        {
            bool dism = ProcessRunner.Run("DISM.exe", "/Online /Cleanup-Image /RestoreHealth", 600000);
            bool sfc = ProcessRunner.Run("sfc.exe", "/scannow", 600000);
            return dism || sfc;
        }
    }
}
