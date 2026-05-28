using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PCOptimizer.Services
{
    public static class HotkeyService
    {
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int HOTKEY_ID = 0x3001;
        private const int WM_HOTKEY = 0x0312;

        private static HwndSource? _hwndSource;
        private static IntPtr _hwnd = IntPtr.Zero;

        public static event Action? HotkeyPressed;

        public static void Initialize(Window window)
        {
            _hwnd = new WindowInteropHelper(window).EnsureHandle();
            _hwndSource = HwndSource.FromHwnd(_hwnd);
            _hwndSource?.AddHook(WndProc);
            Register();
        }

        public static void Register()
        {
            if (_hwnd == IntPtr.Zero) return;
            UnregisterHotKey(_hwnd, HOTKEY_ID);
            var s = SettingsService.Current;
            RegisterHotKey(_hwnd, HOTKEY_ID, s.HotkeyModifiers, s.HotkeyVk);
        }

        public static void Dispose()
        {
            if (_hwnd != IntPtr.Zero)
                UnregisterHotKey(_hwnd, HOTKEY_ID);
            _hwndSource?.RemoveHook(WndProc);
        }

        private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                HotkeyPressed?.Invoke();
                handled = true;
            }
            return IntPtr.Zero;
        }
    }
}
