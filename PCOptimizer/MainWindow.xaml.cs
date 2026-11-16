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
    // As outras partes desta janela ficam em MainWindow.*.cs, uma por assunto.
    public partial class MainWindow : Window
    {
        private bool _isRunning;

        // Progresso ponderado: cada otimização tem uma duração típica estimada,
        // então a previsão é do PROCESSO TOTAL (não passo a passo) e a barra
        // avança proporcional ao trabalho real, não à contagem de passos.
        private int _completedSteps, _totalSelectedSteps;
        private double _doneWeight, _totalWeight;
        private DateTime _runStart;

        // Modo Expert: aviso único por sessão + dados da GPU/ferramenta detectadas
        private bool _expertWarned;
        private GpuTuningService.GpuInfo? _gpuInfo;
        private CpuUndervoltToolService.ToolInfo? _cpuUvTool;
        private System.Collections.Generic.List<CpuTuningDetectionService.DetectedTool>? _detectedTools;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                ((App)Application.Current).InitHotkey(this);
                ApplyTitleBarColor();
                TxtVersion.Text = "v" + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "?");
                ChkSelectAll.IsChecked = true;
                UpdateSelectedCount();
                _ = CheckForUpdatesAsync();
                _ = InitExpertAsync();
                RefreshAutologonStatus();
                _ = RefreshWoLStatusAsync();
                TaskbarTransparencyService.RestoreFromSettings();
                RefreshTaskbarStatus();
                RefreshGameBoostCard();
                InitCoreReserveCard();
                RefreshMouseMenuCard();
                LoadCpuTuningControls();
                WireCpuTuningEvents();
                RefreshCpuTuningCard();
                // O turbo pode ligar/desligar sozinho (jogo abriu ou fechou) —
                // o card se atualiza pelo evento, sem precisar de timer próprio.
                GameBoostService.StatusChanged += () =>
                    Dispatcher.BeginInvoke(new Action(RefreshGameBoostCard));
                CpuTuningService.StatusChanged += () =>
                    Dispatcher.BeginInvoke(new Action(RefreshCpuTuningCard));

                LoadMemoryControls();
                WireMemoryEvents();
                RefreshMemoryCard();
                UpdateMemPollState();

                // Teclados: lê a lista quando a aba Sistema abre (roda o
                // PowerShell uma vez) e marca o teclado em uso ao voltar à janela.
                KeyboardLayoutService.Changed += () => Dispatcher.BeginInvoke(new Action(RefreshKeyboardCard));
                OptList.SelectionChanged += (_, e) =>
                {
                    if (ReferenceEquals(e.OriginalSource, OptList) && ReferenceEquals(OptList.SelectedItem, TabSistema))
                        EnsureKeyboardsLoaded();
                };
                Activated += (_, _) => { if (_kbdLoaded) RefreshKeyboardCard(); };
            };
        }

        private void RefreshGameBoostCard()
        {
            try
            {
                TxtGameBoostStatus.Text = GameBoostService.StatusText();
                BtnGameBoost.IsEnabled  = CpuTopologyService.Get().CanPark;
                BtnGameBoost.Content    = GameBoostService.IsActive ? "Desativar" : "Ativar agora";
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "RefreshGameBoostCard");
                TxtGameBoostStatus.Text = "Não consegui ler os núcleos do processador";
            }
        }

        private async void BtnGameBoost_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning) return;

            BtnGameBoost.IsEnabled = false;
            try
            {
                if (GameBoostService.IsActive)
                {
                    TxtGameBoostStatus.Text = await Task.Run(
                        () => GameBoostService.ReleaseAll("botão manual"));
                    return;
                }

                if (!SettingsService.Current.GameBoostWarningShown)
                {
                    var r = MessageBox.Show(
                        "🚀 Turbo de Jogo\n\n" +
                        "Enquanto o jogo roda, os outros programas (navegador, Discord, " +
                        "fatiador 3D…) são movidos para os E-cores, deixando os P-cores " +
                        "livres para o jogo. É como jogar sem mais nada aberto.\n\n" +
                        "Eles ficam mais lentos nesse período — isso é esperado. Tudo volta " +
                        "ao normal quando o jogo fecha ou você clica em Desativar.\n\n" +
                        "O app NÃO altera o processo do jogo, então não interfere com " +
                        "anticheat.\n\nContinuar?",
                        "PC Optimizer", MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (r != MessageBoxResult.Yes) return;
                    SettingsService.Current.GameBoostWarningShown = true;
                    SettingsService.Save();
                }

                // A janela em primeiro plano agora é a do próprio app — dá tempo
                // de o usuário clicar no jogo antes de capturarmos o alvo.
                for (int i = 5; i > 0; i--)
                {
                    TxtGameBoostStatus.Text = $"Clique na janela do jogo… {i}";
                    await Task.Delay(1000);
                }

                var target = GameTargetService.FromForegroundWindow();
                if (target == null)
                {
                    TxtGameBoostStatus.Text = "Não consegui identificar o jogo — tente de novo";
                    return;
                }

                TxtGameBoostStatus.Text = await Task.Run(
                    () => GameBoostService.ApplyTo(target, manual: true));
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "BtnGameBoost_Click");
                TxtGameBoostStatus.Text = "Erro: " + ex.Message;
            }
            finally
            {
                BtnGameBoost.IsEnabled = CpuTopologyService.Get().CanPark;
                BtnGameBoost.Content   = GameBoostService.IsActive ? "Desativar" : "Ativar agora";
            }
        }

        // ── Controle de CPU ───────────────────────────────────────────────────

        /// <summary>Os controles estão sendo preenchidos; ignora os eventos deles.</summary>
        private bool _cpuTuningLoading;

        private static readonly (string Label, int Value)[] BoostModes =
        {
            ("Desligado", 0), ("Ativado", 1), ("Agressivo", 2),
            ("Eficiente", 3), ("Eficiente agressivo", 4),
        };

        /// <summary>
        /// Liga os controles ao rótulo de valores. Feito em código, e não no
        /// XAML, para não espalhar oito nomes de handler pela marcação.
        /// </summary>
        private void WireCpuTuningEvents()
        {
            foreach (var sld in new[] { SldPMinState, SldPMaxState, SldPEpp, SldPMinCores,
                         SldPMaxCores, SldEMinState, SldEMaxState, SldEEpp, SldEMinCores, SldEMaxCores })
            {
                sld.IsSnapToTickEnabled = true;
                sld.TickFrequency = 1;
                sld.ValueChanged += (_, _) => UpdateCpuTuningValues();
            }
            CmbPBoost.SelectionChanged += (_, _) => UpdateCpuTuningValues();
            foreach (var chk in new[] { ChkCpuIdleDisable, ChkCpuTimer, ChkCpuResponsiveness })
            {
                chk.Checked   += (_, _) => UpdateCpuTuningValues();
                chk.Unchecked += (_, _) => UpdateCpuTuningValues();
            }
        }

        private bool _cpuPrevRunLogged;

        /// <summary>
        /// Em CPU híbrida a 1ª coluna é a classe 1 (P-cores) e a 2ª a classe 0
        /// (E-cores). Em CPU comum só existe a classe 0 — e é ELA que a coluna
        /// visível precisa editar (antes a tela editava a classe 1, que nem é
        /// aplicada nesse caso).
        /// </summary>
        private static (CoreClassTuning Col1, CoreClassTuning Col2) CpuColumns(CpuTuningProfile p, bool hybrid) =>
            hybrid ? (p.Class1, p.Class0) : (p.Class0, p.Class1);

        private CpuTuningLevel CurrentCpuLevel() =>
            CpuTuningProfile.DetectLevel(SettingsService.Current.CpuTuning, CpuTopologyService.Get().IsHybrid);

        private void RefreshCpuTuningCard()
        {
            try
            {
                var topo = CpuTopologyService.Get();
                var level = CurrentCpuLevel();
                string name = CpuTuningProfile.LevelName(level);

                TxtCpuTuningStatus.Text = CpuTuningService.IsActive
                    ? $"{CpuTuningService.StatusText()} · nível {name}"
                    : $"Inativo · escolha um nível para aplicar";
                BtnCpuTuning.Content = CpuTuningService.IsActive ? "Reaplicar ajustes" : "Aplicar ajustes";
                BtnCpuTuningOff.IsEnabled = CpuTuningService.IsActive;
                TxtCpuTuningTopology.Text = CpuTopologyService.Describe()
                    + (CpuInfoService.IsLegacyPStates
                        ? $" · {CpuInfoService.ProcessorName} sem Speed Shift: inclui turbo inteiro e clock que sobe mais cedo"
                        : "");
                TxtCpuLevelSummary.Text = CpuTuningProfile.Describe(level);

                foreach (var (btn, l) in new[] { (BtnCpuLevelBaixo, CpuTuningLevel.Baixo),
                             (BtnCpuLevelMedio, CpuTuningLevel.Medio), (BtnCpuLevelAgressivo, CpuTuningLevel.Agressivo) })
                    btn.Style = (Style)FindResource(CpuTuningService.IsActive && level == l
                        ? "PrimaryButtonStyle" : "ActionButtonStyle");

                // Numa CPU sem P/E existe uma classe só: a segunda coluna some
                // em vez de mostrar controles que não vão a lugar nenhum.
                var second = topo.IsHybrid ? Visibility.Visible : Visibility.Collapsed;
                foreach (var el in new UIElement[] { TxtCpuClass0, SldEMinState, SldEMaxState,
                             SldEEpp, SldEMinCores, SldEMaxCores })
                    el.Visibility = second;

                if (topo.IsHybrid)
                {
                    TxtCpuClass1.Text = $"P-cores ({topo.PCoreCount})";
                    TxtCpuClass0.Text = $"E-cores ({topo.ECoreCount})";
                }
                else
                {
                    TxtCpuClass1.Text = $"Todos os núcleos ({topo.LogicalCount})";
                }

                if (CpuTuningService.FromPreviousRun && !_cpuPrevRunLogged)
                {
                    _cpuPrevRunLogged = true;
                    Log($"ℹ️ O perfil de CPU continua ativo desde a sessão anterior (nível {name})");
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "RefreshCpuTuningCard");
                TxtCpuTuningStatus.Text = "Não consegui ler o processador";
            }
        }

        /// <summary>Joga o perfil salvo (ou o indicado) nos controles da tela.</summary>
        private void LoadCpuTuningControls(CpuTuningProfile? source = null)
        {
            _cpuTuningLoading = true;
            try
            {
                CmbPBoost.ItemsSource = null;
                CmbPBoost.ItemsSource = BoostModes.Select(m => m.Label).ToList();
                // O turbo é um ajuste só para o processador inteiro: uma caixa basta.
                CmbEBoost.Visibility = Visibility.Collapsed;
                System.Windows.Controls.Grid.SetColumnSpan(CmbPBoost, 2);

                var p = (source ?? SettingsService.Current.CpuTuning ?? CpuTuningProfile.Default()).Clone();
                var (col1, col2) = CpuColumns(p, CpuTopologyService.Get().IsHybrid);

                Bind(col1, SldPMinState, SldPMaxState, SldPEpp, SldPMinCores, SldPMaxCores);
                Bind(col2, SldEMinState, SldEMaxState, SldEEpp, SldEMinCores, SldEMaxCores);
                int i = Array.FindIndex(BoostModes, m => m.Value == p.Class0.BoostMode);
                CmbPBoost.SelectedIndex = i >= 0 ? i : 2;

                ChkCpuIdleDisable.IsChecked    = p.DisableIdle;
                ChkCpuTimer.IsChecked          = p.LowLatencyTimer;
                ChkCpuResponsiveness.IsChecked = p.GamingResponsiveness;
            }
            finally { _cpuTuningLoading = false; }

            UpdateCpuTuningValues();

            static void Bind(CoreClassTuning c, System.Windows.Controls.Slider min, System.Windows.Controls.Slider max,
                             System.Windows.Controls.Slider epp, System.Windows.Controls.Slider minCores, System.Windows.Controls.Slider maxCores)
            {
                min.Value = c.MinState;
                max.Value = c.MaxState;
                epp.Value = c.Epp;
                minCores.Value = c.MinCores;
                maxCores.Value = c.MaxCores;
            }
        }

        /// <summary>
        /// Lê os controles de volta para um perfil. Parte do perfil salvo para
        /// preservar a classe que não aparece na tela (CPU sem híbrido).
        /// </summary>
        private CpuTuningProfile ReadCpuTuningControls()
        {
            var p = (SettingsService.Current.CpuTuning ?? CpuTuningProfile.Default()).Clone();
            var (col1, col2) = CpuColumns(p, CpuTopologyService.Get().IsHybrid);
            Read(col1, SldPMinState, SldPMaxState, SldPEpp, SldPMinCores, SldPMaxCores);
            Read(col2, SldEMinState, SldEMaxState, SldEEpp, SldEMinCores, SldEMaxCores);

            int boost = CmbPBoost.SelectedIndex >= 0 && CmbPBoost.SelectedIndex < BoostModes.Length
                ? BoostModes[CmbPBoost.SelectedIndex].Value : 2;
            p.Class0.BoostMode = boost;
            p.Class1.BoostMode = boost;

            p.DisableIdle          = ChkCpuIdleDisable.IsChecked == true;
            p.LowLatencyTimer      = ChkCpuTimer.IsChecked == true;
            p.GamingResponsiveness = ChkCpuResponsiveness.IsChecked == true;
            p.Clamp();
            return p;

            static void Read(CoreClassTuning c, System.Windows.Controls.Slider min, System.Windows.Controls.Slider max,
                             System.Windows.Controls.Slider epp, System.Windows.Controls.Slider minCores, System.Windows.Controls.Slider maxCores)
            {
                c.MinState = (int)min.Value;
                c.MaxState = (int)max.Value;
                c.Epp      = (int)epp.Value;
                c.MinCores = (int)minCores.Value;
                c.MaxCores = (int)maxCores.Value;
            }
        }

        private void UpdateCpuTuningValues()
        {
            if (_cpuTuningLoading) return;
            try
            {
                var p = ReadCpuTuningControls();
                bool hybrid = CpuTopologyService.Get().IsHybrid;
                var level = CpuTuningProfile.DetectLevel(p, hybrid);
                TxtCpuTuningValues.Text = (hybrid
                    ? $"P: {p.Class1.MinState}–{p.Class1.MaxState}%  EPP {p.Class1.Epp}  " +
                      $"núcleos {p.Class1.MinCores}–{p.Class1.MaxCores}%   |   " +
                      $"E: {p.Class0.MinState}–{p.Class0.MaxState}%  EPP {p.Class0.Epp}  " +
                      $"núcleos {p.Class0.MinCores}–{p.Class0.MaxCores}%"
                    : $"{p.Class0.MinState}–{p.Class0.MaxState}%  EPP {p.Class0.Epp}  " +
                      $"núcleos {p.Class0.MinCores}–{p.Class0.MaxCores}%")
                    + $"   ·   nível: {CpuTuningProfile.LevelName(level)}";
            }
            catch (Exception ex) { Logger.Error(ex, "UpdateCpuTuningValues"); }
        }

        /// <summary>Aviso único da primeira vez que o perfil de CPU é aplicado.</summary>
        private static bool ConfirmCpuTuningFirstUse()
        {
            if (SettingsService.Current.CpuTuningWarningShown) return true;
            var answer = MessageBox.Show(
                "⚙️ Controle de CPU\n\n" +
                "Os ajustes vão para um plano de energia CRIADO pelo aplicativo. " +
                "O seu plano não é alterado — desativar devolve ele e apaga a cópia.\n\n" +
                "Diferente do resto do programa, este perfil CONTINUA VALENDO com o " +
                "PC Optimizer fechado, até você desativar.\n\nContinuar?",
                "PC Optimizer", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer != MessageBoxResult.Yes) return false;
            SettingsService.Current.CpuTuningWarningShown = true;
            SettingsService.Save();
            return true;
        }

        /// <summary>Salva e aplica um perfil, com os botões travados durante a troca.</summary>
        private async Task ApplyCpuProfileAsync(CpuTuningProfile profile, string label)
        {
            var buttons = new[] { BtnCpuTuning, BtnCpuTuningDefaults, BtnCpuTuningOff,
                                  BtnCpuLevelBaixo, BtnCpuLevelMedio, BtnCpuLevelAgressivo };
            foreach (var b in buttons) b.IsEnabled = false;
            try
            {
                SettingsService.Current.CpuTuning = profile;
                SettingsService.Save();
                LoadCpuTuningControls();

                TxtCpuTuningStatus.Text = $"Aplicando nível {label}...";
                string result = await Task.Run(() => CpuTuningService.Apply(profile));
                Log($"⚙️ Controle de CPU ({label}): {result}");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "ApplyCpuProfileAsync");
                TxtCpuTuningStatus.Text = "Erro: " + ex.Message;
            }
            finally
            {
                foreach (var b in buttons) b.IsEnabled = true;
                RefreshCpuTuningCard();
            }
        }

        private async void BtnCpuLevel_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning || sender is not System.Windows.Controls.Button btn) return;
            if (!Enum.TryParse(btn.Tag as string, out CpuTuningLevel level)) return;
            if (!ConfirmCpuTuningFirstUse()) return;

            if (level == CpuTuningLevel.Agressivo && !SettingsService.Current.CpuTuningAggressiveWarningShown)
            {
                var answer = MessageBox.Show(
                    "🔥 Nível Agressivo\n\n" +
                    "Desempenho total em todos os núcleos, timer do sistema em 0,5 ms e " +
                    "menos CPU reservada para segundo plano.\n\n" +
                    "O processador esquenta mais. Em notebook, use na tomada.\n\nAplicar?",
                    "PC Optimizer", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
                SettingsService.Current.CpuTuningAggressiveWarningShown = true;
            }

            await ApplyCpuProfileAsync(CpuTuningProfile.ForLevel(level), CpuTuningProfile.LevelName(level));
        }

        private async void BtnCpuTuning_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning) return;
            if (!ConfirmCpuTuningFirstUse()) return;
            var profile = ReadCpuTuningControls();
            await ApplyCpuProfileAsync(profile,
                CpuTuningProfile.LevelName(CpuTuningProfile.DetectLevel(profile, CpuTopologyService.Get().IsHybrid)));
        }

        private async void BtnCpuTuningOff_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning) return;
            BtnCpuTuningOff.IsEnabled = false;
            try
            {
                TxtCpuTuningStatus.Text = "Devolvendo o plano de energia...";
                string result = await Task.Run(CpuTuningService.Restore);
                TxtCpuTuningStatus.Text = result;
                Log($"⚙️ Controle de CPU: {result}");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "BtnCpuTuningOff_Click");
                TxtCpuTuningStatus.Text = "Erro: " + ex.Message;
            }
            finally { RefreshCpuTuningCard(); }
        }

        /// <summary>
        /// "Voltar ao Médio": só carrega o Médio nos controles. O perfil salvo
        /// continua sendo o APLICADO — senão o card diria "Médio" com outro valendo.
        /// </summary>
        private void BtnCpuTuningDefaults_Click(object sender, RoutedEventArgs e)
        {
            LoadCpuTuningControls(CpuTuningProfile.ForLevel(CpuTuningLevel.Medio));
            TxtCpuTuningStatus.Text = CpuTuningService.IsActive
                ? "Médio carregado — clique em “Reaplicar ajustes” ou no botão Médio"
                : "Médio carregado — clique em “Aplicar ajustes” ou no botão Médio";
        }
    }
}
