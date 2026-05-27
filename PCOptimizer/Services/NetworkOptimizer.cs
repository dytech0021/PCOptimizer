using System.Diagnostics;

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
                try
                {
                    var parts = cmd.Split(' ', 2);
                    var psi = new ProcessStartInfo
                    {
                        FileName = parts[0],
                        Arguments = parts.Length > 1 ? parts[1] : "",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };

                    using var process = Process.Start(psi);
                    process?.WaitForExit(10000);
                    steps++;
                }
                catch { }
            }

            return steps;
        }
    }
}
