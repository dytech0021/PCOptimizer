using System;
using System.Runtime.InteropServices;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Atraso dos menus ao passar o mouse (MenuShowDelay): quanto tempo um
    /// submenu espera, com o mouse parado em cima, antes de abrir — "Novo ›",
    /// "Abrir com ›" e afins. O padrão do Windows é 400 ms; a otimização
    /// "Registro" zerava esse valor, e os menus passavam a abrir na hora.
    /// </summary>
    public static class MenuDelayService
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint action, uint param, ref int pvParam, uint init);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint action, uint param, IntPtr pvParam, uint init);

        private const uint SPI_GETMENUSHOWDELAY = 0x006A;
        private const uint SPI_SETMENUSHOWDELAY = 0x006B;
        private const uint SPIF_UPDATEINIFILE = 0x01;
        private const uint SPIF_SENDCHANGE = 0x02;

        /// <summary>Padrão de fábrica do Windows.</summary>
        public const int WindowsDefaultMs = 400;

        /// <summary>Atraso atual em ms; -1 se não deu para ler.</summary>
        public static int GetDelayMs()
        {
            try
            {
                int ms = 0;
                return SystemParametersInfo(SPI_GETMENUSHOWDELAY, 0, ref ms, 0) ? ms : -1;
            }
            catch { return -1; }
        }

        /// <summary>
        /// Liga (400 ms, padrão do Windows) ou desliga (0, instantâneo). Vale na
        /// hora para os próximos menus e fica gravado no perfil do usuário.
        /// </summary>
        public static bool SetEnabled(bool enabled)
        {
            int ms = enabled ? WindowsDefaultMs : 0;
            try
            {
                bool ok = SystemParametersInfo(SPI_SETMENUSHOWDELAY, (uint)ms, IntPtr.Zero,
                    SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
                Logger.Info($"Menus: atraso ao passar o mouse = {ms} ms ({(ok ? "ok" : "falhou")})");
                return ok;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "MenuDelay.Set");
                return false;
            }
        }
    }
}
