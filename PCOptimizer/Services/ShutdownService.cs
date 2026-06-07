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

        /// <summary>Agenda o desligamento para daqui a <paramref name="minutes"/> minutos.</summary>
        public static bool Schedule(int minutes)
        {
            if (minutes <= 0) return false;
            int seconds = minutes * 60;
            try
            {
                // Cancela qualquer agendamento anterior antes de criar um novo
                RunShutdown("/a");
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

        private static void RunShutdown(string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = args,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
        }
    }
}
