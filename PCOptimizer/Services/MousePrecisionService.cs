using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Desativa a "precisão aprimorada do ponteiro" (aceleração do mouse).
    /// Padrão entre jogadores de FPS: o movimento do mouse vira 1:1 com o cursor.
    /// </summary>
    public static class MousePrecisionService
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint action, uint param, int[] vparam, uint init);

        private const uint SPI_SETMOUSE = 0x0004;
        private const uint SPIF_UPDATEINIFILE = 0x01;
        private const uint SPIF_SENDCHANGE = 0x02;

        public static bool Disable()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse", writable: true))
                {
                    if (key == null) return false;
                    key.SetValue("MouseSpeed", "0", RegistryValueKind.String);
                    key.SetValue("MouseThreshold1", "0", RegistryValueKind.String);
                    key.SetValue("MouseThreshold2", "0", RegistryValueKind.String);
                }

                // Aplica imediatamente sem precisar relogar
                SystemParametersInfo(SPI_SETMOUSE, 0, new[] { 0, 0, 0 },
                    SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
