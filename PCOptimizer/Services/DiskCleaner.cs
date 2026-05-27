using System;
using System.IO;

namespace PCOptimizer.Services
{
    public static class DiskCleaner
    {
        public static (int filesDeleted, long bytesFreed) Clean()
        {
            int deleted = 0;
            long freed = 0;

            string[] cachePaths =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Explorer"),
                @"C:\Windows\Logs",
                @"C:\Windows\Prefetch",
            };

            string[] cacheExtensions = { "*.log", "*.tmp", "*.dmp", "*.etl", "*.old", "*.bak" };

            foreach (var path in cachePaths)
            {
                if (!Directory.Exists(path)) continue;
                foreach (var ext in cacheExtensions)
                {
                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(path, ext, SearchOption.AllDirectories))
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
                    }
                    catch { }
                }
            }

            return (deleted, freed);
        }
    }
}
