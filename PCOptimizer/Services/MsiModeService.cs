using System;
using Microsoft.Win32;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Modo Expert: ativa Message Signaled Interrupts na GPU e nas placas de rede
    /// (interrupções mais rápidas que o modo legado line-based) e aplica
    /// Win32PrioritySeparation 0x26 — boost agressivo do app em primeiro plano.
    /// Tweaks de guias de latência competitiva; efeito após reiniciar.
    /// </summary>
    public static class MsiModeService
    {
        public static int Apply()
        {
            int applied = 0;

            try
            {
                using var pci = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Enum\PCI");
                if (pci != null)
                {
                    foreach (var device in pci.GetSubKeyNames())
                    {
                        using var devKey = pci.OpenSubKey(device);
                        if (devKey == null) continue;
                        foreach (var instance in devKey.GetSubKeyNames())
                        {
                            try
                            {
                                using var inst = devKey.OpenSubKey(instance);
                                string? cls = inst?.GetValue("Class") as string;
                                // Só GPU e rede — MSI em controladores errados pode dar BSOD
                                if (cls != "Display" && cls != "Net") continue;

                                using var msi = Registry.LocalMachine.CreateSubKey(
                                    $@"SYSTEM\CurrentControlSet\Enum\PCI\{device}\{instance}" +
                                    @"\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties");
                                if (msi == null) continue;
                                msi.SetValue("MSISupported", 1, RegistryValueKind.DWord);
                                applied++;
                            }
                            catch { /* instância protegida — pula */ }
                        }
                    }
                }
            }
            catch { }

            try
            {
                using var prio = Registry.LocalMachine.CreateSubKey(
                    @"SYSTEM\CurrentControlSet\Control\PriorityControl");
                if (prio != null)
                {
                    prio.SetValue("Win32PrioritySeparation", 0x26, RegistryValueKind.DWord);
                    applied++;
                }
            }
            catch { }

            return applied;
        }
    }
}
