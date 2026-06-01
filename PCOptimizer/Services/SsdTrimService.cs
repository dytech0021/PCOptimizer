namespace PCOptimizer.Services
{
    /// <summary>
    /// Executa o TRIM (retrim) no SSD para manter a performance de escrita.
    /// </summary>
    public static class SsdTrimService
    {
        public static bool Trim(string drive = "C:")
        {
            return ProcessRunner.Run("defrag.exe", $"{drive} /L", 120000); // /L = retrim (TRIM)
        }
    }
}
