using System;
using System.Runtime.InteropServices;
using System.Text;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Chamadas nativas do otimizador de memória. Layouts conferidos contra phnt,
    /// System Informer e a documentação da Microsoft (tamanhos em x64 nos
    /// comentários). Nenhuma delas abre o processo do jogo: quem decide o que
    /// pode ser tocado é <see cref="MemoryOptimizerPolicy"/>.
    /// </summary>
    internal static class MemoryNative
    {
        // ── Leitura barata, sem privilégio ────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        internal struct MEMORYSTATUSEX                 // 64 bytes (x86 e x64)
        {
            public uint  dwLength;                     // PRECISA ser 64 antes da chamada
            public uint  dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;                 // = cache em espera + livre + zerada
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);

        // ── Listas de memória (SYSTEM_INFORMATION_CLASS 80), em PÁGINAS ──────

        [StructLayout(LayoutKind.Sequential)]
        internal struct SYSTEM_MEMORY_LIST_INFORMATION // 176 bytes em x64 (22 × SIZE_T)
        {
            public nuint ZeroPageCount;
            public nuint FreePageCount;
            public nuint ModifiedPageCount;
            public nuint ModifiedNoWritePageCount;
            public nuint BadPageCount;
            public nuint Standby0, Standby1, Standby2, Standby3,   // PageCountByPriority[8]
                         Standby4, Standby5, Standby6, Standby7;
            public nuint Repurposed0, Repurposed1, Repurposed2, Repurposed3,   // contadores cumulativos
                         Repurposed4, Repurposed5, Repurposed6, Repurposed7;
            public nuint ModifiedPageCountPageFile;
        }

        internal const int SystemMemoryListInformation       = 80;
        internal const int MemoryFlushModifiedList           = 3;
        internal const int MemoryPurgeStandbyList            = 4;
        internal const int MemoryPurgeLowPriorityStandbyList = 5;
        // Nunca usados, de propósito: 0/1 (bits de acesso) e 2 (MemoryEmptyWorkingSets),
        // que esvazia TODOS os processos — inclusive o jogo — sem exceção possível.

        internal const int STATUS_SUCCESS            = 0;
        internal const int STATUS_PRIVILEGE_NOT_HELD = unchecked((int)0xC0000061);
        internal const int STATUS_ACCESS_DENIED      = unchecked((int)0xC0000022);
        internal const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
        internal const int STATUS_INVALID_INFO_CLASS = unchecked((int)0xC0000003);
        internal const int ERROR_PRIVILEGE_NOT_HELD  = 1314;

        [DllImport("ntdll.dll")]   // devolve NTSTATUS direto: sem SetLastError
        internal static extern int NtQuerySystemInformation(int systemInformationClass,
            out SYSTEM_MEMORY_LIST_INFORMATION systemInformation, int systemInformationLength, out int returnLength);

        [DllImport("ntdll.dll")]
        internal static extern int NtSetSystemInformation(int systemInformationClass, ref int command, int length);

        internal static readonly int MemoryListInfoSize = Marshal.SizeOf<SYSTEM_MEMORY_LIST_INFORMATION>();

        // ── Cache de arquivos do sistema (documentado) ────────────────────────

        /// <summary>
        /// Esvaziar: (nuint.MaxValue, nuint.MaxValue, 0). Flags 0 mantém os
        /// limites atuais — NUNCA passar FILE_CACHE_MAX_HARD_ENABLE.
        /// Exige SeIncreaseQuotaPrivilege HABILITADO.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetSystemFileCacheSize(nuint minimumFileCacheSize, nuint maximumFileCacheSize, uint flags);

        // ── Por processo, com o mínimo de direitos ────────────────────────────

        internal const uint PROCESS_SET_QUOTA                 = 0x0100;
        internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(uint dwDesiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", EntryPoint = "K32EmptyWorkingSet", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EmptyWorkingSet(IntPtr hProcess);

        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags,
            StringBuilder lpExeName, ref uint lpdwSize);

        /// <summary>Caminho do executável por um handle já aberto, ou null.</summary>
        internal static string? ImagePath(IntPtr handle)
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString(0, (int)size) : null;
        }

        /// <summary>Caminho do executável abrindo só para LEITURA (nunca escreve no processo).</summary>
        internal static string? ImagePathReadOnly(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try { return ImagePath(h); }
            finally { CloseHandle(h); }
        }

        // ── Árvore de processos (Toolhelp) ────────────────────────────────────

        internal const uint TH32CS_SNAPPROCESS = 0x00000002;
        internal static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct PROCESSENTRY32W
        {
            public uint   dwSize;
            public uint   cntUsage;
            public uint   th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint   th32ModuleID;
            public uint   cntThreads;
            public uint   th32ParentProcessID;
            public int    pcPriClassBase;
            public uint   dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

        // ── Janelas ───────────────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT { public int Left, Top, Right, Bottom; }               // 16 bytes

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MONITORINFO                                                  // 40 bytes
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WINDOWPLACEMENT                                              // 44 bytes
        {
            public uint  length;
            public uint  flags;
            public uint  showCmd;
            public POINT ptMinPosition;
            public POINT ptMaxPosition;
            public RECT  rcNormalPosition;
        }

        internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        internal const uint MONITOR_DEFAULTTONEAREST = 2;
        internal const int  GWL_STYLE   = -16;
        internal const int  GWL_EXSTYLE = -20;
        internal const int  DWMWA_CLOAKED = 14;
        internal static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        /// <summary>GetWindowLongPtr só existe em 64 bits; em 32 bits é GetWindowLong.</summary>
        internal static long GetWindowLong(IntPtr hWnd, int index) =>
            IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, index).ToInt64() : GetWindowLong32(hWnd, index);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        [DllImport("user32.dll")]
        internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

        // QUNS_BUSY = 2 (tela cheia), QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4
        [DllImport("shell32.dll")]
        internal static extern int SHQueryUserNotificationState(out int state);

        // ── Privilégios ───────────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        internal struct LUID { public uint LowPart; public int HighPart; }          // 8 bytes

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        internal struct TOKEN_PRIVILEGES_1                                           // 16 bytes
        {
            public uint PrivilegeCount;
            public LUID Luid;
            public uint Attributes;
        }

        internal const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        internal const uint TOKEN_QUERY             = 0x0008;
        internal const uint SE_PRIVILEGE_ENABLED    = 0x00000002;
        internal const string SE_PROF_SINGLE_PROCESS_NAME = "SeProfileSingleProcessPrivilege";
        internal const string SE_INCREASE_QUOTA_NAME      = "SeIncreaseQuotaPrivilege";

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentProcess();   // pseudo-handle (-1): nunca fechar

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AdjustTokenPrivileges(IntPtr tokenHandle,
            [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
            ref TOKEN_PRIVILEGES_1 newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

        /// <summary>
        /// Habilita um privilégio que o token JÁ TEM (administrador elevado); não
        /// cria privilégio nenhum. Vale para o processo inteiro e é inofensivo.
        /// false = não elevado (Debug/asInvoker) ou falha.
        /// </summary>
        internal static bool TryEnablePrivilege(string name)
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token))
                return false;
            try
            {
                if (!LookupPrivilegeValue(null, name, out LUID luid)) return false;
                var tp = new TOKEN_PRIVILEGES_1 { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
                if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) return false;
                // Devolve TRUE mesmo sem atribuir nada; só o último erro distingue
                // ERROR_SUCCESS de ERROR_NOT_ALL_ASSIGNED (1300).
                return Marshal.GetLastPInvokeError() == 0;
            }
            finally { CloseHandle(token); }
        }

        /// <summary>
        /// Conferido uma vez. Se algum tamanho não bater, a leitura das listas é
        /// desligada em vez de chamar o kernel com um buffer errado.
        /// </summary>
        internal static bool LayoutsOk =>
            Marshal.SizeOf<MEMORYSTATUSEX>() == 64 &&
            MemoryListInfoSize == 22 * IntPtr.Size &&
            Marshal.SizeOf<TOKEN_PRIVILEGES_1>() == 16;
    }
}
