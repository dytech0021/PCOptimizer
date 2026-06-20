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
    ///
    /// Estrategia de troca (duas etapas, do mais simples ao mais robusto):
    ///
    /// Etapa 1 — troca em-processo:
    ///   No Windows, um exe em execucao pode ser renomeado (o OS abre com
    ///   FILE_SHARE_DELETE). Entao enquanto o app ainda roda nos renomeamos
    ///   para .bak, movemos o .new para o nosso lugar (agora vago, sem
    ///   overwrite) e lancamos o novo exe diretamente. E a abordagem mais
    ///   confiavel pois nao precisa esperar o processo fechar.
    ///
    /// Etapa 2 — script PowerShell (fallback):
    ///   Se a etapa 1 falhar (raro — ex.: disco somente leitura, AV), um
    ///   script oculto espera o processo fechar e repete a mesma logica
    ///   rename-first: move old→.bak, depois new→old (sem overwrite).
    ///   Se mesmo assim falhar, reinicia sem --updated para o usuario tentar
    ///   de novo em vez de ficar preso na versao antiga silenciosamente.
    /// </summary>
    public static class UpdaterService
    {
        /// <summary>
        /// Baixa o arquivo da URL para destPath.
        /// Reporta (bytesLidos, totalBytes) — totalBytes é -1 se o servidor não enviar Content-Length.
        /// </summary>
        public static async Task DownloadAsync(string url, string destPath,
            IProgress<(long bytesRead, long totalBytes)>? progress, CancellationToken ct = default)
        {
            using var http = HttpFactory.Create(TimeSpan.FromMinutes(5), "PCOptimizer-Updater");

            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            long total = resp.Content.Headers.ContentLength ?? -1L;

            using var src = await resp.Content.ReadAsStreamAsync(ct);
            using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write,
                FileShare.None, 65536, useAsync: true);

            var buffer = new byte[524288]; // 512 KB — melhor throughput em conexões rápidas
            long readTotal = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                readTotal += read;
                progress?.Report((readTotal, total));
            }
        }

        /// <summary>
        /// Aplica a atualizacao e reinicia o app.
        /// O chamador deve encerrar o app logo apos chamar este metodo.
        /// </summary>
        public static void ApplyAndRestart(string newExePath)
        {
            string currentExe = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule!.FileName!;
            string backupPath = currentExe + ".bak";

            // ── Etapa 1: troca em-processo ───────────────────────────────────
            // Um exe em execucao no Windows pode ser RENOMEADO (FILE_SHARE_DELETE)
            // mas nao sobrescrito. Renomear nos mesmos para .bak libera o nome
            // original; mover o .new para esse nome vago nao precisa de overwrite.
            try
            {
                if (File.Exists(backupPath)) File.Delete(backupPath);
                File.Move(currentExe, backupPath);      // nos mesmos → .bak
                try
                {
                    File.Move(newExePath, currentExe);  // .new → nome original (vago)
                    Process.Start(new ProcessStartInfo
                    {
                        FileName        = currentExe,
                        Arguments       = "--updated",
                        UseShellExecute = false,
                        CreateNoWindow  = false
                    });
                    return; // caller chama Shutdown()
                }
                catch
                {
                    // Move do .new falhou — restaura o nome original
                    try { File.Move(backupPath, currentExe); } catch { }
                    throw;
                }
            }
            catch { /* cai no fallback PowerShell abaixo */ }

            // ── Etapa 2: PowerShell (fallback) ───────────────────────────────
            int pid = Environment.ProcessId;
            static string Esc(string s) => s.Replace("'", "''");

            string script = $@"
$ErrorActionPreference = 'SilentlyContinue'
Wait-Process -Id {pid} -Timeout 60 -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
$new = '{Esc(newExePath)}'
$old = '{Esc(currentExe)}'
$bak = $old + '.bak'
if (Test-Path -LiteralPath $bak) {{ Remove-Item -LiteralPath $bak -Force }}
$applied = $false
for ($i = 0; $i -lt 20; $i++) {{
    try {{
        if (Test-Path -LiteralPath $old) {{
            Move-Item -LiteralPath $old -Destination $bak -Force -ErrorAction Stop
        }}
        Move-Item -LiteralPath $new -Destination $old -Force -ErrorAction Stop
        Remove-Item -LiteralPath $bak -Force -ErrorAction SilentlyContinue
        $applied = $true
        break
    }} catch {{
        if (-not (Test-Path -LiteralPath $old) -and (Test-Path -LiteralPath $bak)) {{
            Move-Item -LiteralPath $bak -Destination $old -Force -ErrorAction SilentlyContinue
        }}
        Start-Sleep -Seconds 1
    }}
}}
if (-not $applied) {{
    try {{
        Copy-Item -LiteralPath $new -Destination $old -Force -ErrorAction Stop
        Remove-Item -LiteralPath $new -Force -ErrorAction SilentlyContinue
        $applied = $true
    }} catch {{ }}
}}
if ($applied) {{
    Start-Process -FilePath $old -ArgumentList '--updated'
}} else {{
    if (Test-Path -LiteralPath $old) {{ Start-Process -FilePath $old }}
}}
";

            var psi = new ProcessStartInfo
            {
                FileName       = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow  = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-WindowStyle");
            psi.ArgumentList.Add("Hidden");
            psi.ArgumentList.Add("-EncodedCommand");
            psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));

            Process.Start(psi);
        }

        /// <summary>
        /// Remove o .bak deixado pela etapa 1 da atualizacao anterior, se existir.
        /// Deve ser chamado na inicializacao quando --updated estiver presente.
        /// </summary>
        public static void CleanupBackup()
        {
            try
            {
                string? exe = Environment.ProcessPath;
                if (exe == null) return;
                string bak = exe + ".bak";
                if (File.Exists(bak)) File.Delete(bak);
            }
            catch { }
        }
    }
}
