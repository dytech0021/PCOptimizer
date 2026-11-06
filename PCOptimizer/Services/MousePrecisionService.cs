using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PCOptimizer.Services
{
    /// <summary>
    /// "Precisão aprimorada do ponteiro" (aceleração do mouse) do Windows.
    /// Desligada é o padrão entre jogadores de FPS: o movimento do mouse vira
    /// 1:1 com o cursor. Ligada é o padrão de fábrica do Windows.
    /// </summary>
    public static class MousePrecisionService
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint action, uint param, int[] vparam, uint init);

        private const uint SPI_GETMOUSE = 0x0003;
        private const uint SPI_SETMOUSE = 0x0004;
        private const uint SPIF_UPDATEINIFILE = 0x01;
        private const uint SPIF_SENDCHANGE = 0x02;

        // Valores de fábrica do Windows com a precisão ligada: limiar 1, limiar 2, aceleração.
        private static readonly int[] WindowsDefault = { 6, 10, 1 };
        private static readonly int[] Off = { 0, 0, 0 };

        /// <summary>A precisão aprimorada está ligada agora? null se não deu para ler.</summary>
        public static bool? IsEnabled()
        {
            try
            {
                var v = new int[3];
                return SystemParametersInfo(SPI_GETMOUSE, 0, v, 0) ? v[2] != 0 : null;
            }
            catch { return null; }
        }

        public static bool Disable() => Set(false);

        public static bool Enable() => Set(true);

        public static bool Set(bool enabled)
        {
            var v = enabled ? WindowsDefault : Off;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse", writable: true))
                {
                    if (key == null) return false;
                    key.SetValue("MouseSpeed", v[2].ToString(), RegistryValueKind.String);
                    key.SetValue("MouseThreshold1", v[0].ToString(), RegistryValueKind.String);
                    key.SetValue("MouseThreshold2", v[1].ToString(), RegistryValueKind.String);
                }

                // Aplica imediatamente sem precisar relogar
                SystemParametersInfo(SPI_SETMOUSE, 0, (int[])v.Clone(),
                    SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
                Logger.Info($"Mouse: precisão aprimorada {(enabled ? "ligada" : "desligada")}");
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
