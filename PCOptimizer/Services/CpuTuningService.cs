using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Painel de CPU: aplica o perfil num plano de energia PRÓPRIO, criado por
    /// cópia do plano ativo. O plano do usuário nunca é editado — desfazer é
    /// reativar o dele e apagar a nossa cópia.
    ///
    /// Diferente do resto do app, este perfil é PERSISTENTE: continua valendo
    /// com o programa fechado, até o usuário desativar. Por isso o
    /// <see cref="App"/> não restaura nada ao sair; ele só detecta, na abertura,
    /// que o perfil ficou ativo desde a sessão anterior.
    /// </summary>
    public static class CpuTuningService
    {
        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey,
            out IntPtr activePolicyGuid);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        /// <summary>
        /// GUID fixo da nossa cópia. Fixo de propósito: entre execuções o app
        /// precisa reconhecer o próprio plano, e nunca apagar um do usuário.
        /// </summary>
        private static readonly Guid OurScheme =
            new("7d4c6f10-3f82-4ea9-a1c9-12900a1d0002");
        private static readonly Guid BalancedScheme =
            new("381b4222-f694-41f0-9685-ff5bb260df2e");

        private const string MmcssKey =
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
        private const int GamingResponsiveness = 10;   // padrão do Windows é 20

        private sealed class Recovery
        {
            public string OriginalScheme { get; set; } = "";
            /// <summary>-1 = o valor não existia e deve ser removido ao desfazer.</summary>
            public int PreviousResponsiveness { get; set; } = -1;
            public bool ResponsivenessChanged { get; set; }
        }

        private static string StatePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCOptimizer", "cpu-tuning.json");

        /// <summary>O perfil está aplicado agora (nesta sessão ou numa anterior)?</summary>
        public static bool IsActive { get; private set; }

        /// <summary>Ficou ativo de uma sessão anterior — a interface avisa.</summary>
        public static bool FromPreviousRun { get; private set; }

        public static event Action? StatusChanged;

        private static void Notify()
        {
            try { StatusChanged?.Invoke(); }
            catch (Exception ex) { Logger.Error(ex, "CpuTuning.StatusChanged"); }
        }

        /// <summary>
        /// Na abertura: descobre se o nosso plano ficou ativo da sessão passada.
        /// NÃO desfaz nada — o perfil é persistente por escolha do usuário.
        /// </summary>
        public static void DetectActiveFromPreviousRun()
        {
            try
            {
                if (!File.Exists(StatePath)) return;
                if (!TryGetActiveScheme(out Guid active)) return;

                if (active == OurScheme)
                {
                    IsActive = true;
                    FromPreviousRun = true;
                    Logger.Info("CPU: perfil do painel continua ativo desde a sessão anterior");
                }
                else
                {
                    // Alguém trocou o plano por fora (Windows, driver, outro app).
                    // O registro de recuperação virou lixo: some com ele para não
                    // restaurar um plano errado mais tarde.
                    Logger.Warn("CPU: o plano ativo não é o do painel — descartando recuperação");
                    ClearRecovery();
                }
            }
            catch (Exception ex) { Logger.Error(ex, "CpuTuning.DetectActive"); }
        }

        /// <summary>Aplica o perfil. Devolve o texto para a interface.</summary>
        public static string Apply(CpuTuningProfile profile)
        {
            try
            {
                var topo = CpuTopologyService.Get();

                // Já ativo: o plano atual é a NOSSA cópia, então duplicá-la
                // perderia o plano original do usuário. Volta antes de reaplicar.
                if (IsActive && !RestoreCore()) return "Não consegui trocar o perfil anterior";

                if (!TryGetActiveScheme(out Guid original))
                    return "Não consegui ler o plano de energia atual";

                var recovery = new Recovery { OriginalScheme = original.ToString() };

                ProcessRunner.Run("powercfg.exe", $"/delete {OurScheme}", 10000);
                if (!ProcessRunner.Run("powercfg.exe",
                        $"/duplicatescheme {original} {OurScheme}", 15000))
                    return "Não consegui criar o plano de energia do aplicativo";

                var settings = CpuTuningPlan.Build(profile, topo.IsHybrid);
                int applied = 0;
                foreach (var s in settings)
                    if (ProcessRunner.Run("powercfg.exe",
                            $"/setacvalueindex {OurScheme} SUB_PROCESSOR {s.Name} {s.Value}", 10000))
                        applied++;

                if (applied < CpuTuningPlan.MinimumRequired(settings.Count))
                {
                    // Meio aplicado é pior que nada: o usuário acharia que está
                    // otimizado sem estar. Desfaz e conta o que houve.
                    ProcessRunner.Run("powercfg.exe", $"/setactive {original}", 10000);
                    ProcessRunner.Run("powercfg.exe", $"/delete {OurScheme}", 10000);
                    Logger.Warn($"CPU: só {applied}/{settings.Count} ajustes passaram — desfeito");
                    return "Este processador não aceitou os ajustes";
                }

                if (!ProcessRunner.Run("powercfg.exe", $"/setactive {OurScheme}", 10000))
                {
                    ProcessRunner.Run("powercfg.exe", $"/delete {OurScheme}", 10000);
                    return "Não consegui ativar o plano do aplicativo";
                }

                if (profile.GamingResponsiveness)
                    ApplyResponsiveness(recovery);

                SaveRecovery(recovery);
                IsActive = true;
                FromPreviousRun = false;

                // O timer é por processo e some quando o app fecha — ao contrário
                // do resto do perfil. Fica claro no texto de status.
                if (profile.LowLatencyTimer) TimerResolutionService.Apply();

                Logger.Info($"CPU: perfil aplicado ({applied}/{settings.Count} ajustes, " +
                            $"híbrida={topo.IsHybrid})");
                Notify();
                return StatusText();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "CpuTuning.Apply");
                return "Falhou ao aplicar o perfil de CPU";
            }
        }

        /// <summary>Desfaz tudo: volta ao plano do usuário e apaga a cópia.</summary>
        public static string Restore()
        {
            if (!RestoreCore()) return "⚠ Não consegui restaurar — veja Configurações → Energia";
            Notify();
            return "Inativo — plano de energia devolvido";
        }

        private static bool RestoreCore()
        {
            try
            {
                var recovery = LoadRecovery();
                Guid target = BalancedScheme;
                if (recovery != null && Guid.TryParse(recovery.OriginalScheme, out Guid saved)
                    && saved != Guid.Empty && saved != OurScheme)
                    target = saved;

                if (!ProcessRunner.Run("powercfg.exe", $"/setactive {target}", 10000))
                {
                    Logger.Warn("CPU: não consegui reativar o plano original; recuperação preservada");
                    return false;
                }

                if (recovery?.ResponsivenessChanged == true)
                    RestoreResponsiveness(recovery.PreviousResponsiveness);

                ProcessRunner.Run("powercfg.exe", $"/delete {OurScheme}", 10000);
                ClearRecovery();
                IsActive = false;
                FromPreviousRun = false;
                Logger.Info("CPU: perfil desfeito e plano original reativado");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "CpuTuning.Restore");
                return false;
            }
        }

        public static string StatusText()
        {
            if (!IsActive) return "Inativo";
            return FromPreviousRun
                ? "Ativo desde a sessão anterior"
                : "Ativo — plano próprio do aplicativo";
        }

        // ── MMCSS ─────────────────────────────────────────────────────────────

        private static void ApplyResponsiveness(Recovery recovery)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(MmcssKey, writable: true);
                if (key == null) return;

                object? current = key.GetValue("SystemResponsiveness");
                recovery.PreviousResponsiveness = current is int v ? v : -1;
                recovery.ResponsivenessChanged = true;
                key.SetValue("SystemResponsiveness", GamingResponsiveness, RegistryValueKind.DWord);
            }
            catch (Exception ex) { Logger.Error(ex, "CpuTuning.ApplyResponsiveness"); }
        }

        private static void RestoreResponsiveness(int previous)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(MmcssKey, writable: true);
                if (key == null) return;

                // -1 marca "não existia antes": recriar com um valor inventado
                // seria pior que devolver a chave ao estado em que estava.
                if (previous < 0) key.DeleteValue("SystemResponsiveness", throwOnMissingValue: false);
                else key.SetValue("SystemResponsiveness", previous, RegistryValueKind.DWord);
            }
            catch (Exception ex) { Logger.Error(ex, "CpuTuning.RestoreResponsiveness"); }
        }

        // ── Estado em disco ───────────────────────────────────────────────────

        private static bool TryGetActiveScheme(out Guid scheme)
        {
            scheme = Guid.Empty;
            IntPtr ptr = IntPtr.Zero;
            try
            {
                if (PowerGetActiveScheme(IntPtr.Zero, out ptr) != 0 || ptr == IntPtr.Zero)
                    return false;
                scheme = Marshal.PtrToStructure<Guid>(ptr);
                return scheme != Guid.Empty;
            }
            catch { return false; }
            finally { if (ptr != IntPtr.Zero) LocalFree(ptr); }
        }

        private static void SaveRecovery(Recovery recovery)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
                File.WriteAllText(StatePath, JsonSerializer.Serialize(recovery));
            }
            catch (Exception ex) { Logger.Error(ex, "CpuTuning.SaveRecovery"); }
        }

        private static Recovery? LoadRecovery()
        {
            try
            {
                if (!File.Exists(StatePath)) return null;
                return JsonSerializer.Deserialize<Recovery>(File.ReadAllText(StatePath));
            }
            catch { return null; }
        }

        private static void ClearRecovery()
        {
            try { if (File.Exists(StatePath)) File.Delete(StatePath); } catch { }
            IsActive = false;
            FromPreviousRun = false;
        }
    }
}
