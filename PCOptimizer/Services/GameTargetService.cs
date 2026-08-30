using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Identifica o processo do jogo — usado apenas para saber o que NÃO confinar
    /// e para perceber quando o jogo fechou. O app nunca escreve nesse processo.
    /// </summary>
    public static class GameTargetService
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr h, uint flags,
            System.Text.StringBuilder exeName, ref uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr h, uint ms);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ProcessIdToSessionId(int pid, out int sessionId);

        // Só leitura: nunca pedimos direito de escrita no processo do jogo.
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint PROCESS_SYNCHRONIZE               = 0x00100000;
        private const uint WAIT_OBJECT_0 = 0;

        /// <summary>Janelas do shell que nunca são jogo.</summary>
        private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "ApplicationFrameHost", "SearchHost", "ShellExperienceHost",
            "StartMenuExperienceHost", "TextInputHost", "dwm", "SystemSettings",
            "LockApp", "Taskmgr", "PCOptimizer"
        };

        public sealed class Target : IDisposable
        {
            public int    Pid { get; init; }
            public string Name { get; init; } = "";
            public string? ExePath { get; init; }
            /// <summary>
            /// Handle mantido aberto durante todo o turbo: além de permitir saber
            /// quando o processo morre, impede que o PID seja reciclado — o que
            /// faria a reversão mexer no processo errado.
            /// </summary>
            public IntPtr Handle { get; init; }
            public bool   Manual { get; set; }

            public bool IsAlive =>
                Handle != IntPtr.Zero && WaitForSingleObject(Handle, 0) != WAIT_OBJECT_0;

            public void Dispose()
            {
                if (Handle != IntPtr.Zero) CloseHandle(Handle);
            }
        }

        /// <summary>Captura o processo da janela em primeiro plano. null se não servir.</summary>
        public static Target? FromForegroundWindow()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return null;
                GetWindowThreadProcessId(hwnd, out int pid);
                return FromPid(pid);
            }
            catch (Exception ex) { Logger.Error(ex, "GameTarget.FromForegroundWindow"); return null; }
        }

        /// <summary>Localiza um jogo já aberto pelo nome do executável.</summary>
        public static Target? FromProcessName(string processName)
        {
            try
            {
                foreach (var process in Process.GetProcessesByName(processName))
                {
                    try
                    {
                        int pid = process.Id;
                        var target = FromPid(pid);
                        if (target != null) return target;
                    }
                    finally { process.Dispose(); }
                }
            }
            catch (Exception ex) { Logger.Error(ex, $"GameTarget.FromProcessName({processName})"); }
            return null;
        }

        public static Target? FromPid(int pid)
        {
            if (pid <= 4 || pid == Environment.ProcessId) return null;

            // Serviço da sessão 0 nunca é jogo
            if (ProcessIdToSessionId(pid, out int sess) && sess == 0) return null;

            string name;
            try { using var p = Process.GetProcessById(pid); name = p.ProcessName; }
            catch { return null; }

            if (string.IsNullOrEmpty(name) || NotGames.Contains(name)) return null;

            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SYNCHRONIZE,
                                   false, pid);
            if (h == IntPtr.Zero)
            {
                Logger.Warn($"Turbo: sem acesso ao processo {name} (PID {pid})");
                return null;
            }

            string? path = null;
            try
            {
                var sb = new System.Text.StringBuilder(1024);
                uint size = 1024;
                if (QueryFullProcessImageName(h, 0, sb, ref size)) path = sb.ToString();
            }
            catch { }

            return new Target { Pid = pid, Name = name, ExePath = path, Handle = h };
        }
    }
}
