using System;
using System.Runtime.InteropServices;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Alterna a topologia de vídeo entre "somente a tela principal" e "estender
    /// todas as telas" — o mesmo mecanismo do Win+P, via SetDisplayConfig.
    /// Pensado para acesso remoto: com várias telas o AnyDesk fica pulando de
    /// monitor; um clique deixa só a principal, outro reativa todas.
    /// O Windows memoriza o layout de CADA topologia, então reverter restaura
    /// posições/resoluções exatamente como estavam — sem salvar nada na mão.
    /// </summary>
    public static class MonitorTopologyService
    {
        [DllImport("user32.dll")]
        private static extern int SetDisplayConfig(uint numPathArrayElements, IntPtr pathArray,
            uint numModeInfoArrayElements, IntPtr modeInfoArray, uint flags);

        private const uint SDC_APPLY             = 0x00000080;
        private const uint SDC_TOPOLOGY_INTERNAL = 0x00000001; // "Somente tela do computador"
        private const uint SDC_TOPOLOGY_EXTEND   = 0x00000004; // "Estender"

        /// <summary>Quantidade de telas ATIVAS agora.</summary>
        public static int ActiveScreenCount()
        {
            try { return System.Windows.Forms.Screen.AllScreens.Length; }
            catch { return 1; }
        }

        /// <summary>Deixa ativa somente a tela principal (modo "PC screen only").</summary>
        public static bool UsePrimaryOnly() => ApplyTopology(SDC_TOPOLOGY_INTERNAL, "/internal");

        /// <summary>Reativa todas as telas no modo estendido, com o layout memorizado.</summary>
        public static bool ExtendAll() => ApplyTopology(SDC_TOPOLOGY_EXTEND, "/extend");

        private static bool ApplyTopology(uint topology, string switchArg)
        {
            try
            {
                if (SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | topology) == 0)
                    return true;
                // Fallback: o utilitário do próprio Windows (mesmo efeito do Win+P)
                return ProcessRunner.Run("DisplaySwitch.exe", switchArg, 15000);
            }
            catch (Exception ex) { Logger.Error(ex, "MonitorTopologyService"); return false; }
        }
    }
}
