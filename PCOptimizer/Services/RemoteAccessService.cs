using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Modo Acesso Remoto: um clique prepara o PC para ser acessado de uma tela
    /// 1080p 16:9 comum (AnyDesk etc.) — desliga o HDR, desativa as outras telas
    /// (fica só a principal) e muda a resolução para 1920×1080. O mesmo botão
    /// reverte TUDO, restaurando o estado guardado. Sobrevive a reinício.
    ///
    /// Ordem importa: o HDR é alternado LONGE das trocas de topologia/resolução
    /// (logo após uma troca o vídeo ainda está se reconfigurando e o comando de
    /// HDR falha ou é ignorado pelo driver), e toda alternância de HDR é
    /// VERIFICADA e repetida até pegar.
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

        /// <summary>Ativa o modo. Retorna um resumo curto do que foi feito.</summary>
        public static async Task<string> EnterAsync()
        {
            var s = SettingsService.Current;
            var steps = new List<string>();

            // 0) Neutraliza os FILTROS DE COR. Com HDR ligado o Windows os ignora;
            //    ao desligar o HDR eles passam a valer e, em muitos sistemas, são
            //    aplicados NA COMPOSIÇÃO da imagem — o AnyDesk captura junto e o
            //    acesso remoto fica saturado/escuro. As configurações do usuário
            //    ficam intactas; só o efeito é suspenso até sair do modo.
            bool winNl = NightLightService.GetWindowsNightLightEnabled();
            s.RemotePrevWinNightLight = winNl;
            if (winNl) NightLightService.SetWindowsNightLight(false);
            NightLightService.Reset();  // overlay do app (se ativo) — volta na saída
            GammaRampService.Reset();   // gama/temperatura/RGB neutros
            bool hadFilters = winNl || s.NightLightEnabled ||
                !GammaRampService.IsDefault(s.GammaValue, s.ColorTempK, s.GainR, s.GainG, s.GainB);
            if (hadFilters) { steps.Add("✅ filtros de cor suspensos"); await Task.Delay(500); }

            // 1) HDR off PRIMEIRO, com o vídeo ainda estável — e guarda EM QUAIS
            //    telas estava ligado (posição no desktop virtual) para religar
            //    exatamente nelas na saída.
            List<HdrInfo> hdrOn;
            try { hdrOn = HdrService.GetAllHdrInfo().Where(h => h.IsEnabled).ToList(); }
            catch { hdrOn = new List<HdrInfo>(); }

            s.RemotePrevHdr = hdrOn.Count > 0;
            s.RemoteHdrPositions = string.Join(";", hdrOn.Select(h => $"{h.SourceX},{h.SourceY}"));

            if (hdrOn.Count > 0)
            {
                bool off = await SetHdrVerifiedAsync(enable: false, h => true);
                steps.Add(off ? "✅ HDR off" : "⚠ HDR não desligou");
                await Task.Delay(1000);
            }

            // 2) Só a tela principal (topologia do Win+P — layout fica memorizado)
            bool topo = await Task.Run(MonitorTopologyService.UsePrimaryOnly);
            if (topo) s.MultiMonitorDisabled = true;
            steps.Add(topo ? "✅ 1 tela" : "⚠ telas");
            await Task.Delay(2000); // a troca de topologia precisa assentar

            // 3) 1080p 16:9 (guarda a nativa nas configurações para reversão)
            bool res = await Task.Run(DisplayResolutionService.ApplyRemote1080);
            steps.Add(res ? "✅ 1080p" : "⚠ resolução");

            s.RemoteModeActive = true;
            SettingsService.Save();
            return string.Join(" · ", steps);
        }

        /// <summary>Reverte tudo: resolução → telas → HDR (verificado).</summary>
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

            // 2) Todas as telas de volta (layout restaurado pelo Windows) —
            //    ANTES do HDR: as posições das telas secundárias só existem
            //    com a topologia estendida.
            bool topo = await Task.Run(MonitorTopologyService.ExtendAll);
            if (topo) s.MultiMonitorDisabled = false;
            steps.Add(topo ? "✅ todas as telas" : "⚠ telas: Win+P → Estender");
            await Task.Delay(2500);

            // 3) HDR de volta nas telas onde estava ligado. O verificador repete
            //    a ordem até o driver aceitar (após a troca de topologia o vídeo
            //    leva alguns segundos para voltar a responder).
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
            }

            // 4) Filtros de cor de volta, exatamente como estavam
            bool restored = false;
            if (s.RemotePrevWinNightLight)
            {
                NightLightService.SetWindowsNightLight(true);
                s.RemotePrevWinNightLight = false;
                restored = true;
            }
            if (s.NightLightEnabled)
            {
                NightLightService.SetIntensity(s.NightLightIntensity);
                restored = true;
            }
            if (!GammaRampService.IsDefault(s.GammaValue, s.ColorTempK, s.GainR, s.GainG, s.GainB))
            {
                GammaRampService.RestoreFromSettings();
                restored = true;
            }
            if (restored) steps.Add("✅ filtros restaurados");

            s.RemoteModeActive = false;
            SettingsService.Save();
            return string.Join(" · ", steps);
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

            // Última leitura: pegou ou não?
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
