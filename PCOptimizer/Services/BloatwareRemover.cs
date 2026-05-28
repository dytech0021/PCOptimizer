using System.Diagnostics;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Remove apps pre-instalados inuteis (bloatware) via PowerShell/Appx.
    /// </summary>
    public static class BloatwareRemover
    {
        private static readonly string[] Packages =
        {
            "Microsoft.BingNews",
            "Microsoft.BingWeather",
            "Microsoft.GetHelp",
            "Microsoft.Getstarted",
            "Microsoft.MicrosoftSolitaireCollection",
            "Microsoft.People",
            "Microsoft.PowerAutomateDesktop",
            "Microsoft.Todos",
            "Microsoft.WindowsFeedbackHub",
            "Microsoft.ZuneMusic",
            "Microsoft.ZuneVideo",
            "Clipchamp.Clipchamp",
            "Microsoft.MicrosoftStickyNotes",
            "Microsoft.WindowsMaps"
        };

        public static int Remove()
        {
            int removed = 0;
            foreach (var pkg in Packages)
            {
                if (RemovePackage(pkg)) removed++;
            }
            return removed;
        }

        private static bool RemovePackage(string name)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -WindowStyle Hidden -Command " +
                                $"\"Get-AppxPackage *{name}* | Remove-AppxPackage -ErrorAction SilentlyContinue\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(30000);
                return p?.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
