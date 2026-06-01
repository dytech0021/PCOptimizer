using System.Diagnostics;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Executa processos auxiliares (sc, defrag, powercfg, etc.) de forma segura.
    /// Nao redireciona a saida padrao: programas verbosos como DISM/SFC podem
    /// encher o buffer do pipe e travar o processo se a saida nao for lida.
    /// Por isso a saida e simplesmente descartada (CreateNoWindow oculta a janela).
    /// </summary>
    internal static class ProcessRunner
    {
        /// <summary>
        /// Roda um processo e espera ate o timeout (ms).
        /// Retorna true somente se o processo terminou no prazo com ExitCode 0.
        /// Se estourar o timeout, encerra o processo e retorna false.
        /// </summary>
        public static bool Run(string file, string args, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };

                using var p = Process.Start(psi);
                if (p == null) return false;

                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(true); } catch { /* ja saindo */ }
                    return false;
                }

                return p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
