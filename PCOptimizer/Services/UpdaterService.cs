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
    /// Baixa a nova versao e troca o executavel atual por ela.
    /// A troca e feita por um PowerShell oculto que espera este processo fechar,
    /// move o .new por cima do .exe e reinicia. O .new NUNCA e executado — exe
    /// single-file em execucao nao pode ser renomeado de forma confiavel, o que
    /// deixava o updater antigo em loop infinito sem nunca abrir o app.
    /// </summary>
    public static class UpdaterService
    {
        /// <summary>
        /// Baixa o arquivo da URL para destPath, reportando o progresso (0.0 a 1.0).
        /// </summary>
        public static async Task DownloadAsync(string url, string destPath,
            IProgress<double>? progress, CancellationToken ct = default)
        {
            using var http = HttpFactory.Create(TimeSpan.FromMinutes(5), "PCOptimizer-Updater");

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
        /// Aplica a atualizacao: um PowerShell oculto espera este processo fechar,
        /// move newExePath por cima do exe atual e reabre o app com --updated.
        /// O chamador deve encerrar o app logo apos chamar este metodo.
        /// </summary>
        public static void ApplyAndRestart(string newExePath)
        {
            string currentExe = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule!.FileName!;
            int pid = Environment.ProcessId;

            // Aspas simples do PowerShell: o unico escape necessario e ' -> ''.
            // Parenteses e espacos no caminho (ex.: "PCOptimizer (5).exe") sao seguros.
            static string Esc(string s) => s.Replace("'", "''");

            string script = $@"
$ErrorActionPreference = 'SilentlyContinue'
Wait-Process -Id {pid} -Timeout 60
Start-Sleep -Milliseconds 500
$new = '{Esc(newExePath)}'
$old = '{Esc(currentExe)}'
$moved = $false
for ($i = 0; $i -lt 30; $i++) {{
    try {{
        Move-Item -LiteralPath $new -Destination $old -Force -ErrorAction Stop
        $moved = $true
        break
    }} catch {{ Start-Sleep -Seconds 1 }}
}}
if (-not $moved) {{
    try {{
        Copy-Item -LiteralPath $new -Destination $old -Force -ErrorAction Stop
        Remove-Item -LiteralPath $new -Force
    }} catch {{ }}
}}
Start-Process -FilePath $old -ArgumentList '--updated'
";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-WindowStyle");
            psi.ArgumentList.Add("Hidden");
            // EncodedCommand elimina qualquer problema de quoting no caminho
            psi.ArgumentList.Add("-EncodedCommand");
            psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));

            Process.Start(psi);
        }
    }
}
