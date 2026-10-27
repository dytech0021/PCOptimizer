using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Modo Expert: força o timer do Windows para 0.5ms (NtSetTimerResolution).
    /// Tweak clássico de jogos competitivos — frametimes mais consistentes e menor
    /// input lag em alguns títulos. A resolução vale enquanto o PC Optimizer estiver
    /// aberto (o pedido é por processo desde o Win10 2004); a chave de registro
    /// garante o comportamento global no Win11 após reiniciar.
    /// </summary>
    public static class TimerResolutionService
    {
        [DllImport("ntdll.dll", SetLastError = true)]
        private static extern int NtSetTimerResolution(uint desiredResolution,
            bool setResolution, out uint currentResolution);

        public static bool Apply()
        {
            bool ok = false;
            try
            {
                // 5000 × 100ns = 0.5ms
                ok = NtSetTimerResolution(5000, true, out _) == 0;
            }
            catch { }

            try
            {
                // Win11: faz o kernel respeitar o pedido de maior resolução globalmente
                using var key = Registry.LocalMachine.CreateSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel");
                key?.SetValue("GlobalTimerResolutionRequests", 1, RegistryValueKind.DWord);
            }
            catch { }

            return ok;
        }

        /// <summary>
        /// Desfaz o pedido de 0,5 ms deste processo. O valor de registro do
        /// Windows 11 fica: sozinho ele não muda nada — só faz o Windows respeitar
        /// pedidos de timer de quem pedir.
        /// </summary>
        public static void Revert()
        {
            try { NtSetTimerResolution(5000, false, out _); }
            catch { }
        }
    }
}
