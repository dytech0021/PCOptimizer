using System;
using System.Runtime.InteropServices;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Acesso direto à NVAPI (nvapi64.dll, instalada com todo driver NVIDIA) para
    /// aplicar offset de clock no core e na memória — o mesmo mecanismo usado pelo
    /// MSI Afterburner. O offset desloca a curva tensão/frequência: positivo junto
    /// com uma trava de clock (nvidia-smi -lgc) produz o efeito de undervolt.
    /// O driver mantém as proteções de tensão/temperatura; offset instável causa
    /// no máximo crash do driver, revertido ao reiniciar.
    /// </summary>
    public static class NvapiService
    {
        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface",
                   CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NvAPI_QueryInterface(uint id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int InitializeDel();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int EnumPhysicalGPUsDel([Out] IntPtr[] handles, out int count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int SetPstates20Del(IntPtr gpu, IntPtr pstatesInfo);

        // IDs públicos e estáveis das funções NVAPI
        private const uint IdInitialize = 0x0150E828;
        private const uint IdEnumPhysicalGPUs = 0xE5AC921F;
        private const uint IdSetPstates20 = 0x0F4DAE6B;

        // Domínios de clock (NV_GPU_PUBLIC_CLOCK_ID)
        public const int ClockGraphics = 0;
        public const int ClockMemory = 4;

        // NV_GPU_PERF_PSTATES20_INFO: header 20 + 16 pstates de 456 bytes.
        // V2 acrescenta o bloco ov: numVoltages (4) + 4 entradas de 24 = 100 bytes.
        // O driver valida o campo version contra sizeof exato — 7416, não 7420.
        private const int V1Size = 20 + 16 * 456;       // 7316 (0x1C94)
        private const int V2Size = V1Size + 4 + 4 * 24; // 7416 (0x1CF8)

        private const int NvapiIncompatibleStructVersion = -9;

        /// <summary>Último status devolvido pela NVAPI (0 = OK) — para diagnóstico na UI.</summary>
        public static int LastStatus { get; private set; }

        private static IntPtr _gpu = IntPtr.Zero;
        private static SetPstates20Del? _setPstates;
        private static bool _initTried, _initOk;

        private static T? GetFn<T>(uint id) where T : Delegate
        {
            IntPtr ptr = NvAPI_QueryInterface(id);
            return ptr == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(ptr);
        }

        private static bool EnsureInit()
        {
            if (_initTried) return _initOk;
            _initTried = true;
            try
            {
                var init = GetFn<InitializeDel>(IdInitialize);
                var enumGpus = GetFn<EnumPhysicalGPUsDel>(IdEnumPhysicalGPUs);
                _setPstates = GetFn<SetPstates20Del>(IdSetPstates20);
                if (init == null || enumGpus == null || _setPstates == null) return false;
                if (init() != 0) return false;

                var handles = new IntPtr[64];
                if (enumGpus(handles, out int count) != 0 || count == 0) return false;
                _gpu = handles[0];
                _initOk = true;
            }
            catch
            {
                // nvapi64.dll ausente (sem driver NVIDIA) ou incompatível
                _initOk = false;
            }
            return _initOk;
        }

        public static bool IsAvailable()
        {
            return EnsureInit();
        }

        /// <summary>
        /// Aplica offset de clock em kHz no P-state 0 (carga máxima).
        /// Core: 1 MHz de offset = 1000 kHz. Memória: idem (o driver converte
        /// para a taxa efetiva internamente).
        /// </summary>
        public static bool SetClockOffsetKHz(int domainId, int offsetKHz)
        {
            if (!EnsureInit() || _setPstates == null) return false;

            // V2 primeiro (drivers atuais); se o driver pedir outra versão, tenta V1
            if (TrySetOffset(domainId, offsetKHz, 2, V2Size)) return true;
            if (LastStatus == NvapiIncompatibleStructVersion &&
                TrySetOffset(domainId, offsetKHz, 1, V1Size)) return true;
            return false;
        }

        private static bool TrySetOffset(int domainId, int offsetKHz, int structVer, int structSize)
        {
            var buf = new byte[structSize];
            var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                uint version = ((uint)structVer << 16) | (uint)structSize;
                IntPtr p = h.AddrOfPinnedObject();
                Marshal.WriteInt32(p, 0, unchecked((int)version)); // version
                Marshal.WriteInt32(p, 8, 1);   // numPstates
                Marshal.WriteInt32(p, 12, 1);  // numClocks
                Marshal.WriteInt32(p, 16, 0);  // numBaseVoltages
                Marshal.WriteInt32(p, 20, 0);  // pstates[0].pstateId = P0
                // pstates[0].clocks[0] começa no offset 28
                Marshal.WriteInt32(p, 28, domainId);   // domainId
                Marshal.WriteInt32(p, 32, 0);          // typeId = single
                Marshal.WriteInt32(p, 40, offsetKHz);  // freqDelta_kHz.value

                LastStatus = _setPstates!(_gpu, p);
                return LastStatus == 0;
            }
            catch
            {
                LastStatus = int.MinValue;
                return false;
            }
            finally
            {
                h.Free();
            }
        }

        public static bool SetCoreOffsetMhz(int mhz) => SetClockOffsetKHz(ClockGraphics, mhz * 1000);
        public static bool SetMemoryOffsetMhz(int mhz) => SetClockOffsetKHz(ClockMemory, mhz * 1000);
    }
}
