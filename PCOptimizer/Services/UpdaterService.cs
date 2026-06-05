using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Baixa a nova versao, substitui o executavel atual e reinicia o app.
    /// A troca e feita por um processo externo (PowerShell) que espera o app
    /// fechar antes de sobrescrever o arquivo .exe.
    /// </summary>
    public static class UpdaterService
    {
        /// <summary>
        /// Baixa o arquivo da URL para destPath, reportando o progresso (0.0 a 1.0).
        /// </summary>
        public static async Task DownloadAsync(string url, string destPath,
            IProgress<double>? progress, CancellationToken ct = default)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PCOptimizer-Updater");

            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            long total = resp.Content.Headers.ContentLength ?? -1L;

            using var src = await resp.Content.ReadAsStreamAsync(ct);
            using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);

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

        /// <summary>
        /// Inicia o processo externo que troca o executavel e reinicia o app.
        /// O chamador deve encerrar o app logo apos chamar este metodo.
        /// </summary>
        public static void ApplyAndRestart(string newExePath)
        {
            string currentExe = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule!.FileName!;
            int pid = Environment.ProcessId;

            // Aspas simples sao escapadas dobrando para uso em -LiteralPath do PowerShell.
            static string Esc(string s) => s.Replace("'", "''");

            // Espera o app fechar, tenta mover o novo .exe com retry (ate 15x, 1s entre cada).
            // Windows ou Defender pode manter o handle do .exe por alguns segundos apos o exit.
            // Passa --updated para o processo reiniciado para evitar loop de atualizacao.
            string ps =
                $"Wait-Process -Id {pid} -ErrorAction SilentlyContinue; " +
                $"Start-Sleep -Seconds 2; " +
                $"$moved = $false; " +
                $"for ($i = 0; $i -lt 15; $i++) {{ " +
                $"  try {{ Move-Item -LiteralPath '{Esc(newExePath)}' -Destination '{Esc(currentExe)}' -Force -ErrorAction Stop; $moved = $true; break }} " +
                $"  catch {{ Start-Sleep -Seconds 1 }} " +
                $"}}; " +
                $"Start-Process -FilePath '{Esc(currentExe)}' -ArgumentList '--updated'";

            // -EncodedCommand (UTF-16 base64) evita problemas com acentos no caminho
            // (ex.: "Área de Trabalho").
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(ps));

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -WindowStyle Hidden -EncodedCommand {encoded}",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(psi);
        }
    }
}
