using System;
using System.Runtime.InteropServices;

namespace PCOptimizer.Services
{
    public static class NightLightService
    {
        [DllImport("gdi32.dll")]
        private static extern bool SetDeviceGammaRamp(IntPtr hDC, ref RAMP lpRamp);

        [DllImport("gdi32.dll")]
        private static extern bool GetDeviceGammaRamp(IntPtr hDC, ref RAMP lpRamp);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct RAMP
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public ushort[] Red;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public ushort[] Green;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public ushort[] Blue;
        }

        private static RAMP _originalRamp;
        private static bool _originalSaved;

        /// <summary>
        /// Aplica filtro de luz noturna usando gamma ramp.
        /// intensity: 0 = sem filtro, 100 = máximo (muito quente/laranja)
        /// </summary>
        public static void SetIntensity(int intensity)
        {
            IntPtr hDC = GetDC(IntPtr.Zero);
            try
            {
                if (!_originalSaved)
                {
                    _originalRamp = new RAMP
                    {
                        Red = new ushort[256],
                        Green = new ushort[256],
                        Blue = new ushort[256]
                    };
                    GetDeviceGammaRamp(hDC, ref _originalRamp);
                    _originalSaved = true;
                }

                var ramp = new RAMP
                {
                    Red = new ushort[256],
                    Green = new ushort[256],
                    Blue = new ushort[256]
                };

                // intensity 0-100 → escala de redução do azul e verde
                double factor = intensity / 100.0;
                double greenReduction = factor * 0.15;  // reduz verde levemente
                double blueReduction = factor * 0.45;   // reduz azul bastante

                for (int i = 0; i < 256; i++)
                {
                    int val = i * 256;
                    ramp.Red[i] = (ushort)val;
                    ramp.Green[i] = (ushort)Math.Max(0, val * (1.0 - greenReduction));
                    ramp.Blue[i] = (ushort)Math.Max(0, val * (1.0 - blueReduction));
                }

                SetDeviceGammaRamp(hDC, ref ramp);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, hDC);
            }
        }

        /// <summary>
        /// Restaura a gamma ramp original (desliga a luz noturna).
        /// </summary>
        public static void Reset()
        {
            if (!_originalSaved) return;

            IntPtr hDC = GetDC(IntPtr.Zero);
            try
            {
                // Restaura gamma normal (linear)
                var ramp = new RAMP
                {
                    Red = new ushort[256],
                    Green = new ushort[256],
                    Blue = new ushort[256]
                };

                for (int i = 0; i < 256; i++)
                {
                    int val = i * 256;
                    ramp.Red[i] = (ushort)val;
                    ramp.Green[i] = (ushort)val;
                    ramp.Blue[i] = (ushort)val;
                }

                SetDeviceGammaRamp(hDC, ref ramp);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, hDC);
            }
        }
    }
}
