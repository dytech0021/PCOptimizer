using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Confina os OUTROS programas aos E-cores, deixando os P-cores livres para o
    /// jogo. É o coração do Turbo de Jogo.
    ///
    /// Por que assim e não fixando o jogo nos P-cores: fixar o jogo exigiria
    /// escrever no processo dele, que é protegido por anticheat. Movendo o resto,
    /// o efeito é o mesmo (P-cores vazios) e o app nunca encosta no jogo.
    ///
    /// Processos filhos HERDAM a máscara, então uma aba nova do Chrome já nasce
    /// nos E-cores sem trabalho adicional.
    /// </summary>
    public static class CoreParkingService
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessAffinityMask(IntPtr h,
            out UIntPtr processMask, out UIntPtr systemMask);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessAffinityMask(IntPtr h, UIntPtr mask);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetPriorityClass(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetPriorityClass(IntPtr h, uint priorityClass);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr h, uint flags,
            System.Text.StringBuilder exeName, ref uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ProcessIdToSessionId(int pid, out int sessionId);

        private const uint PROCESS_SET_INFORMATION           = 0x0200;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint BELOW_NORMAL_PRIORITY_CLASS       = 0x00004000;

        /// <summary>
        /// Programas que NUNCA são movidos, mesmo fora do System32.
        /// Comparação por nome sem extensão, sem diferenciar maiúsculas.
        /// </summary>
        private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
        {
            // Anticheat e plataformas — mexer aqui é pedir problema
            "EasyAntiCheat", "EasyAntiCheat_EOS", "start_protected_game",
            "BEService", "BEDaisy", "BattlEyeLauncher",
            "vgc", "vgtray", "GameMon", "GameMon64", "npggNT",
            "steamservice", "GameOverlayUI",

            // Áudio do sistema — confinar o motor de áudio estala o som de TUDO,
            // inclusive o do próprio jogo
            "audiodg",

            // Shell e composição — rede de segurança além do filtro de System32
            "dwm", "explorer", "csrss", "wininit", "winlogon", "smss", "services",
            "lsass", "fontdrvhost", "Registry", "MemCompression",

            // Captura e gravação — quem grava não quer frame perdido
            "obs64", "obs32", "nvcontainer", "NVDisplay.Container",
        };

        private sealed class Parked
        {
            public int    Pid;
            public string Name = "";
            public long   StartUtcTicks;
            public ulong  PrevAffinity;
            public uint   PrevPriority;
            public bool   AffinityChanged;
            public bool   PriorityChanged;
        }

        private static readonly Dictionary<int, Parked> _parked = new();
        private static readonly HashSet<int> _seen = new();
        private static readonly object _lock = new();

        /// <summary>Quantos programas estão confinados agora.</summary>
        public static int ParkedCount { get { lock (_lock) return _parked.Count; } }

        /// <summary>Esquece o que já foi visto — usado ao (re)ativar o turbo.</summary>
        public static void ResetSeen() { lock (_lock) _seen.Clear(); }

        /// <summary>
        /// Confina os processos elegíveis nos E-cores. Na primeira passada varre
        /// tudo; nas seguintes só olha PIDs novos, para não repetir trabalho
        /// durante a partida.
        /// </summary>
        public static int ParkAll(int gamePid, string? gameExePath, bool lowerPriority)
        {
            var topo = CpuTopologyService.Get();
            if (!topo.CanPark) return 0;

            string? gameDir = null;
            string? gameName = null;
            try
            {
                if (!string.IsNullOrEmpty(gameExePath))
                {
                    gameDir  = Path.GetDirectoryName(gameExePath);
                    gameName = Path.GetFileNameWithoutExtension(gameExePath);
                }
            }
            catch { }

            int myPid = Environment.ProcessId;
            int parked = 0;

            Process[] all;
            try { all = Process.GetProcesses(); }
            catch (Exception ex) { Logger.Error(ex, "CoreParking.GetProcesses"); return 0; }

            foreach (var p in all)
            {
                int pid;
                string name;
                try { pid = p.Id; name = p.ProcessName; }
                catch { p.Dispose(); continue; }

                try
                {
                    lock (_lock)
                    {
                        if (_seen.Contains(pid)) continue;
                        _seen.Add(pid);
                    }

                    if (ShouldSkip(pid, name, myPid, gamePid, gameName, gameDir)) continue;
                    if (Park(pid, name, topo.ECoreMask, lowerPriority)) parked++;
                }
                catch (Exception ex) { Logger.Error(ex, $"CoreParking.Park({name})"); }
                finally { p.Dispose(); }
            }

            return parked;
        }

        /// <summary>
        /// Filtro em duas camadas: primeiro o estrutural (mais confiável que uma
        /// lista de nomes), depois a lista fixa e a do usuário.
        /// </summary>
        private static bool ShouldSkip(int pid, string name, int myPid, int gamePid,
                                       string? gameName, string? gameDir)
        {
            if (pid <= 4 || pid == myPid || pid == gamePid) return true;
            if (string.IsNullOrEmpty(name)) return true;

            // Mesmo nome do jogo: jogos multiprocesso
            if (gameName != null && name.Equals(gameName, StringComparison.OrdinalIgnoreCase))
                return true;

            // Serviços da sessão 0 — elimina svchost, lsass, WmiPrvSE, antivírus…
            if (ProcessIdToSessionId(pid, out int sess) && sess == 0) return true;

            if (ProtectedNames.Contains(name)) return true;

            var userList = SettingsService.Current.GameBoostUserProtected;
            if (userList != null && userList.Any(u =>
                    u.Equals(name, StringComparison.OrdinalIgnoreCase))) return true;

            // Caminho do executável: System32/SysWOW64 e a pasta do jogo
            string? path = GetExePath(pid);
            if (path == null) return true;

            string sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (path.StartsWith(Path.Combine(sysRoot, "System32"), StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(Path.Combine(sysRoot, "SysWOW64"), StringComparison.OrdinalIgnoreCase))
                return true;

            // Auxiliares do próprio jogo (launcher, serviço do anticheat…)
            if (gameDir != null)
            {
                string? dir = Path.GetDirectoryName(path);
                if (dir != null && dir.StartsWith(gameDir, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static string? GetExePath(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new System.Text.StringBuilder(1024);
                uint size = 1024;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            catch { return null; }
            finally { CloseHandle(h); }
        }

        private static bool Park(int pid, string name, ulong eCoreMask, bool lowerPriority)
        {
            IntPtr h = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION,
                                   false, pid);
            if (h == IntPtr.Zero) return false;   // protegido ou sem permissão — segue a vida

            try
            {
                if (!GetProcessAffinityMask(h, out UIntPtr prevMask, out UIntPtr sysMask))
                    return false;

                ulong prev = (ulong)prevMask;
                ulong target = eCoreMask & (ulong)sysMask;
                if (target == 0) return false;

                // Já estava só nos E-cores (ou já confinado por nós): não mexe
                if (prev == target) return false;

                long startTicks;
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    startTicks = proc.StartTime.ToUniversalTime().Ticks;
                }
                catch { return false; }

                uint prevPrio = GetPriorityClass(h);
                bool affOk = SetProcessAffinityMask(h, (UIntPtr)target);
                bool prioOk = false;

                if (lowerPriority && prevPrio != 0 && prevPrio != BELOW_NORMAL_PRIORITY_CLASS)
                    prioOk = SetPriorityClass(h, BELOW_NORMAL_PRIORITY_CLASS);

                if (!affOk && !prioOk) return false;

                var entry = new Parked
                {
                    Pid = pid, Name = name, StartUtcTicks = startTicks,
                    PrevAffinity = prev, PrevPriority = prevPrio,
                    AffinityChanged = affOk, PriorityChanged = prioOk
                };
                lock (_lock) _parked[pid] = entry;

                // Grava em disco ANTES de considerar feito: se o app morrer agora,
                // o próximo início desfaz.
                BoostStateStore.Record(new BoostStateStore.Entry
                {
                    Pid = pid, Name = name, StartUtcTicks = startTicks,
                    PrevAffinity = prev, PrevPriority = prevPrio,
                    AffinityChanged = affOk, PriorityChanged = prioOk
                });

                Logger.Info($"Turbo: {name} (PID {pid}) → E-cores" + (prioOk ? " + prioridade baixa" : ""));
                return true;
            }
            catch (Exception ex) { Logger.Error(ex, $"CoreParking.Park({name})"); return false; }
            finally { CloseHandle(h); }
        }

        /// <summary>Devolve todos os processos confinados ao estado original.</summary>
        public static int RestoreAll()
        {
            List<Parked> list;
            lock (_lock)
            {
                list = _parked.Values.ToList();
                _parked.Clear();
                _seen.Clear();
            }

            int n = 0;
            foreach (var e in list)
            {
                bool ok = BoostStateStore.TryRestore(new BoostStateStore.Entry
                {
                    Pid = e.Pid, Name = e.Name, StartUtcTicks = e.StartUtcTicks,
                    PrevAffinity = e.PrevAffinity, PrevPriority = e.PrevPriority,
                    AffinityChanged = e.AffinityChanged, PriorityChanged = e.PriorityChanged
                });
                if (ok)
                {
                    n++;
                    BoostStateStore.Clear(e.Pid);
                }
            }
            return n;
        }
    }
}
