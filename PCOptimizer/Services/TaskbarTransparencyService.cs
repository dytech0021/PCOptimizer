using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PCOptimizer.Services
{
    /// <summary>Modo de aparência da barra de tarefas.</summary>
    /// <remarks>
    /// Transparent/Blur/Acrylic usam ACCENT_POLICY — funciona no Windows 10, mas o
    /// Windows 11 IGNORA (o TranslucentTB só consegue no Win11 injetando uma DLL no
    /// Explorer via InitializeXamlDiagnosticsEx — fora do alcance de um app C#).
    /// WholeBar torna a JANELA INTEIRA da barra translúcida (ícones incluídos) via
    /// WS_EX_LAYERED — visual diferente, mas aceito nativamente pelo Win11.
    /// </remarks>
    public enum TaskbarMode { Off, Transparent, Blur, Acrylic, WholeBar }

    /// <summary>
    /// Deixa a barra de tarefas do Windows translúcida — inspirado no TranslucentTB.
    /// Usa a API não documentada SetWindowCompositionAttribute com ACCENT_POLICY
    /// (a mesma técnica do TranslucentTB / TaskbarTools).
    ///
    /// O efeito NÃO é persistente: o Windows repinta a barra em vários eventos
    /// (maximizar janela, reiniciar o explorer, mudar de DPI…). Por isso um timer
    /// reaplica o efeito periodicamente enquanto o app estiver aberto — exatamente
    /// como o TranslucentTB, que precisa ficar rodando em segundo plano.
    /// Suporta múltiplos monitores (barra primária + secundárias).
    /// </summary>
    public static class TaskbarTransparencyService
    {
        // ───────────────────────── Win32 ─────────────────────────
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SendNotifyMessage(IntPtr hWnd, uint msg, UIntPtr wParam, string? lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

        private static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
        private const uint WM_SETTINGCHANGE = 0x001A;

        private const int  GWL_EXSTYLE   = -20;
        private const int  WS_EX_LAYERED = 0x80000;
        private const uint LWA_ALPHA     = 0x2;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public int GradientColor; // formato ABGR: 0xAABBGGRR
            public int AnimationId;
        }

        private const int WCA_ACCENT_POLICY = 19;

        private const int ACCENT_DISABLED = 0;
        private const int ACCENT_ENABLE_TRANSPARENTGRADIENT = 2;
        private const int ACCENT_ENABLE_BLURBEHIND = 3;
        private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

        // Reaplica a cada 500 ms: rápido o bastante para parecer instantâneo após
        // um repintar do Windows, leve o bastante para não pesar (custo: um
        // FindWindow + SetWindowCompositionAttribute por barra).
        private const int RefreshIntervalMs = 500;

        private static DispatcherTimer? _timer;

        public static bool IsActive { get; private set; }
        public static TaskbarMode CurrentMode { get; private set; } = TaskbarMode.Off;
        public static int TintAlpha { get; private set; } // 0–255 (escurecimento, tom preto)

        /// <summary>
        /// Liga (ou atualiza) o efeito e passa a mantê-lo. mode Off equivale a Stop().
        /// tintAlpha 0–255 controla o escurecimento (tom preto por cima).
        /// </summary>
        public static void Start(TaskbarMode mode, int tintAlpha)
        {
            // Ao SAIR do modo WholeBar, remove a camada da barra antes de trocar.
            if (CurrentMode == TaskbarMode.WholeBar && mode != TaskbarMode.WholeBar)
                ClearWholeBar();

            CurrentMode = mode;

            int a = Math.Clamp(tintAlpha, 0, 255);
            // Acrílico com alpha 0 deixa a barra "click-through" (bug conhecido) —
            // aplica o piso no valor CANÔNICO (não só no render) para que UI,
            // persistência e efeito real fiquem sempre sincronizados.
            if (mode == TaskbarMode.Acrylic) a = Math.Max(a, 0x10);
            // WholeBar: TintAlpha é a INTENSIDADE da transparência (0 = opaco);
            // piso de 40 para ativar já ter efeito visível, teto de 200 para a
            // barra nunca sumir de vez.
            if (mode == TaskbarMode.WholeBar) a = Math.Clamp(a, 40, 200);
            TintAlpha = a;

            if (mode == TaskbarMode.Off) { Stop(); return; }

            // O blur/acrílico só renderiza com "Efeitos de transparência" ligado —
            // garante isso ao ativar (principal motivo de "não mudou nada" no Win11/Win10).
            EnsureSystemTransparency();

            IsActive = true;
            ApplyToAllTaskbars();

            EnsureTimer();
            _timer!.Start();
        }

        /// <summary>
        /// Liga "Efeitos de transparência" do Windows (Personalização → Cores). Sem isso,
        /// o accent de blur/acrílico não tem efeito visível na barra.
        /// </summary>
        private static void EnsureSystemTransparency()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key != null && Convert.ToInt32(key.GetValue("EnableTransparency") ?? 0) != 1)
                {
                    key.SetValue("EnableTransparency", 1, RegistryValueKind.DWord);
                    SendNotifyMessage(HWND_BROADCAST, WM_SETTINGCHANGE, UIntPtr.Zero, "ImmersiveColorSet");
                }
            }
            catch (Exception ex) { Logger.Error(ex, "TaskbarTransparency.EnsureSystemTransparency"); }
        }

        /// <summary>Atualiza os parâmetros ao vivo (mesmo comportamento de Start).</summary>
        public static void Update(TaskbarMode mode, int tintAlpha) => Start(mode, tintAlpha);

        /// <summary>Desliga e restaura a aparência padrão da barra.</summary>
        public static void Stop()
        {
            _timer?.Stop();
            IsActive    = false;
            CurrentMode = TaskbarMode.Off;
            SetAccentOnAll(ACCENT_DISABLED, 0, 0);
            ClearWholeBar();
        }

        /// <summary>Reaplica a partir das configurações salvas (chamar no startup).</summary>
        public static void RestoreFromSettings()
        {
            try
            {
                var s = SettingsService.Current;
                if (!s.TaskbarTransparencyEnabled) return;
                var mode = ParseMode(s.TaskbarMode);
                if (mode == TaskbarMode.Off) return;
                Start(mode, s.TaskbarTintAlpha);
            }
            catch (Exception ex) { Logger.Error(ex, "TaskbarTransparency.RestoreFromSettings"); }
        }

        public static TaskbarMode ParseMode(string? mode) => mode switch
        {
            "Transparent" => TaskbarMode.Transparent,
            "Blur"        => TaskbarMode.Blur,
            "Acrylic"     => TaskbarMode.Acrylic,
            "WholeBar"    => TaskbarMode.WholeBar,
            _             => TaskbarMode.Off
        };

        public static string StatusText() => CurrentMode switch
        {
            TaskbarMode.Transparent => "Transparente",
            TaskbarMode.Blur        => "Desfocada (blur)",
            TaskbarMode.Acrylic     => "Acrílico",
            TaskbarMode.WholeBar    => "Translúcida (barra inteira)",
            _                       => "Desativada"
        };

        private static void EnsureTimer()
        {
            if (_timer != null) return;
            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(RefreshIntervalMs)
            };
            _timer.Tick += (_, _) => { if (IsActive && !_paused) ApplyToAllTaskbars(); };
        }

        // Durante um jogo em tela cheia a barra está coberta: reaplicar o efeito
        // não muda nada na tela e só disputa CPU com o jogo.
        private static bool _paused;

        public static void Pause()
        {
            _paused = true;
            _timer?.Stop();
        }

        public static void Resume()
        {
            _paused = false;
            if (IsActive)
            {
                ApplyToAllTaskbars();   // recupera o que o Windows desfez enquanto isso
                _timer?.Start();
            }
        }

        // Variações de renderização: a MESMA chamada é interpretada diferente
        // conforme a build do Windows 11. Em especial, alpha 0 é tratado como
        // PRETO OPACO em várias builds (barra preta em vez de transparente) —
        // a variação 2 usa piso de alpha 1 (invisível a olho) para contornar.
        // A variação 3 remove os AccentFlags, para builds que os rejeitam.
        public static readonly (int Flags, int MinAlpha, string Label)[] Variations =
        {
            (2, 0, "1 — clássica (TranslucentTB)"),
            (2, 1, "2 — anti-barra-preta (alpha mínimo 1)"),
            (0, 1, "3 — sem flags (builds que rejeitam flags)"),
        };

        private static (int Flags, int MinAlpha) CurrentVariation()
        {
            int i = Math.Clamp(SettingsService.Current.TaskbarVariation, 0, Variations.Length - 1);
            return (Variations[i].Flags, Variations[i].MinAlpha);
        }

        private static void ApplyToAllTaskbars()
        {
            if (CurrentMode == TaskbarMode.WholeBar)
            {
                ApplyWholeBar();
                return;
            }

            int state = CurrentMode switch
            {
                TaskbarMode.Transparent => ACCENT_ENABLE_TRANSPARENTGRADIENT,
                TaskbarMode.Blur        => ACCENT_ENABLE_BLURBEHIND,
                TaskbarMode.Acrylic     => ACCENT_ENABLE_ACRYLICBLURBEHIND,
                _                       => ACCENT_DISABLED
            };

            // TintAlpha já é o valor canônico (o piso do acrílico é aplicado em Start).
            // Tom preto: ABGR = alpha<<24 (R=G=B=0), com o piso da variação ativa.
            var (flags, minAlpha) = CurrentVariation();
            int alpha = Math.Max(TintAlpha, minAlpha);
            SetAccentOnAll(state, flags, alpha << 24);
        }

        /// <summary>
        /// Modo "barra inteira": aplica WS_EX_LAYERED + alpha na própria janela da
        /// barra (ícones inclusos). É o único efeito de transparência que o Windows
        /// 11 aceita SEM injetar código no Explorer. TintAlpha = intensidade
        /// (40–200); opacidade resultante = 255 - TintAlpha.
        /// </summary>
        private static void ApplyWholeBar()
        {
            try
            {
                byte opacity = (byte)Math.Clamp(255 - TintAlpha, 55, 255);
                foreach (var hwnd in GetTaskbarWindows(includeChildren: false))
                {
                    int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                    if ((ex & WS_EX_LAYERED) == 0)
                        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED);
                    SetLayeredWindowAttributes(hwnd, 0, opacity, LWA_ALPHA);
                }
            }
            catch (Exception ex) { Logger.Error(ex, "TaskbarTransparency.ApplyWholeBar"); }
        }

        /// <summary>Remove a camada: opacidade 255 e tira o WS_EX_LAYERED da barra.</summary>
        private static void ClearWholeBar()
        {
            try
            {
                foreach (var hwnd in GetTaskbarWindows(includeChildren: false))
                {
                    int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                    if ((ex & WS_EX_LAYERED) != 0)
                    {
                        SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
                        SetWindowLong(hwnd, GWL_EXSTYLE, ex & ~WS_EX_LAYERED);
                    }
                }
            }
            catch (Exception ex) { Logger.Error(ex, "TaskbarTransparency.ClearWholeBar"); }
        }

        private static void SetAccentOnAll(int accentState, int accentFlags, int gradientColor)
        {
            try
            {
                foreach (var hwnd in GetTaskbarWindows())
                    SetAccent(hwnd, accentState, accentFlags, gradientColor);
            }
            catch (Exception ex) { Logger.Error(ex, "TaskbarTransparency.SetAccentOnAll"); }
        }

        private static void SetAccent(IntPtr hwnd, int accentState, int accentFlags, int gradientColor)
        {
            if (hwnd == IntPtr.Zero) return;

            var accent = new AccentPolicy
            {
                AccentState   = accentState,
                AccentFlags   = accentFlags,
                GradientColor = gradientColor,
                AnimationId   = 0
            };

            int size = Marshal.SizeOf(accent);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute  = WCA_ACCENT_POLICY,
                    Data       = ptr,
                    SizeOfData = size
                };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        // Handles das barras, guardados entre os ticks. Sem isto, o timer varria
        // TODAS as janelas de topo do sistema (EnumWindows) duas vezes por segundo
        // — a operação mais cara do app em segundo plano. Agora só re-varre quando
        // algum handle deixa de ser válido (explorer reiniciado, monitor trocado).
        private static List<IntPtr>? _barsCache;

        private static bool CacheStillValid()
        {
            if (_barsCache == null || _barsCache.Count == 0) return false;
            foreach (var h in _barsCache) if (!IsWindow(h)) return false;
            return true;
        }

        /// <summary>Descarta os handles guardados (troca de monitores, explorer reiniciado).</summary>
        public static void InvalidateBarsCache() => _barsCache = null;

        private static List<IntPtr> GetTaskbarWindows(bool includeChildren = true)
        {
            List<IntPtr> bars;
            if (CacheStillValid())
            {
                bars = _barsCache!;
                if (!includeChildren) return bars;
            }
            else
            {
                bars = new List<IntPtr>();
                IntPtr primary = FindWindow("Shell_TrayWnd", null);
                if (primary != IntPtr.Zero) bars.Add(primary);
                return FinishEnumeration(bars, includeChildren);
            }

            // Cache válido: só resolve as filhas (FindWindowEx é barato)
            return AddChildren(bars);
        }

        private static List<IntPtr> FinishEnumeration(List<IntPtr> bars, bool includeChildren)
        {

            // Barras secundárias (uma por monitor extra). Esta varredura é a parte
            // cara — o resultado fica guardado e só se repete quando invalida.
            EnumWindows((hwnd, _) =>
            {
                var sb = new StringBuilder(64);
                if (GetClassName(hwnd, sb, sb.Capacity) > 0 &&
                    sb.ToString() == "Shell_SecondaryTrayWnd")
                    bars.Add(hwnd);
                return true;
            }, IntPtr.Zero);

            _barsCache = bars;

            // Modo WholeBar usa só as janelas top-level (a camada vale p/ a árvore toda).
            if (!includeChildren) return bars;
            return AddChildren(bars);
        }

        /// <summary>
        /// No Windows 11 o fundo da barra é desenhado por uma janela-filha
        /// (DesktopWindowContentBridge). Aplicar o accent também nela aumenta a
        /// chance de o efeito pegar. Não atrapalha no Win10 (a filha não existe).
        /// </summary>
        private static List<IntPtr> AddChildren(List<IntPtr> bars)
        {
            var targets = new List<IntPtr>(bars);
            foreach (var bar in bars)
            {
                IntPtr child = FindWindowEx(bar, IntPtr.Zero,
                    "Windows.UI.Composition.DesktopWindowContentBridge", null);
                if (child != IntPtr.Zero) targets.Add(child);
            }
            return targets;
        }

        /// <summary>Windows 11 = build 22000 ou superior.</summary>
        public static bool IsWindows11 =>
            Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 22000;

        /// <summary>Texto de diagnóstico: o que o app conseguiu fazer com a barra.</summary>
        public static string Diagnose()
        {
            var sb = new StringBuilder();
            var v = Environment.OSVersion.Version;
            sb.AppendLine($"Windows: {v.Major}.{v.Minor} build {v.Build} ({(IsWindows11 ? "Windows 11" : "Windows 10")})");

            int transp = -1;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                transp = Convert.ToInt32(key?.GetValue("EnableTransparency") ?? -1);
            }
            catch { }
            sb.AppendLine(transp == 1
                ? "✅ Efeitos de transparência: ligado"
                : "❌ Efeitos de transparência: desligado (necessário p/ blur/acrílico)");

            var bars = GetTaskbarWindows();
            sb.AppendLine($"Barras/camadas encontradas: {bars.Count}");
            sb.AppendLine($"Efeito atual: {StatusText()} (ativo: {(IsActive ? "sim" : "não")})");

            int vi = Math.Clamp(SettingsService.Current.TaskbarVariation, 0, Variations.Length - 1);
            var (flags, minAlpha) = CurrentVariation();
            sb.AppendLine($"Variação: {Variations[vi].Label}");
            sb.AppendLine($"Política enviada: flags={flags}, cor=0x{Math.Max(TintAlpha, minAlpha) << 24:X8}");

            if (IsWindows11)
                sb.AppendLine("\n⚠ Windows 11: a barra nova IGNORA o efeito clássico de fundo " +
                              "(Transparente/Blur/Acrílico) — o TranslucentTB só consegue " +
                              "injetando código no Explorer. Alternativas: o modo " +
                              "\"Translúcido total\" (nativo, funciona no Win11) ou o " +
                              "TranslucentTB oficial (botão abaixo).");
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Abre a página de instalação do TranslucentTB oficial (Microsoft Store; se
        /// falhar, a página do GitHub). É a solução confiável para o Windows 11.
        /// </summary>
        public static bool OpenTranslucentTB()
        {
            // Product ID do TranslucentTB na Microsoft Store.
            const string storeUri = "ms-windows-store://pdp/?productid=9PF4KZ2VN4W9";
            const string githubUri = "https://github.com/TranslucentTB/TranslucentTB/releases/latest";
            try
            {
                Process.Start(new ProcessStartInfo(storeUri) { UseShellExecute = true });
                return true;
            }
            catch
            {
                try
                {
                    Process.Start(new ProcessStartInfo(githubUri) { UseShellExecute = true });
                    return true;
                }
                catch (Exception ex) { Logger.Error(ex, "OpenTranslucentTB"); return false; }
            }
        }
    }
}
