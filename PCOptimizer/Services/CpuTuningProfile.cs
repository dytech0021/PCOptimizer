using System;
using System.Collections.Generic;

namespace PCOptimizer.Services
{
    /// <summary>
    /// Ajustes de UMA classe de núcleo. Numa CPU híbrida existem duas (P e E);
    /// numa CPU comum só a primeira é usada.
    /// </summary>
    public sealed class CoreClassTuning
    {
        /// <summary>Piso de frequência, em % do clock base. 100 = nunca reduz.</summary>
        public int MinState { get; set; } = 100;
        /// <summary>Teto de frequência, em %.</summary>
        public int MaxState { get; set; } = 100;
        /// <summary>
        /// Modo de turbo do Windows: 0 desligado, 1 ativado, 2 agressivo,
        /// 3 eficiente, 4 eficiente agressivo, 5/6 com garantia.
        /// </summary>
        public int BoostMode { get; set; } = 2;
        /// <summary>
        /// Preferência energia × desempenho: 0 = desempenho total,
        /// 100 = economia total.
        /// </summary>
        public int Epp { get; set; }
        /// <summary>% mínima de núcleos que o Windows mantém acordados.</summary>
        public int MinCores { get; set; } = 100;
        /// <summary>% máxima de núcleos disponíveis.</summary>
        public int MaxCores { get; set; } = 100;

        public CoreClassTuning Clone() => (CoreClassTuning)MemberwiseClone();
    }

    /// <summary>Níveis prontos do Controle de CPU, pensados para evitar engasgos.</summary>
    public enum CpuTuningLevel { Baixo, Medio, Agressivo, Personalizado }

    /// <summary>
    /// Perfil completo do painel de CPU. Só dados e regras — nada de Windows
    /// aqui, para a montagem dos comandos poder ser conferida por teste.
    /// </summary>
    public sealed class CpuTuningProfile
    {
        /// <summary>Classe 0 do Windows. Em CPU híbrida da Intel, os E-cores.</summary>
        public CoreClassTuning Class0 { get; set; } = new();
        /// <summary>Classe 1 do Windows. Em CPU híbrida da Intel, os P-cores.</summary>
        public CoreClassTuning Class1 { get; set; } = new();

        /// <summary>
        /// Impede os núcleos de entrarem em ociosidade profunda (C-states).
        /// Corta a latência de acordar, mas esquenta e pode comer a margem
        /// térmica do turbo — por isso vem desligado.
        /// </summary>
        public bool DisableIdle { get; set; }

        /// <summary>Timer do sistema em 0,5 ms enquanto o app estiver aberto.</summary>
        public bool LowLatencyTimer { get; set; }

        /// <summary>Reduz a fatia que o Windows reserva a tarefas de segundo plano.</summary>
        public bool GamingResponsiveness { get; set; }

        public CpuTuningProfile Clone() => new()
        {
            // Configurações antigas ou editadas à mão podem trazer null aqui.
            Class0 = (Class0 ?? new CoreClassTuning()).Clone(),
            Class1 = (Class1 ?? new CoreClassTuning()).Clone(),
            DisableIdle = DisableIdle,
            LowLatencyTimer = LowLatencyTimer,
            GamingResponsiveness = GamingResponsiveness
        };

        /// <summary>Perfil de fábrica: máximo desempenho, sem os itens arriscados.</summary>
        public static CpuTuningProfile Default() => new()
        {
            Class0 = new CoreClassTuning { Epp = 25, BoostMode = 4 },   // E-cores
            Class1 = new CoreClassTuning { Epp = 0,  BoostMode = 2 },   // P-cores
            DisableIdle = false,
            LowLatencyTimer = false,
            GamingResponsiveness = false
        };

        /// <summary>
        /// Perfil de um nível. Médio é exatamente o padrão de fábrica — o perfil
        /// que deu +12% num jogo pesado de CPU. Nenhum nível desliga os C-states:
        /// com todos os núcleos sempre ligados sobra menos margem térmica para o
        /// turbo do núcleo do jogo, e isso pode CAUSAR engasgo.
        /// </summary>
        public static CpuTuningProfile ForLevel(CpuTuningLevel level) => level switch
        {
            CpuTuningLevel.Baixo => new CpuTuningProfile
            {
                Class0 = new CoreClassTuning { MinState = 50, BoostMode = 1, Epp = 33 },
                Class1 = new CoreClassTuning { MinState = 50, BoostMode = 1, Epp = 25 },
            },
            CpuTuningLevel.Agressivo => new CpuTuningProfile
            {
                Class0 = new CoreClassTuning { Epp = 0, BoostMode = 2 },
                Class1 = new CoreClassTuning { Epp = 0, BoostMode = 2 },
                LowLatencyTimer = true,
                GamingResponsiveness = true,
            },
            _ => Default(),
        };

        /// <summary>
        /// Qual nível este perfil é? Compara o que de fato é aplicado: a classe 1
        /// só conta em CPU híbrida, e o turbo é global (vem da classe 0).
        /// </summary>
        public static CpuTuningLevel DetectLevel(CpuTuningProfile? profile, bool hybrid)
        {
            if (profile == null) return CpuTuningLevel.Medio;
            var p = profile.Clone();
            p.Clamp();
            foreach (var level in new[] { CpuTuningLevel.Baixo, CpuTuningLevel.Medio, CpuTuningLevel.Agressivo })
            {
                var l = ForLevel(level);
                if (p.DisableIdle != l.DisableIdle || p.LowLatencyTimer != l.LowLatencyTimer
                    || p.GamingResponsiveness != l.GamingResponsiveness) continue;
                if (!SameClass(p.Class0, l.Class0, compareBoost: true)) continue;
                if (hybrid && !SameClass(p.Class1, l.Class1, compareBoost: false)) continue;
                return level;
            }
            return CpuTuningLevel.Personalizado;
        }

        private static bool SameClass(CoreClassTuning a, CoreClassTuning b, bool compareBoost) =>
            a.MinState == b.MinState && a.MaxState == b.MaxState && a.Epp == b.Epp
            && a.MinCores == b.MinCores && a.MaxCores == b.MaxCores
            && (!compareBoost || a.BoostMode == b.BoostMode);

        public static string LevelName(CpuTuningLevel level) => level switch
        {
            CpuTuningLevel.Baixo     => "Baixo",
            CpuTuningLevel.Medio     => "Médio",
            CpuTuningLevel.Agressivo => "Agressivo",
            _                        => "Personalizado",
        };

        /// <summary>O que o nível muda, em português simples, uma linha por item.</summary>
        public static string Describe(CpuTuningLevel level) => level switch
        {
            CpuTuningLevel.Baixo =>
                "• Nenhum núcleo dorme (estacionamento desligado): nada de esperar núcleo acordar\n" +
                "• Frequência mínima 50%: o clock não despenca entre um quadro e outro\n" +
                "• Turbo normal e preferência equilibrada: esquenta pouco",
            CpuTuningLevel.Medio =>
                "• Nenhum núcleo dorme (estacionamento desligado)\n" +
                "• Frequência mínima 100%: o processador não reduz o clock enquanto trabalha\n" +
                "• Turbo eficiente agressivo e preferência puxada para desempenho\n" +
                "• É o perfil de antes, que deu +12% num PC sem híbrido",
            CpuTuningLevel.Agressivo =>
                "• Tudo do Médio, com turbo agressivo e desempenho total em todos os núcleos\n" +
                "• Timer do sistema em 0,5 ms (com o PC Optimizer aberto): quadros mais regulares\n" +
                "• Menos CPU reservada para segundo plano (MMCSS)\n" +
                "• Esquenta mais — em notebook, use na tomada",
            _ =>
                "• Ajustes manuais — veja “Ajustes de CPU”",
        };

        /// <summary>Prende todo valor na faixa que o powercfg aceita.</summary>
        public void Clamp()
        {
            Class0 ??= new CoreClassTuning();
            Class1 ??= new CoreClassTuning();
            foreach (var c in new[] { Class0, Class1 })
            {
                c.MinState  = Math.Clamp(c.MinState, 0, 100);
                c.MaxState  = Math.Clamp(c.MaxState, 0, 100);
                c.BoostMode = Math.Clamp(c.BoostMode, 0, 6);
                c.Epp       = Math.Clamp(c.Epp, 0, 100);
                c.MinCores  = Math.Clamp(c.MinCores, 0, 100);
                c.MaxCores  = Math.Clamp(c.MaxCores, 0, 100);

                // Um teto abaixo do piso faria o Windows recusar o valor em
                // silêncio; o piso cede, porque o teto é o limite real.
                if (c.MinState > c.MaxState) c.MinState = c.MaxState;
                if (c.MinCores > c.MaxCores) c.MinCores = c.MaxCores;
            }
        }
    }

    /// <summary>
    /// Traduz um perfil na lista de ajustes do <c>powercfg</c>. Separado do
    /// serviço para poder ser verificado sem Windows.
    /// </summary>
    public static class CpuTuningPlan
    {
        /// <summary>Um ajuste: o nome do setting em SUB_PROCESSOR e o valor.</summary>
        public readonly record struct Setting(string Name, int Value);

        /// <summary>
        /// Monta os ajustes na ordem em que serão aplicados.
        ///
        /// O sufixo "1" endereça a segunda classe de núcleo. Numa CPU que não é
        /// híbrida ela não existe, e escrever nela só gera erro — por isso
        /// <paramref name="hybrid"/> decide se ela entra.
        /// </summary>
        public static List<Setting> Build(CpuTuningProfile profile, bool hybrid)
        {
            var p = profile.Clone();
            p.Clamp();

            var list = new List<Setting>();
            Add(list, p.Class0, "");
            if (hybrid) Add(list, p.Class1, "1");

            // O modo de turbo é UM ajuste para o processador inteiro: não existe
            // variante por classe de núcleo (PERFBOOSTMODE1 falhava calado).
            list.Add(new Setting("PERFBOOSTMODE", p.Class0.BoostMode));

            // IDLEDISABLE é global: não tem variante por classe de núcleo.
            list.Add(new Setting("IDLEDISABLE", p.DisableIdle ? 1 : 0));
            return list;
        }

        private static void Add(List<Setting> list, CoreClassTuning c, string suffix)
        {
            list.Add(new Setting($"PROCTHROTTLEMIN{suffix}", c.MinState));
            list.Add(new Setting($"PROCTHROTTLEMAX{suffix}", c.MaxState));
            list.Add(new Setting($"PERFEPP{suffix}", c.Epp));
            list.Add(new Setting($"CPMINCORES{suffix}", c.MinCores));
            list.Add(new Setting($"CPMAXCORES{suffix}", c.MaxCores));
        }

        /// <summary>
        /// Quantos ajustes precisam funcionar para o perfil valer alguma coisa.
        /// Abaixo disso o plano seria aplicado só pela metade, o que é pior que
        /// não aplicar — o usuário acharia que está otimizado e não está.
        /// </summary>
        public static int MinimumRequired(int total) => Math.Max(1, total / 2);
    }
}
