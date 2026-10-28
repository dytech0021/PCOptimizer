using System;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PCOptimizer.Services
{
    /// <summary>Nome do processador e o que dá para saber dele pelo nome. Só leitura.</summary>
    public static class CpuInfoService
    {
        private static string? _name;

        /// <summary>Ex.: "Intel(R) Xeon(R) CPU E5-2680 v3 @ 2.50GHz".</summary>
        public static string ProcessorName
        {
            get
            {
                if (_name != null) return _name;
                try
                {
                    using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                    _name = (k?.GetValue("ProcessorNameString") as string ?? "").Trim();
                }
                catch { _name = ""; }
                return _name;
            }
        }

        /// <summary>Processador sem Speed Shift (o Windows controla o clock por P-states).</summary>
        public static bool IsLegacyPStates => CpuNamePolicy.IsLegacyPStateCpu(ProcessorName);

        public static bool IsXeon => CpuNamePolicy.IsXeon(ProcessorName);
    }

    /// <summary>Regras sobre o nome do processador, sem Windows (testáveis).</summary>
    public static class CpuNamePolicy
    {
        // Xeon E5/E7 v1–v4 (Sandy Bridge-EP até Broadwell-EP) e Xeon E3 v1–v4.
        private static readonly Regex XeonLegacy = new(
            @"Xeon.*\b(E5-\d{4}[A-Z]{0,2}(\s+v[1-4])?|E7-\d{4}[A-Z]?(\s+v[1-4])?|E3-\d{4}[A-Z]?(\s+v[1-4])?)\b(?!\s*v[5-9])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Xeons mais antigos (Nehalem/Westmere): X5670, E5645, W3690, L5640…
        private static readonly Regex XeonOld = new(@"Xeon.*\b[XEWL][35]\d{3}\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Core i3/i5/i7 da 1ª à 5ª geração: i7-920, i5-2500K, i7-4790K, i7-5775C…
        private static readonly Regex CoreLegacy = new(@"Core.*\bi[3579]-([2-5]\d{3}|[89]\d{2}|[67]\d{2})[A-Z]{0,2}\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool IsXeon(string? name) =>
            !string.IsNullOrWhiteSpace(name) && name.Contains("Xeon", StringComparison.OrdinalIgnoreCase);

        public static bool IsLegacyPStateCpu(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            if (!name.Contains("Intel", StringComparison.OrdinalIgnoreCase)
                && !name.Contains("Xeon", StringComparison.OrdinalIgnoreCase)) return false;
            if (Regex.IsMatch(name, @"\bv[5-9]\b", RegexOptions.IgnoreCase)) return false;   // E3 v5+ já tem Speed Shift
            return XeonLegacy.IsMatch(name) || XeonOld.IsMatch(name) || CoreLegacy.IsMatch(name);
        }
    }
}
