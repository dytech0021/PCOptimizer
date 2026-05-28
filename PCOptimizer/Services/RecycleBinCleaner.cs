using System;
using System.Runtime.InteropServices;

namespace PCOptimizer.Services
{
    public static class RecycleBinCleaner
    {
        [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);

        private const uint SHERB_NOCONFIRMATION = 0x00000001;
        private const uint SHERB_NOPROGRESSUI = 0x00000002;
        private const uint SHERB_NOSOUND = 0x00000004;

        public static bool Clean()
        {
            try
            {
                uint result = SHEmptyRecycleBin(IntPtr.Zero, "",
                    SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
                return result == 0 || result == 0x80070012;
            }
            catch
            {
                return false;
            }
        }
    }
}
