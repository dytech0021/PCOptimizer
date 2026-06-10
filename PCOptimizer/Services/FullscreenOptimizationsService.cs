using Microsoft.Win32;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Desativa as "Otimizações de Tela Cheia" globalmente — jogos passam a
    /// rodar em fullscreen exclusivo clássico, o que reduz input lag em muitos
    /// títulos (mas alguns jogos preferem o modo otimizado; é reversível).
    /// </summary>
    public static class FullscreenOptimizationsService
    {
        public static bool Disable()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"System\GameConfigStore");
                if (key == null) return false;
                key.SetValue("GameDVR_FSEBehavior", 2, RegistryValueKind.DWord);
                key.SetValue("GameDVR_FSEBehaviorMode", 2, RegistryValueKind.DWord);
                key.SetValue("GameDVR_HonorUserFSEBehaviorMode", 1, RegistryValueKind.DWord);
                key.SetValue("GameDVR_DXGIHonorFSEWindowsCompatible", 1, RegistryValueKind.DWord);
                key.SetValue("GameDVR_EFSEFeatureFlags", 0, RegistryValueKind.DWord);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
