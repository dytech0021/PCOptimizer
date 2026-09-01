using System;
using System.Security.Cryptography;
using System.Text;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Identidade completa de um target do DisplayConfig. O TargetId é único
    /// somente dentro do adaptador; em máquinas com mais de uma GPU ele pode se repetir.
    /// </summary>
    public readonly record struct HdrTargetKey(uint AdapterIdLow, int AdapterIdHigh, uint TargetId);

    /// <summary>Estado normalizado das APIs antiga e nova de Advanced Color.</summary>
    public readonly record struct AdvancedColorState(
        bool HdrSupported,
        bool HdrUserEnabled,
        bool HdrActive,
        bool WcgSupported,
        bool WcgUserEnabled,
        bool WcgActive,
        uint ActiveColorMode)
    {
        public static AdvancedColorState DecodeModern(uint value, uint activeColorMode) => new(
            HdrSupported: (value & (1u << 4)) != 0,
            HdrUserEnabled: (value & (1u << 5)) != 0,
            HdrActive: activeColorMode == 2,
            WcgSupported: (value & (1u << 6)) != 0,
            WcgUserEnabled: (value & (1u << 7)) != 0,
            WcgActive: activeColorMode == 1,
            ActiveColorMode: activeColorMode);

        public static AdvancedColorState DecodeLegacy(uint value)
        {
            bool supported = (value & 1u) != 0;
            bool active = (value & 2u) != 0;
            bool wideColorEnforced = (value & 4u) != 0;

            return wideColorEnforced
                ? new AdvancedColorState(false, false, false, supported, active, active,
                    active ? 1u : 0u)
                : new AdvancedColorState(supported, active, active, false, false, false,
                    active ? 2u : 0u);
        }
    }

    public static class MonitorIdentity
    {
        /// <summary>
        /// Hash determinístico e curto. String.GetHashCode muda entre processos e runtimes,
        /// portanto não serve como chave persistida de aliases/configurações.
        /// </summary>
        public static string StableDiscriminator(string monitorInterfacePath)
        {
            if (string.IsNullOrWhiteSpace(monitorInterfacePath)) return "0000000000000000";

            byte[] bytes = SHA256.HashData(
                Encoding.UTF8.GetBytes(monitorInterfacePath.Trim().ToUpperInvariant()));
            return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
        }
    }

    public static class RecoveryPolicy
    {
        public static bool CanClear(bool restorationSucceeded) => restorationSucceeded;
    }
}
