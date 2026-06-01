using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace PCOptimizer.Services
{
    public class UpdateInfo
    {
        public bool UpdateAvailable { get; set; }
        public string CurrentVersion { get; set; } = string.Empty;
        public string LatestVersion { get; set; } = string.Empty;
        public string ReleaseUrl { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
    }

    public static class UpdateService
    {
        private const string ApiUrl = "https://api.github.com/repos/dytech0021/PCOptimizer/releases/latest";
        private const string ReleasesPage = "https://github.com/dytech0021/PCOptimizer/releases/latest";

        public static async Task<UpdateInfo?> CheckForUpdateAsync()
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("PCOptimizer-UpdateCheck");

                var json = await http.GetStringAsync(ApiUrl);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string tag = root.GetProperty("tag_name").GetString() ?? "";
                string htmlUrl = root.TryGetProperty("html_url", out var hu)
                    ? hu.GetString() ?? ReleasesPage : ReleasesPage;

                string downloadUrl = "";
                if (root.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.GetProperty("name").GetString() ?? "";
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                            break;
                        }
                    }
                }

                var latest = ParseVersion(tag);
                var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

                bool available = latest != null &&
                    new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0))
                        > new Version(current.Major, current.Minor, Math.Max(current.Build, 0));

                return new UpdateInfo
                {
                    UpdateAvailable = available,
                    CurrentVersion = $"v{current.Major}.{current.Minor}.{Math.Max(current.Build, 0)}",
                    LatestVersion = tag,
                    ReleaseUrl = htmlUrl,
                    DownloadUrl = downloadUrl
                };
            }
            catch
            {
                // Sem internet ou erro na API — falha silenciosa, não atrapalha o uso
                return null;
            }
        }

        private static Version? ParseVersion(string tag)
        {
            tag = tag.TrimStart('v', 'V').Trim();
            return Version.TryParse(tag, out var v) ? v : null;
        }
    }
}
