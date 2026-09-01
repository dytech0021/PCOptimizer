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

        /// <summary>
        /// Base de um HardwareId, sem os pedaços que mudam entre versões do
        /// programa: o sufixo de desempate ("#2", "#i0"), o discriminador de
        /// interface anexado com "@" e o hash final depois do último "_".
        ///
        /// Serve para reencontrar estado salvo por uma versão anterior. O
        /// discriminador já mudou de formato uma vez (GetHashCode, que ainda por
        /// cima é aleatório por processo no .NET Core, para SHA-256), e nessa
        /// troca os registros ficaram órfãos — o usuário perdia o botão de
        /// reverter o 16:9. Sem a base não há como recuperar: o valor antigo não
        /// é recalculável, só reconhecível pelo prefixo.
        /// </summary>
        public static string SavedStateBase(string hardwareId)
        {
            if (string.IsNullOrWhiteSpace(hardwareId)) return "";

            string v = hardwareId.Split('#')[0].Split('@')[0];

            // Tira o hash final ("ddc_MG900_a1b2c3d4" -> "ddc_MG900"), mas só se
            // ele for mesmo um hash: um modelo com "_" no nome não pode encolher.
            int i = v.LastIndexOf('_');
            if (i > 0 && IsHex(v.AsSpan(i + 1)) && v.Length - i - 1 >= 8)
                v = v[..i];

            return v.ToLowerInvariant();
        }

        private static bool IsHex(ReadOnlySpan<char> s)
        {
            if (s.Length == 0) return false;
            foreach (char c in s)
                if (!Uri.IsHexDigit(c)) return false;
            return true;
        }
    }

    public static class RecoveryPolicy
    {
        public static bool CanClear(bool restorationSucceeded) => restorationSucceeded;
    }

    /// <summary>O que o botão de 16:9 deve oferecer neste monitor.</summary>
    public enum GameArAction
    {
        /// <summary>Não é ultrawide (ou não deu para ler o painel): nem mostra.</summary>
        Hide,
        /// <summary>Está em ultrawide: oferece aplicar o 16:9.</summary>
        OfferApply,
        /// <summary>Está em 16:9 e temos a resolução anterior guardada.</summary>
        OfferRevertToSaved,
        /// <summary>Está em 16:9 mas o registro se perdeu: volta para a nativa.</summary>
        OfferRevertToNative
    }

    /// <summary>
    /// Decide o estado do botão "Jogar em 16:9 (barras pretas)".
    ///
    /// Fica aqui, separado da janela, por dois motivos: a regra já falhou em
    /// silêncio uma vez (o botão sumiu de todos os monitores por causa de uma
    /// troca de campo) e, solta da UI, ela pode ser coberta por teste.
    ///
    /// A checagem de "é ultrawide" é pela resolução NATIVA, não pela atual —
    /// pela atual o botão sumia justamente depois de aplicado, e não dava mais
    /// para reverter.
    /// </summary>
    public static class GameArPolicy
    {
        private const double UltrawideRatio = 2.0;

        public static GameArAction Decide(
            (int W, int H)? native, (int W, int H)? current, bool hasSavedState)
        {
            if (native == null || native.Value.H <= 0 || native.Value.W <= 0)
                return GameArAction.Hide;
            if (native.Value.W / (double)native.Value.H <= UltrawideRatio)
                return GameArAction.Hide;

            // Painel ultrawide rodando numa proporção estreita = 16:9 já aplicado.
            bool narrowNow = current != null && current.Value.H > 0 && current.Value.W > 0
                          && current.Value.W / (double)current.Value.H <= UltrawideRatio;

            // Já está em ultrawide: só há o que aplicar. Um registro salvo aqui
            // é resto de uma reversão feita por fora (painel do Windows, driver)
            // — o próprio "aplicar" o sobrescreve com a resolução certa.
            if (!narrowNow) return GameArAction.OfferApply;

            // Já está em 16:9. Sem o registro da resolução anterior, aplicar de
            // novo gravaria o PRÓPRIO 16:9 como "anterior" e prenderia o usuário
            // nele — então a única saída correta é voltar para a nativa.
            return hasSavedState ? GameArAction.OfferRevertToSaved
                                 : GameArAction.OfferRevertToNative;
        }

        /// <summary>true se o botão deve aparecer no card do monitor.</summary>
        public static bool ShouldOffer(GameArAction action) => action != GameArAction.Hide;

        /// <summary>true se o botão deve aparecer no estado "voltar" (laranja).</summary>
        public static bool IsRevert(GameArAction action) =>
            action is GameArAction.OfferRevertToSaved or GameArAction.OfferRevertToNative;
    }
}
