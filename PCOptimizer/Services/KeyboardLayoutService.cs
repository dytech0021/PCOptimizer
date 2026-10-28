using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Troca e adição de teclados do Windows. A lista vem das próprias
    /// configurações de idioma do usuário (Get-WinUserLanguageList), a mesma que
    /// aparece em Configurações → Hora e idioma; adicionar e remover usam o
    /// cmdlet oficial. Trocar é instantâneo e vale para todas as janelas.
    /// </summary>
    public static class KeyboardLayoutService
    {
        [DllImport("user32.dll")]
        private static extern int GetKeyboardLayoutList(int nBuff, [Out] IntPtr[]? lpList);

        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint idThread);

        [DllImport("user32.dll")]
        private static extern IntPtr ActivateKeyboardLayout(IntPtr hkl, uint flags);

        [DllImport("user32.dll", EntryPoint = "LoadKeyboardLayoutW", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadKeyboardLayout(string pwszKLID, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint action, uint param, ref IntPtr pvParam, uint winIni);

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        private static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, uint cchOutBuf, IntPtr ppvReserved);

        private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
        private const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;
        private const uint KLF_ACTIVATE = 0x00000001;
        private const uint KLF_SETFORPROCESS = 0x00000100;
        private const uint SPI_SETDEFAULTINPUTLANG = 0x005A;
        private const uint SPIF_SENDCHANGE = 0x0002;

        private const string LayoutsKey = @"SYSTEM\CurrentControlSet\Control\Keyboard Layouts";

        private static readonly object Sync = new();
        private static List<KeyboardEntry> _installed = new();

        /// <summary>Teclados instalados (cópia da última leitura).</summary>
        public static IReadOnlyList<KeyboardEntry> Installed
        {
            get { lock (Sync) return _installed.ToList(); }
        }

        public static event Action? Changed;

        private static void Notify()
        {
            try { Changed?.Invoke(); }
            catch (Exception ex) { Logger.Error(ex, "Keyboard.Changed"); }
        }

        // ── Leitura ───────────────────────────────────────────────────────────

        /// <summary>Relê a lista do Windows (≈1 s: roda o PowerShell). Chame fora da thread de UI.</summary>
        public static IReadOnlyList<KeyboardEntry> Refresh()
        {
            var (code, output) = RunPowerShell(KeyboardLayoutPolicy.ListScript, 20000);
            var list = new List<KeyboardEntry>();
            if (code == 0 && !string.IsNullOrWhiteSpace(output))
            {
                try
                {
                    using var doc = JsonDocument.Parse(output.Trim());
                    foreach (var e in doc.RootElement.EnumerateArray())
                    {
                        string tag = e.GetProperty("Tag").GetString() ?? "";
                        string name = e.GetProperty("Name").GetString() ?? tag;
                        string tip = e.GetProperty("Tip").GetString() ?? "";
                        if (!KeyboardLayoutPolicy.IsValidTip(tip)) continue;
                        list.Add(new KeyboardEntry(tag, name, tip, LayoutNameFor(tip)));
                    }
                }
                catch (Exception ex) { Logger.Error(ex, "Keyboard.Refresh/parse"); }
            }
            else Logger.Warn($"Teclado: não consegui ler os idiomas (código {code})");

            lock (Sync) _installed = list;
            Notify();
            return list;
        }

        /// <summary>Tip do teclado em uso agora (na thread que chamar).</summary>
        public static string? CurrentTip()
        {
            try { return KeyboardLayoutPolicy.TipFor(GetKeyboardLayout(0).ToInt64(), KlidForLayoutId); }
            catch { return null; }
        }

        // ── Trocar ────────────────────────────────────────────────────────────

        /// <summary>
        /// Troca o teclado em todas as janelas abertas e nas que forem abertas
        /// depois — o mesmo efeito do Win + Espaço, sem precisar ciclar a lista.
        /// </summary>
        public static bool Switch(KeyboardEntry entry)
        {
            try
            {
                long? found = KeyboardLayoutPolicy.FindHkl(entry.Tip, ActiveHkls(), KlidForLayoutId);
                IntPtr hkl;
                if (found is long h) hkl = new IntPtr(h);
                else
                {
                    // Recém-adicionado e ainda não carregado nesta sessão.
                    var parsed = KeyboardLayoutPolicy.ParseTip(entry.Tip);
                    if (parsed == null || parsed.Value.Layout.StartsWith("{")) return false;
                    hkl = LoadKeyboardLayout(parsed.Value.Layout, KLF_ACTIVATE);
                    if (hkl == IntPtr.Zero) return false;
                }

                ActivateKeyboardLayout(hkl, KLF_SETFORPROCESS);
                PostMessage(HWND_BROADCAST, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, hkl);
                // Padrão para janelas novas — sem gravar: não muda a configuração do Windows.
                SystemParametersInfo(SPI_SETDEFAULTINPUTLANG, 0, ref hkl, SPIF_SENDCHANGE);
                Logger.Info($"Teclado: trocado para {entry.DisplayName} ({entry.Tip})");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Keyboard.Switch");
                return false;
            }
        }

        private static IEnumerable<long> ActiveHkls()
        {
            int n = GetKeyboardLayoutList(0, null);
            if (n <= 0) return Array.Empty<long>();
            var buf = new IntPtr[n];
            n = GetKeyboardLayoutList(n, buf);
            return buf.Take(Math.Max(0, n)).Select(p => p.ToInt64()).ToList();
        }

        // ── Adicionar / remover ───────────────────────────────────────────────

        /// <summary>Adiciona um teclado nas configurações do Windows. Devolve o texto para a tela.</summary>
        public static string Add(KeyboardOption option)
        {
            try
            {
                var (code, _) = RunPowerShell(KeyboardLayoutPolicy.BuildAddScript(option.LanguageTag, option.Tip), 60000);
                Refresh();
                bool ok = Installed.Any(e => KeyboardLayoutPolicy.SameTip(e.Tip, option.Tip));
                Logger.Info($"Teclado: adicionar {option.Label} → código {code}, presente={ok}");
                return ok ? $"{option.Label} adicionado" : "O Windows não aceitou esse teclado — veja o log";
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Keyboard.Add");
                return "Não consegui adicionar o teclado";
            }
        }

        /// <summary>Remove um teclado. Nunca deixa o Windows sem nenhum.</summary>
        public static string Remove(KeyboardEntry entry)
        {
            try
            {
                if (!KeyboardLayoutPolicy.CanRemove(Installed, entry.Tip))
                    return "É preciso manter pelo menos um teclado";
                var (code, _) = RunPowerShell(KeyboardLayoutPolicy.BuildRemoveScript(entry.Tip), 60000);
                Refresh();
                bool gone = !Installed.Any(e => KeyboardLayoutPolicy.SameTip(e.Tip, entry.Tip));
                Logger.Info($"Teclado: remover {entry.DisplayName} → código {code}, removido={gone}");
                return gone ? $"{entry.DisplayName} removido" : "O Windows não removeu esse teclado — veja o log";
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Keyboard.Remove");
                return "Não consegui remover o teclado";
            }
        }

        // ── Nomes dos layouts (registro) ──────────────────────────────────────

        private static Dictionary<int, string>? _layoutIds;

        /// <summary>"Layout Id" (layouts alternativos, HKL 0xFxxx) → KLID.</summary>
        private static string? KlidForLayoutId(int layoutId)
        {
            if (_layoutIds == null)
            {
                var map = new Dictionary<int, string>();
                try
                {
                    using var root = Registry.LocalMachine.OpenSubKey(LayoutsKey);
                    if (root != null)
                        foreach (string klid in root.GetSubKeyNames())
                        {
                            using var k = root.OpenSubKey(klid);
                            if (k?.GetValue("Layout Id") is string id
                                && int.TryParse(id, System.Globalization.NumberStyles.HexNumber, null, out int n))
                                map[n] = klid;
                        }
                }
                catch (Exception ex) { Logger.Error(ex, "Keyboard.LayoutIds"); }
                _layoutIds = map;
            }
            return _layoutIds.TryGetValue(layoutId, out var v) ? v : null;
        }

        /// <summary>Nome do layout no idioma do Windows ("Português (Brasil ABNT2)").</summary>
        private static string LayoutNameFor(string tip)
        {
            var parsed = KeyboardLayoutPolicy.ParseTip(tip);
            if (parsed == null || parsed.Value.Layout.StartsWith("{")) return "";
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey($@"{LayoutsKey}\{parsed.Value.Layout}");
                if (k == null) return "";
                if (k.GetValue("Layout Display Name") is string indirect && indirect.StartsWith("@"))
                {
                    var sb = new StringBuilder(256);
                    if (SHLoadIndirectString(indirect, sb, (uint)sb.Capacity, IntPtr.Zero) == 0 && sb.Length > 0)
                        return sb.ToString();
                }
                return k.GetValue("Layout Text") as string ?? "";
            }
            catch { return ""; }
        }

        // ── PowerShell ────────────────────────────────────────────────────────

        /// <summary>
        /// Roda um script pelo PowerShell do Windows (codificado, sem problema de
        /// aspas) e devolve código de saída e saída padrão.
        /// </summary>
        private static (int Code, string Output) RunPowerShell(string script, int timeoutMs)
        {
            try
            {
                string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop'\n" + script));
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                using var p = Process.Start(psi);
                if (p == null) return (-1, "");
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(true); } catch { }
                    return (-2, "");
                }
                string err = stderr.Result;
                if (!string.IsNullOrWhiteSpace(err)) Logger.Warn("Teclado (PowerShell): " + err.Trim());
                return (p.ExitCode, stdout.Result);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Keyboard.RunPowerShell");
                return (-1, "");
            }
        }
    }
}
