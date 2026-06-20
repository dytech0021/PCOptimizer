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
            catch { }
            return list;
        }

        /// <summary>
        /// Verifica se o driver da placa tem *WakeOnMagicPacket = 1 no registro.
        /// Isso indica que a configuração no lado do Windows está ativa.
        /// Atenção: o BIOS também precisa estar habilitado — isso não verificamos.
        /// </summary>
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
            catch { }
            return false;
        }

        /// <summary>
        /// Habilita WoL na placa via PowerShell:
        ///   1. Set-NetAdapterAdvancedProperty → *WakeOnMagicPacket = 1 (config do driver)
        ///   2. Enable-NetAdapterPowerManagement → "Permitir que este dispositivo ative o computador"
        /// Requer admin. Retorna true se o PowerShell saiu com ExitCode 0.
        /// </summary>
        public static bool EnableWoL(AdapterInfo adapter)
        {
            string safeName = adapter.Name.Replace("'", "''");
            string script = $@"
$ErrorActionPreference = 'SilentlyContinue'
$n = '{safeName}'
Set-NetAdapterAdvancedProperty -Name $n -RegistryKeyword '*WakeOnMagicPacket' -RegistryValue 1
Enable-NetAdapterPowerManagement -Name $n -WakeOnMagicPacket
";
            return RunPowerShell(script, timeoutMs: 20_000);
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
            catch { return false; }
        }
    }
}
