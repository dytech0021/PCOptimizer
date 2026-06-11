using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PCOptimizer.Services
{
    public static class TempCleaner
    {
        public static (int filesDeleted, long bytesFreed) Clean()
        {
            int deleted = 0;
            long freed = 0;

            var tempPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.GetTempPath(),                           // %TEMP%
                Environment.GetEnvironmentVariable("TMP") ?? "",   // %TMP% (pode diferir)
                @"C:\Windows\Temp",                           // temp do sistema
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "Windows", "INetCache"),
            };
            tempPaths.RemoveWhere(string.IsNullOrWhiteSpace);

            foreach (var path in tempPaths)
            {
                if (!Directory.Exists(path)) continue;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            var info = new FileInfo(file);
                            long size = info.Length;
                            info.Delete();
                            deleted++;
                            freed += size;
                        }
                        catch { }
                    }

                    // Remove subdiretórios vazios após limpar os arquivos
                    foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
                                                 .OrderByDescending(d => d.Length))
                    {
                        try
                        {
                            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                                Directory.Delete(dir);
                        }
                        catch { }
                    }
                }
                catch { }
            }

            return (deleted, freed);
        }
    }
}
