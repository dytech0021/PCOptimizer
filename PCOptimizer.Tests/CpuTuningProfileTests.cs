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
        p.Class1.BoostMode = 99;

        var settings = CpuTuningPlan.Build(p, hybrid: true);

        Assert.Equal(100, Value(settings, "PROCTHROTTLEMIN1"));
        Assert.Equal(0, Value(settings, "PERFEPP1"));
        Assert.Equal(6, Value(settings, "PERFBOOSTMODE1"));
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

    private static int Value(System.Collections.Generic.List<CpuTuningPlan.Setting> s, string name)
        => s.First(x => x.Name == name).Value;
}
