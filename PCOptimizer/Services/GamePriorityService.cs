using Microsoft.Win32;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Dá prioridade máxima de CPU/GPU para jogos via MMCSS e zera a reserva
    /// de CPU que o Windows mantém para tarefas em segundo plano (20% por padrão).
    /// </summary>
    public static class GamePriorityService
    {
        public static bool Apply()
        {
            try
            {
                using (var profile = Registry.LocalMachine.CreateSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile"))
                {
                    if (profile == null) return false;
                    // 0 = nenhuma CPU reservada para tarefas de fundo durante multimídia
                    profile.SetValue("SystemResponsiveness", 0, RegistryValueKind.DWord);
                }

                using (var games = Registry.LocalMachine.CreateSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games"))
                {
                    if (games == null) return false;
                    games.SetValue("GPU Priority", 8, RegistryValueKind.DWord);
                    games.SetValue("Priority", 6, RegistryValueKind.DWord);
                    games.SetValue("Scheduling Category", "High", RegistryValueKind.String);
                    games.SetValue("SFIO Priority", "High", RegistryValueKind.String);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
