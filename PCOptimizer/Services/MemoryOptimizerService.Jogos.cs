using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCOptimizer.Services
{
    // Parte do otimizador de memória: detecção de jogo aberto e jogos "lembrados" (só leitura).
    public static partial class MemoryOptimizerService
    {
        // ── Detecção de jogo (só leitura) ─────────────────────────────────────

        /// <summary>
        /// Checagem leve, para o tick do automático: estado já conhecido do Game
        /// Boost e do detector de tela cheia, o estado de notificações do Windows
        /// e a janela da frente.
        /// </summary>
        private static bool QuickGameCheck()
        {
            if (GameBoostService.IsActive || GameAwarenessService.IsGameRunning) return true;
            if (ShellSaysFullscreen())
            {
                // Lembra quem está em tela cheia, mesmo com o detector de jogo
                // desligado — o Latch já ignora navegador, player e acesso remoto.
                try
                {
                    IntPtr fgw = MemoryNative.GetForegroundWindow();
                    if (fgw != IntPtr.Zero)
                    {
                        MemoryNative.GetWindowThreadProcessId(fgw, out int fgPid);
                        Latch(fgPid, ProcessNameOf(fgPid));
                    }
                }
                catch { }
                return true;
            }
            if (LatchedGameAlive() != null) return true;

            IntPtr prev = TrySetPerMonitorDpi();
            try
            {
                IntPtr fg = MemoryNative.GetForegroundWindow();
                if (fg == IntPtr.Zero) return false;
                MemoryNative.GetWindowThreadProcessId(fg, out int pid);
                string name = ProcessNameOf(pid);
                if (ReadWindowFacts(fg, pid, name) is not WindowFacts f || !MemoryOptimizerPolicy.IsGameLikeWindow(f))
                    return false;
                Latch(pid, name);
                return true;
            }
            finally { RestoreDpi(prev); }
        }

        // ── Jogos "lembrados" ─────────────────────────────────────────────────

        /// <summary>
        /// Processos vistos como jogo continuam contando como "jogo aberto" até
        /// FECHAREM — mesmo depois de um alt-tab que minimiza o jogo e devolve a
        /// resolução da área de trabalho. Guarda um handle só de LEITURA
        /// (consulta + espera), os mesmos direitos que o Game Boost usa.
        /// </summary>
        private sealed record LatchedGame(int Pid, string Name, IntPtr Handle);
        private static readonly object LatchLock = new();
        private static readonly List<LatchedGame> Latched = new();
        private const int MaxLatched = 8;

        private static void Latch(int pid, string name)
        {
            if (pid <= 4 || pid == Environment.ProcessId || !MemoryOptimizerPolicy.CanLatchAsGame(name)) return;
            lock (LatchLock)
            {
                if (Latched.Exists(l => l.Pid == pid)) return;
                IntPtr h = MemoryNative.OpenProcess(
                    MemoryNative.PROCESS_QUERY_LIMITED_INFORMATION | MemoryNative.SYNCHRONIZE, false, pid);
                if (h == IntPtr.Zero) return;
                if (Latched.Count >= MaxLatched)
                {
                    MemoryNative.CloseHandle(Latched[0].Handle);
                    Latched.RemoveAt(0);
                }
                Latched.Add(new LatchedGame(pid, name, h));
                Logger.Info($"Memória: {name} visto como jogo — conta como aberto até fechar");
            }
        }

        /// <summary>Nome do primeiro jogo lembrado que ainda está rodando, ou null.</summary>
        private static string? LatchedGameAlive()
        {
            lock (LatchLock)
            {
                for (int i = Latched.Count - 1; i >= 0; i--)
                {
                    if (MemoryNative.WaitForSingleObject(Latched[i].Handle, 0) == MemoryNative.WAIT_TIMEOUT) continue;
                    MemoryNative.CloseHandle(Latched[i].Handle);   // fechou: esquece
                    Latched.RemoveAt(i);
                }
                return Latched.Count > 0 ? Latched[0].Name : null;
            }
        }

        private static List<int> LatchedPids()
        {
            LatchedGameAlive();
            lock (LatchLock) return Latched.ConvertAll(l => l.Pid);
        }

        /// <summary>
        /// Liga a "memória" de jogo ao detector de tela cheia: quando ele vê um
        /// jogo, o processo da frente é lembrado. Só ASSINA o evento — nada no
        /// detector muda — e nunca deixa exceção voltar para ele.
        /// </summary>
        public static void Initialize()
        {
            GameAwarenessService.GameStateChanged += running =>
            {
                if (!running) return;
                try
                {
                    IntPtr fg = MemoryNative.GetForegroundWindow();
                    if (fg == IntPtr.Zero) return;
                    MemoryNative.GetWindowThreadProcessId(fg, out int pid);
                    Latch(pid, ProcessNameOf(pid));
                }
                catch (Exception ex) { Logger.Error(ex, "MemoryOptimizer.GameStateChanged"); }
            };
        }

        /// <summary>Checagem baratíssima (uma chamada) para a interface pausar a leitura.</summary>
        public static bool IsFullscreenAppActive() =>
            GameBoostService.IsActive || GameAwarenessService.IsGameRunning || ShellSaysFullscreen();

        /// <summary>QUNS 2/3/4: um app em tela cheia (inclusive sem borda) está na frente.</summary>
        private static bool ShellSaysFullscreen()
        {
            try
            {
                return MemoryNative.SHQueryUserNotificationState(out int state) == 0
                       && state is 2 or 3 or 4;
            }
            catch { return false; }
        }

        /// <summary>
        /// Checagem completa, antes de QUALQUER limpeza: há jogo aberto, mesmo que
        /// minimizado ou em outro monitor? Devolve também o retrato das janelas,
        /// reaproveitado para proteger quem o usuário está usando.
        /// </summary>
        private static bool IsGameOpen(WindowScan scan, out string? blocker)
        {
            blocker = null;
            if (GameBoostService.IsActive) { blocker = GameBoostService.TargetName; return true; }
            if (GameAwarenessService.IsGameRunning) return true;
            if (LatchedGameAlive() is string latched) { blocker = latched; return true; }
            if (scan.GamePids.Count > 0) { blocker = scan.Blocker; return true; }
            if (ShellSaysFullscreen()) return true;
            return false;
        }

        private sealed class WindowScan
        {
            public readonly HashSet<int> GamePids = new();
            /// <summary>Donos de janela visível na tela (não minimizada).</summary>
            public readonly HashSet<int> VisiblePids = new();
            /// <summary>Dos visíveis, os que com certeza não são jogo (navegador, Office…).</summary>
            public readonly HashSet<int> NonGameVisiblePids = new();
            /// <summary>Donos de qualquer janela de programa, minimizada inclusive.</summary>
            public readonly HashSet<int> WindowOwners = new();
            /// <summary>Dono da janela mais ao topo: o app usado antes de abrir o PC Optimizer.</summary>
            public int TopZPid;
            public string? Blocker;
        }

        private static WindowScan ScanWindows(IReadOnlyDictionary<int, string> namesByPid)
        {
            var scan = new WindowScan();
            var windowOwners = new HashSet<int>();
            IntPtr prev = TrySetPerMonitorDpi();
            MemoryNative.EnumWindowsProc cb = (hwnd, _) =>
            {
                // Exceção não pode atravessar o EnumWindows nativo: engole e segue.
                try
                {
                    if (!MemoryNative.IsWindowVisible(hwnd)) return true;
                    MemoryNative.GetWindowThreadProcessId(hwnd, out int pid);
                    if (pid <= 4 || pid == Environment.ProcessId) return true;
                    namesByPid.TryGetValue(pid, out string? name);
                    if (ReadWindowFacts(hwnd, pid, name ?? "") is not WindowFacts f) return true;
                    if (MemoryOptimizerPolicy.IsGameLikeWindow(f))
                    {
                        if (scan.GamePids.Add(pid)) scan.Blocker ??= name;
                    }
                    else
                    {
                        if (MemoryOptimizerPolicy.IsVisibleUserWindow(f))
                        {
                            scan.VisiblePids.Add(pid);
                            if (scan.TopZPid == 0) scan.TopZPid = pid;   // EnumWindows vai do topo para baixo
                            if (MemoryOptimizerPolicy.IsKnownNonGameApp(name, f.ClassName))
                                scan.NonGameVisiblePids.Add(pid);
                        }
                        if (!f.Cloaked && !MemoryOptimizerPolicy.IsShellClass(f.ClassName)
                            && (f.ExStyle & (MemoryOptimizerPolicy.WS_EX_TOOLWINDOW | MemoryOptimizerPolicy.WS_EX_TRANSPARENT)) == 0)
                            windowOwners.Add(pid);
                    }
                }
                catch { }
                return true;
            };
            try { MemoryNative.EnumWindows(cb, IntPtr.Zero); }
            catch (Exception ex) { Logger.Error(ex, "MemoryOptimizer.ScanWindows"); }
            finally
            {
                GC.KeepAlive(cb);
                RestoreDpi(prev);
            }

            // Jogo em janela comum (com barra de título) não se denuncia pelo
            // formato: olha onde o executável está instalado. Só leitura, e só
            // para quem tem janela aberta.
            foreach (int pid in windowOwners)
            {
                if (scan.GamePids.Contains(pid)) continue;
                if (MemoryOptimizerPolicy.IsGameInstallPath(MemoryNative.ImagePathReadOnly(pid)))
                {
                    scan.GamePids.Add(pid);
                    namesByPid.TryGetValue(pid, out string? name);
                    scan.Blocker ??= name;
                }
            }

            foreach (int pid in scan.GamePids)
            {
                namesByPid.TryGetValue(pid, out string? name);
                if (name != null) Latch(pid, name);
            }
            scan.WindowOwners.UnionWith(windowOwners);
            scan.WindowOwners.UnionWith(scan.VisiblePids);
            scan.NonGameVisiblePids.ExceptWith(scan.GamePids);
            return scan;
        }

        /// <summary>Lê o que importa de uma janela, sem abrir o processo dono.</summary>
        private static WindowFacts? ReadWindowFacts(IntPtr hwnd, int pid, string name)
        {
            bool minimized = MemoryNative.IsIconic(hwnd);
            bool restoreMaximized = false;
            MemoryNative.RECT r;
            if (minimized)
            {
                // Minimizada: vale o tamanho RESTAURADO — um jogo em tela cheia
                // minimizado por alt-tab continua sendo um jogo aberto.
                var wp = new MemoryNative.WINDOWPLACEMENT
                {
                    length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryNative.WINDOWPLACEMENT>()
                };
                if (!MemoryNative.GetWindowPlacement(hwnd, ref wp)) return null;
                r = wp.rcNormalPosition;
                restoreMaximized = (wp.flags & MemoryNative.WPF_RESTORETOMAXIMIZED) != 0;
            }
            else if (!MemoryNative.GetWindowRect(hwnd, out r)) return null;

            IntPtr mon = MemoryNative.MonitorFromWindow(hwnd, MemoryNative.MONITOR_DEFAULTTONEAREST);
            var mi = new MemoryNative.MONITORINFO
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryNative.MONITORINFO>()
            };
            if (mon == IntPtr.Zero || !MemoryNative.GetMonitorInfo(mon, ref mi)) return null;

            // Minimizada que volta MAXIMIZADA: o tamanho restaurado guardado é o
            // de antes de maximizar; o que vale é o monitor inteiro.
            if (restoreMaximized) r = mi.rcMonitor;

            bool cloaked = false;
            try
            {
                cloaked = MemoryNative.DwmGetWindowAttribute(hwnd, MemoryNative.DWMWA_CLOAKED, out int c, sizeof(int)) == 0
                          && c != 0;
            }
            catch { }

            var cls = new StringBuilder(256);
            MemoryNative.GetClassName(hwnd, cls, cls.Capacity);

            return new WindowFacts(
                new Rect32(r.Left, r.Top, r.Right, r.Bottom),
                new Rect32(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom),
                Visible: true,
                Minimized: minimized,
                Cloaked: cloaked,
                Zoomed: MemoryNative.IsZoomed(hwnd),
                Style: MemoryNative.GetWindowLong(hwnd, MemoryNative.GWL_STYLE),
                ExStyle: MemoryNative.GetWindowLong(hwnd, MemoryNative.GWL_EXSTYLE),
                ClassName: cls.ToString(),
                OwnerName: name,
                OwnerIsSelf: pid == Environment.ProcessId);
        }

        /// <summary>
        /// O app não declara DPI por monitor no manifesto; para comparar a janela
        /// com o monitor em pixels reais, a thread muda de contexto só durante a
        /// leitura.
        /// </summary>
        private static IntPtr TrySetPerMonitorDpi()
        {
            try { return MemoryNative.SetThreadDpiAwarenessContext(MemoryNative.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
            catch { return IntPtr.Zero; }
        }

        private static void RestoreDpi(IntPtr previous)
        {
            if (previous == IntPtr.Zero) return;
            try { MemoryNative.SetThreadDpiAwarenessContext(previous); } catch { }
        }

        /// <summary>Nome do executável, abrindo o processo só para LEITURA.</summary>
        private static string ProcessNameOf(int pid)
        {
            try
            {
                string? path = MemoryNative.ImagePathReadOnly(pid);
                return path == null ? "" : MemoryOptimizerPolicy.FileNameWithoutExtension(path);
            }
            catch { return ""; }
        }
    }
}
