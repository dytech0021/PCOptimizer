using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Desliga todos os monitores (estado de economia de energia) via
    /// WM_SYSCOMMAND + SC_MONITORPOWER. Eles religam sozinhos ao mexer o mouse
    /// ou apertar qualquer tecla — é o comportamento padrão do Windows para
    /// esse estado; não precisamos monitorar nada.
    /// </summary>
    public static class MonitorPowerService
    {
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private const uint WM_SYSCOMMAND    = 0x0112;
        private const int  SC_MONITORPOWER  = 0xF170;
        private const int  MONITOR_OFF      = 2; // 2=desligado, 1=economia, -1=ligado

        /// <summary>
        /// Desliga os monitores após um atraso. Sem o atraso, o soltar do clique /
        /// movimento residual do mouse religava a tela imediatamente — 3 s dão tempo
        /// de o usuário tirar a mão do mouse antes de a tela apagar.
        /// Envia para a PRÓPRIA janela (qualquer janela pode emitir o comando) —
        /// broadcast com SendMessage poderia travar esperando janelas penduradas.
        /// </summary>
        public static async Task TurnOffAsync(IntPtr hwnd, int delayMs = 3000)
        {
            if (hwnd == IntPtr.Zero) return;
            await Task.Delay(delayMs);
            SendMessage(hwnd, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)MONITOR_OFF);
        }
    }
}
