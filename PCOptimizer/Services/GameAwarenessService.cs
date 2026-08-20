using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Detecta quando há um jogo (ou vídeo) em tela cheia e avisa o resto do app
    /// para SAIR DA FRENTE: timers de segundo plano param e o processo cai de
    /// prioridade. Nada do que esses timers fazem é visível durante um jogo em
    /// tela cheia — manter isso rodando só disputaria CPU com o jogo.
    ///
    /// A detecção usa SHQueryUserNotificationState, a mesma API que o Windows usa
    /// para decidir se pode mostrar notificações: uma chamada barata, sem varrer
    /// processos nem janelas.
    /// </summary>
    public static class GameAwarenessService
    {
        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out int state);

        // QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4
        private const int QunsD3DFullScreen = 3;
        private const int QunsPresentation  = 4;

        /// <summary>Há um jogo/app em tela cheia neste momento?</summary>
        public static bool IsGameRunning { get; private set; }

        /// <summary>Disparado quando entra ou sai do estado de tela cheia.</summary>
        public static event Action<bool>? GameStateChanged;

        private static DispatcherTimer? _poll;
        private static ProcessPriorityClass _normalPriority = ProcessPriorityClass.Normal;
        private static bool _priorityLowered;

        /// <summary>
        /// Começa a observar. Intervalo de 4 s: entrar/sair de um jogo não é algo
        /// que precise de reação instantânea, e o custo por verificação é uma
        /// única chamada de API.
        /// </summary>
        public static void Start()
        {
            if (_poll != null) return;
            try { _normalPriority = Process.GetCurrentProcess().PriorityClass; }
            catch { }

            _poll = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(4)
            };
            _poll.Tick += (_, _) => Check();
            _poll.Start();
        }

        public static void Stop()
        {
            _poll?.Stop();
            _poll = null;
            RestorePriority();
        }

        private static void Check()
        {
            bool running;
            try
            {
                if (SHQueryUserNotificationState(out int state) != 0) return;
                running = state == QunsD3DFullScreen || state == QunsPresentation;
            }
            catch { return; }

            if (running == IsGameRunning) return;
            IsGameRunning = running;
            Logger.Info(running
                ? "Jogo em tela cheia detectado — pausando tarefas de segundo plano"
                : "Jogo encerrado — retomando tarefas de segundo plano");

            if (running) LowerPriority(); else RestorePriority();

            try { GameStateChanged?.Invoke(running); }
            catch (Exception ex) { Logger.Error(ex, "GameStateChanged"); }
        }

        private static void LowerPriority()
        {
            try
            {
                var p = Process.GetCurrentProcess();
                _normalPriority = p.PriorityClass;
                p.PriorityClass = ProcessPriorityClass.BelowNormal;
                _priorityLowered = true;
            }
            catch (Exception ex) { Logger.Error(ex, "LowerPriority"); }
        }

        private static void RestorePriority()
        {
            if (!_priorityLowered) return;
            try { Process.GetCurrentProcess().PriorityClass = _normalPriority; }
            catch (Exception ex) { Logger.Error(ex, "RestorePriority"); }
            _priorityLowered = false;
        }
    }
}
