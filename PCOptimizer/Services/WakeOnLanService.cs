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

        /// <summary>Saída do último EnableWoL (o que o driver aceitou/recusou).</summary>
        public static string LastEnableLog { get; private set; } = "";

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
                // Leitura assíncrona: ReadToEnd síncrono bloquearia até o processo
                // sair, anulando o timeout (e prendendo o botão Diagnóstico).
                var outTask = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(5000))
                {
                    try { p.Kill(true); } catch { }
                    return Array.Empty<string>();
                }
                p.WaitForExit(); // drena o stream redirecionado após a saída
                string output = outTask.GetAwaiter().GetResult();
                return output.Split(new[] { '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            catch (Exception ex) { Logger.Error(ex, "GetWakeArmedDevices"); return Array.Empty<string>(); }
        }

        /// <summary>
        /// Acha o nome da subchave (0000, 0001 …) do driver desta placa no registro,
        /// casando pelo NetCfgInstanceId. Retorna null se não encontrar.
        /// </summary>
        private static string? FindAdapterRegistrySubKey(AdapterInfo adapter)
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(AdapterClassKey);
            if (classKey == null) return null;

            // NetworkInterface.Id tem o formato "{GUID}", o registro não tem chaves
            string id = adapter.Id.Trim('{', '}');

            foreach (var sub in classKey.GetSubKeyNames())
            {
                // As entradas numéricas (0000, 0001 …) são instâncias de dispositivos.
                if (!int.TryParse(sub, out _)) continue;
                using var key = classKey.OpenSubKey(sub);
                if (key == null) continue;

                var netId = (key.GetValue("NetCfgInstanceId") as string)?.Trim('{', '}');
                if (string.Equals(netId, id, StringComparison.OrdinalIgnoreCase))
                    return sub;
            }
            return null;
        }

        public static bool IsWoLEnabled(AdapterInfo adapter)
        {
            try
            {
                string? sub = FindAdapterRegistrySubKey(adapter);
                if (sub == null) return false;
                using var key = Registry.LocalMachine.OpenSubKey($@"{AdapterClassKey}\{sub}");
                // Convert.ToString tolera REG_SZ e REG_DWORD — alguns drivers gravam
                // o valor como número, e "as string" retornava null nesses casos.
                return Convert.ToString(key?.GetValue("*WakeOnMagicPacket"))?.Trim() == "1";
            }
            catch (Exception ex) { Logger.Error(ex, "IsWoLEnabled"); }
            return false;
        }

        /// <summary>
        /// Escreve as chaves de Wake-on-LAN direto no registro do driver. Funciona mesmo
        /// quando o driver (ex.: Realtek GbE) não expõe as propriedades avançadas via CIM
        /// (Set-NetAdapterAdvancedProperty). O driver lê esses valores ao reiniciar a placa.
        /// </summary>
        private static string WriteWoLRegistry(AdapterInfo adapter)
        {
            try
            {
                string? sub = FindAdapterRegistrySubKey(adapter);
                if (sub == null) return "registro: chave da placa não encontrada";

                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"{AdapterClassKey}\{sub}", writable: true);
                if (key == null) return "registro: sem permissão de escrita";

                var sb = new StringBuilder();
                void Set(string name, string val)
                {
                    key.SetValue(name, val, RegistryValueKind.String);
                    sb.Append(name).Append('=').Append(val).Append(' ');
                }

                // Liga o magic packet e o WoL de desligado (Realtek = EnableWakeOnLan):
                Set("*WakeOnMagicPacket", "1");
                Set("*WakeOnPattern",     "1");
                Set("EnableWakeOnLan",    "1");
                Set("WolShutdownLinkSpeed", "0"); // 0 = mantém velocidade (não derruba o link)
                // Desliga economias que botam a placa em sono profundo (matam o WoL):
                Set("*EEE",                "0");
                Set("EnableGreenEthernet", "0");
                Set("*PMARPOffload",       "0");
                Set("*PMNSOffload",        "0");

                return "registro: " + sb.ToString().Trim();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "WriteWoLRegistry");
                return "registro: erro " + ex.Message;
            }
        }

        /// <summary>
        /// Habilita WoL de forma robusta (funciona em drivers Realtek que não expõem
        /// propriedades avançadas):
        ///   1. Escreve as chaves de WoL direto no registro do driver
        ///   2. Liga o gerenciamento de energia (acordar por magic packet)
        ///   3. Reinicia a placa para aplicar o registro
        ///   4. Arma o dispositivo via powercfg -deviceenablewake
        ///   5. Desativa a Inicialização Rápida (impede o rearme após desligar)
        /// Requer admin. Confirma lendo o registro e a lista wake_armed.
        /// </summary>
        public static bool EnableWoL(AdapterInfo adapter)
        {
            // O PowerShell também encerra strings single-quoted nas aspas tipográficas
            // (U+2018–U+201B) — normaliza antes de duplicar, senão uma descrição de
            // driver localizada quebra o parse do script inteiro e nada roda.
            string safeDesc = adapter.Description
                .Replace('‘', '\'').Replace('’', '\'')
                .Replace('‚', '\'').Replace('‛', '\'')
                .Replace("'", "''");
            string mac      = adapter.MacFormatted; // "E0:E0:4C:F3:06:C1"

            // 1) Escreve as chaves de WoL DIRETO no registro. Funciona mesmo quando o
            //    driver (ex.: Realtek GbE) não expõe nada via Set-NetAdapterAdvancedProperty.
            string regResult = WriteWoLRegistry(adapter);

            // 2) PowerShell só para: aplicar (reiniciar a placa), ligar o gerenciamento
            //    de energia e armar via powercfg. Acha a placa pelo MAC (à prova de nome).
            //    ProgressPreference Silent evita a saída embaralhada de progresso.
            string script = $@"
$ProgressPreference = 'SilentlyContinue'
$ErrorActionPreference = 'SilentlyContinue'
$mac = '{mac}'
$a = Get-NetAdapter | Where-Object {{ ($_.MacAddress -replace '-',':') -eq $mac }} | Select-Object -First 1
if (-not $a) {{ $a = Get-NetAdapter -InterfaceDescription '{safeDesc}' | Select-Object -First 1 }}
if (-not $a) {{ Write-Output 'FAIL-> placa nao encontrada por MAC nem descricao'; exit 1 }}
$n = $a.Name
Write-Output ('Placa: ' + $n + ' / ' + $a.InterfaceDescription)
function TrySet($desc, $sb) {{
    try {{ & $sb; Write-Output ('OK  -> ' + $desc) }}
    catch {{ Write-Output ('FAIL-> ' + $desc + ' :: ' + $_.Exception.Message) }}
}}
TrySet 'Gerenciamento de energia (acordar por magic packet)' {{ Enable-NetAdapterPowerManagement -Name $n -WakeOnMagicPacket -ErrorAction Stop }}
TrySet 'Reiniciar placa (aplica o registro)' {{ Restart-NetAdapter -Name $n -ErrorAction Stop }}
TrySet 'powercfg -deviceenablewake (armar)' {{ $r = powercfg -deviceenablewake $a.InterfaceDescription; if ($LASTEXITCODE -ne 0) {{ throw ('powercfg saiu ' + $LASTEXITCODE) }} }}
Write-Output '--- Dispositivos armados (wake_armed) ---'
powercfg /devicequery wake_armed
";
            var (ok, output) = RunPowerShell(script, timeoutMs: 45_000);
            output = regResult + "\n" + output;

            // Fast Startup atrapalha o rearme do WoL após o desligamento — desativa.
            bool fastOff = FastStartupService.Disable();

            // Confirma lendo o registro de novo — é a verdade, não o exit code.
            bool applied = IsWoLEnabled(adapter);

            LastEnableLog = output;
            Logger.Info($"EnableWoL '{adapter.Name}' ({adapter.MacFormatted}): " +
                        $"processo={(ok ? "ok" : "falhou")}, registroWoL={applied}, " +
                        $"FastStartupOff={fastOff}\n{output}");
            return applied || ok;
        }

        private static (bool ok, string output) RunPowerShell(string script, int timeoutMs)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName               = "powershell.exe",
                    CreateNoWindow         = true,
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true
                };
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-WindowStyle");
                psi.ArgumentList.Add("Hidden");
                psi.ArgumentList.Add("-EncodedCommand");
                psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));

                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return (false, "Não foi possível iniciar o powershell.exe");

                // Lê os DOIS streams em paralelo e espera com timeout — ReadToEnd
                // síncrono bloquearia até o processo sair, anulando o timeout (e
                // travando a janela se o driver pendurar o PowerShell).
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(true); } catch { }
                    p.WaitForExit(3000);
                    string partial = (outTask.IsCompleted ? outTask.Result : "") +
                                     (errTask.IsCompleted ? errTask.Result : "");
                    return (false, ("timeout\n" + partial).Trim());
                }
                p.WaitForExit(); // drena os streams redirecionados após a saída

                string output = (outTask.GetAwaiter().GetResult() +
                                 errTask.GetAwaiter().GetResult()).Trim();
                return (p.ExitCode == 0, output);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "RunPowerShell");
                return (false, ex.Message);
            }
        }
    }
}
