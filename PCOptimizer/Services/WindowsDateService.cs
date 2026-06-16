using Microsoft.Win32;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Corrige a data e a hora do Windows. Resolve o caso comum de relógio errado
    /// (serviço de horário desativado, "ajustar automaticamente" desligado, bateria
    /// fraca da placa-mãe que dessincroniza o relógio a cada boot):
    ///
    ///   1. Reativa "Definir horário automaticamente" (fonte NTP) no registro.
    ///   2. Garante que o serviço Horário do Windows (W32Time) inicie sozinho e rode.
    ///   3. Aponta para servidores NTP confiáveis.
    ///   4. Força a sincronização imediata com a internet — a data/hora se corrigem na hora.
    ///
    /// Tudo via componentes nativos do Windows (sc, w32tm), sem depender de terceiros.
    /// Requer privilégios de administrador (o app pede UAC no Release).
    /// </summary>
    public static class WindowsDateService
    {
        public static bool Fix()
        {
            // 1. Reativa o "Definir horário automaticamente" (sincronização NTP).
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\W32Time\Parameters", writable: true);
                k?.SetValue("Type", "NTP", RegistryValueKind.String);
            }
            catch { /* sem permissão de registro: seguimos com w32tm mesmo assim */ }

            // 2. Garante que o serviço inicie automaticamente e esteja rodando.
            //    (o espaço depois de "start=" é exigido pelo sc.exe)
            ProcessRunner.Run("sc.exe", "config w32time start= auto", 30000);
            // "start" retorna erro se já estiver rodando — ignoramos o código de saída.
            ProcessRunner.Run("sc.exe", "start w32time", 30000);

            // 3. Define servidores NTP confiáveis e marca a máquina como fonte confiável.
            ProcessRunner.Run("w32tm.exe",
                "/config /manualpeerlist:\"pool.ntp.org time.windows.com\" " +
                "/syncfromflags:manual /reliable:yes /update", 30000);

            // 4. Força a sincronização imediata. O serviço recém-iniciado às vezes
            //    recusa o /force no primeiro instante; nesse caso tenta sem /force.
            bool ok = ProcessRunner.Run("w32tm.exe", "/resync /force", 60000);
            if (!ok)
                ok = ProcessRunner.Run("w32tm.exe", "/resync", 60000);
            return ok;
        }
    }
}
