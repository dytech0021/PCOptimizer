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
            //    reinicializada (conectar o AnyDesk faz isso). Mantém o estado.
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
                { Interval = TimeSpan.FromSeconds(5) };
                _guard.Tick += (_, _) => EnforceTick();
            }
            _guard.Start();
        }

        private static void StopGuard() => _guard?.Stop();

        /// <summary>O vigia precisa estar rodando?</summary>
        public static bool GuardNeeded()
        {
            var s = SettingsService.Current;
            return s.KeepSdrMode
                || (s.RemoteModeActive && (s.RemoteEnforceHdrOff || s.RemoteEnforceAcmOff));
        }

        // Evita ficar repetindo o ciclo de HDR se ele não estiver resolvendo
        private static int _wcgFixAttempts;

        private static void EnforceTick()
        {
            var s = SettingsService.Current;
            if (!GuardNeeded()) { StopGuard(); return; }
            if (_guardBusy) return;
            _guardBusy = true;

            // Fora da thread de UI: alternar HDR reconfigura o vídeo e trava a janela.
            _ = Task.Run(async () =>
            {
                try
                {
                    bool inWcg = false;
                    foreach (var h in HdrService.GetAllHdrInfo())
                    {
                        if (s.RemoteEnforceHdrOff && h.IsSupported && h.IsEnabled)
                        {
                            Logger.Info("Vigia: Windows religou o HDR — desligando de novo");
                            HdrService.SetHdrEnabled(h.AdapterIdLow, h.AdapterIdHigh, h.TargetId, false);
                        }
                        if ((s.RemoteEnforceAcmOff || s.KeepSdrMode) && h.WcgSupported && h.WcgEnabled)
                        {
                            Logger.Info("Vigia: Windows religou o gamut largo — desligando de novo");
                            HdrService.SetWcgEnabled(h.AdapterIdLow, h.AdapterIdHigh, h.TargetId, false);
                        }
                        // WCG = o estado que sai saturado na captura remota
                        if (h.ActiveColorMode == 1) inWcg = true;
                    }

                    if (s.KeepSdrMode && inWcg)
                        await FixWcgAsync();
                    else if (!inWcg)
                        _wcgFixAttempts = 0; // voltou ao normal — zera o contador
                }
                catch (Exception ex) { Logger.Error(ex, "RemoteAccess.EnforceTick"); }
                finally { _guardBusy = false; }
            });
        }

        /// <summary>
        /// Tira a tela do modo WCG (gamut largo) repetindo o que funciona na mão:
        /// LIGA o HDR e DESLIGA em seguida — só esse ciclo faz o Windows assentar
        /// em SDR puro. Desligar o HDR direto (quando já está desligado) não muda
        /// nada, e é por isso que a saturação voltava a cada reconexão.
        /// </summary>
        private static async Task FixWcgAsync()
        {
            if (_wcgFixAttempts >= 3)
            {
                // Já tentou o bastante: evita ficar piscando a tela em loop
                return;
            }
            _wcgFixAttempts++;
            Logger.Info($"Vigia: tela em WCG — ciclo HDR on/off (tentativa {_wcgFixAttempts})");

            try
            {
                var targets = HdrService.GetAllHdrInfo()
                    .Where(h => h.IsSupported && h.ActiveColorMode == 1).ToList();
                if (targets.Count == 0) return;

                foreach (var h in targets)
                    HdrService.SetHdrEnabled(h.AdapterIdLow, h.AdapterIdHigh, h.TargetId, true);
                await Task.Delay(1800); // o vídeo precisa entrar em HDR de fato

                foreach (var h in targets)
                    HdrService.SetHdrEnabled(h.AdapterIdLow, h.AdapterIdHigh, h.TargetId, false);
                await Task.Delay(1500);

                // Com o HDR fora do caminho, o ACM pode ser desligado de vez
                foreach (var h in HdrService.GetAllHdrInfo())
                    if (h.WcgSupported && h.WcgEnabled)
                        HdrService.SetWcgEnabled(h.AdapterIdLow, h.AdapterIdHigh, h.TargetId, false);
            }
            catch (Exception ex) { Logger.Error(ex, "FixWcgAsync"); }
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
