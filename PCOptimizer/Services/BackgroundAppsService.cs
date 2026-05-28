using Microsoft.Win32;

namespace PCOptimizer.Services
{
    public static class BackgroundAppsService
    {
        public static bool Disable()
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications"))
                {
                    key?.SetValue("GlobalUserDisabled", 1, RegistryValueKind.DWord);
                }

                using (var search = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Search"))
                {
                    search?.SetValue("BackgroundAppGlobalToggle", 0, RegistryValueKind.DWord);
                }

                // Politica para todos os usuarios (requer admin)
                using (var policy = Registry.LocalMachine.CreateSubKey(
                    @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy"))
                {
                    policy?.SetValue("LetAppsRunInBackground", 2, RegistryValueKind.DWord); // 2 = bloqueado
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
