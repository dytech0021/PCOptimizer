using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using PCOptimizer.Services;
using PCOptimizer.Views;

namespace PCOptimizer
{
    // Parte da janela principal: card de Memória RAM.
    public partial class MainWindow
    {
        // ── Memória RAM ───────────────────────────────────────────────────────

        /// <summary>Os controles estão sendo preenchidos; ignora os eventos deles.</summary>
        private bool _memLoading;
        private System.Windows.Threading.DispatcherTimer? _memPoll;
        private MemoryGaugeLevel? _memLevel;

        private (System.Windows.Controls.CheckBox Box, MemoryOperation Op)[] MemoryOpBoxes() => new[]
        {
            (ChkMemTrimPrograms, MemoryOperation.TrimPrograms),
            (ChkMemFileCache,    MemoryOperation.SystemFileCache),
            (ChkMemStandbyLow,   MemoryOperation.StandbyLowPriority),
            (ChkMemStandbyFull,  MemoryOperation.StandbyFull),
            (ChkMemModified,     MemoryOperation.ModifiedList),
        };

        /// <summary>Joga as configurações salvas nos controles do Expander.</summary>
        private void LoadMemoryControls()
        {
            _memLoading = true;
            try
            {
                var s = SettingsService.Current;
                foreach (var (box, op) in MemoryOpBoxes())
                    box.IsChecked = (s.MemoryOps & op) != 0;

                ChkMemAuto.IsChecked       = s.MemoryAutoOptimize;
                ChkMemAutoIdle.IsChecked   = s.MemoryAutoOnlyWhenIdle;
                ChkMemAutoNotify.IsChecked = s.MemoryAutoNotify;

                SldMemThreshold.IsSnapToTickEnabled = true;
                SldMemThreshold.TickFrequency = MemoryOptimizerPolicy.ThresholdStep;
                SldMemThreshold.Value = MemoryOptimizerPolicy.ClampThreshold(s.MemoryAutoThresholdPercent);
                TxtMemThreshold.Text = $"{(int)SldMemThreshold.Value}%";

                bool auto = s.MemoryAutoOptimize;
                ChkMemAutoIdle.IsEnabled = ChkMemAutoNotify.IsEnabled = SldMemThreshold.IsEnabled = auto;

                // Habilita os privilégios de administrador já aqui: sem isso a tela
                // diria "requer administrador" mesmo rodando como administrador.
                TxtMemAdmin.Visibility = MemoryOptimizerService.CacheOpsAvailable
                    ? Visibility.Collapsed : Visibility.Visible;
            }
            catch (Exception ex) { Logger.Error(ex, "LoadMemoryControls"); }
            finally { _memLoading = false; }
        }

        /// <summary>
        /// Liga os controles às configurações e o leitor de memória ao estado da
        /// janela. Feito em código, no mesmo padrão do Controle de CPU. As
        /// assinaturas nos eventos do Game Boost e do detector de jogo só LEEM o
        /// estado — nada neles muda.
        /// </summary>
        private void WireMemoryEvents()
        {
            foreach (var (box, _) in MemoryOpBoxes())
            {
                box.Checked   += (_, _) => SaveMemorySettings();
                box.Unchecked += (_, _) => SaveMemorySettings();
            }
            foreach (var box in new[] { ChkMemAutoIdle, ChkMemAutoNotify })
            {
                box.Checked   += (_, _) => SaveMemorySettings();
                box.Unchecked += (_, _) => SaveMemorySettings();
            }

            ChkMemAuto.Checked   += (_, _) => OnMemoryAutoToggled();
            ChkMemAuto.Unchecked += (_, _) => OnMemoryAutoToggled();

            SldMemThreshold.ValueChanged += (_, _) =>
            {
                TxtMemThreshold.Text = $"{(int)SldMemThreshold.Value}%";
                SaveMemorySettings();
            };

            // O SelectionChanged das ComboBoxes da mesma aba sobe até aqui: só
            // interessa a troca de aba do próprio menu.
            OptList.SelectionChanged += (_, e) =>
            {
                if (ReferenceEquals(e.OriginalSource, OptList)) UpdateMemPollState();
            };
            StateChanged      += (_, _) => UpdateMemPollState();
            IsVisibleChanged  += (_, _) => UpdateMemPollState();

            MemoryOptimizerService.StatusChanged += () =>
                Dispatcher.BeginInvoke(new Action(RefreshMemoryCard));
            GameBoostService.StatusChanged += () =>
                Dispatcher.BeginInvoke(new Action(() => { UpdateMemPollState(); RefreshMemoryCard(); }));
            GameAwarenessService.GameStateChanged += _ =>
                Dispatcher.BeginInvoke(new Action(() => { UpdateMemPollState(); RefreshMemoryCard(); }));
        }

        private void SaveMemorySettings()
        {
            if (_memLoading) return;
            try
            {
                var s = SettingsService.Current;
                var ops = MemoryOperation.None;
                foreach (var (box, op) in MemoryOpBoxes())
                    if (box.IsChecked == true) ops |= op;

                s.MemoryOps = ops;
                s.MemoryAutoOptimize = ChkMemAuto.IsChecked == true;
                s.MemoryAutoOnlyWhenIdle = ChkMemAutoIdle.IsChecked == true;
                s.MemoryAutoNotify = ChkMemAutoNotify.IsChecked == true;
                s.MemoryAutoThresholdPercent = MemoryOptimizerPolicy.ClampThreshold((int)SldMemThreshold.Value);
                SettingsService.Save();
            }
            catch (Exception ex) { Logger.Error(ex, "SaveMemorySettings"); }
            RefreshMemoryCard();
        }

        private void OnMemoryAutoToggled()
        {
            if (_memLoading) return;
            bool auto = ChkMemAuto.IsChecked == true;
            ChkMemAutoIdle.IsEnabled = ChkMemAutoNotify.IsEnabled = SldMemThreshold.IsEnabled = auto;
            SaveMemorySettings();
            MemoryOptimizerService.ApplyAutoSetting();
            Log(auto
                ? $"🧠 Memória: automático ligado — acima de {(int)SldMemThreshold.Value}% de uso"
                : "🧠 Memória: automático desligado");
        }

        /// <summary>
        /// O leitor de 2 s só roda com a janela visível, na aba Desempenho e sem
        /// jogo — fora disso, nenhuma leitura acontece.
        /// </summary>
        private void UpdateMemPollState()
        {
            bool want = IsVisible
                        && WindowState != WindowState.Minimized
                        && ReferenceEquals(OptList.SelectedItem, TabDesempenho)
                        && !GameAwarenessService.IsGameRunning
                        && !GameBoostService.IsActive;

            if (want)
            {
                if (_memPoll == null)
                {
                    _memPoll = new System.Windows.Threading.DispatcherTimer(
                        System.Windows.Threading.DispatcherPriority.Background)
                    {
                        Interval = TimeSpan.FromSeconds(2)
                    };
                    _memPoll.Tick += (_, _) =>
                    {
                        // Jogo sem borda não aciona o detector de tela cheia; a
                        // checagem do shell é uma chamada só e evita disputar com ele.
                        if (MemoryOptimizerService.IsFullscreenAppActive()) return;
                        RefreshMemoryReadout();
                    };
                }
                if (!_memPoll.IsEnabled)
                {
                    RefreshMemoryReadout();
                    _memPoll.Start();
                }
            }
            else
            {
                _memPoll?.Stop();
                // Aba aberta mas com jogo: uma leitura só (duas chamadas baratas),
                // marcada como pausada — em vez de ficar em "Lendo memória…".
                if (IsVisible && WindowState != WindowState.Minimized
                    && ReferenceEquals(OptList.SelectedItem, TabDesempenho))
                {
                    RefreshMemoryReadout();
                    TxtMemDetail.Text += " · leitura pausada durante o jogo";
                }
            }
        }

        private void RefreshMemoryReadout()
        {
            try
            {
                var snap = MemoryOptimizerService.ReadSnapshot();
                var (usage, detail) = MemoryOptimizerPolicy.DescribeSnapshot(snap, MemoryOptimizerService.ListsNeedAdmin);
                TxtMemUsage.Text = usage;
                TxtMemDetail.Text = detail;
                BarMemUsage.Value = snap.IsValid ? snap.InUsePercent : 0;

                var level = MemoryOptimizerPolicy.Level(snap.InUsePercent);
                if (level != _memLevel)
                {
                    _memLevel = level;
                    BarMemUsage.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, level switch
                    {
                        MemoryGaugeLevel.Danger  => "DangerBrush",
                        MemoryGaugeLevel.Warning => "WarningBrush",
                        _                        => "AccentBrush",
                    });
                }
            }
            catch (Exception ex) { Logger.Error(ex, "RefreshMemoryReadout"); }
        }

        private void RefreshMemoryCard()
        {
            try
            {
                bool game = GameAwarenessService.IsGameRunning || GameBoostService.IsActive;
                bool anyOp = MemoryOptimizerPolicy.EffectiveOps(SettingsService.Current.MemoryOps,
                                 MemoryOptimizerService.CurrentLevel) != MemoryOperation.None;

                var level = MemoryOptimizerService.CurrentLevel;
                TxtMemLevelSummary.Text = MemoryOptimizerPolicy.DescribeLevel(level);
                foreach (var (btn, l) in new[] { (BtnMemLevelLeve, MemoryLevel.Leve),
                             (BtnMemLevelAgressivo, MemoryLevel.Agressivo), (BtnMemLevelMaximo, MemoryLevel.Maximo) })
                    btn.Style = (Style)FindResource(level == l ? "PrimaryButtonStyle" : "ActionButtonStyle");

                BtnMemOptimize.IsEnabled = !_isRunning && !MemoryOptimizerService.IsRunning && !game && anyOp;
                BtnMemOptimize.Content = MemoryOptimizerService.IsRunning ? "Otimizando…" : "Otimizar agora";

                string result = MemoryOptimizerService.IsRunning ? "Otimizando…"
                    : game ? MemoryOptimizerPolicy.GamePausedText.Replace(" — nada foi alterado", "")
                    : !anyOp ? "Nenhuma área marcada em Ajustes de memória"
                    : MemoryOptimizerService.LastResultText ?? "";
                TxtMemResult.Text = result;

                string auto = MemoryOptimizerService.AutoStatusText();
                TxtMemStatus.Text = auto;
                TxtMemStatus.Visibility = auto.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex) { Logger.Error(ex, "RefreshMemoryCard"); }
        }

        private void BtnMemLevel_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn) return;
            if (!Enum.TryParse(btn.Tag as string, out MemoryLevel level)) return;
            try
            {
                SettingsService.Current.MemoryLevel = level;
                SettingsService.Save();
                // Prioridade de memória baixada só vale nos níveis que a usam.
                if (level == MemoryLevel.Leve) _ = Task.Run(() => MemoryOptimizerService.RestoreMemoryPriorities());
                Log($"🧠 Memória: intensidade {MemoryOptimizerPolicy.LevelName(level)}");
            }
            catch (Exception ex) { Logger.Error(ex, "BtnMemLevel_Click"); }
            RefreshMemoryCard();
        }

        private async void BtnMemOptimize_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning) return;
            BtnMemOptimize.IsEnabled = false;
            TxtMemResult.Text = "Otimizando…";
            try
            {
                var r = await MemoryOptimizerService.OptimizeAsync(
                    SettingsService.Current.MemoryOps, MemoryTrigger.Manual);
                TxtMemResult.Text = r.Text;
                Log("🧠 Memória: " + r.Text);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "BtnMemOptimize_Click");
                TxtMemResult.Text = "Erro: " + ex.Message;
            }
            finally
            {
                RefreshMemoryCard();
                RefreshMemoryReadout();
            }
        }
    }
}
