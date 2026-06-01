namespace PCOptimizer.Services
{
    public static class NetworkOptimizer
    {
        public static int Optimize()
        {
            int steps = 0;

            string[] commands =
            {
                "ipconfig /flushdns",
                "netsh int ip reset",
                "netsh winsock reset",
                "netsh int tcp set global autotuninglevel=normal",
                "netsh int tcp set global chimney=enabled",
                "netsh int tcp set global rss=enabled",
            };

            foreach (var cmd in commands)
            {
                var parts = cmd.Split(' ', 2);
                ProcessRunner.Run(parts[0], parts.Length > 1 ? parts[1] : "", 10000);
                steps++;
            }

            return steps;
        }
    }
}
