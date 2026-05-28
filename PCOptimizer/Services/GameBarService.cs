using Microsoft.Win32;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Desativa a Xbox Game Bar e a gravacao em segundo plano (Game DVR).
    /// </summary>
    public static class GameBarService
    {
        public static bool Disable()
        {
            try
            {
                using (var gameStore = Registry.CurrentUser.CreateSubKey(@"System\GameConfigStore"))
                {
                    gameStore?.SetValue("GameDVR_Enabled", 0, RegistryValueKind.DWord);
                    gameStore?.SetValue("GameDVR_FSEBehaviorMode", 2, RegistryValueKind.DWord);
                }

                using (var gameDvr = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\GameDVR"))
                {
                    gameDvr?.SetValue("AppCaptureEnabled", 0, RegistryValueKind.DWord);
                }

                using (var policy = Registry.LocalMachine.CreateSubKey(
                    @"SOFTWARE\Policies\Microsoft\Windows\GameDVR"))
                {
                    policy?.SetValue("AllowGameDVR", 0, RegistryValueKind.DWord);
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
