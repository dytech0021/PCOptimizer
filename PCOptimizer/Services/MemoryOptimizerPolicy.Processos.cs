using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PCOptimizer.Services
{
    // Parte das regras de memória: quais processos limpar e como tratar a árvore de cada app.
    public static partial class MemoryOptimizerPolicy
    {
        // ── Processos ─────────────────────────────────────────────────────────

        /// <summary>Filtro ANTES de abrir qualquer processo.</summary>
        /// <summary>Programa comum, no nível Leve, pode ser esvaziado?</summary>
        public static bool ShouldTrim(ProcessCandidate c, TrimContext ctx) =>
            Classify(c, ctx, SpecFor(MemoryLevel.Leve), TrimKind.Normal) == TrimVerdict.Trim;

        /// <summary>Em que passada o processo cairia: serviço, app na tela ou comum.</summary>
        public static TrimKind KindOf(ProcessCandidate c, TrimContext ctx) =>
            c.SessionId == 0 ? TrimKind.Service
            : ctx.VisibleNonGamePids.Contains(c.Pid) ? TrimKind.VisibleApp
            : TrimKind.Normal;

        /// <summary>
        /// Filtro ANTES de abrir qualquer processo, para uma passada. Devolve o
        /// motivo, que alimenta o "o que estava protegido" do resultado.
        /// </summary>
        public static TrimVerdict Classify(ProcessCandidate c, TrimContext ctx, MemoryLevelSpec spec, TrimKind pass)
        {
            if (c.Pid <= 4) return TrimVerdict.System;
            if (c.Pid == ctx.MyPid) return TrimVerdict.Self;
            if (ctx.ProtectedPids.Contains(c.Pid)) return TrimVerdict.Protected;
            if (string.IsNullOrWhiteSpace(c.Name)) return TrimVerdict.ProtectedName;
            if (IsProtectedName(c.Name, ctx.ExtraProtectedNames, ctx.UserProtected)) return TrimVerdict.ProtectedName;
            if (KindOf(c, ctx) != pass) return TrimVerdict.NotThisPass;
            if (c.WorkingSetBytes < spec.MinTrimWorkingSetBytes) return TrimVerdict.TooSmall;
            if (pass == TrimKind.Service && ctx.ServiceDenyPids.Contains(c.Pid)) return TrimVerdict.ServiceDenied;
            return TrimVerdict.Trim;
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
        public static bool PathExcluded(string? exePath, TrimContext ctx, bool allowWindowsDir = false)
        {
            if (string.IsNullOrWhiteSpace(exePath)) return true;
            if (!allowWindowsDir && !string.IsNullOrWhiteSpace(ctx.WindowsDir) && IsUnder(exePath, ctx.WindowsDir))
                return true;
            foreach (var dir in ctx.ProtectedDirs)
                if (IsUnder(exePath, dir)) return true;

            // Launchers e ferramentas instalados em pasta de jogos, e apps da
            // Store que não são jogo, não são jogo: podem ser esvaziados.
            foreach (var ex in GameInstallExceptions)
                if (exePath.Contains(ex, StringComparison.OrdinalIgnoreCase)) return false;
            foreach (var pkg in NonGameStorePackages)
                if (exePath.Contains(pkg, StringComparison.OrdinalIgnoreCase)) return false;
            foreach (var marker in GameLibraryMarkers)
                if (exePath.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Um app com janela na tela é, com certeza, não-jogo?</summary>
        public static bool IsKnownNonGameApp(string? ownerName, string? className) =>
            (!string.IsNullOrWhiteSpace(ownerName) && KnownNonGameApps.Contains(StripExe(ownerName)))
            || (!string.IsNullOrWhiteSpace(className) && KnownNonGameWindowClasses.Contains(className));

        /// <summary>
        /// Candidato a prioridade de memória baixa: programa em segundo plano SEM
        /// nenhuma janela (minimizada também conta como janela), fora de qualquer
        /// proteção. Assim, quando o jogo pedir memória, o Windows tira destes
        /// primeiro — e nada que o usuário abre fica lento.
        /// </summary>
        public static bool IsPriorityCandidate(ProcessCandidate c, TrimContext ctx, IReadOnlySet<int> windowOwners) =>
            IsPriorityCandidate(c, ctx, windowOwners, EmptyPids);

        /// <summary>
        /// <paramref name="parentPids"/>: quem tem processos filhos agora. Quem
        /// abre programas nunca entra — os filhos herdam a prioridade, e um jogo
        /// aberto por um launcher nasceria com prioridade de memória baixa.
        /// </summary>
        public static bool IsPriorityCandidate(ProcessCandidate c, TrimContext ctx, IReadOnlySet<int> windowOwners,
                                               IReadOnlySet<int> parentPids)
        {
            if (c.Pid <= 4 || c.Pid == ctx.MyPid || c.SessionId == 0) return false;
            if (ctx.ProtectedPids.Contains(c.Pid) || windowOwners.Contains(c.Pid)) return false;
            if (parentPids.Contains(c.Pid)) return false;
            if (string.IsNullOrWhiteSpace(c.Name)) return false;
            string n = StripExe(c.Name);
            if (GameLauncherProcesses.Contains(n) || LauncherOwners.Contains(n) || KnownNonGameApps.Contains(n)) return false;
            if (IsKnownGameProcess(n)) return false;
            return !IsProtectedName(c.Name, ctx.ExtraProtectedNames, ctx.UserProtected);
        }

        /// <summary>Processo aberto há pouco pode ser um jogo ainda carregando, sem janela.</summary>
        public static readonly TimeSpan PriorityMinAge = TimeSpan.FromMinutes(2);

        private static readonly IReadOnlySet<int> EmptyPids = new HashSet<int>();

        /// <summary>
        /// Árvore de um app na tela que não é jogo, para o nível Máximo: segue só
        /// os filhos com o MESMO executável (navegador → navegador). Um filho com
        /// outro executável pode ser um jogo aberto pelo app (Battle.net → Diablo,
        /// launcher → javaw): ele e a subárvore dele voltam para a proteção.
        /// </summary>
        public static (HashSet<int> SameApp, HashSet<int> ForeignRoots) SplitNonGameTree(
            IEnumerable<(int Pid, int ParentPid)> processes, IReadOnlyDictionary<int, string> namesByPid,
            IEnumerable<int> roots)
        {
            var children = new Dictionary<int, List<int>>();
            foreach (var (pid, parent) in processes)
            {
                if (pid == parent) continue;
                if (!children.TryGetValue(parent, out var list)) children[parent] = list = new List<int>();
                list.Add(pid);
            }

            var same = new HashSet<int>();
            var foreign = new HashSet<int>();
            foreach (int root in roots)
            {
                if (root <= 4 || !same.Add(root)) continue;
                namesByPid.TryGetValue(root, out string? rootName);
                var queue = new Queue<int>();
                queue.Enqueue(root);
                while (queue.Count > 0)
                {
                    int p = queue.Dequeue();
                    if (!children.TryGetValue(p, out var kids)) continue;
                    foreach (int k in kids)
                    {
                        if (k <= 4 || same.Contains(k) || foreign.Contains(k)) continue;
                        namesByPid.TryGetValue(k, out string? kidName);
                        if (rootName != null && kidName != null
                            && string.Equals(StripExe(kidName), StripExe(rootName), StringComparison.OrdinalIgnoreCase))
                        {
                            same.Add(k);
                            queue.Enqueue(k);
                        }
                        else foreign.Add(k);
                    }
                }
            }
            return (same, foreign);
        }

        /// <summary>A memória modificada já foi gravada (ou parou de cair, ou deu o tempo)?</summary>
        public static bool SettleComplete(ulong previousModified, ulong currentModified, TimeSpan elapsed) =>
            elapsed >= SettleTimeout
            || currentModified <= SettledModifiedBytes
            || currentModified + (16UL << 20) >= previousModified;

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

        /// <summary>Filtra, ordena do maior para o menor e limita conforme o nível.</summary>
        public static IReadOnlyList<ProcessCandidate> PickTrimTargets(
            IEnumerable<ProcessCandidate> all, TrimContext ctx,
            MemoryLevel level = MemoryLevel.Leve, TrimKind pass = TrimKind.Normal)
        {
            var spec = SpecFor(level);
            return all.Where(c => Classify(c, ctx, spec, pass) == TrimVerdict.Trim)
                      .OrderByDescending(c => c.WorkingSetBytes)
                      .Take(spec.MaxTrimProcesses)
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
    }
}
