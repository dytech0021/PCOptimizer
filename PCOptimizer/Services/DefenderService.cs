using System;
using System.Diagnostics;
using System.Text;

namespace PCOptimizer.Services
{
    public sealed class DefenderStatus
    {
        public bool   Available           { get; init; }
        public bool   AntivirusEnabled    { get; init; }
        public bool   RealTimeEnabled     { get; init; }
        public int    SignatureAgeDays    { get; init; } = -1;
        public string SignatureVersion    { get; init; } = "";
        public string LastQuickScan       { get; init; } = "";
        public string RawError            { get; init; } = "";
    }

    /// <summary>
    /// Integração com o Microsoft Defender (antivírus embutido do Windows 10/11).
    /// Usa os cmdlets do módulo Defender via PowerShell. Não reinventa engine de AV:
    /// dá uma interface melhor para o que o Windows já faz (status, atualizar
    /// definições, varredura rápida/completa e histórico de ameaças).
    /// </summary>
    public static class DefenderService
    {
        /// <summary>Lê o status atual do Defender (Get-MpComputerStatus).</summary>
        public static DefenderStatus GetStatus()
        {
            string script =
                "$ErrorActionPreference='Stop';" +
                "$s = Get-MpComputerStatus;" +
                "Write-Output ('AV=' + $s.AntivirusEnabled);" +
                "Write-Output ('RT=' + $s.RealTimeProtectionEnabled);" +
                "Write-Output ('AGE=' + $s.AntivirusSignatureAge);" +
                "Write-Output ('VER=' + $s.AntivirusSignatureVersion);" +
                "Write-Output ('LASTQS=' + $s.QuickScanEndTime);";

            var (ok, output) = RunPowerShell(script, 15000);
            if (!ok)
                return new DefenderStatus { Available = false, RawError = output };

            bool av = false, rt = false;
            int age = -1;
            string ver = "", lastQs = "";
            foreach (var line in output.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("AV="))      bool.TryParse(t.Substring(3), out av);
                else if (t.StartsWith("RT=")) bool.TryParse(t.Substring(3), out rt);
                else if (t.StartsWith("AGE=")) int.TryParse(t.Substring(4), out age);
                else if (t.StartsWith("VER=")) ver = t.Substring(4);
                else if (t.StartsWith("LASTQS=")) lastQs = t.Substring(7);
            }

            return new DefenderStatus
            {
                Available        = true,
                AntivirusEnabled = av,
                RealTimeEnabled  = rt,
                SignatureAgeDays = age,
                SignatureVersion = ver,
                LastQuickScan    = lastQs
            };
        }

        /// <summary>Histórico de ameaças já detectadas pelo Defender (Get-MpThreatDetection).</summary>
        public static string GetThreatHistory()
        {
            // Monta hashtable de ThreatID → ThreatName em O(n) antes do loop,
            // evitando chamar Get-MpThreat dentro de cada iteração (bug O(n²) anterior).
            string script =
                "$ErrorActionPreference='SilentlyContinue';" +
                "$tmap = @{};" +
                "Get-MpThreat | ForEach-Object { $tmap[$_.ThreatID] = $_.ThreatName };" +
                "$d = Get-MpThreatDetection | Sort-Object InitialDetectionTime -Descending | Select-Object -First 15;" +
                "if (-not $d) { Write-Output 'Nenhuma ameaça no histórico.' } else {" +
                "$d | ForEach-Object { Write-Output (($_.InitialDetectionTime) + '  ' + $tmap[$_.ThreatID]) } }";

            var (ok, output) = RunPowerShell(script, 20000);
            return ok ? output.Trim() : "Não foi possível ler o histórico.\n" + output;
        }

        /// <summary>Atualiza as definições de vírus (Update-MpSignature).</summary>
        public static bool UpdateSignatures()
        {
            var (ok, output) = RunPowerShell("Update-MpSignature -ErrorAction Stop; Write-Output 'OK'", 120000);
            Logger.Info($"Defender UpdateSignatures: {(ok ? "ok" : "falhou")} {output}");
            return ok;
        }

        /// <summary>Varredura rápida (Start-MpScan -ScanType QuickScan). Pode levar minutos.</summary>
        public static bool QuickScan()  => StartScan("QuickScan");

        /// <summary>Varredura completa (Start-MpScan -ScanType FullScan). Pode levar muito tempo.</summary>
        public static bool FullScan()   => StartScan("FullScan");

        private static bool StartScan(string scanType)
        {
            // timeout alto: o scan rápido leva alguns minutos; o completo, bem mais.
            int timeout = scanType == "FullScan" ? 3_600_000 : 600_000;
            var (ok, output) = RunPowerShell(
                $"Start-MpScan -ScanType {scanType} -ErrorAction Stop; Write-Output 'OK'", timeout);
            Logger.Info($"Defender {scanType}: {(ok ? "ok" : "falhou")} {output}");
            return ok;
        }

        private static (bool ok, string output) RunPowerShell(string script, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo
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

                using var p = Process.Start(psi);
                if (p == null) return (false, "Não foi possível iniciar o powershell.exe");

                // Lê os DOIS streams em paralelo e espera com timeout — ReadToEnd
                // síncrono bloquearia até o processo sair, anulando o timeout.
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(true); } catch { }
                    return (false, "timeout");
                }
                p.WaitForExit(); // drena os streams redirecionados após a saída

                return (p.ExitCode == 0,
                    (outTask.GetAwaiter().GetResult() + errTask.GetAwaiter().GetResult()).Trim());
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "DefenderService.RunPowerShell");
                return (false, ex.Message);
            }
        }
    }
}
