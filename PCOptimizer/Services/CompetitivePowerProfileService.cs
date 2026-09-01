using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Cria um plano temporário para a partida. O plano original nunca é editado:
    /// ele é reativado e a cópia é apagada ao sair, inclusive no próximo início
    /// caso o aplicativo tenha sido encerrado à força.
    /// </summary>
    internal static class CompetitivePowerProfileService
    {
        [DllImport("powrprof.dll", SetLastError = true)]
        private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey,
            out IntPtr activePolicyGuid);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        private static readonly Guid TemporaryScheme =
            new("7d4c6f10-3f82-4ea9-a1c9-12900a1d0001");
        private static readonly Guid BalancedScheme =
            new("381b4222-f694-41f0-9685-ff5bb260df2e");

        private sealed class Recovery
        {
            public string OriginalScheme { get; set; } = "";
        }

        private static string StatePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCOptimizer", "competitive-power.json");

        private static Guid? _original;

        public static bool HasPendingRecovery => File.Exists(StatePath);

        public static bool Apply(CompetitiveModeService.Profile profile)
        {
            if (File.Exists(StatePath) && !Restore()) return false;
            if (!TryGetActiveScheme(out Guid original)) return false;
            _original = original;
            SaveRecovery(original);

            // Remove apenas a nossa cópia fixa, nunca um plano do usuário.
            ProcessRunner.Run("powercfg.exe", $"/delete {TemporaryScheme}", 10000);
            if (!ProcessRunner.Run("powercfg.exe",
                $"/duplicatescheme {original} {TemporaryScheme}", 15000))
            {
                ClearRecovery();
                _original = null;
                return false;
            }

            int applied = 0;
            // Classe 0 = E-cores; classe 1 = P-cores no 12900H. Os P-cores ficam
            // em resposta máxima, enquanto os E-cores preservam orçamento térmico.
            applied += Set("PERFEPP", profile.ECoreEpp);
            applied += Set("PERFEPP1", profile.PCoreEpp);
            applied += Set("PERFBOOSTMODE", 4); // E-cores: Efficient Aggressive
            applied += Set("PERFBOOSTMODE1", profile.BoostMode);
            applied += Set("PROCTHROTTLEMIN", 5);
            applied += Set("PROCTHROTTLEMIN1", 5);
            applied += Set("PROCTHROTTLEMAX", 100);
            applied += Set("PROCTHROTTLEMAX1", 100);
            applied += Set("CPMINCORES", 100);
            applied += Set("CPMINCORES1", 100);
            applied += Set("CPMAXCORES", 100);
            applied += Set("CPMAXCORES1", 100);
            bool active = ProcessRunner.Run("powercfg.exe",
                $"/setactive {TemporaryScheme}", 10000);
            Logger.Info($"Competitivo: plano temporário aplicado ({applied}/12 ajustes)");
            return active && applied >= 5;
        }

        private static int Set(string setting, int value) =>
            ProcessRunner.Run("powercfg.exe",
                $"/setacvalueindex {TemporaryScheme} SUB_PROCESSOR {setting} {value}", 10000)
                ? 1 : 0;

        public static bool Restore()
        {
            Guid? saved = _original ?? LoadRecovery();
            if (saved == null) return true;
            Guid target = saved.Value == Guid.Empty ? BalancedScheme : saved.Value;
            if (!ProcessRunner.Run("powercfg.exe", $"/setactive {target}", 10000))
            {
                Logger.Warn("Competitivo: não foi possível restaurar o plano de energia; recuperação preservada");
                return false;
            }
            ProcessRunner.Run("powercfg.exe", $"/delete {TemporaryScheme}", 10000);
            _original = null;
            ClearRecovery();
            return true;
        }

        public static void RestoreOrphanFromPreviousRun()
        {
            Guid? saved = LoadRecovery();
            if (saved == null) return;
            Logger.Warn("Competitivo: restaurando plano de energia de uma execução interrompida");
            _original = saved;
            Restore();
        }

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

        private static void SaveRecovery(Guid original)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
                File.WriteAllText(StatePath, JsonSerializer.Serialize(new Recovery
                {
                    OriginalScheme = original.ToString()
                }));
            }
            catch (Exception ex) { Logger.Error(ex, "CompetitivePower.SaveRecovery"); }
        }

        private static Guid? LoadRecovery()
        {
            try
            {
                if (!File.Exists(StatePath)) return null;
                var state = JsonSerializer.Deserialize<Recovery>(File.ReadAllText(StatePath));
                return Guid.TryParse(state?.OriginalScheme, out Guid g) ? g : null;
            }
            catch { return null; }
        }

        private static void ClearRecovery()
        {
            try { if (File.Exists(StatePath)) File.Delete(StatePath); } catch { }
        }
    }
}
