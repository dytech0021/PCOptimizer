using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using Microsoft.Win32;

namespace PCOptimizer.Services
{
    public record AdapterInfo(string Name, string Description, string MacFormatted, string Id);

    public static class WakeOnLanService
    {
        // Subchave do Device Manager onde ficam os drivers de placa de rede.
        private const string AdapterClassKey =
            @"SYSTEM\CurrentControlSet\Control\Class\{4D36E972-E325-11CE-BFC1-08002BE10318}";

        /// <summary>
        /// Retorna as placas Ethernet físicas detectadas (exclui virtuais/loopback).
        /// </summary>
        public static List<AdapterInfo> GetPhysicalAdapters()
        {
            var list = new List<AdapterInfo>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet) continue;
                    if (ni.IsReceiveOnly) continue;

                    var macBytes = ni.GetPhysicalAddress().GetAddressBytes();
                    if (macBytes.Length != 6 || macBytes.All(b => b == 0)) continue;

                    string desc = ni.Description;
                    if (desc.Contains("Virtual",    StringComparison.OrdinalIgnoreCase) ||
                        desc.Contains("Miniport",   StringComparison.OrdinalIgnoreCase) ||
                        desc.Contains("Loopback",   StringComparison.OrdinalIgnoreCase) ||
                        desc.Contains("Hyper-V",    StringComparison.OrdinalIgnoreCase) ||
                        desc.Contains("VMware",     StringComparison.OrdinalIgnoreCase) ||
                        desc.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string mac = string.Join(":", Array.ConvertAll(macBytes, b => b.ToString("X2")));
                    list.Add(new AdapterInfo(ni.Name, desc, mac, ni.Id));
                }
            }
            catch (Exception ex) { Logger.Error(ex, "GetPhysicalAdapters"); }
            return list;
        }

        /// <summary>
        /// Verifica se o driver da placa tem *WakeOnMagicPacket = 1 no registro.
        /// Isso indica que a configuração no lado do Windows está ativa.
        /// Atenção: o BIOS também precisa estar habilitado — isso não verificamos.
        /// </summary>
        public static string GetAdapterIpAddress(AdapterInfo adapter)
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (!string.Equals(ni.Id, adapter.Id, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            return ua.Address.ToString();
                    }
                }
            }
            catch (Exception ex) { Logger.Error(ex, "GetAdapterIpAddress"); }
            return "—";
        }

        /// <summary>
        /// Retorna dispositivos atualmente "armados" para acordar o PC (powercfg /devicequery wake_armed).
        /// Requer admin para resultado completo.
        /// </summary>
        public static string[] GetWakeArmedDevices()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("powercfg")
                {
                    Arguments        = "/devicequery wake_armed",
                    RedirectStandardOutput = true,
                    UseShellExecute  = false,
                    CreateNoWindow   = true
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return Array.Empty<string>();
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                return output.Split(new[] { '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            catch (Exception ex) { Logger.Error(ex, "GetWakeArmedDevices"); return Array.Empty<string>(); }
        }

        public static bool IsWoLEnabled(AdapterInfo adapter)
        {
            try
            {
                using var classKey = Registry.LocalMachine.OpenSubKey(AdapterClassKey);
                if (classKey == null) return false;

                // NetworkInterface.Id tem o formato "{GUID}", o registro não tem chaves
                string id = adapter.Id.Trim('{', '}');

                foreach (var sub in classKey.GetSubKeyNames())
                {
                    // As entradas numéricas (0000, 0001 …) são instâncias de dispositivos.
                    if (!int.TryParse(sub, out _)) continue;
                    using var key = classKey.OpenSubKey(sub);
                    if (key == null) continue;

                    var netId = (key.GetValue("NetCfgInstanceId") as string)?.Trim('{', '}');
                    if (!string.Equals(netId, id, StringComparison.OrdinalIgnoreCase)) continue;

                    return (key.GetValue("*WakeOnMagicPacket") as string) == "1";
                }
            }
            catch (Exception ex) { Logger.Error(ex, "IsWoLEnabled"); }
            return false;
        }

        /// <summary>
        /// Habilita WoL na placa via PowerShell:
        ///   1. *WakeOnMagicPacket = 1 (config do driver)
        ///   2. Enable-NetAdapterPowerManagement → "Permitir que este dispositivo ative o computador"
        ///   3. Desliga Energy Efficient Ethernet / Green Ethernet (atrapalham o WoL
        ///      ao colocar a placa em economia de energia profunda)
        ///   4. Desativa a Inicialização Rápida (Fast Startup) — o "desligamento falso"
        ///      do Windows impede o rearme do WoL após desligar.
        /// Requer admin. Retorna true se o PowerShell saiu com ExitCode 0.
        /// </summary>
        public static bool EnableWoL(AdapterInfo adapter)
        {
            string safeName = adapter.Name.Replace("'", "''");
            string safeDesc = adapter.Description.Replace("'", "''");
            // Liga o WoL e o "acordar por magic packet"; desliga as economias de
            // energia da placa que costumam quebrar o WoL. Cada keyword pode não
            // existir em todo driver — SilentlyContinue ignora as ausentes.
            string script = $@"
$ErrorActionPreference = 'SilentlyContinue'
$n = '{safeName}'
Set-NetAdapterAdvancedProperty -Name $n -RegistryKeyword '*WakeOnMagicPacket' -RegistryValue 1
Enable-NetAdapterPowerManagement -Name $n -WakeOnMagicPacket
# Desliga economias que botam a placa em sono profundo (matam o WoL):
Set-NetAdapterAdvancedProperty -Name $n -RegistryKeyword '*EEE' -RegistryValue 0
Set-NetAdapterAdvancedProperty -Name $n -DisplayName 'Energy Efficient Ethernet' -DisplayValue 'Disabled'
Set-NetAdapterAdvancedProperty -Name $n -DisplayName 'Green Ethernet' -DisplayValue 'Disabled'
Set-NetAdapterAdvancedProperty -Name $n -RegistryKeyword 'EnableGreenEthernet' -RegistryValue 0
# Garante que a placa pode ligar o PC mesmo no modo de menor energia
# (powercfg usa a DESCRIÇÃO do dispositivo, não o nome da conexão):
powercfg -deviceenablewake '{safeDesc}'
";
            bool ok = RunPowerShell(script, timeoutMs: 25_000);

            // Fast Startup atrapalha o rearme do WoL após o desligamento — desativa.
            bool fastOff = FastStartupService.Disable();

            Logger.Info($"EnableWoL '{adapter.Name}' ({adapter.MacFormatted}): " +
                        $"PowerShell={(ok ? "ok" : "falhou")}, FastStartupOff={fastOff}");
            return ok;
        }

        private static bool RunPowerShell(string script, int timeoutMs)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = "powershell.exe",
                    CreateNoWindow  = true,
                    UseShellExecute = false
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-WindowStyle");
                psi.ArgumentList.Add("Hidden");
                psi.ArgumentList.Add("-EncodedCommand");
                psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));

                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return false;
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return false; }
                return p.ExitCode == 0;
            }
            catch (Exception ex) { Logger.Error(ex, "RunPowerShell"); return false; }
        }
    }
}
