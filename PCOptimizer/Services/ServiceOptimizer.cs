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
                ProcessRunner.Run("sc.exe", $"stop {serviceName}", 10000);
                if (ProcessRunner.Run("sc.exe", $"config {serviceName} start= disabled", 10000))
                    optimized++;
            }

            return optimized;
        }
    }
}
