using System;
using System.Diagnostics;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Reparo profundo do sistema: DISM + SFC + CHKDSK. São processos demorados
    /// (de vários minutos a horas), por isso ficam num botão separado do fluxo
    /// normal de otimização. O CHKDSK /r não roda com o Windows aberto na unidade
    /// do sistema — ele é AGENDADO para a próxima reinicialização.
    /// Requer admin.
    /// </summary>
    public static class DeepRepairService
    {
        public sealed class Result
        {
            public bool Dism;
            public bool Sfc;
            public bool ChkdskScheduled;
        }

        /// <param name="progress">Recebe o nome da etapa atual (para o log).</param>
        public static Result Run(Action<string>? progress = null)
        {
            var r = new Result();

            // DISM antes do SFC: o DISM repara a "store" de componentes que o SFC
            // usa como fonte para restaurar os arquivos do sistema.
            progress?.Invoke("DISM /RestoreHealth — reparando a imagem do Windows (pode demorar bastante)...");
            r.Dism = ProcessRunner.Run("DISM.exe",
                "/Online /Cleanup-Image /RestoreHealth", 1_800_000); // até 30 min

            progress?.Invoke("SFC /scannow — verificando os arquivos do sistema...");
            r.Sfc = ProcessRunner.Run("sfc.exe", "/scannow", 1_800_000);

            progress?.Invoke("CHKDSK /r — agendando verificação do disco para o próximo reinício...");
            r.ChkdskScheduled = ScheduleChkdsk();

            return r;
        }

        /// <summary>
        /// Agenda o CHKDSK C: /r /f para a próxima reinicialização. Na unidade do
        /// sistema o chkdsk não consegue travar o volume e pergunta se deve agendar
        /// no boot — respondemos automaticamente (S em pt-BR, Y em inglês).
        /// </summary>
        private static bool ScheduleChkdsk()
        {
            try
            {
                string sysDrive = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
                    .Substring(0, 2); // "C:"

                var psi = new ProcessStartInfo
                {
                    FileName               = "chkdsk.exe",
                    Arguments              = $"{sysDrive} /r /f",
                    RedirectStandardInput  = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                };

                using var p = Process.Start(psi);
                if (p == null) return false;

                // Responde ao prompt de agendamento. Manda as duas letras: só a do
                // idioma do sistema é aceita, a outra é ignorada.
                try
                {
                    p.StandardInput.WriteLine("S");
                    p.StandardInput.WriteLine("Y");
                    p.StandardInput.Close();
                }
                catch { /* sem prompt (unidade travável) — chkdsk segue sozinho */ }

                // Drena a saída em paralelo para não travar o pipe, com timeout.
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(120_000))
                {
                    try { p.Kill(true); } catch { }
                    return false;
                }
                p.WaitForExit();
                string output = outTask.GetAwaiter().GetResult() + errTask.GetAwaiter().GetResult();
                Logger.Info("CHKDSK agendamento: exit=" + p.ExitCode);

                // "will be checked" (en) / "será verificado" (pt) confirmam o agendamento;
                // na dúvida, exit 0 já indica sucesso do agendamento.
                return p.ExitCode == 0
                    || output.IndexOf("verificad", StringComparison.OrdinalIgnoreCase) >= 0
                    || output.IndexOf("will be checked", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (Exception ex) { Logger.Error(ex, "ScheduleChkdsk"); return false; }
        }
    }
}
