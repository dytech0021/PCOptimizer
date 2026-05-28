using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Libera memoria RAM esvaziando o working set dos processos acessiveis.
    /// </summary>
    public static class MemoryService
    {
        [DllImport("psapi.dll")]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        public static int ClearStandby()
        {
            int cleared = 0;
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (EmptyWorkingSet(proc.Handle))
                        cleared++;
                }
                catch
                {
                    // Processos do sistema negam acesso — ignorado
                }
                finally
                {
                    proc.Dispose();
                }
            }
            return cleared;
        }
    }
}
