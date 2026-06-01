namespace PCOptimizer.Services
{
    public static class DefragService
    {
        public static bool Optimize(string drive = "C:")
        {
            return ProcessRunner.Run("defrag.exe", $"{drive} /O", 300000);
        }
    }
}
