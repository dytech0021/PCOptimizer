using System;
using System.IO;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Limpa o cache de downloads do Windows Update (SoftwareDistribution\Download).
    /// </summary>
    public static class WindowsUpdateCacheService
    {
        public static long Clean()
        {
            long freed = 0;
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "SoftwareDistribution", "Download");

                if (!Directory.Exists(dir)) return 0;

                foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
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

                foreach (var sub in Directory.GetDirectories(dir))
                {
                    try { Directory.Delete(sub, true); } catch { }
                }
            }
            catch { }
            return freed;
        }
    }
}
