using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PCOptimizer.Services
{
    /// <summary>
    /// HDR do menu do monitor (OSD) por DDC/CI: aprende qual código o monitor
    /// usa e liga/desliga junto com o HDR do Windows. Tudo aqui fala com o
    /// barramento do monitor (lento, centenas de ms) — chame fora da thread de UI.
    /// </summary>
    public static class MonitorHdrDdcService
    {
        private static readonly object Bus = new();

        /// <summary>O que foi aprendido para este monitor (vazio = nada).</summary>
        public static List<HdrDdcCode> Get(string hardwareId)
        {
            var d = SettingsService.Current.MonitorHdrDdc;
            string? key = MonitorHdrDdcPolicy.FindKey(d.Keys, hardwareId);
            return key != null ? MonitorHdrDdcPolicy.Parse(d[key]) : new List<HdrDdcCode>();
        }

        public static bool IsConfigured(string hardwareId) => Get(hardwareId).Count > 0;

        public static void Save(string hardwareId, IReadOnlyCollection<HdrDdcCode> codes)
        {
            var d = SettingsService.Current.MonitorHdrDdc;
            string? old = MonitorHdrDdcPolicy.FindKey(d.Keys, hardwareId);
            if (old != null) d.Remove(old);
            d[hardwareId] = MonitorHdrDdcPolicy.Serialize(codes);
            SettingsService.Save();
            Logger.Info($"HDR do monitor: aprendido {hardwareId} → {MonitorHdrDdcPolicy.Describe(codes)}");
        }

        public static void Forget(string hardwareId)
        {
            var d = SettingsService.Current.MonitorHdrDdc;
            string? key = MonitorHdrDdcPolicy.FindKey(d.Keys, hardwareId);
            if (key == null) return;
            d.Remove(key);
            SettingsService.Save();
            Logger.Info($"HDR do monitor: esquecido {key}");
        }

        /// <summary>Códigos que valem ser lidos neste monitor.</summary>
        public static List<byte> CodesFor(int monitorIndex) =>
            MonitorHdrDdcPolicy.CodesToRead(MonitorService.GetCapabilitiesForIndex(monitorIndex));

        /// <summary>Foto dos códigos: o que o monitor respondeu agora.</summary>
        public static Dictionary<byte, uint> Snapshot(int monitorIndex, IReadOnlyList<byte> codes)
        {
            var map = new Dictionary<byte, uint>();
            lock (Bus)
                foreach (byte c in codes)
                {
                    if (MonitorService.TryGetVcpForIndex(monitorIndex, c, out uint v)) map[c] = v;
                    Thread.Sleep(40);   // DDC/CI pede intervalo entre comandos
                }
            return map;
        }

        /// <summary>
        /// Põe o HDR do menu no estado pedido. Escreve, confere lendo de volta e
        /// insiste: logo após o Windows trocar o HDR o monitor ressincroniza a
        /// imagem e pode ignorar o primeiro comando.
        /// </summary>
        public static bool Apply(int monitorIndex, IReadOnlyCollection<HdrDdcCode> codes, bool hdrOn)
        {
            if (codes.Count == 0) return false;
            bool all = true;
            lock (Bus)
                foreach (var c in codes)
                {
                    uint want = hdrOn ? c.OnValue : c.OffValue;
                    bool ok = false;
                    for (int attempt = 0; attempt < 3 && !ok; attempt++)
                    {
                        if (attempt > 0) Thread.Sleep(1000);
                        if (!MonitorService.SetVcpForIndex(monitorIndex, c.Code, want)) continue;
                        Thread.Sleep(300);
                        // Monitor que não devolve a leitura: o envio aceito basta.
                        ok = !MonitorService.TryGetVcpForIndex(monitorIndex, c.Code, out uint now) || now == want;
                    }
                    Logger.Info($"HDR do monitor: 0x{c.Code:X2} = {want} ({(ok ? "ok" : "falhou")})");
                    all &= ok;
                }
            return all;
        }

        /// <summary>Acha o índice DDC atual do monitor pelo id (a ordem pode mudar).</summary>
        public static int ResolveIndex(string hardwareId, int fallback)
        {
            try
            {
                var e = MonitorService.GetMonitorEntries()
                    .FirstOrDefault(x => x.HardwareId.Equals(hardwareId, StringComparison.OrdinalIgnoreCase)
                                         && !x.IsWmi && !x.IsSoftware);
                return e?.Index ?? fallback;
            }
            catch { return fallback; }
        }
    }
}
