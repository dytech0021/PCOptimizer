using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace PCOptimizer.Services
{
    /// <summary>Um teclado instalado no Windows, como aparece em Configurações → Idioma.</summary>
    public sealed record KeyboardEntry(string LanguageTag, string LanguageName, string Tip, string LayoutName)
    {
        /// <summary>Nome para a tela: "Português (Brasil) — ABNT2".</summary>
        public string DisplayName =>
            string.IsNullOrWhiteSpace(LayoutName) || LayoutName.Equals(LanguageName, StringComparison.OrdinalIgnoreCase)
                ? LanguageName
                : $"{LanguageName} — {LayoutName}";
    }

    /// <summary>Teclado que dá para adicionar com um clique.</summary>
    public sealed record KeyboardOption(string Label, string LanguageTag, string Tip);

    /// <summary>
    /// Regras do troca-teclado, sem Windows: identificar teclados, montar os
    /// comandos do PowerShell e decidir o que pode ser removido. Separado para
    /// ser coberto por teste.
    /// </summary>
    public static class KeyboardLayoutPolicy
    {
        /// <summary>
        /// Os teclados mais pedidos por quem usa PT-BR. "Tip" é o identificador
        /// do Windows: idioma:layout (ex.: Português com teclado EUA-Internacional).
        /// </summary>
        public static readonly IReadOnlyList<KeyboardOption> CommonOptions = new[]
        {
            new KeyboardOption("Português (Brasil) — ABNT2",                 "pt-BR", "0416:00000416"),
            new KeyboardOption("Português (Brasil) — EUA Internacional",     "pt-BR", "0416:00020409"),
            new KeyboardOption("Português (Brasil) — EUA (sem acentos)",     "pt-BR", "0416:00000409"),
            new KeyboardOption("Inglês (EUA) — padrão",                      "en-US", "0409:00000409"),
            new KeyboardOption("Inglês (EUA) — Internacional",               "en-US", "0409:00020409"),
            new KeyboardOption("Inglês (Reino Unido)",                       "en-GB", "0809:00000809"),
            new KeyboardOption("Português (Portugal)",                       "pt-PT", "0816:00000816"),
            new KeyboardOption("Espanhol (Espanha)",                         "es-ES", "0C0A:0000040A"),
            new KeyboardOption("Espanhol (América Latina)",                  "es-MX", "080A:0000080A"),
            new KeyboardOption("Francês (França)",                           "fr-FR", "040C:0000040C"),
            new KeyboardOption("Alemão (Alemanha)",                          "de-DE", "0407:00000407"),
            new KeyboardOption("Italiano (Itália)",                          "it-IT", "0410:00000410"),
            new KeyboardOption("Russo",                                      "ru-RU", "0419:00000419"),
        };

        private static readonly Regex TagPattern = new(@"^[A-Za-z]{2,3}(-[A-Za-z0-9]{1,8})*$", RegexOptions.CultureInvariant);
        private static readonly Regex TipPattern = new(@"^[0-9A-Fa-f]{4}:[0-9A-Fa-f{}\-]{8,90}$", RegexOptions.CultureInvariant);

        /// <summary>Só deixa chegar ao PowerShell o formato que o Windows usa.</summary>
        public static bool IsValidTag(string? tag) => !string.IsNullOrWhiteSpace(tag) && TagPattern.IsMatch(tag);
        public static bool IsValidTip(string? tip) => !string.IsNullOrWhiteSpace(tip) && TipPattern.IsMatch(tip);

        public static bool SameTip(string? a, string? b) =>
            string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>Idioma (4 dígitos) e layout (KLID ou GUIDs de IME) de um tip.</summary>
        public static (ushort LangId, string Layout)? ParseTip(string? tip)
        {
            if (!IsValidTip(tip)) return null;
            var parts = tip!.Split(':', 2);
            return ushort.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort lang)
                ? (lang, parts[1].ToUpperInvariant())
                : null;
        }

        /// <summary>
        /// KLID (nome da pasta em "Keyboard Layouts") de um HKL ativo no Windows.
        /// - palavra alta 0xFxxx: layout alternativo; o número fica em "Layout Id"
        ///   no registro (<paramref name="klidForLayoutId"/>);
        /// - palavra alta 0xExxx: IME antigo — o KLID é o próprio HKL;
        /// - caso comum: "0000" + palavra alta.
        /// </summary>
        public static string? KlidFor(long hkl, Func<int, string?> klidForLayoutId)
        {
            ushort high = (ushort)((hkl >> 16) & 0xFFFF);
            ushort low = (ushort)(hkl & 0xFFFF);
            if ((high & 0xF000) == 0xF000) return klidForLayoutId(high & 0x0FFF)?.ToUpperInvariant();
            if ((high & 0xF000) == 0xE000) return ((uint)hkl).ToString("X8", CultureInfo.InvariantCulture);
            return "0000" + (high == 0 ? low : high).ToString("X4", CultureInfo.InvariantCulture);
        }

        /// <summary>Tip do Windows ("0416:00020409") para um HKL ativo.</summary>
        public static string? TipFor(long hkl, Func<int, string?> klidForLayoutId)
        {
            string? klid = KlidFor(hkl, klidForLayoutId);
            ushort lang = (ushort)(hkl & 0xFFFF);
            return klid == null ? null : $"{lang:X4}:{klid}";
        }

        /// <summary>
        /// Qual HKL ativo corresponde a um tip. Para IMEs (tip com GUIDs) vale o
        /// idioma: o Windows ativa o IME padrão daquele idioma.
        /// </summary>
        public static long? FindHkl(string tip, IEnumerable<long> activeHkls, Func<int, string?> klidForLayoutId)
        {
            var parsed = ParseTip(tip);
            if (parsed == null) return null;
            var list = activeHkls.ToList();
            foreach (long h in list)
                if (SameTip(TipFor(h, klidForLayoutId), tip)) return h;
            if (parsed.Value.Layout.StartsWith("{"))
                foreach (long h in list)
                    if ((ushort)(h & 0xFFFF) == parsed.Value.LangId) return h;
            return null;
        }

        /// <summary>Remover deixaria o Windows sem nenhum teclado?</summary>
        public static bool CanRemove(IReadOnlyCollection<KeyboardEntry> installed, string tip) =>
            installed.Count > 1 && installed.Any(e => SameTip(e.Tip, tip));

        /// <summary>Opções da lista de adicionar que ainda não estão instaladas.</summary>
        public static IReadOnlyList<KeyboardOption> AvailableToAdd(IEnumerable<KeyboardEntry> installed)
        {
            var have = installed.Select(e => e.Tip).ToList();
            return CommonOptions.Where(o => !have.Any(t => SameTip(t, o.Tip))).ToList();
        }

        // ── PowerShell ────────────────────────────────────────────────────────

        private static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

        /// <summary>Lista os teclados do usuário em JSON (uma linha por teclado).</summary>
        public const string ListScript =
            "[Console]::OutputEncoding = [Text.Encoding]::UTF8\n" +
            "$out = foreach ($l in Get-WinUserLanguageList) {\n" +
            "  foreach ($t in $l.InputMethodTips) {\n" +
            "    [pscustomobject]@{ Tag = $l.LanguageTag; Name = $l.LocalizedName; Tip = $t }\n" +
            "  }\n" +
            "}\n" +
            "ConvertTo-Json -InputObject @($out) -Compress";

        /// <summary>
        /// Adiciona um teclado. Se o idioma ainda não existe, ele entra só com
        /// esse teclado (o Windows poria o teclado padrão do idioma junto).
        /// </summary>
        public static string BuildAddScript(string languageTag, string tip)
        {
            if (!IsValidTag(languageTag)) throw new ArgumentException("Idioma inválido", nameof(languageTag));
            if (!IsValidTip(tip)) throw new ArgumentException("Teclado inválido", nameof(tip));
            string tag = Quote(languageTag), t = Quote(tip);
            return
                "$l = Get-WinUserLanguageList\n" +
                $"$lang = $l | Where-Object {{ $_.LanguageTag -eq {tag} }} | Select-Object -First 1\n" +
                "if (-not $lang) {\n" +
                $"  $l.Add({tag})\n" +
                "  $lang = $l[$l.Count - 1]\n" +
                "  $lang.InputMethodTips.Clear()\n" +
                "}\n" +
                $"if (-not ($lang.InputMethodTips -contains {t})) {{ $lang.InputMethodTips.Add({t}) }}\n" +
                "Set-WinUserLanguageList $l -Force";
        }

        /// <summary>
        /// Remove um teclado; o idioma que ficar sem nenhum teclado sai junto.
        /// Recusa (código 3) se o Windows ficaria sem teclado.
        /// </summary>
        public static string BuildRemoveScript(string tip)
        {
            if (!IsValidTip(tip)) throw new ArgumentException("Teclado inválido", nameof(tip));
            string t = Quote(tip);
            return
                "$l = Get-WinUserLanguageList\n" +
                "foreach ($x in @($l)) {\n" +
                $"  $hit = @($x.InputMethodTips | Where-Object {{ $_ -eq {t} }})\n" +
                "  foreach ($h in $hit) { [void]$x.InputMethodTips.Remove($h) }\n" +
                "}\n" +
                "foreach ($x in @($l | Where-Object { $_.InputMethodTips.Count -eq 0 })) { [void]$l.Remove($x) }\n" +
                "if ($l.Count -eq 0) { exit 3 }\n" +
                "Set-WinUserLanguageList $l -Force";
        }
    }
}
