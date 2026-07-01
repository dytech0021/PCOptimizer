using System;
using System.Diagnostics;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Agenda ou cancela o desligamento do computador usando o comando shutdown do Windows.
    /// </summary>
    public static class ShutdownService
    {
        /// <summary>Horário em que o desligamento foi agendado (null se nenhum agendado).</summary>
        public static DateTime? ScheduledAt { get; private set; }

        /// <summary>Agenda o desligamento para daqui a <paramref name="minutes"/> minutos (máx. 30 dias).</summary>
        public static bool Schedule(int minutes)
        {
            // 43200 min = 30 dias. Acima disso minutes*60 estouraria o int e o
            // shutdown.exe rejeitaria o /t — mas retornaríamos true mesmo assim.
            if (minutes <= 0 || minutes > 43_200) return false;
            int seconds = minutes * 60;
            try
            {
                // Cancela qualquer agendamento anterior e ESPERA concluir — se o /s
                // rodar antes do /a terminar, o Windows rejeita com erro 1190 e o
                // reagendamento falha silenciosamente.
                RunShutdown("/a", wait: true);
                RunShutdown($"/s /f /t {seconds}");
                ScheduledAt = DateTime.Now.AddMinutes(minutes);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Cancela um desligamento agendado.</summary>
        public static bool Cancel()
        {
            try
            {
                RunShutdown("/a");
                ScheduledAt = null;
                return true;
            }
            catch { return false; }
        }

        private static void RunShutdown(string args, bool wait = false)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = args,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var p = Process.Start(psi);
            if (wait) p?.WaitForExit(5000);
        }
    }
}
