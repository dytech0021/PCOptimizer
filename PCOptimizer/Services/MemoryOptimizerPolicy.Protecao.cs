using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PCOptimizer.Services
{
    // Parte das regras de memória: listas de processos e pastas que a limpeza nunca toca.
    public static partial class MemoryOptimizerPolicy
    {
        // ── Processos que a limpeza nunca toca ────────────────────────────────

        /// <summary>
        /// Nomes (sem extensão) que nunca são esvaziados. A base é a mesma lista
        /// do Game Boost — copiada, não compartilhada, para este recurso não
        /// mexer em nada do Game Boost.
        /// </summary>
        public static readonly IReadOnlySet<string> ProtectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Anticheat e plataformas
            "EasyAntiCheat", "EasyAntiCheat_EOS", "start_protected_game",
            "BEService", "BEDaisy", "BattlEyeLauncher",
            "vgc", "vgtray", "GameMon", "GameMon64", "npggNT",
            "steamservice", "GameOverlayUI",

            // Áudio do sistema
            "audiodg",

            // Shell, composição e núcleo do Windows
            "dwm", "explorer", "csrss", "wininit", "winlogon", "smss", "services",
            "lsass", "fontdrvhost", "Registry", "MemCompression", "Memory Compression",
            "System", "Idle", "Secure System",

            // Digitação e sessão: esvaziar estes dá atraso ao digitar e no menu
            "ctfmon", "TextInputHost", "sihost", "MsMpEng", "NisSrv", "SecurityHealthService",

            // Captura e gravação
            "obs64", "obs32", "nvcontainer", "NVDisplay.Container", "Streamlabs OBS",

            // Máquinas virtuais: esvaziar a RAM delas derruba o convidado
            "vmmem", "vmmemWSL", "VBoxHeadless", "VirtualBoxVM", "vmware-vmx",

            // Voz e música: esvaziar a memória deles estala o áudio — o som de
            // um app sai do processo DELE, não só do audiodg
            "Discord", "DiscordPTB", "DiscordCanary", "Spotify",
            "ts3client_win64", "ts3client_win32", "TeamSpeak", "mumble",
            "Zoom", "ms-teams", "Teams", "Skype", "WhatsApp",
            "NVIDIA Broadcast", "SteelSeriesSonar", "SteelSeriesGG", "WaveLink",
            "vlc", "foobar2000", "AIMP", "MusicBee", "Soundpad",
        };

        /// <summary>Prefixos de nome protegidos (famílias de programas).</summary>
        public static readonly IReadOnlyList<string> ProtectedPrefixes = new[]
        {
            "voicemeeter", "EasyAntiCheat", "BattlEye",
        };

        /// <summary>
        /// Trechos de caminho de bibliotecas de jogos. Um processo instalado
        /// nelas nunca é esvaziado, mesmo que não pareça um jogo agora.
        /// </summary>
        public static readonly IReadOnlyList<string> GameLibraryMarkers = new[]
        {
            @"\steamapps\common\", @"\Epic Games\", @"\Riot Games\", @"\XboxGames\",
            @"\GOG Galaxy\Games\", @"\GOG Games\", @"\Ubisoft Game Launcher\games\",
            @"\EA Games\", @"\WindowsApps\",
        };

        /// <summary>
        /// Donos de janelas que cobrem a tela sem serem jogo: sobreposições de
        /// placa de vídeo, Game Bar e partes do shell.
        /// </summary>
        public static readonly IReadOnlySet<string> NonGameWindowOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "dwm", "nvcontainer", "NVIDIA Share", "NVIDIA Overlay", "nvsphelper64",
            "GameBar", "GameBarFTServer", "XboxGameBarWidgets", "XboxPcAppFT",
            "RTSS", "EncoderServer", "TextInputHost", "ShellExperienceHost",
            "StartMenuExperienceHost", "SearchHost", "SearchApp", "LockApp",
            "ApplicationFrameHost", "SystemSettings", "AMDRSServ", "RadeonSoftware",
        };

        /// <summary>
        /// Launchers: a janela do Epic é UnrealWindow e a do Steam é SDL_app, as
        /// mesmas classes de jogos — mas eles não são jogo. Ficam fora da lista
        /// acima porque continuam podendo proteger a própria árvore (o jogo
        /// aberto por eles é filho deles).
        /// </summary>
        public static readonly IReadOnlySet<string> LauncherOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "EpicGamesLauncher", "steamwebhelper", "steam", "Battle.net", "EADesktop",
            "RiotClientUx", "RiotClientServices", "GalaxyClient", "upc", "UbisoftConnect",
        };

        /// <summary>
        /// Apps que, com janela na tela, nunca são jogo: só eles podem ser
        /// esvaziados no nível Máximo. Por nome do executável…
        /// </summary>
        public static readonly IReadOnlySet<string> KnownNonGameApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "iexplore",
            "Code", "Cursor", "devenv", "rider64", "idea64", "pycharm64", "notepad", "notepad++",
            "slack", "Notion", "Obsidian", "Figma", "Postman", "Telegram",
            "WINWORD", "EXCEL", "POWERPNT", "OUTLOOK", "ONENOTE", "Acrobat", "AcroRd32",
            "EpicGamesLauncher", "steamwebhelper", "steam", "Battle.net", "EADesktop",
            "RiotClientUx", "RiotClientServices", "GalaxyClient", "upc", "UbisoftConnect",
            "qbittorrent", "Photoshop", "Illustrator",
            // Electron conhecidos (pela classe da janela não dá: jogos usam a mesma)
            "Teams", "ms-teams", "Zoom", "Signal", "GitHubDesktop", "Postman", "Insomnia",
            "Trello", "Todoist", "1Password", "Bitwarden", "Mattermost", "Element",
        };

        /// <summary>
        /// …ou pela classe da janela (Firefox, Office). A classe do Chromium/
        /// Electron NÃO entra: jogos feitos em NW.js/Electron (RPG Maker e muitos
        /// do itch.io) usam a mesma — esses apps são reconhecidos pelo nome.
        /// </summary>
        public static readonly IReadOnlySet<string> KnownNonGameWindowClasses = new HashSet<string>(StringComparer.Ordinal)
        {
            "MozillaWindowClass", "OpusApp", "XLMAIN", "PPTFrameClass", "rctrl_renwnd32", "Notepad",
        };

        /// <summary>
        /// Processos que abrem jogos. Nunca têm a prioridade de memória baixada:
        /// o filho herda a prioridade do pai, e o jogo nasceria com prioridade baixa.
        /// </summary>
        public static readonly IReadOnlySet<string> GameLauncherProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "steam", "steamwebhelper", "EpicGamesLauncher", "EpicWebHelper", "Battle.net", "Agent",
            "EADesktop", "EABackgroundService", "RiotClientServices", "RiotClientUx", "upc", "UbisoftConnect",
            "GalaxyClient", "GalaxyClientService", "Playnite.DesktopApp", "Playnite.FullscreenApp", "itch",
            "Heroic", "LaunchBox", "BigBox", "MinecraftLauncher", "PrismLauncher", "XboxPcApp",
            "Amazon Games UI", "Launcher", "RockstarService", "PowerToys.PowerLauncher", "explorer",
        };

        /// <summary>Serviços que nunca são esvaziados no nível Máximo.</summary>
        public static readonly IReadOnlySet<string> ServiceDenyList = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Áudio e Bluetooth (fone)
            "Audiosrv", "AudioEndpointBuilder", "MMCSS", "BthAvctpSvc", "BTAGService", "bthserv",
            // Rede: ping e DNS
            "Dhcp", "Dnscache", "NlaSvc", "nsi", "WlanSvc", "Netman", "netprofm", "LanmanWorkstation",
            // Jogos e Xbox
            "GamingServices", "GamingServicesNet", "XblAuthManager", "XblGameSave", "XboxNetApiSvc", "XboxGipSvc",
            // Anticheat
            "BEService", "EasyAntiCheat", "EasyAntiCheat_EOS", "vgc", "faceit", "FACEITService",
            // Interface: tema, fontes, digitação
            "Themes", "FontCache", "TextInputManagementService", "TabletInputService",
            // Proteção do sistema
            "WinDefend", "mpssvc", "BFE", "SecurityHealthService", "RpcSs", "RpcEptMapper", "DcomLaunch",
            "LSM", "EventLog", "Power", "PlugPlay",
        };

        /// <summary>Apps da Store que não são jogo: fora da exclusão de "\WindowsApps\".</summary>
        public static readonly IReadOnlyList<string> NonGameStorePackages = new[]
        {
            @"\WindowsApps\Microsoft.YourPhone", @"\WindowsApps\MicrosoftWindows.Client.WebExperience",
            @"\WindowsApps\Microsoft.WindowsTerminal", @"\WindowsApps\Microsoft.OutlookForWindows",
            @"\WindowsApps\Microsoft.GamingApp", @"\WindowsApps\Microsoft.WindowsStore",
            @"\WindowsApps\Microsoft.Windows.Photos", @"\WindowsApps\Microsoft.ScreenSketch",
            @"\WindowsApps\Microsoft.WindowsCalculator", @"\WindowsApps\Microsoft.WindowsNotepad",
            @"\WindowsApps\Microsoft.Paint", @"\WindowsApps\Microsoft.Todos",
            @"\WindowsApps\Microsoft.MicrosoftStickyNotes", @"\WindowsApps\Microsoft.ZuneMusic",
            @"\WindowsApps\Microsoft.ZuneVideo", @"\WindowsApps\Microsoft.BingWeather",
            @"\WindowsApps\Microsoft.BingNews", @"\WindowsApps\MSTeams", @"\WindowsApps\Microsoft.Copilot",
            @"\WindowsApps\Clipchamp", @"\WindowsApps\Microsoft.PowerAutomateDesktop",
        };

        /// <summary>
        /// Processos que nunca viram RAIZ de árvore protegida. O explorer é pai de
        /// quase tudo que se abre pelo menu Iniciar, área de trabalho ou barra de
        /// tarefas: usá-lo como raiz protegia praticamente todos os programas e a
        /// limpeza não fazia nada. Eles continuam protegidos, só que sozinhos.
        /// </summary>
        public static readonly IReadOnlySet<string> NeverTreeRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "dwm", "sihost", "svchost", "RuntimeBroker", "ApplicationFrameHost",
            "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp",
            "TextInputHost", "LockApp", "ctfmon", "winlogon", "csrss", "services", "wininit",
        };

        /// <summary>
        /// Classes de janela que denunciam o motor do jogo, em qualquer tamanho —
        /// em janela, sem borda, em tela cheia ou minimizado.
        /// </summary>
        public static readonly IReadOnlySet<string> GameWindowClasses = new HashSet<string>(StringComparer.Ordinal)
        {
            "UnityWndClass",                 // Unity
            "UnrealWindow",                  // Unreal Engine
            "LaunchUnrealUWindowsClient",    // Unreal (antigo)
            "SDL_app",                       // SDL — Source 2 (CS2, Dota 2) e muitos outros
            "Valve001",                      // Source 1
            "GLFW30",                        // GLFW — Minecraft Java (1.13+)
            "LWJGL",                         // LWJGL 2 — Minecraft Java antigo
            "YYGameMakerYY",                 // GameMaker
            "CryENGINE",
        };

        /// <summary>Executáveis de jogos conhecidos (sem extensão).</summary>
        public static readonly IReadOnlySet<string> KnownGameProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "RobloxPlayerBeta", "Minecraft.Windows", "osu!", "League of Legends", "dota2", "cs2", "csgo",
            "r5apex", "r5apex_dx12", "GTA5", "GTA5_Enhanced", "RDR2", "eldenring", "RainbowSix", "RainbowSix_Vulkan",
            "Overwatch", "TslGame", "destiny2", "bf2042", "cod", "Warframe.x64", "PathOfExile", "PathOfExile_x64",
            "PathOfExileSteam", "GenshinImpact", "StarRail", "ZenlessZoneZero", "witcher3", "Cyberpunk2077",
            "RustClient", "EscapeFromTarkov", "Wow", "WowClassic", "Hearthstone", "Diablo IV", "SC2_x64",
            "TheFinals",
        };

        /// <summary>Sufixos de executável de jogos em Unreal Engine.</summary>
        public static readonly IReadOnlyList<string> GameProcessSuffixes = new[]
        {
            "-Win64-Shipping", "-Win32-Shipping", "-WinGDK-Shipping",
        };

        /// <summary>
        /// Pastas onde só ficam jogos instalados. Os launchers (Epic, Riot) e
        /// ferramentas vendidas na Steam ficam de fora — eles abrem o dia todo
        /// e travariam a otimização à toa.
        /// </summary>
        public static readonly IReadOnlyList<string> GameInstallMarkers = new[]
        {
            @"\steamapps\common\", @"\XboxGames\", @"\GOG Galaxy\Games\", @"\GOG Games\",
            @"\Epic Games\", @"\Riot Games\", @"\Ubisoft Game Launcher\games\", @"\EA Games\",
        };

        public static readonly IReadOnlyList<string> GameInstallExceptions = new[]
        {
            @"\Epic Games\Launcher\", @"\Riot Games\Riot Client\", @"\steamapps\common\wallpaper_engine\",
            @"\steamapps\common\Steamworks Shared\", @"\steamapps\common\Blender\",
            @"\steamapps\common\Aseprite\", @"\steamapps\common\Soundpad\",
        };

        /// <summary>
        /// Donos de tela cheia que NÃO são jogo e não devem ser "lembrados" como
        /// jogo depois que saem da tela cheia (navegador, player, acesso remoto).
        /// Enquanto estão em tela cheia, ainda pausam — mas só enquanto durar.
        /// </summary>
        public static readonly IReadOnlySet<string> NeverLatchOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "chrome", "msedge", "firefox", "opera", "opera_gx", "brave", "vivaldi", "iexplore",
            "vlc", "mpc-hc64", "mpc-hc", "mpc-be64", "PotPlayerMini64", "PotPlayer", "Netflix",
            "mstsc", "AnyDesk", "TeamViewer", "rustdesk", "parsecd", "obs64", "Discord", "Spotify",
            "Teams", "ms-teams", "Zoom", "POWERPNT", "Acrobat", "AcroRd32",
        };
    }
}
