using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Registra uma sessão comparável sem instalar capturadores dentro do jogo.
    /// FPS/frametime continuam sendo medidos pelo overlay escolhido pelo usuário;
    /// aqui ficam duração, CPU e memória sob o mesmo perfil para auditoria.
    /// </summary>
    internal static class GameSessionTelemetryService
    {
        private static int _pid;
        private static string _game = "";
        private static string _mode = "";
        private static DateTime _start;
        private static TimeSpan _initialCpu;
        private static long _workingSetSum;
        private static long _workingSetPeak;
        private static int _samples;

        private static string CsvPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCOptimizer", "competitive-sessions.csv");

        public static void Start(int pid, string game, string mode)
        {
            _pid = pid;
            _game = game;
            _mode = mode;
            _start = DateTime.Now;
            _workingSetSum = _workingSetPeak = 0;
            _samples = 0;
            try
            {
                using var process = Process.GetProcessById(pid);
                _initialCpu = process.TotalProcessorTime;
            }
            catch { _initialCpu = TimeSpan.Zero; }
        }

        public static void Sample()
        {
            if (_pid == 0) return;
            try
            {
                using var process = Process.GetProcessById(_pid);
                long working = process.WorkingSet64;
                _workingSetSum += working;
                if (working > _workingSetPeak) _workingSetPeak = working;
                _samples++;
            }
            catch { }
        }

        public static string Stop(string reason)
        {
            if (_pid == 0) return "";
            DateTime end = DateTime.Now;
            TimeSpan cpu = TimeSpan.Zero;
            try
            {
                using var process = Process.GetProcessById(_pid);
                cpu = process.TotalProcessorTime - _initialCpu;
            }
            catch { }

            double avgMb = _samples == 0 ? 0 : _workingSetSum / (double)_samples / 1048576.0;
            double peakMb = _workingSetPeak / 1048576.0;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CsvPath)!);
                if (!File.Exists(CsvPath))
                    File.AppendAllText(CsvPath,
                        "inicio,fim,jogo,modo,duracao_s,cpu_s,mem_media_mb,mem_pico_mb,motivo\r\n");
                string line = string.Join(",",
                    _start.ToString("O", CultureInfo.InvariantCulture),
                    end.ToString("O", CultureInfo.InvariantCulture),
                    Escape(_game), Escape(_mode),
                    (end - _start).TotalSeconds.ToString("F1", CultureInfo.InvariantCulture),
                    cpu.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture),
                    avgMb.ToString("F1", CultureInfo.InvariantCulture),
                    peakMb.ToString("F1", CultureInfo.InvariantCulture), Escape(reason));
                File.AppendAllText(CsvPath, line + "\r\n");
            }
            catch (Exception ex) { Logger.Error(ex, "GameSessionTelemetry.Stop"); }

            string summary = $"sessão {(end - _start):hh\\:mm\\:ss} salva";
            _pid = 0;
            return summary;
        }

        private static string Escape(string text) =>
            "\"" + (text ?? "").Replace("\"", "\"\"") + "\"";
    }
}
