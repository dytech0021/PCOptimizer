using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCOptimizer.Services
{
    // Parte do otimizador de memória: medição antes e depois e o que ficou protegido.
    public static partial class MemoryOptimizerService
    {
        // ── Medição ───────────────────────────────────────────────────────────

        /// <summary>Respiro curto para não pegar a foto no meio de uma troca de lista.</summary>
        private static MemorySnapshot SettleShort()
        {
            Thread.Sleep(250);
            return ReadSnapshot();
        }

        /// <summary>
        /// Depois de gravar a memória modificada, espera ela assentar (até 2 s):
        /// a foto tirada cedo demais mostrava páginas ainda a caminho do disco.
        /// </summary>
        private static MemorySnapshot WaitModifiedSettled()
        {
            var sw = Stopwatch.StartNew();
            Thread.Sleep(250);
            var snap = ReadSnapshot();
            ulong prev = ulong.MaxValue;
            while (snap.ModifiedBytes is ulong mod
                   && !MemoryOptimizerPolicy.SettleComplete(prev, mod, sw.Elapsed))
            {
                prev = mod;
                Thread.Sleep(250);
                snap = ReadSnapshot();
            }
            return snap;
        }

        /// <summary>Tamanho do depósito de memória comprimida (conta como "em uso").</summary>
        private static long CompressionStoreBytes(List<ProcInfo>? processes)
        {
            try
            {
                if (processes != null)
                    return processes.Where(p => p.Name is "MemCompression" or "Memory Compression")
                                    .Sum(p => p.WorkingSet);
                long total = 0;
                foreach (var name in new[] { "MemCompression", "Memory Compression" })
                    foreach (var p in Process.GetProcessesByName(name))
                        using (p) total += p.WorkingSet64;
                return total;
            }
            catch { return 0; }
        }

        /// <summary>Os 3 maiores que ficaram de fora e por quê.</summary>
        private static IReadOnlyList<ProtectedEntry> TopProtected(List<ProcInfo> processes, TrimContext ctx,
            MemoryLevelSpec spec, Dictionary<int, string> reasons)
        {
            var list = new List<ProtectedEntry>();
            foreach (var p in processes)
            {
                if (p.Pid <= 4 || p.Pid == ctx.MyPid || p.WorkingSet < (long)MemoryOptimizerPolicy.SmallGainBytes) continue;
                var c = new ProcessCandidate(p.Pid, p.Name, p.SessionId, p.WorkingSet);
                string? reason = null;
                if (reasons.TryGetValue(p.Pid, out var r)
                    && (ctx.ProtectedPids.Contains(p.Pid) || ctx.ServiceDenyPids.Contains(p.Pid)
                        || (ctx.VisibleNonGamePids.Contains(p.Pid) && spec.Level != MemoryLevel.Maximo)))
                    reason = r;
                else if (p.SessionId != 0
                         && MemoryOptimizerPolicy.IsProtectedName(p.Name, ctx.ExtraProtectedNames, ctx.UserProtected)
                         && !MemoryOptimizerPolicy.NeverTreeRoots.Contains(p.Name)
                         && p.Name is not ("dwm" or "MemCompression" or "Memory Compression"))
                    reason = MemoryOptimizerPolicy.ReasonName;
                if (reason != null) list.Add(new ProtectedEntry(p.Name, reason, (ulong)p.WorkingSet));
            }
            return list.OrderByDescending(e => e.Bytes).Take(3).ToList();
        }
    }
}
