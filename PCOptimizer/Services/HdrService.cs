using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PCOptimizer.Services
{
    public class HdrInfo
    {
        public int SourceX { get; set; }
        public int SourceY { get; set; }
        public bool IsSupported { get; set; }
        public bool IsEnabled { get; set; }
        public uint AdapterIdLow { get; set; }
        public int AdapterIdHigh { get; set; }
        public uint TargetId { get; set; }
        // Painel interno do notebook (eDP/LVDS/INTERNAL) — WMI só controla este
        public bool IsInternal { get; set; }
        // ACM/WCG (Win11 24H2): "Gerenciar cores automaticamente para apps"
        public bool WcgSupported { get; set; }
        public bool WcgEnabled { get; set; }
        // 0 = SDR, 1 = WCG (gamut largo), 2 = HDR — o modo em que a área de
        // trabalho é composta. WCG é o que faz a captura remota sair saturada.
        public uint ActiveColorMode { get; set; }
        // Nome do monitor vindo do MESMO path do DisplayConfig — correlação
        // exata, sem depender de casar listas de fontes diferentes (WMI × DDC),
        // que trocava os nomes entre monitores iguais.
        public string FriendlyName { get; set; } = "";
    }

    public static class HdrService
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_RATIONAL
        {
            public uint Numerator;
            public uint Denominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public int outputTechnology;
            public int rotation;
            public int scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public int scanLineOrdering;
            public int targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        // 64-byte struct: 4+4+8 header + 48-byte union; source mode fields overlaid at offset 16.
        [StructLayout(LayoutKind.Explicit, Size = 64)]
        private struct DISPLAYCONFIG_MODE_INFO
        {
            [FieldOffset(0)]  public uint infoType;         // 1=source, 2=target
            [FieldOffset(4)]  public uint id;
            // adapterId (LUID) — usado para endereçar o target nas chamadas de HDR
            [FieldOffset(8)]  public uint adapterIdLow;
            [FieldOffset(12)] public int  adapterIdHigh;
            [FieldOffset(16)] public uint sourceWidth;
            [FieldOffset(20)] public uint sourceHeight;
            [FieldOffset(24)] public int  pixelFormat;
            [FieldOffset(28)] public int  sourcePositionX;
            [FieldOffset(32)] public int  sourcePositionY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
        {
            public int  type;
            public uint size;
            public LUID adapterId;
            public uint id;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint value; // bit0=supported, bit1=enabled, bit2=wideColor, bit3=forceDisabled
            // O Windows valida o tamanho EXATO do pacote (32 bytes) — sem estes dois
            // campos o struct tinha 24 bytes e a chamada falhava sempre com
            // ERROR_INVALID_PARAMETER, fazendo todo monitor parecer "sem HDR".
            public uint colorEncoding;       // DISPLAYCONFIG_COLOR_ENCODING
            public uint bitsPerColorChannel;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint value; // bit0=enableAdvancedColor
        }

        // "Brilho do conteúdo SDR" com HDR ativo — o mesmo slider das Configurações.
        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_SDR_WHITE_LEVEL
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint SDRWhiteLevel; // 1000 = 80 nits; escala do slider: 1000 + pct*50
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_SET_SDR_WHITE_LEVEL
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint SDRWhiteLevel;
            public byte finalValue; // 1 = aplica de fato (0 = prévia durante arraste)
        }

        // Win11 24H2: estado de cor avançado detalhado (HDR e WCG separados).
        // ATENÇÃO ao tamanho: 36 bytes (header 20 + value 4 + colorEncoding 4 +
        // bitsPerColorChannel 4 + activeColorMode 4). O Windows valida o tamanho
        // EXATO — declarar sem os dois campos do meio fazia a chamada falhar
        // sempre, e o app caía na API antiga (que não distingue HDR de WCG).
        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            // bit0 advancedColorSupported, bit1 advancedColorActive,
            // bit3 limitedByPolicy, bit4 hdrSupported, bit5 hdrUserEnabled,
            // bit6 wideColorSupported, bit7 wideColorUserEnabled
            public uint value;
            public uint colorEncoding;
            public uint bitsPerColorChannel;
            public uint activeColorMode; // 0=SDR 1=WCG 2=HDR
        }

        // Win11 24H2: liga/desliga o ACM ("gerenciar cores automaticamente")
        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_SET_WCG_STATE
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint value; // bit0 = enableWcg
        }

        // Win11 24H2: alterna SÓ o HDR. Necessário porque o antigo
        // SET_ADVANCED_COLOR_STATE não funciona quando o usuário tem
        // "Gerenciar cores automaticamente para apps" (ACM) ligado.
        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_SET_HDR_STATE
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint value; // bit0 = enableHdr
        }

        // Nome/caminho do monitor associado a um target do DisplayConfig
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint flags;
            public int  outputTechnology;
            public ushort edidManufactureId;
            public ushort edidProductCodeId;
            public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]  public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
        }

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags,
            ref uint numPathArrayElements, ref uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags,
            ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
            ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
            IntPtr currentTopologyId);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(
            ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigSetDeviceInfo(
            ref DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE setPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(
            ref DISPLAYCONFIG_SDR_WHITE_LEVEL requestPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigSetDeviceInfo(
            ref DISPLAYCONFIG_SET_SDR_WHITE_LEVEL setPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(
            ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 requestPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigSetDeviceInfo(
            ref DISPLAYCONFIG_SET_WCG_STATE setPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigSetDeviceInfo(
            ref DISPLAYCONFIG_SET_HDR_STATE setPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(
            ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

        private const uint QDC_ONLY_ACTIVE_PATHS = 2;
        private const int  GET_ADVANCED_COLOR_INFO  = 9;
        private const int  SET_ADVANCED_COLOR_STATE = 10;
        private const int  GET_SDR_WHITE_LEVEL      = 11;
        // Tipo NÃO documentado usado pelo slider "Brilho do conteúdo SDR" das
        // Configurações do Windows (confirmado em implementações open-source).
        private const int  SET_SDR_WHITE_LEVEL      = unchecked((int)0xFFFFFFEE);
        private const int  GET_TARGET_NAME           = 2;
        // Win11 24H2 (valores sequenciais no enum oficial após o 11)
        private const int  GET_ADVANCED_COLOR_INFO_2 = 15;
        private const int  SET_HDR_STATE             = 16;
        private const int  SET_WCG_STATE             = 17;

        public static List<HdrInfo> GetAllHdrInfo()
        {
            var result = new List<HdrInfo>();
            try
            {
                uint numPaths = 0, numModes = 0;
                if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, ref numPaths, ref numModes) != 0)
                    return result;

                var paths = new DISPLAYCONFIG_PATH_INFO[numPaths];
                var modes = new DISPLAYCONFIG_MODE_INFO[numModes];
                if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS,
                        ref numPaths, paths, ref numModes, modes, IntPtr.Zero) != 0)
                    return result;

                // Itera só os numPaths ATUALIZADOS pelo QueryDisplayConfig — se um
                // monitor foi desconectado entre as duas chamadas, o final do array
                // contém entradas zeradas que gerariam monitores "fantasma".
                for (int pi = 0; pi < numPaths; pi++)
                {
                    var path = paths[pi];
                    int srcX = 0, srcY = 0;
                    uint modeIdx = path.sourceInfo.modeInfoIdx;
                    if (modeIdx < numModes && modes[modeIdx].infoType == 1) // source mode
                    {
                        srcX = modes[modeIdx].sourcePositionX;
                        srcY = modes[modeIdx].sourcePositionY;
                    }

                    // Endereça o target pelo MODE INFO quando disponível — é assim
                    // que as implementações que funcionam no 24H2 fazem; o
                    // path.targetInfo nem sempre casa com o que a API de cor espera.
                    var tAdapter = path.targetInfo.adapterId;
                    uint tId     = path.targetInfo.id;
                    uint tIdx    = path.targetInfo.modeInfoIdx;
                    if (tIdx < numModes && modes[tIdx].infoType == 2) // 2 = target mode
                    {
                        tAdapter = new LUID
                        {
                            LowPart  = modes[tIdx].adapterIdLow,
                            HighPart = modes[tIdx].adapterIdHigh
                        };
                        tId = modes[tIdx].id;
                    }

                    var req = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
                    {
                        header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                        {
                            type      = GET_ADVANCED_COLOR_INFO,
                            size      = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>(),
                            adapterId = tAdapter,
                            id        = tId
                        }
                    };

                    int hdrResult = DisplayConfigGetDeviceInfo(ref req);

                    // D3DKMDT_VIDEO_OUTPUT_TECHNOLOGY: 6=LVDS, 11=DisplayPort embutido,
                    // 13=UDI embutido, 0x80000000=INTERNAL — todos são painel de notebook
                    int tech = path.targetInfo.outputTechnology;
                    bool isInternal = tech == unchecked((int)0x80000000)
                                   || tech == 6 || tech == 11 || tech == 13;

                    // Estado de cor pela API NOVA (Win11 24H2+), que separa HDR de
                    // WCG. Quando disponível ela é a fonte da verdade: a antiga
                    // (tipo 9) reporta "advanced color" ligado tanto em HDR quanto
                    // em WCG, o que confundia o botão de HDR.
                    bool wcgSup = false, wcgOn = false;
                    bool hdrSup = hdrResult == 0 && (req.value & 1) != 0;
                    bool hdrOn  = hdrResult == 0 && (req.value & 2) != 0;
                    uint colorMode = 0;

                    var req2 = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2
                    {
                        header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                        {
                            type      = GET_ADVANCED_COLOR_INFO_2,
                            size      = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2>(),
                            adapterId = tAdapter,
                            id        = tId
                        }
                    };
                    if (DisplayConfigGetDeviceInfo(ref req2) == 0)
                    {
                        hdrSup    = (req2.value & (1u << 4)) != 0; // highDynamicRangeSupported
                        hdrOn     = (req2.value & (1u << 5)) != 0; // highDynamicRangeUserEnabled
                        wcgSup    = (req2.value & (1u << 6)) != 0; // wideColorSupported
                        wcgOn     = (req2.value & (1u << 7)) != 0; // wideColorUserEnabled
                        colorMode = req2.activeColorMode;          // 0=SDR 1=WCG 2=HDR
                    }

                    // Nome do monitor pelo MESMO path — correlação exata
                    string friendly = "";
                    var reqName = new DISPLAYCONFIG_TARGET_DEVICE_NAME
                    {
                        header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                        {
                            type      = GET_TARGET_NAME,
                            size      = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                            adapterId = tAdapter,
                            id        = tId
                        }
                    };
                    if (DisplayConfigGetDeviceInfo(ref reqName) == 0)
                        friendly = (reqName.monitorFriendlyDeviceName ?? "").Trim();

                    result.Add(new HdrInfo
                    {
                        SourceX       = srcX,
                        SourceY       = srcY,
                        IsSupported   = hdrSup,
                        IsEnabled     = hdrOn,
                        AdapterIdLow  = tAdapter.LowPart,
                        AdapterIdHigh = tAdapter.HighPart,
                        TargetId      = tId,
                        IsInternal      = isInternal,
                        WcgSupported    = wcgSup,
                        WcgEnabled      = wcgOn,
                        ActiveColorMode = colorMode,
                        FriendlyName    = friendly
                    });
                }
            }
            catch { }
            return result;
        }

        /// <summary>
        /// Percentual atual do "brilho do conteúdo SDR" (0–100, o slider do Windows
        /// quando o HDR está ativo). Retorna -1 se indisponível.
        /// </summary>
        public static int GetSdrBrightness(uint adapterIdLow, int adapterIdHigh, uint targetId)
        {
            try
            {
                var req = new DISPLAYCONFIG_SDR_WHITE_LEVEL
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type      = GET_SDR_WHITE_LEVEL,
                        size      = (uint)Marshal.SizeOf<DISPLAYCONFIG_SDR_WHITE_LEVEL>(),
                        adapterId = new LUID { LowPart = adapterIdLow, HighPart = adapterIdHigh },
                        id        = targetId
                    }
                };
                if (DisplayConfigGetDeviceInfo(ref req) != 0) return -1;
                return Math.Clamp(((int)req.SDRWhiteLevel - 1000) / 50, 0, 100);
            }
            catch { return -1; }
        }

        /// <summary>
        /// Define o "brilho do conteúdo SDR" (0–100) de um monitor com HDR ativo —
        /// com HDR ligado o monitor ignora brilho DDC/CI e gamma ramp, então este
        /// é o caminho que realmente funciona (o mesmo das Configurações do Windows).
        /// </summary>
        public static bool SetSdrBrightness(uint adapterIdLow, int adapterIdHigh, uint targetId, int percent)
        {
            try
            {
                var pkt = new DISPLAYCONFIG_SET_SDR_WHITE_LEVEL
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type      = SET_SDR_WHITE_LEVEL,
                        size      = (uint)Marshal.SizeOf<DISPLAYCONFIG_SET_SDR_WHITE_LEVEL>(),
                        adapterId = new LUID { LowPart = adapterIdLow, HighPart = adapterIdHigh },
                        id        = targetId
                    },
                    SDRWhiteLevel = (uint)(1000 + Math.Clamp(percent, 0, 100) * 50),
                    finalValue    = 1
                };
                return DisplayConfigSetDeviceInfo(ref pkt) == 0;
            }
            catch (Exception ex) { Logger.Error(ex, "SetSdrBrightness"); return false; }
        }

        /// <summary>
        /// Liga/desliga o ACM — "Gerenciar cores automaticamente para apps"
        /// (Win11 24H2+). Com ACM ligado e HDR desligado, o Windows converte a
        /// área de trabalho para o gamut LARGO do monitor: quem captura a tela
        /// (AnyDesk, prints) recebe cores super-saturadas numa tela sRGB comum.
        /// </summary>
        public static bool SetWcgEnabled(uint adapterIdLow, int adapterIdHigh, uint targetId, bool enabled)
        {
            try
            {
                var pkt = new DISPLAYCONFIG_SET_WCG_STATE
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type      = SET_WCG_STATE,
                        size      = (uint)Marshal.SizeOf<DISPLAYCONFIG_SET_WCG_STATE>(),
                        adapterId = new LUID { LowPart = adapterIdLow, HighPart = adapterIdHigh },
                        id        = targetId
                    },
                    value = enabled ? 1u : 0u
                };
                return DisplayConfigSetDeviceInfo(ref pkt) == 0;
            }
            catch (Exception ex) { Logger.Error(ex, "SetWcgEnabled"); return false; }
        }

        /// <summary>
        /// Liga/desliga o HDR. Tenta primeiro a API do Win11 24H2 (SET_HDR_STATE):
        /// a antiga (SET_ADVANCED_COLOR_STATE) NÃO funciona quando o usuário tem
        /// "Gerenciar cores automaticamente para apps" (ACM) ligado — era por isso
        /// que o botão de HDR não respondia. Em Windows mais antigos a API nova
        /// falha e caímos na antiga, que lá funciona normalmente.
        /// </summary>
        public static bool SetHdrEnabled(uint adapterIdLow, int adapterIdHigh, uint targetId, bool enabled)
        {
            var luid = new LUID { LowPart = adapterIdLow, HighPart = adapterIdHigh };

            try
            {
                var pkt = new DISPLAYCONFIG_SET_HDR_STATE
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type      = SET_HDR_STATE,
                        size      = (uint)Marshal.SizeOf<DISPLAYCONFIG_SET_HDR_STATE>(),
                        adapterId = luid,
                        id        = targetId
                    },
                    value = enabled ? 1u : 0u
                };
                if (DisplayConfigSetDeviceInfo(ref pkt) == 0) return true;
            }
            catch { /* Windows sem a API nova — usa a antiga abaixo */ }

            try
            {
                var req = new DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type      = SET_ADVANCED_COLOR_STATE,
                        size      = (uint)Marshal.SizeOf<DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE>(),
                        adapterId = luid,
                        id        = targetId
                    },
                    value = enabled ? 1u : 0u
                };
                return DisplayConfigSetDeviceInfo(ref req) == 0;
            }
            catch { return false; }
        }
    }
}
