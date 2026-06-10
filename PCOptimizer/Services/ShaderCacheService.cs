using System;
using System.IO;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Limpa os caches de shaders DirectX/NVIDIA/AMD. Os jogos recompilam os
    /// shaders na próxima execução — resolve stuttering após atualizar o
    /// driver de vídeo e libera espaço.
    /// </summary>
    public static class ShaderCacheService
    {
        public static long Clean()
        {
            long freed = 0;
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string localLow = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData", "LocalLow");

            string[] cacheDirs =
            {
                Path.Combine(local, "D3DSCache"),                       // DirectX 12
                Path.Combine(local, "NVIDIA", "DXCache"),               // NVIDIA DX
                Path.Combine(local, "NVIDIA", "GLCache"),               // NVIDIA OpenGL/Vulkan
                Path.Combine(localLow, "NVIDIA", "PerDriverVersion", "DXCache"),
                Path.Combine(localLow, "NVIDIA", "PerDriverVersion", "GLCache"),
                Path.Combine(local, "AMD", "DxCache"),                  // AMD DX
                Path.Combine(local, "AMD", "DxcCache"),
                Path.Combine(local, "AMD", "GLCache"),
                Path.Combine(local, "Intel", "ShaderCache"),            // Intel
            };

            foreach (var dir in cacheDirs)
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            var fi = new FileInfo(file);
                            long size = fi.Length;
                            fi.Delete();
                            freed += size;
                        }
                        catch { /* arquivo em uso pelo driver — pula */ }
                    }
                }
                catch { }
            }

            return freed;
        }
    }
}
