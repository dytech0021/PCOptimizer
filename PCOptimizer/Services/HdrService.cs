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
            // adapterId LUID at offsets 8–15 (unused here)
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

        // Win11 24H2: estado de cor avançado detalhado (HDR e WCG separados)
        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            // bit0 advancedColorSupported, bit1 advancedColorActive,
            // bit3 limitedByPolicy, bit4 hdrSupported, bit5 hdrUserEnabled,
            // bit6 wideColorSupported, bit7 wideColorUserEnabled
            public uint value;
            public uint activeColorMode;
        }

        // Win11 24H2: liga/desliga o ACM ("gerenciar cores automaticamente")
        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_SET_WCG_STATE
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint value; // bit0 = enableWcg
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

        private const uint QDC_ONLY_ACTIVE_PATHS = 2;
        private const int  GET_ADVANCED_COLOR_INFO  = 9;
        private const int  SET_ADVANCED_COLOR_STATE = 10;
        private const int  GET_SDR_WHITE_LEVEL      = 11;
        // Tipo NÃO documentado usado pelo slider "Brilho do conteúdo SDR" das
        // Configurações do Windows (confirmado em implementações open-source).
        private const int  SET_SDR_WHITE_LEVEL      = unchecked((int)0xFFFFFFEE);
        // Win11 24H2 (valores sequenciais no enum oficial após o 11)
        private const int  GET_ADVANCED_COLOR_INFO_2 = 15;
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

                    var req = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
                    {
                        header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                        {
                            type      = GET_ADVANCED_COLOR_INFO,
                            size      = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>(),
                            adapterId = path.targetInfo.adapterId,
                            id        = path.targetInfo.id
                        }
                    };

                    int hdrResult = DisplayConfigGetDeviceInfo(ref req);

                    // D3DKMDT_VIDEO_OUTPUT_TECHNOLOGY: 6=LVDS, 11=DisplayPort embutido,
                    // 13=UDI embutido, 0x80000000=INTERNAL — todos são painel de notebook
                    int tech = path.targetInfo.outputTechnology;
                    bool isInternal = tech == unchecked((int)0x80000000)
                                   || tech == 6 || tech == 11 || tech == 13;

                    // ACM/WCG (só existe no Win11 24H2+; falha silenciosa antes disso)
                    bool wcgSup = false, wcgOn = false;
                    var req2 = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2
                    {
                        header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                        {
                            type      = GET_ADVANCED_COLOR_INFO_2,
                            size      = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2>(),
                            adapterId = path.targetInfo.adapterId,
                            id        = path.targetInfo.id
                        }
                    };
                    if (DisplayConfigGetDeviceInfo(ref req2) == 0)
                    {
                        wcgSup = (req2.value & (1u << 6)) != 0; // wideColorSupported
                        wcgOn  = (req2.value & (1u << 7)) != 0; // wideColorUserEnabled
                    }

                    result.Add(new HdrInfo
                    {
                        SourceX       = srcX,
                        SourceY       = srcY,
                        IsSupported   = hdrResult == 0 && (req.value & 1) != 0,
                        IsEnabled     = hdrResult == 0 && (req.value & 2) != 0,
                        AdapterIdLow  = path.targetInfo.adapterId.LowPart,
                        AdapterIdHigh = path.targetInfo.adapterId.HighPart,
                        TargetId      = path.targetInfo.id,
                        IsInternal    = isInternal,
                        WcgSupported  = wcgSup,
                        WcgEnabled    = wcgOn
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

        public static bool SetHdrEnabled(uint adapterIdLow, int adapterIdHigh, uint targetId, bool enabled)
        {
            try
            {
                var req = new DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type      = SET_ADVANCED_COLOR_STATE,
                        size      = (uint)Marshal.SizeOf<DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE>(),
                        adapterId = new LUID { LowPart = adapterIdLow, HighPart = adapterIdHigh },
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
