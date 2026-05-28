using System;
using System.IO;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Limpa o cache de miniaturas e ícones do Explorer.
    /// </summary>
    public static class ThumbnailCacheService
    {
        public static long Clean()
        {
            long freed = 0;
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "Windows", "Explorer");

                if (!Directory.Exists(dir)) return 0;

                foreach (var pattern in new[] { "thumbcache_*.db", "iconcache_*.db" })
                {
                    foreach (var file in Directory.GetFiles(dir, pattern))
                    {
                        try
                        {
                            var fi = new FileInfo(file);
                            long size = fi.Length;
                            fi.Delete();
                            freed += size;
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return freed;
        }
    }
}
