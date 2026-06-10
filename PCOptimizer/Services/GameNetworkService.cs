using Microsoft.Win32;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Reduz a latência de rede em jogos online: desativa o throttling de
    /// pacotes do Windows e o algoritmo de Nagle (que agrupa pacotes pequenos
    /// antes de enviar — péssimo para ping em LoL/CS/Valorant).
    /// </summary>
    public static class GameNetworkService
    {
        public static int Apply()
        {
            int tweaks = 0;

            // O Windows limita a taxa de pacotes quando há mídia em reprodução;
            // 0xFFFFFFFF desativa o limite.
            try
            {
                using var profile = Registry.LocalMachine.CreateSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile");
                if (profile != null)
                {
                    profile.SetValue("NetworkThrottlingIndex",
                        unchecked((int)0xFFFFFFFF), RegistryValueKind.DWord);
                    tweaks++;
                }
            }
            catch { }

            // Nagle off em cada interface de rede (TcpAckFrequency=1 + TCPNoDelay=1)
            try
            {
                const string basePath =
                    @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
                using var interfaces = Registry.LocalMachine.OpenSubKey(basePath);
                if (interfaces != null)
                {
                    foreach (var name in interfaces.GetSubKeyNames())
                    {
                        try
                        {
                            using var iface = Registry.LocalMachine.OpenSubKey(
                                basePath + "\\" + name, writable: true);
                            // Só toca em interfaces com IP configurado (ativas)
                            if (iface?.GetValue("DhcpIPAddress") == null &&
                                iface?.GetValue("IPAddress") == null) continue;

                            iface!.SetValue("TcpAckFrequency", 1, RegistryValueKind.DWord);
                            iface.SetValue("TCPNoDelay", 1, RegistryValueKind.DWord);
                            tweaks++;
                        }
                        catch { }
                    }
                }
            }
            catch { }

            return tweaks;
        }
    }
}
