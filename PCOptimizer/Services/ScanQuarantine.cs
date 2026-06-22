using System;
using System.IO;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Faz backup dos itens antes de removê-los, para o usuário poder restaurar.
    /// Local: %LOCALAPPDATA%\PCOptimizer\quarantine\
    ///   - arquivos/pastas movidos para cá
    ///   - chaves de registro exportadas em .reg
    ///   - tarefas agendadas exportadas em .xml
    ///   - quarantine-log.txt com o histórico de ações
    /// </summary>
    public static class ScanQuarantine
    {
        public static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCOptimizer", "quarantine");

        private static string LogFile => Path.Combine(Dir, "quarantine-log.txt");

        public static void EnsureDir()
        {
            try { Directory.CreateDirectory(Dir); } catch { /* sem permissão: segue */ }
        }

        public static void Log(string text)
        {
            try
            {
                EnsureDir();
                File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text}{Environment.NewLine}");
            }
            catch { /* log nunca quebra a remoção */ }
        }

        /// <summary>Exporta uma chave inteira do registro para um .reg antes de mexer nela.</summary>
        public static bool BackupRegistryKey(string fullKeyPath)
        {
            try
            {
                EnsureDir();
                string safe = Sanitize(fullKeyPath);
                string dest = Unique(Path.Combine(Dir, $"reg_{safe}.reg"));
                bool ok = ProcessRunner.Run("reg.exe", $"export \"{fullKeyPath}\" \"{dest}\" /y", 15000);
                Log($"BACKUP registro {fullKeyPath} -> {dest} ({(ok ? "ok" : "falhou")})");
                return ok;
            }
            catch (Exception ex) { Logger.Error(ex, "BackupRegistryKey"); return false; }
        }

        /// <summary>Move um arquivo ou pasta para a quarentena (em vez de apagar de vez).</summary>
        public static bool MoveToQuarantine(string path)
        {
            try
            {
                EnsureDir();
                string name = Path.GetFileName(path.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(name)) name = "item";
                string dest = Unique(Path.Combine(Dir, name));

                if (Directory.Exists(path))      Directory.Move(path, dest);
                else if (File.Exists(path))      File.Move(path, dest);
                else { Log($"MOVE ignorado (não existe): {path}"); return false; }

                Log($"QUARENTENA {path} -> {dest}");
                return true;
            }
            catch (Exception ex) { Logger.Error(ex, "MoveToQuarantine: " + path); return false; }
        }

        /// <summary>Caminho dentro da quarentena para salvar um export (ex.: XML de tarefa).</summary>
        public static string PathFor(string fileName)
        {
            EnsureDir();
            return Unique(Path.Combine(Dir, Sanitize(fileName)));
        }

        private static string Unique(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
            string dir  = Path.GetDirectoryName(path) ?? Dir;
            string name = Path.GetFileNameWithoutExtension(path);
            string ext  = Path.GetExtension(path);
            return Path.Combine(dir, $"{name}_{DateTime.Now:HHmmssfff}{ext}");
        }

        private static string Sanitize(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            return s.Length > 80 ? s.Substring(0, 80) : s;
        }
    }
}
