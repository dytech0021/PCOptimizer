using System;
using System.IO;
using Microsoft.Win32;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Undervolt de CPU exige acesso de kernel aos MSRs — embutir um driver
    /// (WinRing0 etc.) seria um risco de segurança e bloqueio do Defender.
    /// Em vez disso, detectamos a ferramenta oficial adequada ao processador
    /// (ThrottleStop/XTU para Intel, Ryzen Master para AMD) e abrimos ela,
    /// ou levamos o usuário à página de download.
    /// </summary>
    public static class CpuUndervoltToolService
    {
        public record ToolInfo(string CpuVendor, string ToolName, string? InstalledPath, string DownloadUrl);

        public static ToolInfo Detect()
        {
            bool isAmd = IsAmdCpu();

            if (isAmd)
            {
                string[] ryzenMaster =
                {
                    @"C:\Program Files\AMD\RyzenMaster\bin\AMD Ryzen Master.exe",
                    @"C:\Program Files\AMD\RyzenMaster\bin\Ryzen Master.exe",
                };
                foreach (var p in ryzenMaster)
                    if (File.Exists(p))
                        return new ToolInfo("AMD", "Ryzen Master", p,
                            "https://www.amd.com/pt/products/software/ryzen-master.html");
                return new ToolInfo("AMD", "Ryzen Master", null,
                    "https://www.amd.com/pt/products/software/ryzen-master.html");
            }

            string[] throttleStop =
            {
                @"C:\Program Files\ThrottleStop\ThrottleStop.exe",
                @"C:\Program Files (x86)\ThrottleStop\ThrottleStop.exe",
                @"C:\ThrottleStop\ThrottleStop.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "ThrottleStop", "ThrottleStop.exe"),
            };
            foreach (var p in throttleStop)
                if (File.Exists(p))
                    return new ToolInfo("Intel", "ThrottleStop", p,
                        "https://www.techpowerup.com/download/techpowerup-throttlestop/");

            string[] xtu =
            {
                @"C:\Program Files (x86)\Intel\Intel(R) Extreme Tuning Utility\Client\XtuShell.exe",
                @"C:\Program Files\Intel\Intel(R) Extreme Tuning Utility\Client\XtuShell.exe",
            };
            foreach (var p in xtu)
                if (File.Exists(p))
                    return new ToolInfo("Intel", "Intel XTU", p,
                        "https://www.intel.com.br/content/www/br/pt/download/17881/");

            return new ToolInfo("Intel", "ThrottleStop", null,
                "https://www.techpowerup.com/download/techpowerup-throttlestop/");
        }

        private static bool IsAmdCpu()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                return (key?.GetValue("VendorIdentifier") as string)?
                    .Contains("AuthenticAMD", StringComparison.OrdinalIgnoreCase) == true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Abre a ferramenta instalada, ou a página de download se ausente.</summary>
        public static bool OpenToolOrDownload(ToolInfo tool)
        {
            try
            {
                string target = tool.InstalledPath ?? tool.DownloadUrl;
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
                return tool.InstalledPath != null;
            }
            catch
            {
                return false;
            }
        }
    }
}
