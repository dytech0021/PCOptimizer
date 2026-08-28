using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Guarda em disco o que o Turbo de Jogo alterou em cada processo, para
    /// conseguir desfazer mesmo que o app seja morto ou trave.
    ///
    /// Sem isto, um travamento deixaria Chrome, Discord e afins presos nos
    /// E-cores até o próximo reboot — o usuário ficaria com o PC lento sem
    /// explicação e sem saber como reverter.
    ///
    /// Arquivo separado do settings.json: aqui há gravação a cada processo
    /// alterado, e o SettingsService reserializa o objeto inteiro a cada chamada.
    /// </summary>
    public static class BoostStateStore
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessAffinityMask(IntPtr h, UIntPtr mask);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetPriorityClass(IntPtr h, uint priorityClass);

        private const uint PROCESS_SET_INFORMATION           = 0x0200;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        public sealed class Entry
        {
            public int    Pid { get; set; }
            public string Name { get; set; } = "";
            /// <summary>Identidade do processo: um PID reciclado tem outro início.</summary>
            public long   StartUtcTicks { get; set; }
            public ulong  PrevAffinity { get; set; }
            public uint   PrevPriority { get; set; }
            public bool   AffinityChanged { get; set; }
            public bool   PriorityChanged { get; set; }
        }

        private sealed class StateFile
        {
            public long BootTicks { get; set; }
            public List<Entry> Entries { get; set; } = new();
        }

        private static readonly object _io = new();

        private static string StatePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCOptimizer", "boost-state.json");

        /// <summary>
        /// Identifica o boot atual. Prioridade e afinidade não sobrevivem a um
        /// reboot, e os PIDs são reciclados — um arquivo de outro boot tem que ser
        /// descartado inteiro, nunca aplicado.
        /// </summary>
        private static long CurrentBootTicks()
        {
            var boot = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
            return boot.Ticks / TimeSpan.TicksPerSecond; // segundo cheio: tolera jitter
        }

        private static StateFile LoadFile()
        {
            try
            {
                if (!File.Exists(StatePath)) return new StateFile { BootTicks = CurrentBootTicks() };
                var f = JsonSerializer.Deserialize<StateFile>(File.ReadAllText(StatePath));
                return f ?? new StateFile { BootTicks = CurrentBootTicks() };
            }
            catch { return new StateFile { BootTicks = CurrentBootTicks() }; }
        }

        private static void SaveFile(StateFile f)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
                string tmp = StatePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(f));
                File.Move(tmp, StatePath, overwrite: true);
            }
            catch (Exception ex) { Logger.Error(ex, "BoostStateStore.SaveFile"); }
        }

        /// <summary>Anota que um processo foi alterado.</summary>
        public static void Record(Entry e)
        {
            lock (_io)
            {
                var f = LoadFile();
                f.BootTicks = CurrentBootTicks();
                f.Entries.RemoveAll(x => x.Pid == e.Pid);
                f.Entries.Add(e);
                SaveFile(f);
            }
        }

        /// <summary>Tira um processo da lista (já foi restaurado ou morreu).</summary>
        public static void Clear(int pid)
        {
            lock (_io)
            {
                var f = LoadFile();
                if (f.Entries.RemoveAll(x => x.Pid == pid) > 0) SaveFile(f);
            }
        }

        /// <summary>Esvazia a lista — tudo já foi restaurado.</summary>
        public static void ClearAll()
        {
            lock (_io)
            {
                try { if (File.Exists(StatePath)) File.Delete(StatePath); }
                catch (Exception ex) { Logger.Error(ex, "BoostStateStore.ClearAll"); }
            }
        }

        /// <summary>
        /// Restaura processos que ficaram alterados de uma execução anterior do
        /// app. Chamado no início, antes de qualquer outra coisa.
        /// </summary>
        public static int RestoreOrphansFromPreviousRun()
        {
            lock (_io)
            {
                StateFile f;
                try
                {
                    if (!File.Exists(StatePath)) return 0;
                    f = LoadFile();
                }
                catch { return 0; }

                // De outro boot: os PIDs já foram reciclados e as alterações
                // morreram junto com o desligamento. Descarta sem tentar nada.
                if (f.BootTicks != CurrentBootTicks())
                {
                    Logger.Info("Turbo: estado antigo de outro boot — descartado");
                    ClearAllNoLock();
                    return 0;
                }

                int restored = 0;
                foreach (var e in f.Entries)
                    if (TryRestore(e)) restored++;

                ClearAllNoLock();
                if (restored > 0)
                    Logger.Info($"Turbo: {restored} processo(s) restaurado(s) de uma execução anterior");
                return restored;
            }
        }

        private static void ClearAllNoLock()
        {
            try { if (File.Exists(StatePath)) File.Delete(StatePath); } catch { }
        }

        /// <summary>
        /// Devolve afinidade e prioridade originais. Confere a hora de início do
        /// processo antes: sem isso, um PID reciclado receberia a configuração de
        /// um programa completamente diferente.
        /// </summary>
        public static bool TryRestore(Entry e)
        {
            try
            {
                using (var p = Process.GetProcessById(e.Pid))
                {
                    if (p.StartTime.ToUniversalTime().Ticks != e.StartUtcTicks) return false;
                }
            }
            catch { return false; } // processo já morreu — nada a fazer
            return ApplyRestore(e);
        }

        /// <summary>Restaura sem reconferir a identidade (o chamador já conferiu).</summary>
        public static bool ApplyRestore(Entry e)
        {
            IntPtr h = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION,
                                   false, e.Pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                bool ok = false;
                if (e.AffinityChanged && e.PrevAffinity != 0)
                    ok |= SetProcessAffinityMask(h, (UIntPtr)e.PrevAffinity);
                if (e.PriorityChanged && e.PrevPriority != 0)
                    ok |= SetPriorityClass(h, e.PrevPriority);
                if (ok) Logger.Info($"Turbo: {e.Name} (PID {e.Pid}) restaurado");
                return ok;
            }
            catch (Exception ex) { Logger.Error(ex, "BoostStateStore.ApplyRestore"); return false; }
            finally { CloseHandle(h); }
        }
    }
}
