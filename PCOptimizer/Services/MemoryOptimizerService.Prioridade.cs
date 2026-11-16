using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCOptimizer.Services
{
    // Parte do otimizador de memória: prioridade de memória baixada e devolvida.
    public static partial class MemoryOptimizerService
    {
        // ── Prioridade de memória ─────────────────────────────────────────────

        private sealed record Lowered(int Pid, long CreationTime, uint Original);
        private static readonly object LoweredLock = new();
        private static readonly Dictionary<int, Lowered> LoweredPids = new();
        /// <summary>Ligado ao sair do app: nada mais é baixado depois do desfazer final.</summary>
        private static bool _lowerBlocked;

        /// <summary>
        /// Programas em segundo plano SEM janela ficam com prioridade de memória
        /// baixa: quando o jogo pedir RAM, o Windows tira deles primeiro. Guarda o
        /// valor original para devolver ao sair do app ou ao voltar ao nível Leve.
        /// </summary>
        private static int LowerPriorities(List<ProcInfo> processes, TrimContext ctx, WindowScan scan)
        {
            int changed = 0;
            var parents = new HashSet<int>(processes.Select(p => p.ParentPid));
            long minCreated = DateTime.UtcNow.ToFileTimeUtc() - MemoryOptimizerPolicy.PriorityMinAge.Ticks;
            foreach (var p in processes)
            {
                var c = new ProcessCandidate(p.Pid, p.Name, p.SessionId, p.WorkingSet);
                if (!MemoryOptimizerPolicy.IsPriorityCandidate(c, ctx, scan.WindowOwners, parents)) continue;
                lock (LoweredLock) if (_lowerBlocked || LoweredPids.ContainsKey(p.Pid)) continue;

                IntPtr h = MemoryNative.OpenProcess(
                    MemoryNative.PROCESS_SET_INFORMATION | MemoryNative.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Pid);
                if (h == IntPtr.Zero) continue;
                try
                {
                    string? path = MemoryNative.ImagePath(h);
                    if (!MemoryOptimizerPolicy.ImageNameMatches(path, p.Name)) continue;
                    if (MemoryOptimizerPolicy.PathExcluded(path, ctx)) continue;
                    if (!MemoryNative.GetProcessInformation(h, MemoryNative.ProcessMemoryPriority, out uint original, 4))
                        continue;
                    if (original <= MemoryNative.MEMORY_PRIORITY_LOW) continue;   // nunca sobe
                    if (!MemoryNative.GetProcessTimes(h, out long created, out _, out _, out _)) continue;
                    if (created > minCreated) continue;   // recém-aberto: pode ser um jogo carregando

                    // Baixar e registrar num passo só: um desfazer que comece no
                    // meio não pode deixar um processo baixado sem registro.
                    uint low = MemoryNative.MEMORY_PRIORITY_LOW;
                    lock (LoweredLock)
                    {
                        if (_lowerBlocked || CurrentLevel == MemoryLevel.Leve) break;
                        if (!MemoryNative.SetProcessInformation(h, MemoryNative.ProcessMemoryPriority, ref low, 4)) continue;
                        LoweredPids[p.Pid] = new Lowered(p.Pid, created, original);
                    }
                    changed++;
                }
                finally { MemoryNative.CloseHandle(h); }
            }
            if (changed > 0) Logger.Info($"Memória: prioridade baixa em {changed} processo(s) sem janela");
            return MemoryNative.STATUS_SUCCESS;
        }

        /// <summary>
        /// Devolve a prioridade de memória original. Confere o horário de criação
        /// para não mexer num processo novo que herdou o mesmo PID.
        /// </summary>
        public static void RestoreMemoryPriorities(bool block = false)
        {
            List<Lowered> list;
            lock (LoweredLock)
            {
                if (block) _lowerBlocked = true;
                list = LoweredPids.Values.ToList();
                LoweredPids.Clear();
            }
            int restored = 0;
            foreach (var l in list)
            {
                IntPtr h = MemoryNative.OpenProcess(
                    MemoryNative.PROCESS_SET_INFORMATION | MemoryNative.PROCESS_QUERY_LIMITED_INFORMATION, false, l.Pid);
                if (h == IntPtr.Zero) continue;   // já fechou
                try
                {
                    if (!MemoryNative.GetProcessTimes(h, out long created, out _, out _, out _) || created != l.CreationTime)
                        continue;
                    uint value = l.Original;
                    if (MemoryNative.SetProcessInformation(h, MemoryNative.ProcessMemoryPriority, ref value, 4)) restored++;
                }
                catch { }
                finally { MemoryNative.CloseHandle(h); }
            }
            if (restored > 0) Logger.Info($"Memória: prioridade original devolvida a {restored} processo(s)");
        }
    }
}
