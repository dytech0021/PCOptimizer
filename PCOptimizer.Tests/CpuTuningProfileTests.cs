using System.Linq;
using PCOptimizer.Services;
using Xunit;

namespace PCOptimizer.Tests;

/// <summary>
/// Trava a tradução do perfil em comandos do powercfg. É a parte que decide o
/// que vai ser escrito no plano de energia, então um erro aqui aparece como
/// "otimizei" sem ter otimizado nada.
/// </summary>
public sealed class CpuTuningProfileTests
{
    [Fact]
    public void CpuNaoHibridaNaoRecebeAjustesDaSegundaClasse()
    {
        // O sufixo "1" endereça a segunda classe de núcleo. Numa CPU comum ela
        // não existe e o powercfg recusa — só geraria erro no log.
        var settings = CpuTuningPlan.Build(CpuTuningProfile.Default(), hybrid: false);

        Assert.DoesNotContain(settings, s => s.Name.EndsWith("1"));
        Assert.Contains(settings, s => s.Name == "PROCTHROTTLEMIN");
    }

    [Fact]
    public void CpuHibridaRecebeAsDuasClasses()
    {
        var settings = CpuTuningPlan.Build(CpuTuningProfile.Default(), hybrid: true);

        Assert.Contains(settings, s => s.Name == "PERFEPP");    // classe 0 — E-cores
        Assert.Contains(settings, s => s.Name == "PERFEPP1");   // classe 1 — P-cores
    }

    [Fact]
    public void CadaAjusteApareceUmaVezSo()
    {
        var settings = CpuTuningPlan.Build(CpuTuningProfile.Default(), hybrid: true);

        Assert.Equal(settings.Count, settings.Select(s => s.Name).Distinct().Count());
    }

    [Fact]
    public void ValoresForaDaFaixaSaoPresosNoLimite()
    {
        var p = CpuTuningProfile.Default();
        p.Class1.MinState = 500;
        p.Class1.Epp = -20;
        p.Class0.BoostMode = 99;

        var settings = CpuTuningPlan.Build(p, hybrid: true);

        Assert.Equal(100, Value(settings, "PROCTHROTTLEMIN1"));
        Assert.Equal(0, Value(settings, "PERFEPP1"));
        Assert.Equal(6, Value(settings, "PERFBOOSTMODE"));
    }

    [Fact]
    public void PisoAcimaDoTetoCedeParaOTeto()
    {
        // O Windows recusa em silêncio um mínimo maior que o máximo; melhor
        // corrigir aqui do que aplicar meio perfil sem ninguém perceber.
        var p = CpuTuningProfile.Default();
        p.Class1.MinState = 90;
        p.Class1.MaxState = 60;
        p.Class1.MinCores = 100;
        p.Class1.MaxCores = 50;

        var settings = CpuTuningPlan.Build(p, hybrid: true);

        Assert.Equal(60, Value(settings, "PROCTHROTTLEMIN1"));
        Assert.Equal(50, Value(settings, "CPMINCORES1"));
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void CStatesEntramComoAjusteGlobal(bool disableIdle, int expected)
    {
        var p = CpuTuningProfile.Default();
        p.DisableIdle = disableIdle;

        var settings = CpuTuningPlan.Build(p, hybrid: true);

        Assert.Equal(expected, Value(settings, "IDLEDISABLE"));
        Assert.DoesNotContain(settings, s => s.Name == "IDLEDISABLE1");
    }

    [Fact]
    public void BuildNaoAlteraOPerfilRecebido()
    {
        // A janela guarda o perfil do usuário; corrigir faixa não pode mexer
        // no objeto dela pelas costas.
        var p = CpuTuningProfile.Default();
        p.Class1.MinState = 500;

        CpuTuningPlan.Build(p, hybrid: true);

        Assert.Equal(500, p.Class1.MinState);
    }

    [Fact]
    public void PadraoNaoLigaOsAjustesArriscados()
    {
        var p = CpuTuningProfile.Default();

        Assert.False(p.DisableIdle);            // esquenta e pode cortar o turbo
        Assert.False(p.LowLatencyTimer);
        Assert.False(p.GamingResponsiveness);
    }

    // ── Níveis ────────────────────────────────────────────────────────────

    [Fact]
    public void TurboEhUmAjusteSoParaOProcessadorInteiro()
    {
        // PERFBOOSTMODE1 não existe na lista da Microsoft: falhava calado.
        var settings = CpuTuningPlan.Build(CpuTuningProfile.Default(), hybrid: true);
        Assert.DoesNotContain(settings, s => s.Name == "PERFBOOSTMODE1");
        Assert.Single(settings, s => s.Name == "PERFBOOSTMODE");
    }

    [Fact]
    public void MedioEhOPadraoQueDeuOsDozePorCento()
    {
        var medio = CpuTuningProfile.ForLevel(CpuTuningLevel.Medio);
        var padrao = CpuTuningProfile.Default();
        Assert.Equal(CpuTuningPlan.Build(padrao, true), CpuTuningPlan.Build(medio, true));
        Assert.Equal(CpuTuningLevel.Medio, CpuTuningProfile.DetectLevel(padrao, hybrid: false));
        Assert.Equal(CpuTuningLevel.Medio, CpuTuningProfile.DetectLevel(padrao, hybrid: true));
    }

    [Theory]
    [InlineData(CpuTuningLevel.Baixo, true)]
    [InlineData(CpuTuningLevel.Baixo, false)]
    [InlineData(CpuTuningLevel.Medio, true)]
    [InlineData(CpuTuningLevel.Medio, false)]
    [InlineData(CpuTuningLevel.Agressivo, true)]
    [InlineData(CpuTuningLevel.Agressivo, false)]
    public void CadaNivelEhReconhecido(CpuTuningLevel level, bool hybrid) =>
        Assert.Equal(level, CpuTuningProfile.DetectLevel(CpuTuningProfile.ForLevel(level), hybrid));

    [Fact]
    public void MexerNumControleViraPersonalizado()
    {
        var p = CpuTuningProfile.ForLevel(CpuTuningLevel.Medio);
        p.Class0.Epp = 60;
        Assert.Equal(CpuTuningLevel.Personalizado, CpuTuningProfile.DetectLevel(p, hybrid: false));
    }

    [Fact]
    public void CpuSemHibridoIgnoraAClasseQueNaoExiste()
    {
        var p = CpuTuningProfile.ForLevel(CpuTuningLevel.Medio);
        p.Class1.Epp = 77;   // classe 1 não existe numa CPU comum
        Assert.Equal(CpuTuningLevel.Medio, CpuTuningProfile.DetectLevel(p, hybrid: false));
        Assert.Equal(CpuTuningLevel.Personalizado, CpuTuningProfile.DetectLevel(p, hybrid: true));
    }

    [Fact]
    public void NenhumNivelDesligaOsCStatesNemCortaFrequenciaOuNucleos()
    {
        foreach (var level in new[] { CpuTuningLevel.Baixo, CpuTuningLevel.Medio, CpuTuningLevel.Agressivo })
        {
            var p = CpuTuningProfile.ForLevel(level);
            Assert.False(p.DisableIdle);
            foreach (var c in new[] { p.Class0, p.Class1 })
            {
                Assert.Equal(100, c.MaxState);
                Assert.Equal(100, c.MinCores);   // estacionamento desligado em todos
                Assert.Equal(100, c.MaxCores);
            }
        }
    }

    [Fact]
    public void SoOAgressivoLigaTimerEMmcss()
    {
        Assert.False(CpuTuningProfile.ForLevel(CpuTuningLevel.Baixo).LowLatencyTimer);
        Assert.False(CpuTuningProfile.ForLevel(CpuTuningLevel.Medio).GamingResponsiveness);
        var agr = CpuTuningProfile.ForLevel(CpuTuningLevel.Agressivo);
        Assert.True(agr.LowLatencyTimer);
        Assert.True(agr.GamingResponsiveness);
    }

    [Fact]
    public void NiveisSobemDeFormaCoerente()
    {
        var b = CpuTuningProfile.ForLevel(CpuTuningLevel.Baixo);
        var m = CpuTuningProfile.ForLevel(CpuTuningLevel.Medio);
        var a = CpuTuningProfile.ForLevel(CpuTuningLevel.Agressivo);
        Assert.True(b.Class0.MinState <= m.Class0.MinState && m.Class0.MinState <= a.Class0.MinState);
        Assert.True(b.Class0.Epp >= m.Class0.Epp && m.Class0.Epp >= a.Class0.Epp);
    }

    [Fact]
    public void PerfilComClasseNulaNaoQuebra()
    {
        var p = new CpuTuningProfile { Class0 = null!, Class1 = null! };
        Assert.NotEmpty(CpuTuningPlan.Build(p, hybrid: true));
        Assert.Equal(CpuTuningLevel.Medio, CpuTuningProfile.DetectLevel(new CpuTuningProfile
            { Class0 = new CoreClassTuning { Epp = 25, BoostMode = 4 }, Class1 = null! }, hybrid: false));
    }

    private static int Value(System.Collections.Generic.List<CpuTuningPlan.Setting> s, string name)
        => s.First(x => x.Name == name).Value;
}
