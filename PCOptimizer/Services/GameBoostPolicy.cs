using System;
using System.Collections.Generic;
using System.Linq;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Regras comuns aos dois Game Boosts (híbrido e núcleos reservados), sem
    /// Windows, para poderem ser cobertas por teste.
    /// </summary>
    public static class GameBoostPolicy
    {
        /// <summary>
        /// Programas que o Game Boost NUNCA prende nos núcleos de fundo nem baixa
        /// a prioridade. Todos trabalham colados no jogo:
        /// - plataformas (Steam, Epic…): o jogo faz chamadas ao cliente e ESPERA a
        ///   resposta — cliente lento trava o jogo junto. E o jogo nasce deles:
        ///   um processo filho HERDA afinidade e prioridade baixa, então um jogo
        ///   aberto pelo Steam preso rodaria inteiro em 2 núcleos (ou nos E-cores);
        /// - overlays e captura: desenham ou gravam junto com cada quadro;
        /// - voz e roteamento de áudio: o som do jogo pode passar por eles.
        /// </summary>
        public static readonly IReadOnlySet<string> KeepOutNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Plataformas e launchers
            "steam", "steamwebhelper", "EpicGamesLauncher", "EpicWebHelper",
            "EOSOverlayRenderer-Win64-Shipping", "EOSOverlayRenderer-Win32-Shipping",
            "Battle.net", "Agent", "EADesktop", "EABackgroundService", "Origin", "OriginWebHelperService",
            "RiotClientServices", "RiotClientUx", "RiotClientUxRender", "upc", "UbisoftConnect", "UplayWebCore",
            "GalaxyClient", "GalaxyClientService", "XboxPcApp", "FACEIT",
            "Playnite.DesktopApp", "Playnite.FullscreenApp",

            // Overlays e captura
            "GameBar", "GameBarFTServer", "NVIDIA Share", "nvsphelper64", "NVIDIA Overlay", "NVIDIA app",
            "AMDRSServ", "AMDRSSrcExt", "RadeonSoftware", "amdow", "RTSS", "MSIAfterburner",
            "Streamlabs OBS",

            // Voz e áudio
            "Discord", "DiscordPTB", "DiscordCanary", "ts3client_win64", "ts3client_win32", "TeamSpeak",
            "mumble", "NVIDIA Broadcast", "SteelSeriesSonar", "WaveLink",
        };

        /// <summary>Famílias de programas pelo começo do nome.</summary>
        public static readonly IReadOnlyList<string> KeepOutPrefixes = new[]
        {
            "voicemeeter", "RTSSHooksLoader", "EAAntiCheat",
        };

        public static bool IsKeepOut(string? name) =>
            !string.IsNullOrEmpty(name)
            && (KeepOutNames.Contains(name)
                || KeepOutPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)));

        /// <summary>
        /// A ativação automática pega a janela da frente. Navegador em vídeo de tela
        /// cheia, Steam em Big Picture, Discord… não são jogo: virar "alvo" prenderia
        /// o resto — inclusive o Steam, que depois abriria o jogo já preso.
        /// </summary>
        public static bool IsPlausibleAutoTarget(string? name) =>
            !string.IsNullOrEmpty(name)
            && !MemoryOptimizerPolicy.KnownNonGameApps.Contains(name)
            && !IsKeepOut(name);

        public const uint IdlePriorityClass = 0x00000040;
        public const uint BelowNormalPriorityClass = 0x00004000;

        /// <summary>
        /// O jogo está preso como se fosse um programa de fundo? Acontece quando
        /// ele herda a afinidade/prioridade de quem o abriu. Com o jogo assim, o
        /// Game Boost só piora: tudo disputa os mesmos poucos núcleos.
        /// </summary>
        public static bool GameLooksConfined(ulong gameAffinity, uint gamePriority, ulong confineMask)
        {
            if (gamePriority == BelowNormalPriorityClass || gamePriority == IdlePriorityClass) return true;
            return confineMask != 0 && gameAffinity != 0 && (gameAffinity & ~confineMask) == 0;
        }

        public const string ConfinedGameText =
            "O jogo está com núcleos limitados ou prioridade baixa (herdou de quem o abriu) — " +
            "o Game Boost foi desligado. Feche o jogo e abra de novo";
    }
}
