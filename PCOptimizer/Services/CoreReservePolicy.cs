using System;
using System.Collections.Generic;
using System.Linq;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Regras do Game Boost para processadores SEM P-cores/E-cores (Xeon, Ryzen,
    /// Intel até a 11ª geração): reserva os primeiros núcleos para o jogo e manda
    /// os outros programas para os últimos. Sem Windows aqui — testável.
    /// </summary>
    public static class CoreReservePolicy
    {
        /// <summary>Núcleos físicos que ficam para os outros programas (com os 2 threads de cada).</summary>
        public const int BackgroundCores = 2;

        /// <summary>Abaixo disso, 2 núcleos para o resto apertaria demais o sistema.</summary>
        public const int MinPhysicalCores = 6;

        public static bool IsSupported(int physicalCores, bool hybrid, bool multiGroup) =>
            !hybrid && !multiGroup && physicalCores >= MinPhysicalCores;

        /// <summary>
        /// Máscara dos núcleos para os outros programas: os ÚLTIMOS núcleos
        /// físicos, com os dois threads de cada (HyperThreading) — assim nenhum
        /// programa de fundo divide núcleo físico com o jogo. Num Ryzen com dois
        /// chiplets, ou num Xeon duplo, os últimos ficam no segundo chip.
        /// </summary>
        public static ulong BackgroundMask(IReadOnlyList<ulong> coreMasks, int backgroundCores = BackgroundCores)
        {
            var cores = coreMasks.Where(m => m != 0).OrderBy(LowestBit).ToList();
            if (cores.Count < MinPhysicalCores || backgroundCores <= 0 || backgroundCores >= cores.Count) return 0;
            ulong mask = 0;
            foreach (ulong m in cores.Skip(cores.Count - backgroundCores)) mask |= m;
            return mask;
        }

        /// <summary>Núcleos que ficam livres para o jogo.</summary>
        public static ulong GameMask(IReadOnlyList<ulong> coreMasks, int backgroundCores = BackgroundCores)
        {
            ulong all = 0;
            foreach (ulong m in coreMasks) all |= m;
            ulong bg = BackgroundMask(coreMasks, backgroundCores);
            return bg == 0 ? 0 : all & ~bg;
        }

        public static int CountBits(ulong v)
        {
            int n = 0;
            while (v != 0) { v &= v - 1; n++; }
            return n;
        }

        /// <summary>"12 núcleos (24 threads) — jogo com 10, outros programas com 2".</summary>
        public static string Describe(IReadOnlyList<ulong> coreMasks)
        {
            int physical = coreMasks.Count(m => m != 0);
            int logical = coreMasks.Sum(CountBits);
            string cpu = logical > physical ? $"{physical} núcleos ({logical} threads)" : $"{physical} núcleos";
            return physical >= MinPhysicalCores
                ? $"{cpu} — jogo com {physical - BackgroundCores}, outros programas com {BackgroundCores}"
                : $"{cpu} — precisa de pelo menos {MinPhysicalCores} núcleos";
        }

        private static int LowestBit(ulong m)
        {
            for (int i = 0; i < 64; i++) if ((m & (1UL << i)) != 0) return i;
            return 64;
        }
    }
}
