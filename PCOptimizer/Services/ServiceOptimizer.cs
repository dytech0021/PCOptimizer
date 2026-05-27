using System;
using System.Collections.Generic;
using System.ServiceProcess;
using Microsoft.Win32;

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
                try
                {
                    using var sc = new ServiceController(serviceName);
                    if (sc.Status == ServiceControllerStatus.Running)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                    }

                    using var key = Registry.LocalMachine.OpenSubKey(
                        $@"SYSTEM\CurrentControlSet\Services\{serviceName}", writable: true);
                    if (key != null)
                    {
                        key.SetValue("Start", 4); // 4 = Disabled
                        optimized++;
                    }
                }
                catch { }
            }

            return optimized;
        }
    }
}
