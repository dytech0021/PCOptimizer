using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
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
        /// Inicia a instalacao da nova versao: executa o novo .exe diretamente com --install-over.
        /// O novo exe (quando inicia com essa flag) espera o processo atual fechar, move o arquivo
        /// sobre si mesmo e reinicia. Isso e mais confiavel do que PowerShell Move-Item.
        /// O chamador deve encerrar o app logo apos chamar este metodo.
        /// </summary>
        public static void ApplyAndRestart(string newExePath)
        {
            string currentExe = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule!.FileName!;
            int pid = Environment.ProcessId;

            // Executa o novo exe com --install-over para que ele mesmo se instale apos sairmos.
            // Usa ArgumentList para escape correto de caminhos com espacos ou caracteres especiais.
            var psi = new ProcessStartInfo
            {
                FileName = newExePath,
                UseShellExecute = false,
                CreateNoWindow = false
            };
            psi.ArgumentList.Add("--install-over");
            psi.ArgumentList.Add(currentExe);
            psi.ArgumentList.Add(pid.ToString());

            Process.Start(psi);
        }
    }
}
