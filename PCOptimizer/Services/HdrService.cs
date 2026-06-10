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
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint value; // bit0=enableAdvancedColor
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

        private const uint QDC_ONLY_ACTIVE_PATHS = 2;
        private const int  GET_ADVANCED_COLOR_INFO  = 9;
        private const int  SET_ADVANCED_COLOR_STATE = 10;

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

                foreach (var path in paths)
                {
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

                    result.Add(new HdrInfo
                    {
                        SourceX       = srcX,
                        SourceY       = srcY,
                        IsSupported   = hdrResult == 0 && (req.value & 1) != 0,
                        IsEnabled     = hdrResult == 0 && (req.value & 2) != 0,
                        AdapterIdLow  = path.targetInfo.adapterId.LowPart,
                        AdapterIdHigh = path.targetInfo.adapterId.HighPart,
                        TargetId      = path.targetInfo.id,
                        IsInternal    = isInternal
                    });
                }
            }
            catch { }
            return result;
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
