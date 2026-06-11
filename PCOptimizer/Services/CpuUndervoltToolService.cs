using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Undervolt de CPU exige acesso de kernel aos MSRs — embutir um driver
    /// (WinRing0 etc.) seria um risco de segurança e bloqueio do Defender.
    /// Em vez disso, detectamos a ferramenta oficial adequada ao processador
    /// (ThrottleStop/XTU para Intel, Ryzen Master para AMD): se já estiver
    /// instalada, abrimos; senão, baixamos o instalador automaticamente.
    /// Como os links diretos mudam a cada versão do fabricante, o download tem
    /// fallback: se a URL direta falhar ou devolver HTML, abrimos a página oficial.
    /// </summary>
    public static class CpuUndervoltToolService
    {
        /// <param name="DirectUrl">Link direto do instalador (best-effort, pode mudar).</param>
        /// <param name="PageUrl">Página oficial — fallback sempre válido.</param>
        public record ToolInfo(string CpuVendor, string ToolName, string? InstalledPath,
            string? DirectUrl, string PageUrl);

        public static ToolInfo Detect()
        {
            bool isAmd = IsAmdCpu();

            if (isAmd)
            {
                string[] ryzenMaster =
                {
                    @"C:\Program Files\AMD\RyzenMaster\bin\AMD Ryzen Master.exe",
                    @"C:\Program Files\AMD\RyzenMaster\bin\Ryzen Master.exe",
                    @"C:\Program Files\AMD\RyzenMaster\bin\AMDRyzenMaster.exe",
                    @"C:\Program Files\AMD\RyzenMaster\bin\RyzenMaster.exe",
                    @"C:\Program Files\AMD\RyzenMaster\AMD Ryzen Master.exe",
                    @"C:\Program Files\AMD\RyzenMaster\AMDRyzenMaster.exe",
                    @"C:\Program Files\AMD\AMD Ryzen Master\bin\AMD Ryzen Master.exe",
                    @"C:\Program Files\AMD\AMD Ryzen Master\bin\AMDRyzenMaster.exe",
                };
                string[] ryzenExeNames = { "AMD Ryzen Master.exe", "Ryzen Master.exe", "AMDRyzenMaster.exe", "RyzenMaster.exe" };
                string? found = FirstExisting(ryzenMaster)
                    ?? CpuTuningDetectionService.FindViaRegistry("Ryzen Master", ryzenExeNames);
                return new ToolInfo("AMD", "Ryzen Master", found,
                    "https://download.amd.com/dir/bin/AMDRyzenMasterSetup.exe",
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
            string? ts = FirstExisting(throttleStop);
            if (ts != null)
                return new ToolInfo("Intel", "ThrottleStop", ts, null,
                    "https://www.techpowerup.com/download/techpowerup-throttlestop/");

            string[] xtu =
            {
                @"C:\Program Files (x86)\Intel\Intel(R) Extreme Tuning Utility\Client\XtuShell.exe",
                @"C:\Program Files\Intel\Intel(R) Extreme Tuning Utility\Client\XtuShell.exe",
            };
            string? x = FirstExisting(xtu);
            if (x != null)
                return new ToolInfo("Intel", "Intel XTU", x, null,
                    "https://www.intel.com.br/content/www/br/pt/download/17881/");

            // Nenhuma instalada: ThrottleStop (TechPowerUp não tem link direto estável,
            // então DirectUrl fica nulo e cai direto na página oficial).
            return new ToolInfo("Intel", "ThrottleStop", null, null,
                "https://www.techpowerup.com/download/techpowerup-throttlestop/");
        }

        private static string? FirstExisting(string[] paths)
        {
            foreach (var p in paths)
                if (File.Exists(p)) return p;
            return null;
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

        public enum AcquireResult { OpenedInstalled, Downloaded, OpenedPage, Failed }

        /// <summary>
        /// Se a ferramenta já está instalada, abre. Senão, tenta baixar o instalador
        /// (com progresso) e abri-lo. Se o link direto falhar ou devolver uma página
        /// HTML em vez do arquivo, abre a página oficial no navegador.
        /// </summary>
        public static async Task<AcquireResult> AcquireAsync(ToolInfo tool,
            IProgress<double>? progress, CancellationToken ct = default)
        {
            if (tool.InstalledPath != null)
                return Open(tool.InstalledPath) ? AcquireResult.OpenedInstalled : AcquireResult.Failed;

            if (!string.IsNullOrEmpty(tool.DirectUrl))
            {
                string? file = await TryDownloadInstallerAsync(tool, progress, ct);
                if (file != null)
                    return Open(file) ? AcquireResult.Downloaded : AcquireResult.OpenedPage;
            }

            // Sem link direto utilizável — abre a página oficial
            return Open(tool.PageUrl) ? AcquireResult.OpenedPage : AcquireResult.Failed;
        }

        /// <summary>
        /// Baixa o instalador para a pasta Downloads. Retorna null (para acionar o
        /// fallback) se a resposta não for sucesso ou vier como HTML (página, não arquivo).
        /// </summary>
        private static async Task<string?> TryDownloadInstallerAsync(ToolInfo tool,
            IProgress<double>? progress, CancellationToken ct)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("PCOptimizer");

                using var resp = await http.GetAsync(tool.DirectUrl,
                    HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode) return null;

                // Se o servidor devolveu uma página web, não é o instalador
                string? mime = resp.Content.Headers.ContentType?.MediaType;
                if (mime != null && mime.Contains("html", StringComparison.OrdinalIgnoreCase))
                    return null;

                string fileName = GetFileName(resp, tool);
                string downloads = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                Directory.CreateDirectory(downloads);
                string destPath = Path.Combine(downloads, fileName);

                long total = resp.Content.Headers.ContentLength ?? -1L;
                using (var src = await resp.Content.ReadAsStreamAsync(ct))
                using (var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920];
                    long readTotal = 0;
                    int read;
                    while ((read = await src.ReadAsync(buffer, ct)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                        readTotal += read;
                        if (total > 0) progress?.Report((double)readTotal / total);
                    }
                }

                // Arquivo minúsculo provavelmente é erro/redirect disfarçado
                if (new FileInfo(destPath).Length < 50_000)
                {
                    try { File.Delete(destPath); } catch { }
                    return null;
                }

                return destPath;
            }
            catch
            {
                return null;
            }
        }

        private static string GetFileName(HttpResponseMessage resp, ToolInfo tool)
        {
            string? fromHeader = resp.Content.Headers.ContentDisposition?.FileNameStar
                ?? resp.Content.Headers.ContentDisposition?.FileName;
            if (!string.IsNullOrWhiteSpace(fromHeader))
                return fromHeader.Trim('"');

            string fromUrl = Path.GetFileName(new Uri(tool.DirectUrl!).LocalPath);
            return string.IsNullOrWhiteSpace(fromUrl)
                ? tool.ToolName.Replace(" ", "") + "_Setup.exe"
                : fromUrl;
        }

        private static bool Open(string target)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
