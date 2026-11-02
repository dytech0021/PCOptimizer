using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PCOptimizer.Services
{
    /// <summary>Um código DDC/CI e os valores dele com o HDR do menu ligado e desligado.</summary>
    public readonly record struct HdrDdcCode(byte Code, uint OnValue, uint OffValue);

    /// <summary>
    /// Regras do "HDR do monitor" (o do menu/OSD), sem Windows. Cada fabricante
    /// guarda o HDR do menu num código DDC/CI diferente — nenhum padrão define —,
    /// então o app aprende: lê os códigos com o HDR do menu ligado, de novo com
    /// ele desligado, e o que mudou é o HDR. Separado para ser coberto por teste.
    /// </summary>
    public static class MonitorHdrDdcPolicy
    {
        /// <summary>
        /// Nunca lidos nem escritos: comandos (reset, degauss), entrada de vídeo,
        /// energia, idioma do menu, contadores e campos só de informação — mudar
        /// algum deles por engano apagaria ou desligaria o monitor.
        /// </summary>
        private static readonly HashSet<byte> Excluded = new()
        {
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A,   // comandos e resets
            0x10, 0x12,             // brilho e contraste: o app controla em separado
            0x52,                   // código ativo (muda sozinho)
            0x60,                   // entrada de vídeo
            0x62, 0x8D, 0x8F, 0x91, // áudio
            0xAC, 0xAE,             // frequências do sinal
            0xB2, 0xB6,             // tipo do painel
            0xC0,                   // horas de uso (muda sozinho)
            0xC6, 0xC8, 0xC9, 0xCA, 0xCC,   // chave de app, controlador, firmware, menu, idioma
            0xD6,                   // energia: escrever pode DESLIGAR o monitor
            0xDF,                   // versão do MCCS
        };

        /// <summary>
        /// Ajustes de imagem que um modo HDR do menu costuma arrastar junto
        /// (predefinição de cor, ganhos, nitidez). Só são usados quando nada mais
        /// mudou — senão desligar o HDR sobrescreveria a cor que o usuário ajustou.
        /// </summary>
        private static readonly HashSet<byte> PictureCodes = new()
        {
            0x0B, 0x0C, 0x14, 0x16, 0x18, 0x1A, 0x6C, 0x6E, 0x70, 0x87, 0x8A,
        };

        /// <summary>
        /// Quando o monitor não informa a lista de códigos: o modo de exibição
        /// (0xDC), os de imagem e a faixa reservada aos fabricantes (0xE0–0xFF),
        /// onde os monitores baratos costumam pôr o HDR.
        /// </summary>
        public static IReadOnlyList<byte> FallbackCodes { get; } =
            new byte[] { 0x0C, 0x14, 0x16, 0x18, 0x1A, 0x87, 0x8A, 0xDC }
                .Concat(Enumerable.Range(0xE0, 0x20).Select(i => (byte)i))
                .ToArray();

        /// <summary>No máximo tantos códigos por monitor — mais que isso não é o HDR.</summary>
        public const int MaxCodes = 4;

        public static bool IsExcluded(byte code) => Excluded.Contains(code);

        /// <summary>
        /// Códigos do campo vcp(...) da string de capacidades, sem as listas de
        /// valores entre parênteses: "vcp(10 14(05 08) DC)" → 10, 14, DC.
        /// </summary>
        public static List<byte> ParseVcpCodes(string? caps)
        {
            var list = new List<byte>();
            if (string.IsNullOrEmpty(caps)) return list;

            var m = Regex.Match(caps, @"vcp\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!m.Success) return list;

            int depth = 1;
            var token = new StringBuilder();
            void Flush()
            {
                if (token.Length == 0) return;
                string t = token.ToString();
                token.Clear();
                // Alguns monitores escrevem sem espaço: "021012" → 02, 10, 12.
                if (t.Length % 2 != 0) return;
                for (int i = 0; i < t.Length; i += 2)
                    if (byte.TryParse(t.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b)
                        && !list.Contains(b))
                        list.Add(b);
            }

            for (int i = m.Index + m.Length; i < caps.Length && depth > 0; i++)
            {
                char c = caps[i];
                if (c == '(') { if (depth == 1) Flush(); depth++; }
                else if (c == ')') { if (depth == 1) Flush(); depth--; }
                else if (depth == 1)
                {
                    if (Uri.IsHexDigit(c)) token.Append(c);
                    else Flush();
                }
            }
            Flush();
            return list;
        }

        /// <summary>O que vale a pena ler deste monitor.</summary>
        public static List<byte> CodesToRead(string? caps)
        {
            var parsed = ParseVcpCodes(caps);
            var source = parsed.Count > 0 ? parsed : FallbackCodes.ToList();
            return source.Where(c => !IsExcluded(c)).Distinct().ToList();
        }

        /// <summary>
        /// Códigos que oscilaram entre duas leituras seguidas, sem ninguém mexer
        /// no monitor — barulho, não HDR.
        /// </summary>
        public static HashSet<byte> Unstable(IReadOnlyDictionary<byte, uint> first, IReadOnlyDictionary<byte, uint> second)
        {
            var set = new HashSet<byte>();
            foreach (var (code, v) in first)
                if (!second.TryGetValue(code, out uint w) || w != v) set.Add(code);
            foreach (var code in second.Keys)
                if (!first.ContainsKey(code)) set.Add(code);
            return set;
        }

        /// <summary>
        /// O que mudou entre "HDR do menu ligado" e "desligado". Preferência para
        /// os códigos de modo (0xDC e os do fabricante); os de imagem só entram se
        /// forem a única diferença. Vazio = o monitor não expõe o HDR pelo cabo.
        /// </summary>
        public static List<HdrDdcCode> Learn(IReadOnlyDictionary<byte, uint> hdrOn,
                                             IReadOnlyDictionary<byte, uint> hdrOff,
                                             IReadOnlySet<byte>? unstable = null)
        {
            var diff = new List<HdrDdcCode>();
            foreach (var (code, on) in hdrOn.OrderBy(kv => kv.Key))
            {
                if (IsExcluded(code) || unstable?.Contains(code) == true) continue;
                if (hdrOff.TryGetValue(code, out uint off) && off != on)
                    diff.Add(new HdrDdcCode(code, on, off));
            }

            var mode = diff.Where(d => !PictureCodes.Contains(d.Code)).ToList();
            var chosen = mode.Count > 0 ? mode : diff;
            return chosen.Count is > 0 and <= MaxCodes ? chosen : new List<HdrDdcCode>();
        }

        // ── Gravação nas configurações ─────────────────────────────────────────

        /// <summary>"DC:5>0;F0:1>0" — código em hexa, valor ligado > valor desligado.</summary>
        public static string Serialize(IEnumerable<HdrDdcCode> codes) =>
            string.Join(";", codes.Select(c => string.Create(CultureInfo.InvariantCulture,
                $"{c.Code:X2}:{c.OnValue}>{c.OffValue}")));

        public static List<HdrDdcCode> Parse(string? value)
        {
            var list = new List<HdrDdcCode>();
            if (string.IsNullOrWhiteSpace(value)) return list;
            foreach (string part in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var m = Regex.Match(part.Trim(), @"^([0-9A-Fa-f]{2}):(\d{1,10})>(\d{1,10})$");
                if (!m.Success) return new List<HdrDdcCode>();   // corrompido: melhor não escrever nada
                byte code = byte.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (IsExcluded(code)
                    || !uint.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out uint on)
                    || !uint.TryParse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out uint off))
                    return new List<HdrDdcCode>();
                list.Add(new HdrDdcCode(code, on, off));
            }
            return list.Count <= MaxCodes ? list : new List<HdrDdcCode>();
        }

        /// <summary>
        /// Chave salva deste monitor. Aceita a mesma base antes do "#" e, por
        /// último, o mesmo modelo — mas só se houver UMA chave assim: com dois
        /// monitores iguais, um não pode usar o aprendizado do outro.
        /// </summary>
        public static string? FindKey(IEnumerable<string> savedKeys, string? hardwareId)
        {
            if (string.IsNullOrEmpty(hardwareId)) return null;
            var keys = savedKeys.ToList();
            foreach (var k in keys)
                if (k.Equals(hardwareId, StringComparison.OrdinalIgnoreCase)) return k;

            string baseId = hardwareId.Split('#')[0];
            foreach (var k in keys)
                if (k.Split('#')[0].Equals(baseId, StringComparison.OrdinalIgnoreCase)) return k;

            string wanted = MonitorIdentity.SavedStateBase(hardwareId);
            if (wanted.Length == 0) return null;
            string? only = null;
            foreach (var k in keys)
                if (MonitorIdentity.SavedStateBase(k) == wanted)
                {
                    if (only != null) return null;
                    only = k;
                }
            return only;
        }

        /// <summary>Texto curto do que foi aprendido: "0xDC (5 → 0)".</summary>
        public static string Describe(IEnumerable<HdrDdcCode> codes) =>
            string.Join(", ", codes.Select(c => string.Create(CultureInfo.InvariantCulture,
                $"0x{c.Code:X2} ({c.OnValue} → {c.OffValue})")));
    }
}
