using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PCOptimizer.Services
{
    /// <summary>Áreas da memória que o usuário pode mandar limpar, uma a uma.</summary>
    [Flags]
    public enum MemoryOperation
    {
        None               = 0,
        /// <summary>Esvazia o working set de programas abertos (nunca o jogo).</summary>
        TrimPrograms       = 1,
        /// <summary>Encolhe o working set do cache de arquivos do sistema.</summary>
        SystemFileCache    = 2,
        /// <summary>Descarta só o cache em espera que o Windows marcou como descartável.</summary>
        StandbyLowPriority = 4,
        /// <summary>Descarta TODO o cache em espera. Só no botão manual.</summary>
        StandbyFull        = 8,
        /// <summary>Grava a memória modificada no disco. Só no botão manual.</summary>
        ModifiedList       = 16,
    }

    /// <summary>De onde veio o pedido de limpeza.</summary>
    public enum MemoryTrigger { Manual, Batch, Tray, Auto }

    public enum MemoryRunOutcome { Done, NothingToDo, NeedsAdmin, Paused, Busy, Failed }

    public enum MemoryGaugeLevel { Normal, Warning, Danger }

    public enum AutoSkipReason { None, Disabled, Game, NoReading, Cooldown, BelowThreshold, NotIdle, NothingToDo, ManualOnlyChecked }

    /// <summary>
    /// Foto da memória. Os campos das listas são null quando não dá para lê-las
    /// (sem administrador) — nunca inventamos o tamanho do cache.
    /// </summary>
    public readonly record struct MemorySnapshot(
        ulong TotalBytes, ulong AvailableBytes,
        ulong? FreeBytes, ulong? StandbyBytes, ulong? ModifiedBytes)
    {
        public bool IsValid  => TotalBytes > 0;
        public bool HasLists => FreeBytes.HasValue && StandbyBytes.HasValue && ModifiedBytes.HasValue;

        /// <summary>
        /// "Em uso" como o Gerenciador de Tarefas mostra: o total menos o
        /// disponível (livre + cache em espera) e, quando conhecida, menos a
        /// memória modificada — que o Gerenciador desenha à parte.
        /// </summary>
        public ulong InUseBytes
        {
            get
            {
                if (TotalBytes == 0 || AvailableBytes >= TotalBytes) return 0;
                ulong used = TotalBytes - AvailableBytes;
                if (ModifiedBytes is ulong m) used = used > m ? used - m : 0;
                return used;
            }
        }

        public double InUsePercent => TotalBytes == 0 ? 0 : InUseBytes * 100.0 / TotalBytes;
    }

    /// <summary>Um processo visto no retrato do sistema, sem abrir nada.</summary>
    public readonly record struct ProcessCandidate(int Pid, string Name, int SessionId, long WorkingSetBytes);

    /// <summary>O que NÃO pode ser tocado numa passada de limpeza.</summary>
    public sealed record TrimContext(
        int MyPid,
        IReadOnlySet<int> ProtectedPids,
        IReadOnlyCollection<string> ExtraProtectedNames,
        IReadOnlyCollection<string> ProtectedDirs,
        IReadOnlyCollection<string> UserProtected,
        string WindowsDir);

    /// <summary>Plano de uma execução: o que roda e o que ficou de fora, e por quê.</summary>
    public readonly record struct MemoryPlan(
        MemoryOperation Ops, MemoryOperation SkippedNoAdmin, MemoryOperation SkippedByPolicy);

    public sealed record AutoInputs(
        bool Enabled, int ThresholdPercent, MemorySnapshot Snapshot, bool GameOpen,
        bool RequireIdle, bool IsIdle, DateTime NowUtc, DateTime? LastRunUtc,
        TimeSpan Cooldown, MemoryOperation Configured, bool Aggressive = false);

    public readonly record struct AutoDecision(bool Run, AutoSkipReason Reason);

    public sealed record MemoryRunResult(
        MemoryRunOutcome Outcome, MemorySnapshot Before, MemorySnapshot After,
        MemoryOperation Done, MemoryOperation Failed, MemoryOperation SkippedNoAdmin,
        int TrimmedProcesses, string Text);

    /// <summary>Retângulo em pixels, com bordas exclusivas à direita/embaixo (como RECT).</summary>
    public readonly record struct Rect32(int Left, int Top, int Right, int Bottom)
    {
        public long Width  => (long)Right - Left;
        public long Height => (long)Bottom - Top;
    }

    /// <summary>O que se sabe de uma janela de topo, lido sem tocar no processo dono.</summary>
    public readonly record struct WindowFacts(
        Rect32 Rect, Rect32 Monitor, bool Visible, bool Minimized, bool Cloaked, bool Zoomed,
        long Style, long ExStyle, string ClassName, string OwnerName, bool OwnerIsSelf);

    /// <summary>
    /// Regras do otimizador de memória. Nada aqui fala com o Windows — o serviço
    /// lê o sistema e esta classe decide. Separado para poder ser coberto por
    /// teste: a regra de "nunca mexer no jogo" não pode falhar em silêncio.
    /// </summary>
    public static class MemoryOptimizerPolicy
    {
        // ── Limites ────────────────────────────────────────────────────────────
        public const int   ThresholdMin = 50, ThresholdMax = 95, ThresholdStep = 5, ThresholdDefault = 85;
        public const long  MinTrimWorkingSetBytes = 64L << 20;   // 64 MB
        public const int   MaxTrimProcesses = 40;
        /// <summary>Modo agressivo: todo programa elegível a partir de 16 MB.</summary>
        public const long  AggressiveMinTrimWorkingSetBytes = 16L << 20;
        public const int   AggressiveMaxTrimProcesses = 400;
        public const ulong SmallGainBytes = 150UL << 20;         // abaixo disso = "pouco a liberar"

        public static readonly TimeSpan AutoPollInterval  = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan BaseCooldown      = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan MaxCooldown       = TimeSpan.FromHours(2);
        /// <summary>Menor que a espera: a checagem do ganho sempre acontece antes da próxima execução.</summary>
        public static readonly TimeSpan PersistCheckDelay = TimeSpan.FromMinutes(8);
        /// <summary>Jogo achado pela varredura completa: não varre de novo por esse tempo.</summary>
        public static readonly TimeSpan GamePauseBackoff  = TimeSpan.FromMinutes(5);

        public const int IdleCpuPercent = 10;
        public static readonly TimeSpan IdleRequired = TimeSpan.FromMinutes(5);

        /// <summary>Áreas que só rodam no botão "Otimizar agora", nunca sozinhas.</summary>
        public const MemoryOperation ManualOnly = MemoryOperation.StandbyFull | MemoryOperation.ModifiedList;

        public const MemoryOperation All =
            MemoryOperation.TrimPrograms | MemoryOperation.SystemFileCache |
            MemoryOperation.StandbyLowPriority | MemoryOperation.StandbyFull | MemoryOperation.ModifiedList;

        /// <summary>O que vem marcado de fábrica: só o que não tem efeito colateral sério.</summary>
        public const MemoryOperation DefaultOps =
            MemoryOperation.TrimPrograms | MemoryOperation.SystemFileCache | MemoryOperation.StandbyLowPriority;

        /// <summary>
        /// O que o modo agressivo soma às áreas marcadas. O cache em espera
        /// completo fica de fora de propósito: ele faz o próximo jogo carregar
        /// tudo do disco de novo — e a regra é não afetar jogos.
        /// </summary>
        public const MemoryOperation AggressiveOps =
            MemoryOperation.TrimPrograms | MemoryOperation.SystemFileCache |
            MemoryOperation.StandbyLowPriority | MemoryOperation.ModifiedList;

        /// <summary>Áreas que dependem de privilégio de administrador habilitado.</summary>
        public const MemoryOperation ListOps =
            MemoryOperation.StandbyLowPriority | MemoryOperation.StandbyFull | MemoryOperation.ModifiedList;

        public const string GamePausedText = "Pausado: há um jogo ou app em tela cheia aberto — nada foi alterado";

        /// <summary>Texto da pausa nomeando quem bloqueou, para o usuário saber o porquê.</summary>
        public static string PausedText(string? blocker) =>
            string.IsNullOrWhiteSpace(blocker)
                ? GamePausedText
                : $"Pausado: {StripExe(blocker)} aberto (jogo ou tela cheia) — nada foi alterado";

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
            "lsass", "fontdrvhost", "Registry", "MemCompression", "System", "Idle",
            "Secure System",

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
            "vlc", "foobar2000", "AIMP", "MusicBee",
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

        // ── Leitura ───────────────────────────────────────────────────────────

        /// <summary>
        /// Monta a foto a partir dos contadores do kernel (em PÁGINAS). Cache em
        /// espera = soma das 8 prioridades; livre = livre + zerada; modificada
        /// inclui a que não vai para o arquivo de paginação.
        /// </summary>
        public static MemorySnapshot FromLists(ulong totalPhys, ulong availPhys,
            IReadOnlyList<ulong> standbyPagesByPriority, ulong zeroPages, ulong freePages,
            ulong modifiedPages, ulong modifiedNoWritePages, int pageSize)
        {
            ulong page = (ulong)Math.Max(1, pageSize);
            ulong standby = 0;
            foreach (var p in standbyPagesByPriority) standby += p;
            return new MemorySnapshot(totalPhys, availPhys,
                (zeroPages + freePages) * page,
                standby * page,
                (modifiedPages + modifiedNoWritePages) * page);
        }

        // ── Plano ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Monta o plano a partir do que o usuário marcou.
        /// - Fora do botão manual, as áreas pesadas (cache completo, memória
        ///   modificada) saem: uma otimização em lote ou automática não pode jogar
        ///   fora todo o cache de arquivos sem o usuário estar olhando.
        /// - O cache completo já inclui o de baixa prioridade.
        /// - Sem privilégio, a área vira "pulada", não "falhou".
        /// </summary>
        public static MemoryPlan BuildPlan(MemoryOperation configured, MemoryTrigger trigger,
                                           bool canPurgeLists, bool canTrimFileCache, bool aggressive = false)
        {
            var ops = EffectiveOps(configured, aggressive);
            var byPolicy = MemoryOperation.None;
            var noAdmin = MemoryOperation.None;

            if (trigger != MemoryTrigger.Manual)
            {
                var manualOnly = ManualOnlyFor(aggressive);
                byPolicy |= ops & manualOnly;
                ops &= ~manualOnly;
            }

            if ((ops & MemoryOperation.StandbyFull) != 0)
                ops &= ~MemoryOperation.StandbyLowPriority;

            if (!canPurgeLists)
            {
                noAdmin |= ops & ListOps;
                ops &= ~ListOps;
            }
            if (!canTrimFileCache)
            {
                noAdmin |= ops & MemoryOperation.SystemFileCache;
                ops &= ~MemoryOperation.SystemFileCache;
            }
            return new MemoryPlan(ops, noAdmin, byPolicy);
        }

        /// <summary>Áreas marcadas, somadas às do modo agressivo quando ligado.</summary>
        public static MemoryOperation EffectiveOps(MemoryOperation configured, bool aggressive) =>
            (aggressive ? configured | AggressiveOps : configured) & All;

        /// <summary>
        /// Áreas que só rodam no botão. No modo agressivo a memória modificada
        /// pode rodar sozinha (só grava no disco; o cache continua); o cache
        /// completo nunca, porque faz o próximo jogo carregar do disco.
        /// </summary>
        public static MemoryOperation ManualOnlyFor(bool aggressive) =>
            aggressive ? MemoryOperation.StandbyFull : ManualOnly;

        /// <summary>
        /// Ordem de execução. Os caches vêm ANTES dos programas: as páginas tiradas
        /// dos programas caem num cache em espera recém-limpo e continuam em RAM —
        /// se o programa voltar a usá-las, custa um soft fault, não uma leitura de
        /// disco. A memória modificada vai primeiro para virar cache e ser limpa
        /// junto.
        /// </summary>
        public static IReadOnlyList<MemoryOperation> OrderedSteps(MemoryOperation ops)
        {
            var order = new[]
            {
                MemoryOperation.ModifiedList,
                MemoryOperation.StandbyFull,
                MemoryOperation.StandbyLowPriority,
                MemoryOperation.SystemFileCache,
                MemoryOperation.TrimPrograms,
            };
            return order.Where(o => (ops & o) != 0).ToList();
        }

        // ── Automático ────────────────────────────────────────────────────────

        public static int ClampThreshold(int value)
        {
            int rounded = (int)Math.Round(value / (double)ThresholdStep, MidpointRounding.AwayFromZero) * ThresholdStep;
            return Math.Clamp(rounded, ThresholdMin, ThresholdMax);
        }

        /// <summary>Decide se o automático roda agora, na ordem em que as razões importam.</summary>
        public static AutoDecision DecideAuto(AutoInputs i)
        {
            if (!i.Enabled) return new(false, AutoSkipReason.Disabled);
            if (i.GameOpen) return new(false, AutoSkipReason.Game);
            if (!i.Snapshot.IsValid) return new(false, AutoSkipReason.NoReading);
            if (i.LastRunUtc is DateTime last && i.NowUtc - last < i.Cooldown)
                return new(false, AutoSkipReason.Cooldown);
            if (i.Snapshot.InUsePercent < ClampThreshold(i.ThresholdPercent))
                return new(false, AutoSkipReason.BelowThreshold);
            if (i.RequireIdle && !i.IsIdle) return new(false, AutoSkipReason.NotIdle);
            if (BuildPlan(i.Configured, MemoryTrigger.Auto, true, true, i.Aggressive).Ops == MemoryOperation.None)
                return new(false, EffectiveOps(i.Configured, i.Aggressive) == MemoryOperation.None
                    ? AutoSkipReason.NothingToDo
                    : AutoSkipReason.ManualOnlyChecked);
            return new(true, AutoSkipReason.None);
        }

        /// <summary>
        /// O ganho de uma limpeza automática se SUSTENTOU? Logo depois de esvaziar
        /// os programas o "em uso" sempre cai — eles puxam as páginas de volta em
        /// minutos. Conta como ganho real só se, um tempo depois, o uso não
        /// recuperou mais da metade da queda nem voltou a passar do limite.
        /// </summary>
        public static bool GainHeld(ulong beforeInUse, ulong afterInUse, ulong laterInUse,
                                    ulong totalBytes, int thresholdPercent)
        {
            if (totalBytes == 0) return false;
            ulong drop = beforeInUse > afterInUse ? beforeInUse - afterInUse : 0;
            if (drop < SmallGainBytes) return false;
            ulong recovered = laterInUse > afterInUse ? laterInUse - afterInUse : 0;
            if (recovered * 2 > drop) return false;
            return laterInUse * 100.0 / totalBytes < ClampThreshold(thresholdPercent);
        }

        /// <summary>Ganho que não se sustenta dobra a espera (até 4 h); ganho real volta a 30 min.</summary>
        public static TimeSpan NextCooldown(TimeSpan current, bool productive)
        {
            if (productive) return BaseCooldown;
            var doubled = TimeSpan.FromTicks(Math.Max(current.Ticks, BaseCooldown.Ticks) * 2);
            return doubled > MaxCooldown ? MaxCooldown : doubled;
        }

        // ── Processos ─────────────────────────────────────────────────────────

        /// <summary>Filtro ANTES de abrir qualquer processo.</summary>
        public static bool ShouldTrim(ProcessCandidate c, TrimContext ctx) =>
            ShouldTrim(c, ctx, MinTrimWorkingSetBytes);

        public static bool ShouldTrim(ProcessCandidate c, TrimContext ctx, long minWorkingSetBytes)
        {
            if (c.Pid <= 4 || c.Pid == ctx.MyPid) return false;
            if (c.SessionId == 0) return false;
            if (ctx.ProtectedPids.Contains(c.Pid)) return false;
            if (c.WorkingSetBytes < minWorkingSetBytes) return false;
            if (string.IsNullOrWhiteSpace(c.Name)) return false;
            if (IsProtectedName(c.Name, ctx.ExtraProtectedNames, ctx.UserProtected)) return false;
            return true;
        }

        public static bool IsProtectedName(string name, IReadOnlyCollection<string> extra,
                                           IReadOnlyCollection<string> userProtected)
        {
            string n = StripExe(name);
            if (ProtectedNames.Contains(n)) return true;
            foreach (var p in ProtectedPrefixes)
                if (n.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var e in extra)
                if (string.Equals(StripExe(e), n, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var u in userProtected)
                if (string.Equals(StripExe(u), n, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Filtro DEPOIS de abrir (com o caminho real do executável): nada do
        /// Windows, nada de bibliotecas de jogos, nada nas pastas protegidas.
        /// Caminho desconhecido também fica de fora — na dúvida, não mexe.
        /// </summary>
        public static bool PathExcluded(string? exePath, TrimContext ctx)
        {
            if (string.IsNullOrWhiteSpace(exePath)) return true;
            if (!string.IsNullOrWhiteSpace(ctx.WindowsDir) && IsUnder(exePath, ctx.WindowsDir)) return true;
            foreach (var marker in GameLibraryMarkers)
                if (exePath.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var dir in ctx.ProtectedDirs)
                if (IsUnder(exePath, dir)) return true;
            return false;
        }

        /// <summary>
        /// O executável aberto é mesmo o processo do retrato? Se o PID foi
        /// reaproveitado entre o retrato e a abertura, o nome não bate e o
        /// processo novo fica em paz.
        /// </summary>
        public static bool ImageNameMatches(string? exePath, string snapshotName)
        {
            if (string.IsNullOrWhiteSpace(exePath)) return false;
            return string.Equals(FileNameWithoutExtension(exePath), StripExe(snapshotName),
                                 StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Filtra, ordena do maior para o menor e limita a 40.</summary>
        public static IReadOnlyList<ProcessCandidate> PickTrimTargets(
            IEnumerable<ProcessCandidate> all, TrimContext ctx, bool aggressive = false)
        {
            long minWs = aggressive ? AggressiveMinTrimWorkingSetBytes : MinTrimWorkingSetBytes;
            int max = aggressive ? AggressiveMaxTrimProcesses : MaxTrimProcesses;
            return all.Where(c => ShouldTrim(c, ctx, minWs))
                      .OrderByDescending(c => c.WorkingSetBytes)
                      .Take(max)
                      .ToList();
        }

        /// <summary>
        /// Separa quem pode ser raiz de árvore protegida de quem só protege a si
        /// mesmo. Shell e serviços do Windows nunca viram raiz — ver
        /// <see cref="NeverTreeRoots"/>.
        /// </summary>
        public static (HashSet<int> TreeRoots, HashSet<int> Leaves) SplitRoots(
            IEnumerable<int> roots, IReadOnlyDictionary<int, string> namesByPid)
        {
            var tree = new HashSet<int>();
            var leaves = new HashSet<int>();
            foreach (int pid in roots)
            {
                if (pid <= 4) continue;
                namesByPid.TryGetValue(pid, out string? name);
                if (name != null && (NeverTreeRoots.Contains(StripExe(name)) ||
                                     NonGameWindowOwners.Contains(StripExe(name))))
                    leaves.Add(pid);
                else
                    tree.Add(pid);
            }
            return (tree, leaves);
        }

        /// <summary>
        /// Raízes mais todos os descendentes. Jogos e navegadores rodam em vários
        /// processos (renderizador, áudio, CEF): proteger só o PID principal
        /// deixaria os filhos expostos.
        /// </summary>
        public static HashSet<int> ExpandProcessTree(IEnumerable<(int Pid, int ParentPid)> processes,
                                                     IEnumerable<int> roots)
        {
            var children = new Dictionary<int, List<int>>();
            foreach (var (pid, parent) in processes)
            {
                if (pid == parent) continue;
                if (!children.TryGetValue(parent, out var list)) children[parent] = list = new List<int>();
                list.Add(pid);
            }

            var result = new HashSet<int>();
            var queue = new Queue<int>();
            foreach (var r in roots)
                if (r > 4 && result.Add(r)) queue.Enqueue(r);

            while (queue.Count > 0)
            {
                int p = queue.Dequeue();
                if (!children.TryGetValue(p, out var kids)) continue;
                foreach (var k in kids)
                    if (k > 4 && result.Add(k)) queue.Enqueue(k);   // Add falso = já visto: corta ciclos
            }
            return result;
        }

        // ── Janelas ───────────────────────────────────────────────────────────

        public const long WS_POPUP         = 0x80000000;
        public const long WS_CAPTION       = 0x00C00000;
        public const long WS_THICKFRAME    = 0x00040000;
        public const long WS_EX_TRANSPARENT = 0x00000020;
        public const long WS_EX_TOOLWINDOW = 0x00000080;
        public const long WS_EX_NOACTIVATE = 0x08000000;

        public static bool IsShellClass(string? cls) =>
            cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

        /// <summary>
        /// Uma janela parece um jogo aberto? Jogos em tela cheia — exclusiva ou sem
        /// borda — usam uma janela sem barra de título do tamanho do monitor.
        /// Para janela minimizada, <see cref="WindowFacts.Rect"/> deve ser o
        /// tamanho RESTAURADO: um jogo em tela cheia minimizado por alt-tab
        /// continua aberto. Janela maximizada comum (com moldura) não conta.
        /// Sobreposições (transparentes, de ferramenta, sem ativação) e o shell
        /// ficam de fora.
        /// </summary>
        public static bool IsGameLikeWindow(WindowFacts w)
        {
            if (!w.Visible || w.Cloaked || w.OwnerIsSelf) return false;
            if (IsShellClass(w.ClassName)) return false;
            if (NonGameWindowOwners.Contains(StripExe(w.OwnerName))) return false;
            if ((w.ExStyle & (WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) return false;

            // Sinais fortes, em qualquer tamanho ou estado (em janela, minimizado):
            // a classe da janela do motor do jogo, ou o executável de um jogo.
            if (GameWindowClasses.Contains(w.ClassName ?? "")) return true;
            if (IsKnownGameProcess(w.OwnerName)) return true;

            if ((w.Style & WS_CAPTION) == WS_CAPTION) return false;
            if (w.Zoomed && (w.Style & WS_THICKFRAME) != 0) return false;
            if (w.Monitor.Width <= 0 || w.Monitor.Height <= 0) return false;

            // Minimizado: o tamanho restaurado de um jogo em tela cheia exclusiva
            // é a resolução DELE, que pode ser menor que a do monitor (1280×960
            // esticado num monitor 1920×1080). Janela pop-up sem moldura de
            // redimensionar e de tamanho de jogo conta — apps sem borda comuns
            // (Discord, Spotify, Steam) são redimensionáveis e ficam de fora.
            if (w.Minimized)
                return (w.Style & WS_POPUP) != 0 && (w.Style & WS_THICKFRAME) == 0
                       && w.Rect.Width >= 640 && w.Rect.Height >= 480;

            // Tolerância de 2 px: arredondamento de DPI e bordas invisíveis
            return w.Rect.Width >= w.Monitor.Width - 2 && w.Rect.Height >= w.Monitor.Height - 2;
        }

        /// <summary>O executável é de um jogo conhecido (lista ou padrão do Unreal)?</summary>
        public static bool IsKnownGameProcess(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string n = StripExe(name);
            if (KnownGameProcesses.Contains(n)) return true;
            foreach (var suffix in GameProcessSuffixes)
                if (n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>O executável está instalado numa pasta de jogos (fora dos launchers)?</summary>
        public static bool IsGameInstallPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string p = path.Replace('/', '\\');
            foreach (var ex in GameInstallExceptions)
                if (p.Contains(ex, StringComparison.OrdinalIgnoreCase)) return false;
            foreach (var marker in GameInstallMarkers)
                if (p.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Um processo visto como jogo pode ser "lembrado" como jogo aberto até
        /// fechar? Navegador, player e acesso remoto em tela cheia, não — eles
        /// ficam abertos o dia todo e travariam a otimização.
        /// </summary>
        public static bool CanLatchAsGame(string? ownerName) =>
            !string.IsNullOrWhiteSpace(ownerName) && !NeverLatchOwners.Contains(StripExe(ownerName))
            && !NonGameWindowOwners.Contains(StripExe(ownerName));

        /// <summary>
        /// Janela de programa visível na tela (não minimizada, não oculta, não
        /// sobreposição): o usuário está vendo, então o dono e os filhos não são
        /// esvaziados — inclusive um jogo em janela pequena que não se denunciou.
        /// </summary>
        public static bool IsVisibleUserWindow(WindowFacts w)
        {
            if (!w.Visible || w.Minimized || w.Cloaked || w.OwnerIsSelf) return false;
            if (IsShellClass(w.ClassName)) return false;
            if ((w.ExStyle & (WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW)) != 0) return false;
            return w.Rect.Width >= 120 && w.Rect.Height >= 80;   // ignora janelinhas utilitárias
        }

        /// <summary>
        /// Janela visível que ocupa pelo menos metade do monitor: o usuário está
        /// olhando para ela, então o dono (e os filhos) não é esvaziado.
        /// </summary>
        public static bool IsBigVisibleWindow(WindowFacts w)
        {
            if (!w.Visible || w.Cloaked || w.OwnerIsSelf) return false;
            if (IsShellClass(w.ClassName)) return false;
            if ((w.ExStyle & (WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW)) != 0) return false;
            long area = w.Rect.Width * w.Rect.Height;
            long mon  = w.Monitor.Width * w.Monitor.Height;
            return mon > 0 && area * 2 >= mon;
        }

        // ── Textos ────────────────────────────────────────────────────────────

        public static MemoryGaugeLevel Level(double inUsePercent) =>
            inUsePercent >= 90 ? MemoryGaugeLevel.Danger
            : inUsePercent >= 70 ? MemoryGaugeLevel.Warning
            : MemoryGaugeLevel.Normal;

        /// <summary>Bytes em pt-BR: "512 MB" abaixo de 1 GB, "1,3 GB" a partir dele.</summary>
        public static string FormatBytes(ulong bytes)
        {
            const double MiB = 1024.0 * 1024.0, GiB = MiB * 1024.0;
            if (bytes < 1024UL * 1024 * 1024)
                return ((ulong)Math.Round(bytes / MiB)).ToString(CultureInfo.InvariantCulture) + " MB";
            return (bytes / GiB).ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + " GB";
        }

        public static (string Usage, string Detail) DescribeSnapshot(MemorySnapshot s, bool listsNeedAdmin)
        {
            if (!s.IsValid) return ("Não consegui ler a memória", "—");

            string usage = $"Em uso {Pct(s.InUsePercent)}% · {FormatBytes(s.InUseBytes)} de {FormatBytes(s.TotalBytes)}";
            string detail = $"Disponível {FormatBytes(s.AvailableBytes)}";
            if (s.HasLists)
            {
                detail += $" · livre {FormatBytes(s.FreeBytes!.Value)} · cache em espera {FormatBytes(s.StandbyBytes!.Value)}";
                if (s.ModifiedBytes!.Value >= SmallGainBytes)
                    detail += $" · modificada {FormatBytes(s.ModifiedBytes.Value)}";
            }
            else if (listsNeedAdmin)
            {
                detail += " · cache em espera: requer administrador";
            }
            return (usage, detail);
        }

        public static MemoryRunOutcome Classify(MemoryOperation done, MemoryOperation failed,
                                                MemoryOperation skippedNoAdmin, bool paused, bool busy)
        {
            if (busy) return MemoryRunOutcome.Busy;
            if (paused) return MemoryRunOutcome.Paused;
            if (done != MemoryOperation.None) return MemoryRunOutcome.Done;
            if (failed != MemoryOperation.None) return MemoryRunOutcome.Failed;
            if (skippedNoAdmin != MemoryOperation.None) return MemoryRunOutcome.NeedsAdmin;
            return MemoryRunOutcome.NothingToDo;
        }

        /// <summary>
        /// Texto do resultado. Honesto de propósito: esvaziar o cache em espera
        /// NÃO aumenta a RAM disponível (o cache já contava como disponível) — ele
        /// só vira memória livre, e o texto diz exatamente isso.
        /// </summary>
        public static string DescribeResult(MemoryRunOutcome outcome, MemorySnapshot before, MemorySnapshot after,
                                            MemoryOperation failed, MemoryOperation skippedNoAdmin, DateTime localTime)
            => DescribeResult(outcome, before, after, failed, skippedNoAdmin, MemoryOperation.None, localTime);

        public static string DescribeResult(MemoryRunOutcome outcome, MemorySnapshot before, MemorySnapshot after,
                                            MemoryOperation failed, MemoryOperation skippedNoAdmin,
                                            MemoryOperation skippedByPolicy, DateTime localTime)
        {
            string when = " · " + localTime.ToString("HH:mm", CultureInfo.InvariantCulture);
            switch (outcome)
            {
                case MemoryRunOutcome.Busy:        return "Já há uma otimização em andamento";
                case MemoryRunOutcome.Paused:      return GamePausedText;
                case MemoryRunOutcome.NeedsAdmin:  return "Requer administrador — nada foi alterado";
                case MemoryRunOutcome.Failed:      return "Não consegui otimizar — detalhes no log" + when;
                case MemoryRunOutcome.NothingToDo:
                    return (skippedByPolicy != MemoryOperation.None
                        ? "As áreas marcadas só rodam no botão “Otimizar agora”"
                        : "Nenhuma área para limpar agora") + when;
            }

            string text;
            ulong freed = before.InUseBytes > after.InUseBytes ? before.InUseBytes - after.InUseBytes : 0;
            ulong freeGain = 0;
            if (before.HasLists && after.HasLists && after.FreeBytes!.Value > before.FreeBytes!.Value)
                freeGain = after.FreeBytes.Value - before.FreeBytes.Value;

            if (freed >= SmallGainBytes)
            {
                text = $"Em uso {Pct(before.InUsePercent)}% → {Pct(after.InUsePercent)}% (−{FormatBytes(freed)})";
                if (freeGain >= SmallGainBytes) text += $" · livre +{FormatBytes(freeGain)}";
            }
            else if (freeGain >= SmallGainBytes)
            {
                text = $"Cache em espera virou memória livre: +{FormatBytes(freeGain)} " +
                       "(o “em uso” não muda — o cache já contava como disponível)";
            }
            else
            {
                text = $"Pouco a liberar (−{FormatBytes(freed)}) — o Windows já está gerenciando bem a memória";
            }

            int noAdmin = PopCount(skippedNoAdmin);
            if (noAdmin > 0) text += $" · {noAdmin} limpeza(s) pulada(s): requer administrador";
            int fails = PopCount(failed);
            if (fails > 0) text += $" · {fails} falha(s) — veja o log";
            if (skippedByPolicy != MemoryOperation.None)
                text += " · " + string.Join(" e ", OrderedSteps(skippedByPolicy).Select(LabelOf)) + ": só no botão";
            return text + when;
        }

        public static string LabelOf(MemoryOperation op) => op switch
        {
            MemoryOperation.TrimPrograms       => "programas abertos",
            MemoryOperation.SystemFileCache    => "cache de arquivos do sistema",
            MemoryOperation.StandbyLowPriority => "cache em espera de baixa prioridade",
            MemoryOperation.StandbyFull        => "cache em espera completo",
            MemoryOperation.ModifiedList       => "memória modificada",
            _ => op.ToString(),
        };

        // ── Auxiliares ────────────────────────────────────────────────────────

        private static string Pct(double v) =>
            Math.Round(v, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);

        private static int PopCount(MemoryOperation ops)
        {
            int n = 0;
            for (int v = (int)ops; v != 0; v &= v - 1) n++;
            return n;
        }

        public static string StripExe(string name) =>
            name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

        public static string FileNameWithoutExtension(string path)
        {
            int slash = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
            string file = slash >= 0 ? path[(slash + 1)..] : path;
            int dot = file.LastIndexOf('.');
            return dot > 0 ? file[..dot] : file;
        }

        /// <summary>
        /// <paramref name="path"/> está dentro de <paramref name="dir"/>? Respeita o
        /// separador: "D:\Jogos\Foo" não contém "D:\Jogos\FooBar".
        /// </summary>
        public static bool IsUnder(string path, string dir)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(dir)) return false;
            string d = dir.Replace('/', '\\').TrimEnd('\\') + "\\";
            string p = path.Replace('/', '\\');
            return p.StartsWith(d, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Pasta de um executável ("C:\a\b.exe" → "C:\a"), ou null.</summary>
        public static string? DirectoryOf(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            int slash = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
            return slash > 0 ? path[..slash] : null;
        }
    }

    /// <summary>
    /// "PC ocioso" como no Wise: CPU abaixo de 10% em todas as amostras dos
    /// últimos 5 minutos. Recebe os contadores crus de GetSystemTimes.
    /// Não é thread-safe — é usado só dentro do tick do automático.
    /// </summary>
    public sealed class CpuIdleTracker
    {
        private readonly int _idlePercent;
        private readonly TimeSpan _required;
        private long _idle, _kernel, _user;
        private DateTime? _lastSampleUtc;
        private DateTime? _idleSinceUtc;

        public CpuIdleTracker(int idlePercent, TimeSpan required)
        {
            _idlePercent = idlePercent;
            _required = required;
        }

        public double? LastBusyPercent { get; private set; }

        /// <summary>Contadores de GetSystemTimes; o tempo de kernel INCLUI o ocioso.</summary>
        public void Push(long idle, long kernel, long user, DateTime nowUtc)
        {
            if (_lastSampleUtc is DateTime prevUtc)
            {
                long di = idle - _idle, dk = kernel - _kernel, du = user - _user;
                long total = dk + du;
                if (total > 0 && di >= 0 && di <= total)
                {
                    double busy = 100.0 * (total - di) / total;
                    LastBusyPercent = busy;
                    if (busy < _idlePercent) _idleSinceUtc ??= prevUtc;
                    else _idleSinceUtc = null;
                }
            }
            _idle = idle; _kernel = kernel; _user = user;
            _lastSampleUtc = nowUtc;
        }

        public void Reset()
        {
            _lastSampleUtc = null;
            _idleSinceUtc = null;
            LastBusyPercent = null;
        }

        public bool IsIdle(DateTime nowUtc) =>
            _idleSinceUtc is DateTime since && nowUtc - since >= _required;
    }
}
