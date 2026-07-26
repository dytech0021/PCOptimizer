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

            // 1) HDR off, com o vídeo ainda estável — guarda EM QUAIS telas estava
            //    ligado para religar exatamente nelas na saída.
            List<HdrInfo> hdrOn;
            try { hdrOn = HdrService.GetAllHdrInfo().Where(h => h.IsEnabled).ToList(); }
            catch { hdrOn = new List<HdrInfo>(); }

            if (keepHdr)
            {
                s.RemotePrevHdr = false;
                s.RemoteHdrPositions = "";
                if (hdrOn.Count > 0) steps.Add("• HDR mantido");
            }
            else
            {
                s.RemotePrevHdr = hdrOn.Count > 0;
                s.RemoteHdrPositions = string.Join(";", hdrOn.Select(h => $"{h.SourceX},{h.SourceY}"));
                if (hdrOn.Count > 0)
                {
                    bool off = await SetHdrVerifiedAsync(enable: false, h => true);
                    steps.Add(off ? "✅ HDR off" : "⚠ HDR não desligou");
                    await Task.Delay(1200);
                }
            }

            // 2) ACM/WCG off — SÓ AGORA, depois do HDR. Ao desligar o HDR com o ACM
            //    ligado, a tela não vai para SDR: cai no modo WCG (gamut largo), e
            //    a captura remota sai supersaturada. Desligando o ACM em seguida a
            //    tela finalmente entra em SDR puro. Guarda o estado para a saída.
            List<HdrInfo> wcgOn;
            try { wcgOn = HdrService.GetAllHdrInfo().Where(h => h.WcgEnabled).ToList(); }
            catch { wcgOn = new List<HdrInfo>(); }

            s.RemoteWcgPositions = string.Join(";", wcgOn.Select(h => $"{h.SourceX},{h.SourceY}"));
            if (wcgOn.Count > 0)
            {
                foreach (var h in wcgOn)
                    HdrService.SetWcgEnabled(h.AdapterIdLow, h.AdapterIdHigh, h.TargetId, false);
                steps.Add("✅ gamut largo off");
                await Task.Delay(1200);
            }

            // 3) Só a tela principal (topologia do Win+P — layout fica memorizado)
            bool topo = await Task.Run(MonitorTopologyService.UsePrimaryOnly);
            if (topo) s.MultiMonitorDisabled = true;
            steps.Add(topo ? "✅ 1 tela" : "⚠ telas");
            await Task.Delay(2000); // a troca de topologia precisa assentar

            // 4) 1080p 16:9 (guarda a nativa nas configurações para reversão)
            bool res = await Task.Run(DisplayResolutionService.ApplyRemote1080);
            steps.Add(res ? "✅ 1080p" : "⚠ resolução");

            s.RemoteModeActive = true;
            SettingsService.Save();
            return string.Join(" · ", steps);
        }

        /// <summary>Reverte tudo: resolução → telas → HDR → ACM → filtros.</summary>
        public static async Task<string> ExitAsync()
        {
            var s = SettingsService.Current;
            var steps = new List<string>();

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
