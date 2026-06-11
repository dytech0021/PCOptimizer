using System;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Operações de alto nível do Modo Expert para GPU NVIDIA:
    /// overclock (offsets + power limit) e undervolt (trava de clock + offset).
    /// Nada persiste após reiniciar o Windows — instabilidade se resolve com reboot.
    /// </summary>
    public static class GpuTuningService
    {
        public record GpuInfo(string Name, int BoostMhz, int DefaultPowerW, int MaxPowerW, bool SupportsLock);

        /// <summary>Detecta a GPU NVIDIA e lê os dados necessários para OC/UV. Null se ausente.</summary>
        public static GpuInfo? Detect()
        {
            if (!NvidiaSmiService.IsAvailable) return null;
            string name = NvidiaSmiService.GetGpuName();
            if (string.IsNullOrEmpty(name)) return null;

            int boost = NvidiaSmiService.QueryInt("clocks.max.graphics");
            int defPower = NvidiaSmiService.QueryInt("power.default_limit");
            int maxPower = NvidiaSmiService.QueryInt("power.max_limit");

            // Trava de clock (-lgc) exige Turing+ — GTX 16xx, RTX 20xx em diante.
            // Pascal (GTX 10xx) e anteriores não suportam; nelas só offset/power limit.
            bool supportsLock = boost > 0 && !IsPreTuring(name);

            return new GpuInfo(name, boost, defPower, maxPower, supportsLock);
        }

        private static bool IsPreTuring(string name)
        {
            string n = name.ToUpperInvariant();
            // GTX 9xx/10xx e Quadros da era Pascal/Maxwell
            return n.Contains("GTX 10") || n.Contains("GTX 9") || n.Contains("GT 7") ||
                   n.Contains("GT 1030") || n.Contains("TITAN X") || n.Contains("TITAN XP");
        }

        /// <summary>Overclock: offsets de core e memória em MHz (sliders da UI).</summary>
        public static (bool core, bool mem) ApplyOverclock(int coreOffsetMhz, int memOffsetMhz)
        {
            bool core = NvapiService.SetCoreOffsetMhz(coreOffsetMhz);
            bool mem = memOffsetMhz == 0 || NvapiService.SetMemoryOffsetMhz(memOffsetMhz);
            return (core, mem);
        }

        /// <summary>Aplica o power limit máximo permitido pelo fabricante da placa.</summary>
        public static bool ApplyMaxPowerLimit(GpuInfo gpu)
        {
            return gpu.MaxPowerW > 0 && NvidiaSmiService.SetPowerLimit(gpu.MaxPowerW);
        }

        /// <summary>
        /// Undervolt por trava de clock: limita o boost a (boost × fator) e desloca a
        /// curva com offset positivo — mesmo clock final com tensão menor.
        /// lockFactor: 0.95 (leve) / 0.90 (médio) / 0.85 (agressivo).
        /// </summary>
        public static (bool ok, int lockMhz) ApplyUndervolt(GpuInfo gpu, double lockFactor, int offsetMhz)
        {
            if (!gpu.SupportsLock || gpu.BoostMhz <= 0) return (false, 0);

            // Clocks NVIDIA andam em degraus de 15 MHz
            int lockMhz = (int)(gpu.BoostMhz * lockFactor / 15) * 15;

            // Offset primeiro: se a trava falhar, um offset positivo sozinho é inócuo;
            // o inverso (trava sem offset) derrubaria o desempenho.
            if (!NvapiService.SetCoreOffsetMhz(offsetMhz)) return (false, 0);
            if (!NvidiaSmiService.LockGraphicsClock(0, lockMhz))
            {
                NvapiService.SetCoreOffsetMhz(0);
                return (false, 0);
            }
            return (true, lockMhz);
        }

        /// <summary>Reverte tudo para o padrão de fábrica: offsets 0, sem trava, power limit default.</summary>
        public static bool RevertAll(GpuInfo? gpu)
        {
            bool ok = true;
            ok &= NvapiService.SetCoreOffsetMhz(0);
            NvapiService.SetMemoryOffsetMhz(0);
            ok &= NvidiaSmiService.ResetGraphicsClock();
            if (gpu is { DefaultPowerW: > 0 })
                NvidiaSmiService.SetPowerLimit(gpu.DefaultPowerW);
            return ok;
        }
    }
}
