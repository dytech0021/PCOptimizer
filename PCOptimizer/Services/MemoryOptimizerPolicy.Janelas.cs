using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PCOptimizer.Services
{
    // Parte das regras de memória: o que conta como jogo, janela na tela ou janela do sistema.
    public static partial class MemoryOptimizerPolicy
    {
        // ── Janelas ───────────────────────────────────────────────────────────

        public const long WS_POPUP         = 0x80000000;
        public const long WS_CAPTION       = 0x00C00000;
        public const long WS_THICKFRAME    = 0x00040000;
        public const long WS_EX_TRANSPARENT = 0x00000020;
        public const long WS_EX_TOOLWINDOW = 0x00000080;
        public const long WS_EX_NOACTIVATE = 0x08000000;

        public static bool IsShellClass(string? cls) =>
            cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

        /// <summary>
        /// Uma janela parece um jogo aberto? Jogos em tela cheia — exclusiva ou sem
        /// borda — usam uma janela sem barra de título do tamanho do monitor.
        /// Para janela minimizada, <see cref="WindowFacts.Rect"/> deve ser o
        /// tamanho RESTAURADO: um jogo em tela cheia minimizado por alt-tab
        /// continua aberto. Janela maximizada comum (com moldura) não conta.
        /// Sobreposições (transparentes, de ferramenta, sem ativação) e o shell
        /// ficam de fora.
        /// </summary>
        public static bool IsGameLikeWindow(WindowFacts w)
        {
            if (!w.Visible || w.Cloaked || w.OwnerIsSelf) return false;
            if (IsShellClass(w.ClassName)) return false;
            if (NonGameWindowOwners.Contains(StripExe(w.OwnerName))) return false;
            if (LauncherOwners.Contains(StripExe(w.OwnerName))) return false;
            if ((w.ExStyle & (WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) return false;

            // Sinais fortes, em qualquer tamanho ou estado (em janela, minimizado):
            // a classe da janela do motor do jogo, ou o executável de um jogo.
            if (GameWindowClasses.Contains(w.ClassName ?? "")) return true;
            if (IsKnownGameProcess(w.OwnerName)) return true;

            if ((w.Style & WS_CAPTION) == WS_CAPTION) return false;
            if (w.Zoomed && (w.Style & WS_THICKFRAME) != 0) return false;
            if (w.Monitor.Width <= 0 || w.Monitor.Height <= 0) return false;

            // Minimizado: o tamanho restaurado de um jogo em tela cheia exclusiva
            // é a resolução DELE, que pode ser menor que a do monitor (1280×960
            // esticado num monitor 1920×1080). Janela pop-up sem moldura de
            // redimensionar e de tamanho de jogo conta — apps sem borda comuns
            // (Discord, Spotify, Steam) são redimensionáveis e ficam de fora.
            if (w.Minimized)
                return (w.Style & WS_POPUP) != 0 && (w.Style & WS_THICKFRAME) == 0
                       && w.Rect.Width >= 640 && w.Rect.Height >= 480;

            // Tolerância de 2 px: arredondamento de DPI e bordas invisíveis
            return w.Rect.Width >= w.Monitor.Width - 2 && w.Rect.Height >= w.Monitor.Height - 2;
        }

        /// <summary>O executável é de um jogo conhecido (lista ou padrão do Unreal)?</summary>
        public static bool IsKnownGameProcess(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string n = StripExe(name);
            if (KnownGameProcesses.Contains(n)) return true;
            foreach (var suffix in GameProcessSuffixes)
                if (n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>O executável está instalado numa pasta de jogos (fora dos launchers)?</summary>
        public static bool IsGameInstallPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string p = path.Replace('/', '\\');
            foreach (var ex in GameInstallExceptions)
                if (p.Contains(ex, StringComparison.OrdinalIgnoreCase)) return false;
            foreach (var marker in GameInstallMarkers)
                if (p.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Um processo visto como jogo pode ser "lembrado" como jogo aberto até
        /// fechar? Navegador, player e acesso remoto em tela cheia, não — eles
        /// ficam abertos o dia todo e travariam a otimização.
        /// </summary>
        public static bool CanLatchAsGame(string? ownerName) =>
            !string.IsNullOrWhiteSpace(ownerName) && !NeverLatchOwners.Contains(StripExe(ownerName))
            && !NonGameWindowOwners.Contains(StripExe(ownerName)) && !LauncherOwners.Contains(StripExe(ownerName));

        /// <summary>
        /// Janela de programa visível na tela (não minimizada, não oculta, não
        /// sobreposição): o usuário está vendo, então o dono e os filhos não são
        /// esvaziados — inclusive um jogo em janela pequena que não se denunciou.
        /// </summary>
        public static bool IsVisibleUserWindow(WindowFacts w)
        {
            if (!w.Visible || w.Minimized || w.Cloaked || w.OwnerIsSelf) return false;
            if (IsShellClass(w.ClassName)) return false;
            if ((w.ExStyle & (WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW)) != 0) return false;
            return w.Rect.Width >= 120 && w.Rect.Height >= 80;   // ignora janelinhas utilitárias
        }

        /// <summary>
        /// Janela visível que ocupa pelo menos metade do monitor: o usuário está
        /// olhando para ela, então o dono (e os filhos) não é esvaziado.
        /// </summary>
        public static bool IsBigVisibleWindow(WindowFacts w)
        {
            if (!w.Visible || w.Cloaked || w.OwnerIsSelf) return false;
            if (IsShellClass(w.ClassName)) return false;
            if ((w.ExStyle & (WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW)) != 0) return false;
            long area = w.Rect.Width * w.Rect.Height;
            long mon  = w.Monitor.Width * w.Monitor.Height;
            return mon > 0 && area * 2 >= mon;
        }
    }
}
