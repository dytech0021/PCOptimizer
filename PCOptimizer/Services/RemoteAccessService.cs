using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Modo Acesso Remoto: um clique prepara o PC para ser acessado de uma tela
    /// 1080p 16:9 comum (AnyDesk etc.) — desativa as outras telas (fica só a
    /// principal), desliga o HDR e muda a resolução para 1920×1080. O mesmo
    /// botão reverte TUDO na ordem inversa, restaurando o estado guardado
    /// (inclusive se o HDR estava ligado). Sobrevive a reinício do PC/app.
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

            // 1) Só a tela principal (topologia do Win+P — layout fica memorizado)
            bool topo = await Task.Run(MonitorTopologyService.UsePrimaryOnly);
            if (topo) s.MultiMonitorDisabled = true;
            steps.Add(topo ? "✅ 1 tela" : "⚠ telas");
            await Task.Delay(2000); // a troca de topologia precisa assentar

            // 2) HDR off na tela que sobrou (em HDR o acesso remoto fica lavado/
            //    escuro). Guarda se estava ligado para religar na saída.
            bool hadHdr = false;
            try
            {
                foreach (var h in HdrService.GetAllHdrInfo())
                    if (h.IsEnabled)
                    {
                        hadHdr = true;
                        HdrService.SetHdrEnabled(h.AdapterIdLow, h.AdapterIdHigh, h.TargetId, false);
                    }
            }
            catch (Exception ex) { Logger.Error(ex, "RemoteAccess.HdrOff"); }
            s.RemotePrevHdr = hadHdr;
            if (hadHdr) { steps.Add("✅ HDR off"); await Task.Delay(1200); }

            // 3) 1080p 16:9 (guarda a nativa nas configurações para reversão)
            bool res = await Task.Run(DisplayResolutionService.ApplyRemote1080);
            steps.Add(res ? "✅ 1080p" : "⚠ resolução");

            s.RemoteModeActive = true;
            SettingsService.Save();
            return string.Join(" · ", steps);
        }

        /// <summary>Reverte tudo, na ordem inversa da ativação.</summary>
        public static async Task<string> ExitAsync()
        {
            var s = SettingsService.Current;
            var steps = new List<string>();

            // 1) Resolução nativa
            if (s.RemoteResActive)
            {
                bool res = await Task.Run(DisplayResolutionService.RestoreNative);
                steps.Add(res ? "✅ resolução nativa" : "⚠ resolução");
                await Task.Delay(1200);
            }

            // 2) HDR de volta, se estava ligado — religa na tela principal
            //    (posição 0,0 do desktop virtual, a mesma onde desligamos)
            if (s.RemotePrevHdr)
            {
                bool hdrOk = false;
                try
                {
                    foreach (var h in HdrService.GetAllHdrInfo())
                        if (h.SourceX == 0 && h.SourceY == 0 && h.IsSupported && !h.IsEnabled)
                            hdrOk |= HdrService.SetHdrEnabled(
                                h.AdapterIdLow, h.AdapterIdHigh, h.TargetId, true);
                }
                catch (Exception ex) { Logger.Error(ex, "RemoteAccess.HdrOn"); }
                steps.Add(hdrOk ? "✅ HDR on" : "⚠ HDR");
                s.RemotePrevHdr = false;
                await Task.Delay(1200);
            }

            // 3) Todas as telas de volta (layout restaurado pelo Windows)
            bool topo = await Task.Run(MonitorTopologyService.ExtendAll);
            if (topo) s.MultiMonitorDisabled = false;
            steps.Add(topo ? "✅ todas as telas" : "⚠ telas: Win+P → Estender");

            s.RemoteModeActive = false;
            SettingsService.Save();
            return string.Join(" · ", steps);
        }
    }
}
