using System;
using System.Runtime.InteropServices;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Descobre quais núcleos lógicos são P-cores (rápidos) e quais são E-cores
    /// (eficiência) em processadores híbridos — Intel 12ª geração em diante.
    ///
    /// É a base do Turbo de Jogo: confinando os outros programas aos E-cores, os
    /// P-cores ficam livres para a thread pesada do jogo, sem o app precisar
    /// tocar no processo do jogo.
    /// </summary>
    public static class CpuTopologyService
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformationEx(
            int relationshipType, IntPtr buffer, ref uint returnedLength);

        private const int RelationProcessorCore    = 0;
        private const int ErrorInsufficientBuffer  = 122;

        public sealed class Topology
        {
            public int   LogicalCount { get; init; }
            /// <summary>Máscara com TODOS os lógicos dos P-cores (inclui HyperThreading).</summary>
            public ulong PCoreMask { get; init; }
            /// <summary>Um lógico por P-core físico (sem o irmão de HyperThreading).</summary>
            public ulong PCorePhysicalMask { get; init; }
            public ulong ECoreMask { get; init; }
            public int   PCoreCount { get; init; }
            public int   ECoreCount { get; init; }
            /// <summary>Há núcleos de classes diferentes (P + E)?</summary>
            public bool  IsHybrid { get; init; }
            /// <summary>
            /// Sistema com mais de um grupo de processadores (mais de 64 lógicos).
            /// A máscara de afinidade é POR GRUPO — forçá-la prenderia os processos
            /// no grupo 0, então o recurso se desativa nesses sistemas.
            /// </summary>
            public bool  MultiGroup { get; init; }
            /// <summary>Dá para usar o confinamento em E-cores nesta máquina?</summary>
            public bool  CanPark => IsHybrid && !MultiGroup && ECoreMask != 0;
        }

        private static Topology? _cache;

        /// <summary>Topologia da CPU (lida uma vez — não muda em execução).</summary>
        public static Topology Get() => _cache ??= Read();

        private static Topology Empty() => new()
        {
            LogicalCount = Environment.ProcessorCount,
            IsHybrid = false
        };

        private static Topology Read()
        {
            IntPtr buffer = IntPtr.Zero;
            try
            {
                // Duas chamadas: a primeira só descobre o tamanho necessário.
                uint len = 0;
                if (GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref len))
                    return Empty(); // sucesso com buffer nulo não deveria acontecer
                if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer || len == 0)
                    return Empty();

                buffer = Marshal.AllocHGlobal((int)len);
                if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref len))
                    return Empty();

                return Parse(buffer, len);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "CpuTopologyService.Read");
                return Empty();
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// Percorre o buffer devolvido pelo Windows. Cada entrada tem TAMANHO
        /// VARIÁVEL (por causa do array GroupMask no fim), então o avanço tem que
        /// usar o campo Size da própria entrada — Marshal.SizeOf de um struct fixo
        /// daria offsets errados a partir da segunda entrada.
        ///
        /// Layout em x64:
        ///   +0  Relationship      +4  Size (total desta entrada)
        ///   +8  Flags             +9  EfficiencyClass
        ///   +30 GroupCount        +32 GroupAffinity[] (16 bytes cada)
        /// GroupAffinity: Mask (IntPtr.Size bytes) + Group (2 bytes) + reservado.
        /// </summary>
        private static Topology Parse(IntPtr buffer, uint length)
        {
            // Acumula por classe de eficiência: maior classe = P-core
            var maskByEff = new System.Collections.Generic.Dictionary<byte, ulong>();
            var physByEff = new System.Collections.Generic.Dictionary<byte, ulong>();
            var countByEff = new System.Collections.Generic.Dictionary<byte, int>();
            bool multiGroup = false;

            int gaSize = IntPtr.Size + 8; // 16 em x64, 12 em x86
            uint offset = 0;

            while (offset + 8 <= length)
            {
                IntPtr entry = IntPtr.Add(buffer, (int)offset);
                int rel  = Marshal.ReadInt32(entry, 0);
                int size = Marshal.ReadInt32(entry, 4);
                if (size <= 0) break; // buffer inconsistente — não arrisca laço infinito

                if (rel == RelationProcessorCore)
                {
                    byte eff = Marshal.ReadByte(entry, 9);
                    int groupCount = (ushort)Marshal.ReadInt16(entry, 30);

                    for (int i = 0; i < groupCount; i++)
                    {
                        int gaOff = 32 + i * gaSize;
                        if (offset + (uint)(gaOff + gaSize) > length) break;

                        ulong  mask  = unchecked((ulong)Marshal.ReadIntPtr(entry, gaOff).ToInt64());
                        ushort group = (ushort)Marshal.ReadInt16(entry, gaOff + IntPtr.Size);
                        if (group != 0) { multiGroup = true; continue; }

                        maskByEff[eff] = (maskByEff.TryGetValue(eff, out var m) ? m : 0UL) | mask;

                        // Um bit por núcleo FÍSICO: o menos significativo da máscara
                        // (o outro, quando existe, é o irmão de HyperThreading).
                        ulong lowest = mask & (~mask + 1);
                        physByEff[eff] = (physByEff.TryGetValue(eff, out var p) ? p : 0UL) | lowest;

                        countByEff[eff] = (countByEff.TryGetValue(eff, out var c) ? c : 0) + 1;
                    }
                }

                offset += (uint)size;
            }

            if (maskByEff.Count == 0) return Empty();

            byte maxEff = 0;
            foreach (var e in maskByEff.Keys) if (e > maxEff) maxEff = e;
            bool hybrid = maxEff > 0 && maskByEff.Count > 1;

            ulong pMask = maskByEff.TryGetValue(maxEff, out var pm) ? pm : 0UL;
            ulong pPhys = physByEff.TryGetValue(maxEff, out var pp) ? pp : 0UL;
            int   pCnt  = countByEff.TryGetValue(maxEff, out var pc) ? pc : 0;

            // Tudo que não é da classe mais alta conta como E-core
            ulong eMask = 0; int eCnt = 0;
            foreach (var kv in maskByEff)
                if (kv.Key != maxEff) { eMask |= kv.Value; eCnt += countByEff[kv.Key]; }

            if (Environment.ProcessorCount > 64) multiGroup = true;

            var topo = new Topology
            {
                LogicalCount      = Environment.ProcessorCount,
                PCoreMask         = pMask,
                PCorePhysicalMask = pPhys,
                ECoreMask         = eMask,
                PCoreCount        = pCnt,
                ECoreCount        = eCnt,
                IsHybrid          = hybrid,
                MultiGroup        = multiGroup
            };

            Logger.Info($"CPU: {Describe(topo)} | P=0x{pMask:X} E=0x{eMask:X} " +
                        $"híbrida={hybrid} multiGrupo={multiGroup}");
            return topo;
        }

        /// <summary>Texto curto da topologia, para mostrar na interface.</summary>
        public static string Describe() => Describe(Get());

        private static string Describe(Topology t)
        {
            if (!t.IsHybrid)
                return $"{t.LogicalCount} núcleos lógicos (CPU não híbrida)";

            int pThreads = CountBits(t.PCoreMask);
            string p = pThreads > t.PCoreCount
                ? $"{t.PCoreCount} P-cores ({pThreads} threads)"
                : $"{t.PCoreCount} P-cores";
            return $"{p} + {t.ECoreCount} E-cores";
        }

        private static int CountBits(ulong v)
        {
            int n = 0;
            while (v != 0) { v &= v - 1; n++; }
            return n;
        }
    }
}
