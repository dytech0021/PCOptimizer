using Microsoft.Win32;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Garante que o Modo de Jogo do Windows esteja ativado.
    /// O Windows prioriza o jogo em primeiro plano e suspende tarefas de fundo.
    /// </summary>
    public static class GameModeService
    {
        public static bool Enable()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\GameBar");
                if (key == null) return false;
                key.SetValue("AutoGameModeEnabled", 1, RegistryValueKind.DWord);
                key.SetValue("AllowAutoGameMode", 1, RegistryValueKind.DWord);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
