using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCOptimizer.Services
{
    // Parte do otimizador de memória: leitura dos processos, proteções e limpeza.
    public static partial class MemoryOptimizerService
    {
        // ── Processos ─────────────────────────────────────────────────────────

        private sealed record ProcInfo(int Pid, int ParentPid, string Name, int SessionId, long WorkingSet);

        /// <summary>
        /// Retrato dos processos sem abrir nenhum: nome, sessão e working set vêm
        /// do retrato do sistema; o pai vem do Toolhelp.
        /// </summary>
        private static List<ProcInfo> SnapshotProcesses()
        {
            var parents = new Dictionary<int, int>();
            IntPtr snap = MemoryNative.CreateToolhelp32Snapshot(MemoryNative.TH32CS_SNAPPROCESS, 0);
            if (snap != IntPtr.Zero && snap != MemoryNative.INVALID_HANDLE_VALUE)
            {
                try
                {
                    var e = new MemoryNative.PROCESSENTRY32W
                    {
                        dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryNative.PROCESSENTRY32W>()
                    };
                    if (MemoryNative.Process32First(snap, ref e))
                        do { parents[(int)e.th32ProcessID] = (int)e.th32ParentProcessID; }
                        while (MemoryNative.Process32Next(snap, ref e));
                }
                finally { MemoryNative.CloseHandle(snap); }
            }

            var list = new List<ProcInfo>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    list.Add(new ProcInfo(p.Id, parents.TryGetValue(p.Id, out int parent) ? parent : 0,
                                          p.ProcessName, p.SessionId, p.WorkingSet64));
                }
                catch { /* processo saiu no meio do retrato */ }
                finally { p.Dispose(); }
            }
            return list;
        }

        /// <summary>O que a limpeza não pode tocar, e por quê (para explicar ganho pequeno).</summary>
        private sealed record Protection(TrimContext Context, Dictionary<int, string> Reasons);

        /// <summary>
        /// Monta, uma vez por execução, tudo que fica de fora:
        /// - árvore do jogo, dos jogos lembrados, da janela da frente e da janela
        ///   mais ao topo (o app que o usuário usava antes de abrir o PC Optimizer);
        /// - árvore de toda janela na tela que NÃO é sabidamente não-jogo;
        /// - apps na tela que não são jogo ficam num conjunto à parte, que só a
        ///   passada do nível Máximo usa.
        /// Shell e serviços do Windows protegem só a si mesmos (o explorer é pai de
        /// quase tudo).
        /// </summary>
        private static Protection BuildProtection(List<ProcInfo> processes, WindowScan scan, bool needServices)
        {
            int myPid = Environment.ProcessId;
            var namesByPid = new Dictionary<int, string>();
            foreach (var p in processes) namesByPid[p.Pid] = p.Name;
            var edges = processes.Select(p => (p.Pid, p.ParentPid)).ToList();
            var reasons = new Dictionary<int, string>();

            int fgPid = 0;
            try
            {
                IntPtr fg = MemoryNative.GetForegroundWindow();
                if (fg != IntPtr.Zero) MemoryNative.GetWindowThreadProcessId(fg, out fgPid);
            }
            catch { }

            HashSet<int> Protect(IEnumerable<int> roots, string reason)
            {
                var (tree, leaves) = MemoryOptimizerPolicy.SplitRoots(roots, namesByPid);
                var set = MemoryOptimizerPolicy.ExpandProcessTree(edges, tree);
                set.UnionWith(leaves);
                foreach (int pid in set) reasons.TryAdd(pid, reason);
                return set;
            }

            var hard = new HashSet<int>();
            hard.UnionWith(Protect(scan.GamePids.Concat(LatchedPids()), MemoryOptimizerPolicy.ReasonGame));
            hard.UnionWith(Protect(new[] { fgPid, scan.TopZPid }.Where(p => p > 4), MemoryOptimizerPolicy.ReasonInUse));
            hard.UnionWith(Protect(scan.VisiblePids.Except(scan.NonGameVisiblePids), MemoryOptimizerPolicy.ReasonOnScreen));
            hard.Add(myPid);

            // Apps na tela que não são jogo: só o próprio app (e os processos com
            // o mesmo executável) pode ser esvaziado; qualquer outro filho dele —
            // que pode ser um jogo aberto por um launcher — volta para a proteção.
            var (sameApp, foreignRoots) = MemoryOptimizerPolicy.SplitNonGameTree(
                edges, namesByPid, scan.NonGameVisiblePids.Except(hard));
            hard.UnionWith(Protect(foreignRoots, MemoryOptimizerPolicy.ReasonOnScreen));
            var nonGame = new HashSet<int>(sameApp);
            foreach (int pid in nonGame) reasons.TryAdd(pid, MemoryOptimizerPolicy.ReasonOnScreen);
            nonGame.ExceptWith(hard);

            var dirs = new List<string>();
            foreach (int pid in scan.GamePids)
                if (MemoryOptimizerPolicy.DirectoryOf(MemoryNative.ImagePathReadOnly(pid)) is string d) dirs.Add(d);

            var extraNames = new List<string>();
            if (GameBoostService.TargetName is string boostName && boostName.Length > 0) extraNames.Add(boostName);

            List<string> userProtected;
            try { userProtected = SettingsService.Current.GameBoostUserProtected?.ToList() ?? new List<string>(); }
            catch { userProtected = new List<string>(); }

            var deny = new HashSet<int>();
            if (needServices)
            {
                foreach (var (name, pid) in MemoryNative.ActiveServices())
                    if (MemoryOptimizerPolicy.ServiceDenyList.Contains(name)) deny.Add(pid);
                foreach (int pid in deny) reasons.TryAdd(pid, MemoryOptimizerPolicy.ReasonService);
            }

            var ctx = new TrimContext(myPid, hard, extraNames, dirs, userProtected,
                Environment.GetFolderPath(Environment.SpecialFolder.Windows))
            {
                VisibleNonGamePids = nonGame,
                ServiceDenyPids = deny,
            };
            return new Protection(ctx, reasons);
        }

        /// <summary>
        /// Uma passada de limpeza: escolhe, abre só com SET_QUOTA + consulta,
        /// confere nome e pasta depois de abrir, e esvazia.
        /// </summary>
        private static int TrimPass(List<ProcInfo> processes, TrimContext ctx, MemoryLevelSpec spec,
                                    TrimKind pass, ref int trimmed)
        {
            var candidates = processes.Select(p => new ProcessCandidate(p.Pid, p.Name, p.SessionId, p.WorkingSet));
            // Serviços moram em System32; apps do Windows na sessão do usuário
            // também — só o nível Máximo os alcança.
            bool allowWindowsDir = pass == TrimKind.Service || spec.AllowWindowsDir;
            foreach (var target in MemoryOptimizerPolicy.PickTrimTargets(candidates, ctx, spec.Level, pass))
            {
                IntPtr h = MemoryNative.OpenProcess(
                    MemoryNative.PROCESS_SET_QUOTA | MemoryNative.PROCESS_QUERY_LIMITED_INFORMATION, false, target.Pid);
                if (h == IntPtr.Zero) continue;   // protegido ou sem permissão — segue
                try
                {
                    string? path = MemoryNative.ImagePath(h);
                    if (!MemoryOptimizerPolicy.ImageNameMatches(path, target.Name)) continue;   // PID reaproveitado
                    if (MemoryOptimizerPolicy.PathExcluded(path, ctx, allowWindowsDir)) continue;
                    if (MemoryNative.EmptyWorkingSet(h)) trimmed++;
                }
                finally { MemoryNative.CloseHandle(h); }

                // Espaça as limpezas para o gravador de páginas e a compressão de
                // memória não receberem tudo de uma vez.
                Thread.Sleep(5);
            }
            return MemoryNative.STATUS_SUCCESS;
        }

        /// <summary>
        /// Serviços rodam como SYSTEM: abrir com SET_QUOTA pede o privilégio de
        /// depuração, ligado só durante esta passada e desligado logo depois.
        /// </summary>
        private static int TrimServicesPass(List<ProcInfo> processes, TrimContext ctx, MemoryLevelSpec spec, ref int trimmed)
        {
            bool debug = MemoryNative.TrySetPrivilege(MemoryNative.SE_DEBUG_NAME, true);
            try { return TrimPass(processes, ctx, spec, TrimKind.Service, ref trimmed); }
            finally { if (debug) MemoryNative.TrySetPrivilege(MemoryNative.SE_DEBUG_NAME, false); }
        }
    }
}
