using System;
using System.Diagnostics;
using System.IO;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Wrapper do nvidia-smi.exe (instalado junto com o driver NVIDIA).
    /// Usado para consultar clocks/limites e aplicar lock de clock e power limit
    /// — base do undervolt por trava de clock (Turing+) e do power limit máximo.
    /// </summary>
    public static class NvidiaSmiService
    {
        private static string? _path;

        public static string? FindSmi()
        {
            if (_path != null) return _path;
            string[] candidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe"),
                @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe",
            };
            foreach (var c in candidates)
                if (File.Exists(c)) { _path = c; return c; }
            return null;
        }

        public static bool IsAvailable => FindSmi() != null;

        /// <summary>Roda nvidia-smi e captura a saída (curta, sem risco de encher o pipe).</summary>
        private static (int exitCode, string output) RunCapture(string args)
        {
            string? smi = FindSmi();
            if (smi == null) return (-1, "");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = smi,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var p = Process.Start(psi);
                if (p == null) return (-1, "");
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(15000);
                return (p.HasExited ? p.ExitCode : -1, output.Trim());
            }
            catch
            {
                return (-1, "");
            }
        }

        /// <summary>Consulta um campo numérico via --query-gpu (ex.: "clocks.max.graphics").</summary>
        public static int QueryInt(string field)
        {
            var (code, output) = RunCapture($"--query-gpu={field} --format=csv,noheader,nounits");
            if (code != 0) return -1;
            // Multi-GPU: usa a primeira linha (GPU 0)
            string first = output.Split('\n')[0].Trim();
            return double.TryParse(first,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v)
                ? (int)Math.Round(v) : -1;
        }

        public static string QueryText(string field)
        {
            var (code, output) = RunCapture($"--query-gpu={field} --format=csv,noheader");
            return code == 0 ? output.Split('\n')[0].Trim() : "";
        }

        public static string GetGpuName() => QueryText("name");

        /// <summary>Trava o clock do core entre min e max MHz. Requer Turing (GTX 16xx / RTX) ou mais novo.</summary>
        public static bool LockGraphicsClock(int minMhz, int maxMhz)
        {
            var (code, _) = RunCapture($"-lgc {minMhz},{maxMhz}");
            return code == 0;
        }

        /// <summary>Remove a trava de clock — volta ao gerenciamento automático do driver.</summary>
        public static bool ResetGraphicsClock()
        {
            var (code, _) = RunCapture("-rgc");
            return code == 0;
        }

        /// <summary>Aplica o power limit em watts. Retorna false se a placa não suporta.</summary>
        public static bool SetPowerLimit(int watts)
        {
            var (code, _) = RunCapture($"-pl {watts}");
            return code == 0;
        }
    }
}
