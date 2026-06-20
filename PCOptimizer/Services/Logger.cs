using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Log de diagnóstico gravado em arquivo, oculto no uso normal.
    /// Serve para identificar a causa de falhas que antes eram engolidas por
    /// blocos catch vazios (auto-login, Wake-on-LAN, updater, luz noturna…).
    ///
    /// Local: %LOCALAPPDATA%\PCOptimizer\logs\log-AAAA-MM-DD.txt
    /// Cada dia gera um arquivo; mantém os últimos 7 dias (rotação automática).
    /// Thread-safe e nunca lança exceção — log nunca pode quebrar o app.
    /// </summary>
    public static class Logger
    {
        private const int RetentionDays = 7;

        private static readonly object Gate = new();

        private static readonly string LogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCOptimizer", "logs");

        private static bool _initialized;

        public static string LogDirectory => LogDir;

        /// <summary>Caminho do arquivo de log do dia atual.</summary>
        public static string CurrentLogFile =>
            Path.Combine(LogDir, $"log-{DateTime.Now:yyyy-MM-dd}.txt");

        /// <summary>
        /// Inicializa o log: cria a pasta, faz a rotação e escreve um cabeçalho
        /// de sessão com a versão e o ambiente. Chame uma vez no startup.
        /// </summary>
        public static void Init()
        {
            lock (Gate)
            {
                if (_initialized) return;
                _initialized = true;
                try
                {
                    Directory.CreateDirectory(LogDir);
                    Cleanup();

                    var v = typeof(Logger).Assembly.GetName().Version?.ToString(3) ?? "?";
                    var sb = new StringBuilder();
                    sb.AppendLine();
                    sb.AppendLine("════════════════════════════════════════════════════════");
                    sb.AppendLine($"  Sessão iniciada — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    sb.AppendLine($"  PC Optimizer v{v}");
                    sb.AppendLine($"  Windows: {Environment.OSVersion.Version}  |  x64: {Environment.Is64BitOperatingSystem}");
                    sb.AppendLine($"  Usuário: {Environment.UserName}  |  Máquina: {Environment.MachineName}");
                    sb.AppendLine("════════════════════════════════════════════════════════");
                    WriteRaw(sb.ToString());
                }
                catch { /* log nunca quebra o app */ }
            }
        }

        /// <summary>Registra uma mensagem informativa.</summary>
        public static void Info(string message,
            [CallerMemberName] string caller = "",
            [CallerFilePath] string file = "")
            => Write("INFO ", message, caller, file);

        /// <summary>Registra um aviso.</summary>
        public static void Warn(string message,
            [CallerMemberName] string caller = "",
            [CallerFilePath] string file = "")
            => Write("WARN ", message, caller, file);

        /// <summary>Registra um erro com mensagem livre.</summary>
        public static void Error(string message,
            [CallerMemberName] string caller = "",
            [CallerFilePath] string file = "")
            => Write("ERROR", message, caller, file);

        /// <summary>
        /// Registra uma exceção (mensagem, tipo e stack trace). É o método
        /// principal para substituir os 'catch { }' mudos das funções críticas.
        /// </summary>
        public static void Error(Exception ex, string? context = null,
            [CallerMemberName] string caller = "",
            [CallerFilePath] string file = "")
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(context)) sb.Append(context).Append(" — ");
            sb.Append(ex.GetType().Name).Append(": ").Append(ex.Message);
            if (ex.InnerException != null)
                sb.Append(" | inner: ").Append(ex.InnerException.GetType().Name)
                  .Append(": ").Append(ex.InnerException.Message);
            sb.AppendLine();
            sb.Append("        stack: ").Append(ex.StackTrace?.Trim().Replace("\n", "\n        "));
            Write("ERROR", sb.ToString(), caller, file);
        }

        private static void Write(string level, string message, string caller, string file)
        {
            try
            {
                if (!_initialized) Init();
                string src = Path.GetFileNameWithoutExtension(file);
                string line = $"[{DateTime.Now:HH:mm:ss}] {level} {src}.{caller}() — {message}";
                lock (Gate) WriteRaw(line + Environment.NewLine);
            }
            catch { /* log nunca quebra o app */ }
        }

        private static void WriteRaw(string text)
        {
            try { File.AppendAllText(CurrentLogFile, text, Encoding.UTF8); }
            catch { }
        }

        /// <summary>Remove arquivos de log mais antigos que RetentionDays.</summary>
        private static void Cleanup()
        {
            try
            {
                var cutoff = DateTime.Now.AddDays(-RetentionDays);
                foreach (var f in Directory.GetFiles(LogDir, "log-*.txt"))
                {
                    try { if (File.GetLastWriteTime(f) < cutoff) File.Delete(f); }
                    catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// Devolve o conteúdo recente do log (todos os arquivos, mais novos por
        /// último), limitado aos últimos maxChars caracteres — para o botão
        /// "copiar log" colar no chat sem estourar o tamanho.
        /// </summary>
        public static string GetRecentLog(int maxChars = 40_000)
        {
            try
            {
                if (!Directory.Exists(LogDir)) return "(nenhum log gerado ainda)";
                var files = Directory.GetFiles(LogDir, "log-*.txt")
                    .OrderBy(File.GetLastWriteTime)
                    .ToArray();
                if (files.Length == 0) return "(nenhum log gerado ainda)";

                var sb = new StringBuilder();
                foreach (var f in files)
                {
                    try { sb.AppendLine(File.ReadAllText(f)); } catch { }
                }
                string all = sb.ToString();
                if (all.Length > maxChars)
                    all = "…(início cortado)…\n" + all[^maxChars..];
                return all;
            }
            catch (Exception ex)
            {
                return "(falha ao ler o log: " + ex.Message + ")";
            }
        }
    }
}
