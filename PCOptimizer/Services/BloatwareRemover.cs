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
                string cmd = $"-NoProfile -WindowStyle Hidden -Command " +
                             $"\"Get-AppxPackage *{pkg}* | Remove-AppxPackage -ErrorAction SilentlyContinue\"";
                if (ProcessRunner.Run("powershell.exe", cmd, 30000)) removed++;
            }
            return removed;
        }
    }
}
