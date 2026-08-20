using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Modo Acesso Remoto: um clique prepara o PC para ser acessado de uma tela
    /// 1080p 16:9 comum (AnyDesk etc.) — neutraliza o que distorce a imagem
    /// capturada, desativa as outras telas (fica só a principal) e muda a
    /// resolução para 1920×1080. O mesmo botão reverte TUDO.
    ///
    /// Sobre COR: o que aparece no AnyDesk é o framebuffer da área de trabalho.
    /// Só entra na captura o que o Windows aplica na COMPOSIÇÃO — ou seja, o
    /// ACM/WCG ("gerenciar cores automaticamente") e o modo HDR. A gamma ramp
    /// (Cor avançada) NÃO entra: é aplicada pela GPU depois do framebuffer.
    /// Por isso o ACM é o principal responsável por imagem saturada no remoto:
    /// com HDR desligado e ACM ligado, a área de trabalho é convertida para o
    /// gamut LARGO do monitor — numa tela sRGB comum isso vira supersaturação.
    ///
    /// Ordem importa: HDR/ACM são alternados LONGE das trocas de topologia e
    /// resolução (logo após uma troca o vídeo ainda se reconfigura e o comando
    /// é ignorado), e cada alternância é VERIFICADA e repetida até pegar.
    /// </summary>
    public static class RemoteAccessService
    {
        /// <summary>O modo (ou qualquer resto dele de versões antigas) está ativo?</summary>
        public static bool IsActive
        {
            get
            {
                var s = SettingsService.Current;
                return s.RemoteModeActive || s.MultiMonitorDisabled || s.RemoteResActive;
            }
        }

        /// <summary>
        /// Ativa o modo. Se <paramref name="keepHdr"/> for true, o HDR é mantido
        /// ligado (em alguns PCs a imagem remota fica melhor assim).
        /// </summary>
        public static async Task<string> EnterAsync(bool keepHdr)
        {
            var s = SettingsService.Current;
            var steps = new List<string>();

            // 0) Filtros do app/Windows que podem entrar na composição — suspensos
            //    (as configurações do usuário ficam intactas, voltam na saída).
            bool winNl = NightLightService.GetWindowsNightLightEnabled();
            s.RemotePrevWinNightLight = winNl;
            if (winNl) NightLightService.SetWindowsNightLight(false);
            NightLightService.Reset();
            GammaRampService.Reset();

            // Anota o estado ORIGINAL de HDR/ACM antes de mexer em qualquer coisa
            // (as trocas de topologia/resolução mais abaixo alteram esses valores).
            List<HdrInfo> hdrOn, wcgOn;
            try
            {
                var all = HdrService.GetAllHdrInfo();
                hdrOn = all.Where(h => h.IsEnabled).ToList();
                wcgOn = all.Where(h => h.WcgEnabled).ToList();
            }
            catch { hdrOn = new List<HdrInfo>(); wcgOn = new List<HdrInfo>(); }

            s.RemotePrevHdr      = !keepHdr && hdrOn.Count > 0;
            s.RemoteHdrPositions = keepHdr ? ""
                : string.Join(";", hdrOn.Select(h => $"{h.SourceX},{h.SourceY}"));
            s.RemoteWcgPositions = string.Join(";", wcgOn.Select(h => $"{h.SourceX},{h.SourceY}"));

            // 1) Só a tela principal (topologia do Win+P — layout fica memorizado)
            bool topo = await Task.Run(MonitorTopologyService.UsePrimaryOnly);
            if (topo) s.MultiMonitorDisabled = true;
            steps.Add(topo ? "✅ 1 tela" : "⚠ telas");
            await Task.Delay(2000); // a troca de topologia precisa assentar

            // 2) 1080p 16:9 (guarda a nativa nas configurações para reversão)
            bool res = await Task.Run(DisplayResolutionService.ApplyRemote1080);
            steps.Add(res ? "✅ 1080p" : "⚠ resolução");
            await Task.Delay(1500);

            // 3) HDR/ACM off SÓ AGORA, por último. Toda troca de topologia ou
            //    resolução faz o Windows REAPLICAR a configuração salva da tela —
            //    que tem HDR ligado. Desligar antes era inútil: o HDR voltava
            //    sozinho no passo seguinte.
            s.RemoteEnforceHdrOff = !keepHdr;
            s.RemoteEnforceAcmOff = true;

            if (keepHdr)
            {
                if (hdrOn.Count > 0) steps.Add("• HDR mantido");
            }
            else
            {
                bool off = await SetHdrVerifiedAsync(enable: false, h => true);
                steps.Add(off ? "✅ HDR off" : "⚠ HDR não desligou");
                await Task.Delay(1200);
            }

            // 4) ACM/WCG off depois do HDR: com o ACM ligado a tela não vai para
            //    SDR ao desligar o HDR — para no modo WCG (gamut largo), que é o
            //    que sai supersaturado na captura remota.
            if (SetAcmAll(false)) steps.Add("✅ gamut largo off");

            s.RemoteModeActive = true;
            SettingsService.Save();

            // 5) Vigia: o Windows religa HDR/ACM sozinho sempre que a exibição é
            //    reinicializada (conectar o AnyDesk faz isso). Mantém o estado,
            //    com teto de correções para nunca virar briga infinita.
            ResetGuardBudget();
            StartGuard();
            return string.Join(" · ", steps);
        }

        // ── Vigia do estado de cor ───────────────────────────────────────────
        // Enquanto o modo remoto estiver ativo, reforça HDR/ACM desligados a cada
        // 5 s. Sem isso o Windows os religa sozinho em qualquer reconfiguração de
        // vídeo (reconexão do acesso remoto, troca de modo, retomada de sessão).
        private static System.Windows.Threading.DispatcherTimer? _guard;
        private static bool _guardBusy;

        /// <summary>Liga o vigia (idempotente). Chamado ao entrar no modo e no startup.</summary>
        public static void StartGuard()
        {
            if (System.Windows.Application.Current == null) return;
            if (_guard == null)
            {
                _guard = new System.Windows.Threading.DispatcherTimer(
                    System.Windows.Threading.DispatcherPriority.Background)
                { Interval = TimeSpan.FromSeconds(15) };
                _guard.Tick += (_, _) => EnforceTick();
            }
            if (!_guardPaused) _guard.Start();
        }

        // Durante jogo em tela cheia não há acesso remoto para atender — o vigia
        // só disputaria CPU com o jogo.
        private static bool _guardPaused;

        public static void Pause() { _guardPaused = true; _guard?.Stop(); }

        public static void Resume()
        {
            _guardPaused = false;
            if (GuardNeeded()) StartGuard();
        }

        private static void StopGuard() => _guard?.Stop();

        /// <summary>
        /// Ciclo manual: LIGA o HDR e DESLIGA em seguida — o procedimento que tira
        /// a tela do modo WCG (gamut largo) e a faz assentar em SDR puro. É o que
        /// o usuário faria nas Configurações do Windows, num clique só.
        /// Deliberadamente MANUAL: automatizar isso reconfigura o vídeo e, se o
        /// Windows insistir em voltar, vira um ciclo infinito piscando a tela.
        /// </summary>
        /// <summary>
        /// Garante o HDR desligado, tentando na ordem: (1) API do DisplayConfig e,
        /// se ela for recusada nesta máquina, (2) o atalho nativo Win+Alt+B, que é
        /// o mesmo caminho da interface do Windows. Devolve o que aconteceu.
        /// </summary>
        /// <param name="allowHotkey">
        /// Permite recorrer ao Win+Alt+B. SÓ para ação manual do usuário: esse
        /// atalho ALTERNA o HDR (não desliga), então se ele agir numa tela que já
        /// estava sem HDR, LIGA — e num vigia automático isso vira cabo de guerra,
        /// com a tela piscando entre normal e saturado.
        /// </param>
        public static async Task<(bool Ok, string Detail)> EnsureHdrOffAsync(bool allowHotkey)
        {
            if (!HdrService.AnyHdrOn()) return (true, "HDR já estava desligado");

            string detail = "";
            try
            {
                foreach (var h in HdrService.GetAllHdrInfo())
                    if (h.IsSupported && h.IsEnabled)
                        HdrService.SetHdrEnabledEx(h.AdapterIdLow, h.AdapterIdHigh,
                                                   h.TargetId, false, out detail);
            }
            catch (Exception ex) { Logger.Error(ex, "EnsureHdrOffAsync/api"); }

            await Task.Delay(1500);
            if (!HdrService.AnyHdrOn()) return (true, "HDR desligado pela API");

            if (!allowHotkey)
                return (false, $"A API recusou desligar o HDR. {detail}");

            // Último recurso, e só a pedido do usuário
            HdrService.PressHdrHotkey();
            await Task.Delay(2000);
            if (!HdrService.AnyHdrOn())
                return (true, "HDR desligado pelo atalho Win+Alt+B");

            return (false, $"Não consegui desligar o HDR. {detail}. " +
                           "O atalho Win+Alt+B também não pegou.");
        }

        // ── Correção automática por EVENTO ───────────────────────────────────
        // Conectar o acesso remoto reconfigura o vídeo e religa o HDR. Em vez de
        // ficar verificando de tempos em tempos (o que virava cabo de guerra e
        // piscava a tela), o app reage ao EVENTO de mudança de vídeo: uma
        // correção por evento, com trava enquanto corrige e carência depois.
        private static bool _autoHooked;
        private static bool _autoBusy;
        private static DateTime _autoLastFix = DateTime.MinValue;
        private const int AutoCooldownSeconds = 30;

        /// <summary>Liga/desliga a escuta do evento de mudança de vídeo.</summary>
        public static void SetAutoFixHook(bool on)
        {
            try
            {
                if (on && !_autoHooked)
                {
                    Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
                    _autoHooked = true;
                    Logger.Info("AutoFix de cor: escutando mudanças de vídeo");
                }
                else if (!on && _autoHooked)
                {
                    Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                    _autoHooked = false;
                    Logger.Info("AutoFix de cor: parou de escutar");
                }
            }
            catch (Exception ex) { Logger.Error(ex, "SetAutoFixHook"); }
        }

        private static DateTime _suppressUntil = DateTime.MinValue;

        /// <summary>
        /// Silencia a correção automática por alguns segundos. Usado sempre que é o
        /// PRÓPRIO app que muda o vídeo (16:9, maximizar tela, ligar/desligar
        /// monitor): essas mudanças disparam o mesmo evento de uma reconexão
        /// remota, e sem isso o app desligava o HDR do usuário por engano.
        /// </summary>
        public static void SuppressAutoFixFor(int seconds)
            => _suppressUntil = DateTime.Now.AddSeconds(seconds);

        private static void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            if (!SettingsService.Current.AutoFixColorOnDisplayChange) return;
            if (DateTime.Now < _suppressUntil) return;  // mudança feita pelo app
            if (_autoBusy) return;   // a nossa própria correção dispara este evento
            if ((DateTime.Now - _autoLastFix).TotalSeconds < AutoCooldownSeconds) return;
            _ = HandleDisplayChangeAsync();
        }

        private static async Task HandleDisplayChangeAsync()
        {
            _autoBusy = true;
            try
            {
                await Task.Delay(2500); // deixa o vídeo assentar antes de olhar

                // Só age se a tela REALMENTE está no estado que satura o remoto
                uint mode = PrimaryColorMode();
                if (mode != 1 && mode != 2) return;   // já está em SDR

                Logger.Info($"AutoFix: vídeo mudou e a tela está em " +
                            $"{(mode == 2 ? "HDR" : "WCG")} — corrigindo");
                string result = await FixColorNowAsync();
                Logger.Info("AutoFix: " + result);
            }
            catch (Exception ex) { Logger.Error(ex, "HandleDisplayChangeAsync"); }
            finally
            {
                _autoLastFix = DateTime.Now;   // carência conta a partir do FIM
                _autoBusy = false;
            }
        }

        public static async Task<string> FixColorNowAsync()
        {
            try
            {
                var targets = HdrService.GetAllHdrInfo().Where(h => h.IsSupported).ToList();
                if (targets.Count == 0) return "Nenhuma tela com suporte a HDR";

                // 1) Desliga o HDR (API e, se ela falhar, o atalho do Windows)
                var (ok, detail) = await EnsureHdrOffAsync(allowHotkey: true);

                // 2) Com o HDR fora do caminho, desliga o gamut largo
                foreach (var h in HdrService.GetAllHdrInfo())
                    if (h.WcgSupported && h.WcgEnabled)
                        HdrService.SetWcgEnabled(h.AdapterIdLow, h.AdapterIdHigh, h.TargetId, false);
                await Task.Delay(900);

                if (!ok) return "⚠ " + detail;

                uint mode = PrimaryColorMode();
                return mode switch
                {
                    0 => $"✅ Tela em SDR puro — cor normalizada ({detail})",
                    1 => $"⚠ Ainda em gamut largo ({detail}) — tente pelas Configurações do Windows",
                    2 => $"Tela ainda em HDR ({detail}) — clique de novo",
                    _ => detail
                };
            }
            catch (Exception ex) { Logger.Error(ex, "FixColorNowAsync"); return "Erro: " + ex.Message; }
        }

        /// <summary>O vigia precisa estar rodando?</summary>
        public static bool GuardNeeded()
        {
            var s = SettingsService.Current;
            return s.KeepHdrOff
                || (s.RemoteModeActive && (s.RemoteEnforceHdrOff || s.RemoteEnforceAcmOff));
        }

        // Limite de taxa. O vigia só DESLIGA o HDR (nunca liga), então sozinho ele
        // não oscila; o risco seria o Windows religar sem parar. Nesse caso, em vez
        // de brigar — o que aparecia como a tela piscando —, ele recua por um tempo.
        private static DateTime _lastCorrectionAt = DateTime.MinValue;
        private static DateTime _backoffUntil = DateTime.MinValue;
        private static readonly List<DateTime> _recent = new();
        private const int MinSecondsBetween   = 10;
        private const int BurstLimit          = 5;   // correções…
        private const int BurstWindowSeconds  = 60;  // …nesta janela dispara o recuo
        private const int BackoffMinutes      = 5;

        /// <summary>Libera o vigia (ao entrar no modo remoto ou ligar a opção).</summary>
        public static void ResetGuardBudget()
        {
            _backoffUntil = DateTime.MinValue;
            _lastCorrectionAt = DateTime.MinValue;
            _recent.Clear();
        }

        // Controle do ciclo de HDR. O ciclo PASSA por "HDR ligado", então nunca
        // se pode concluir "voltou ao normal" olhando um instante qualquer — era
        // isso que zerava o contador e fazia a tela piscar sem parar.

        /// <summary>Modo de cor da tela principal, ou 99 se não der para ler.</summary>
        private static uint PrimaryColorMode()
        {
            try
            {
                foreach (var h in HdrService.GetAllHdrInfo())
                    if (h.SourceX == 0 && h.SourceY == 0) return h.ActiveColorMode;
            }
            catch { }
            return 99;
        }

        private static void EnforceTick()
        {
            var s = SettingsService.Current;
            if (!GuardNeeded()) { StopGuard(); return; }
            if (_guardBusy) return;
            _guardBusy = true;

            // Fora da thread de UI: alternar HDR reconfigura o vídeo e trava a janela.
            _ = Task.Run(() =>
            {
                try
                {
                    var now = DateTime.Now;
                    if (now < _backoffUntil) return;
                    if ((now - _lastCorrectionAt).TotalSeconds < MinSecondsBetween) return;

                    bool wantHdrOff = s.KeepHdrOff || s.RemoteEnforceHdrOff;
                    bool wantAcmOff = s.KeepHdrOff || s.RemoteEnforceAcmOff;
                    bool acted = false;

                    // UMA leitura por tick: cada GetAllHdrInfo percorre todos os
                    // paths com 3 consultas por path — fazer duas era desperdício.
                    var infos = HdrService.GetAllHdrInfo();
                    bool hdrOn = false;
                    foreach (var h in infos) if (h.IsEnabled) { hdrOn = true; break; }

                    // Só DESLIGA — nunca liga. Assim o vigia não oscila sozinho.
                    if (wantHdrOff && hdrOn)
                    {
                        Logger.Info("Vigia: HDR foi religado (reconexão do acesso remoto?) — desligando");
                        // allowHotkey: false — o vigia NUNCA usa Win+Alt+B. Ele
                        // alterna, e num laço automático acaba LIGANDO o HDR de
                        // volta: era isso que piscava entre normal e saturado.
                        var (ok, detail) = EnsureHdrOffAsync(allowHotkey: false)
                                           .GetAwaiter().GetResult();
                        Logger.Info("Vigia: " + detail);
                        acted = true;
                        if (!ok)
                        {
                            // A API é recusada nesta máquina: insistir não resolve.
                            // Desliga a opção e avisa no log, em vez de ficar tentando.
                            s.KeepHdrOff = false;
                            SettingsService.Save();
                            _backoffUntil = now.AddMinutes(BackoffMinutes);
                            Logger.Warn("Vigia: a API de HDR é recusada neste PC — opção " +
                                        "'Manter o HDR desligado' desativada. Use o botão " +
                                        "manual 'Corrigir cor agora'.");
                            return;
                        }
                    }

                    if (wantAcmOff)
                        foreach (var h in infos)
                            if (h.WcgSupported && h.WcgEnabled)
                            {
                                HdrService.SetWcgEnabled(h.AdapterIdLow, h.AdapterIdHigh, h.TargetId, false);
                                acted = true;
                            }

                    if (!acted) return;

                    _lastCorrectionAt = now;
                    _recent.Add(now);
                    _recent.RemoveAll(t => (now - t).TotalSeconds > BurstWindowSeconds);

                    // Rajada de correções = o Windows está religando sem parar.
                    // Recua em vez de brigar (era assim que a tela ficava piscando).
                    if (_recent.Count >= BurstLimit)
                    {
                        _backoffUntil = now.AddMinutes(BackoffMinutes);
                        _recent.Clear();
                        Logger.Warn($"Vigia: o Windows está religando o HDR sem parar — " +
                                    $"pausando por {BackoffMinutes} min para não piscar a tela");
                    }
                }
                catch (Exception ex) { Logger.Error(ex, "RemoteAccess.EnforceTick"); }
                finally { _guardBusy = false; }
            });
        }


        /// <summary>Reverte tudo: resolução → telas → HDR → ACM → filtros.</summary>
        public static async Task<string> ExitAsync()
        {
            var s = SettingsService.Current;
            var steps = new List<string>();

            // Solta o vigia ANTES de restaurar, senão ele desfaz o que religarmos
            s.RemoteEnforceHdrOff = false;
            s.RemoteEnforceAcmOff = false;
            StopGuard();

            // 1) Resolução nativa
            if (s.RemoteResActive)
            {
                bool res = await Task.Run(DisplayResolutionService.RestoreNative);
                steps.Add(res ? "✅ resolução nativa" : "⚠ resolução");
                await Task.Delay(1500);
            }

            // 2) Todas as telas de volta — ANTES do HDR/ACM: as posições das
            //    secundárias só existem com a topologia estendida.
            bool topo = await Task.Run(MonitorTopologyService.ExtendAll);
            if (topo) s.MultiMonitorDisabled = false;
            steps.Add(topo ? "✅ todas as telas" : "⚠ telas: Win+P → Estender");
            await Task.Delay(2500);

            // 3) HDR de volta nas telas onde estava ligado
            if (s.RemotePrevHdr)
            {
                var wanted = ParsePositions(s.RemoteHdrPositions);
                bool on = await SetHdrVerifiedAsync(enable: true, h =>
                    h.IsSupported &&
                    (wanted.Count == 0
                        ? h.SourceX == 0 && h.SourceY == 0        // fallback: principal
                        : wanted.Contains((h.SourceX, h.SourceY))));
                steps.Add(on ? "✅ HDR on" : "⚠ HDR: religue manualmente");
                s.RemotePrevHdr = false;
                s.RemoteHdrPositions = "";
                await Task.Delay(1200);
            }

            // 4) ACM/WCG de volta nas telas onde estava ligado
            var wantWcg = ParsePositions(s.RemoteWcgPositions);
            if (wantWcg.Count > 0)
            {
                try
                {
                    foreach (var h in HdrService.GetAllHdrInfo())
                        if (h.WcgSupported && !h.WcgEnabled &&
                            wantWcg.Contains((h.SourceX, h.SourceY)))
                            HdrService.SetWcgEnabled(h.AdapterIdLow, h.AdapterIdHigh, h.TargetId, true);
                }
                catch (Exception ex) { Logger.Error(ex, "RemoteAccess.WcgOn"); }
                s.RemoteWcgPositions = "";
                steps.Add("✅ cores automáticas on");
            }

            // 5) Filtros de cor de volta, exatamente como estavam
            if (s.RemotePrevWinNightLight)
            {
                NightLightService.SetWindowsNightLight(true);
                s.RemotePrevWinNightLight = false;
            }
            if (s.NightLightEnabled) NightLightService.SetIntensity(s.NightLightIntensity);
            if (!GammaRampService.IsDefault(s.GammaValue, s.ColorTempK, s.GainR, s.GainG, s.GainB))
                GammaRampService.RestoreFromSettings();

            s.RemoteModeActive = false;
            SettingsService.Save();
            return string.Join(" · ", steps);
        }

        // ── Controles individuais (janela de ajustes finos) ──────────────────
        // Servem para ISOLAR qual etapa afeta a imagem no acesso remoto: o
        // usuário liga uma de cada vez e observa o resultado ao vivo.

        /// <summary>Estado atual de cada etapa: (1 tela, 1080p, HDR ligado, ACM ligado).</summary>
        public static (bool Single, bool Res1080, bool HdrOn, bool AcmOn) ReadState()
        {
            bool single = MonitorTopologyService.ActiveScreenCount() <= 1;

            var cur = DisplayResolutionService.GetCurrent();
            bool res = cur != null && cur.Value.W == 1920 && cur.Value.H == 1080;

            bool hdr = false, acm = false;
            try
            {
                foreach (var h in HdrService.GetAllHdrInfo())
                {
                    if (h.IsEnabled) hdr = true;
                    if (h.WcgEnabled) acm = true;
                }
            }
            catch { }
            return (single, res, hdr, acm);
        }

        /// <summary>
        /// Modo de cor em que a tela principal está compondo a imagem — é isso
        /// que o programa de acesso remoto captura. WCG é o vilão: gamut largo
        /// interpretado como sRGB do outro lado = imagem saturada e escura.
        /// </summary>
        public static string DescribeColorMode()
        {
            try
            {
                foreach (var h in HdrService.GetAllHdrInfo())
                {
                    if (h.SourceX != 0 || h.SourceY != 0) continue; // tela principal
                    return h.ActiveColorMode switch
                    {
                        2 => "Tela principal: HDR",
                        1 => "Tela principal: WCG (gamut largo) — é o que satura o acesso remoto",
                        _ => "Tela principal: SDR — ideal para acesso remoto"
                    };
                }
            }
            catch { }
            return "";
        }

        public static async Task<bool> SetSingleScreenAsync(bool single)
        {
            bool ok = await Task.Run(() => single
                ? MonitorTopologyService.UsePrimaryOnly()
                : MonitorTopologyService.ExtendAll());
            if (ok)
            {
                SettingsService.Current.MultiMonitorDisabled = single;
                SettingsService.Save();
            }
            return ok;
        }

        public static async Task<bool> Set1080Async(bool on) =>
            await Task.Run(() => on
                ? DisplayResolutionService.ApplyRemote1080()
                : DisplayResolutionService.RestoreNative());

        public static Task<bool> SetHdrAllAsync(bool enable) =>
            SetHdrVerifiedAsync(enable, h => true);

        /// <summary>Liga/desliga o ACM em todas as telas que suportam.</summary>
        public static bool SetAcmAll(bool enable)
        {
            bool any = false;
            try
            {
                foreach (var h in HdrService.GetAllHdrInfo())
                    if (h.WcgSupported && h.WcgEnabled != enable)
                        any |= HdrService.SetWcgEnabled(
                            h.AdapterIdLow, h.AdapterIdHigh, h.TargetId, enable);
            }
            catch (Exception ex) { Logger.Error(ex, "SetAcmAll"); }
            return any;
        }

        /// <summary>
        /// Liga/desliga o HDR nas telas selecionadas por <paramref name="match"/> e
        /// CONFERE relendo o estado; repete até 4 vezes com pausa — logo após uma
        /// troca de vídeo, a primeira ordem costuma ser ignorada pelo driver.
        /// </summary>
        private static async Task<bool> SetHdrVerifiedAsync(bool enable, Func<HdrInfo, bool> match)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                bool pending = false;
                try
                {
                    foreach (var h in HdrService.GetAllHdrInfo())
                        if (match(h) && h.IsSupported && h.IsEnabled != enable)
                        {
                            pending = true;
                            HdrService.SetHdrEnabled(h.AdapterIdLow, h.AdapterIdHigh, h.TargetId, enable);
                        }
                }
                catch (Exception ex) { Logger.Error(ex, "SetHdrVerifiedAsync"); }

                if (!pending) return true; // tudo já no estado desejado
                await Task.Delay(1500);    // deixa o vídeo assentar e re-verifica
            }

            try
            {
                foreach (var h in HdrService.GetAllHdrInfo())
                    if (match(h) && h.IsSupported && h.IsEnabled != enable) return false;
                return true;
            }
            catch { return false; }
        }

        private static HashSet<(int X, int Y)> ParsePositions(string? raw)
        {
            var set = new HashSet<(int, int)>();
            foreach (var part in (raw ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var xy = part.Split(',');
                if (xy.Length == 2 && int.TryParse(xy[0], out int x) && int.TryParse(xy[1], out int y))
                    set.Add((x, y));
            }
            return set;
        }
    }
}
