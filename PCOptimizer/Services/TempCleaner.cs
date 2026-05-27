using System;
using System.IO;

namespace PCOptimizer.Services
{
    public static class TempCleaner
    {
        public static (int filesDeleted, long bytesFreed) Clean()
        {
            int deleted = 0;
            long freed = 0;

            string[] tempPaths =
            {
                Path.GetTempPath(),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"),
                @"C:\Windows\Temp",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "INetCache"),
            };

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
                }
                catch { }
            }

            return (deleted, freed);
        }
    }
}
