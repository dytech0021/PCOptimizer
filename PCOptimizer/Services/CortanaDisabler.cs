using Microsoft.Win32;

namespace PCOptimizer.Services
{
    public static class CortanaDisabler
    {
        public static bool Disable()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", writable: true)
                    ?? Registry.LocalMachine.CreateSubKey(
                    @"SOFTWARE\Policies\Microsoft\Windows\Windows Search");

                if (key != null)
                {
                    key.SetValue("AllowCortana", 0, RegistryValueKind.DWord);
                    key.SetValue("AllowSearchToUseLocation", 0, RegistryValueKind.DWord);
                    key.SetValue("AllowCortanaAboveLock", 0, RegistryValueKind.DWord);
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}
