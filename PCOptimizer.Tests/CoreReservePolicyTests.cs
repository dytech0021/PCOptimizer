using System.Linq;
using PCOptimizer.Services;
using Xunit;

namespace PCOptimizer.Tests;

/// <summary>
/// Trava o Game Boost de núcleos reservados (CPU sem P/E). O ponto crítico é a
/// máscara: um erro aqui prenderia os programas de fundo nos núcleos do jogo.
/// </summary>
public sealed class CoreReservePolicyTests
{
    /// <summary>Xeon E5-2680 v3: 12 núcleos com HyperThreading (threads 2n e 2n+1).</summary>
    private static ulong[] Xeon2680v3() =>
        Enumerable.Range(0, 12).Select(i => 0b11UL << (2 * i)).ToArray();

    [Fact]
    public void XeonFicaComDezNucleosParaOJogoEDoisParaOResto()
    {
        var cores = Xeon2680v3();
        ulong bg = CoreReservePolicy.BackgroundMask(cores);
        ulong game = CoreReservePolicy.GameMask(cores);

        Assert.Equal(0xF00000UL, bg);                       // threads 20–23 = núcleos 10 e 11
        Assert.Equal(4, CoreReservePolicy.CountBits(bg));
        Assert.Equal(20, CoreReservePolicy.CountBits(game));
        Assert.Equal(0UL, bg & game);
    }

    [Fact]
    public void FundoLevaOsDoisThreadsDeCadaNucleo()
    {
        // Nenhum programa de fundo divide núcleo físico com o jogo.
        var cores = Xeon2680v3();
        ulong bg = CoreReservePolicy.BackgroundMask(cores);
        foreach (ulong core in cores)
            Assert.True((core & bg) == 0 || (core & bg) == core);
    }

    [Fact]
    public void ProcessadorSemHyperThreading()
    {
        var cores = Enumerable.Range(0, 8).Select(i => 1UL << i).ToArray();
        Assert.Equal(0b1100_0000UL, CoreReservePolicy.BackgroundMask(cores));
    }

    [Theory]
    [InlineData(12, false, false, true)]
    [InlineData(6, false, false, true)]
    [InlineData(4, false, false, false)]   // poucos núcleos
    [InlineData(14, true, false, false)]   // híbrido: usa o Game Boost normal
    [InlineData(64, false, true, false)]   // mais de 64 threads
    public void QuandoOModoSeAplica(int cores, bool hybrid, bool multi, bool expected) =>
        Assert.Equal(expected, CoreReservePolicy.IsSupported(cores, hybrid, multi));

    [Fact]
    public void PoucosNucleosNaoGeraMascara() =>
        Assert.Equal(0UL, CoreReservePolicy.BackgroundMask(Enumerable.Range(0, 4).Select(i => 3UL << (2 * i)).ToArray()));

    [Fact]
    public void DescricaoDoXeon() =>
        Assert.Equal("12 núcleos (24 threads) — jogo com 10, outros programas com 2",
                     CoreReservePolicy.Describe(Xeon2680v3()));

    [Theory]
    [InlineData("Intel(R) Xeon(R) CPU E5-2680 v3 @ 2.50GHz", true)]
    [InlineData("Intel(R) Xeon(R) CPU E5-2670 0 @ 2.60GHz", true)]
    [InlineData("Intel(R) Xeon(R) CPU E5-2690 v4 @ 2.60GHz", true)]
    [InlineData("Intel(R) Xeon(R) CPU E3-1230 v3 @ 3.30GHz", true)]
    [InlineData("Intel(R) Xeon(R) CPU E3-1230 v5 @ 3.40GHz", false)]   // Skylake: tem Speed Shift
    [InlineData("Intel(R) Xeon(R) CPU X5670 @ 2.93GHz", true)]
    [InlineData("Intel(R) Core(TM) i7-4790K CPU @ 4.00GHz", true)]
    [InlineData("Intel(R) Core(TM) i5-2500K CPU @ 3.30GHz", true)]
    [InlineData("Intel(R) Core(TM) i7-8700K CPU @ 3.70GHz", false)]
    [InlineData("12th Gen Intel(R) Core(TM) i7-12700K", false)]
    [InlineData("AMD Ryzen 7 5800X 8-Core Processor", false)]
    [InlineData("", false)]
    public void ProcessadorSemSpeedShift(string name, bool expected) =>
        Assert.Equal(expected, CpuNamePolicy.IsLegacyPStateCpu(name));

    [Fact]
    public void PerfilDoXeonIncluiTurboEClockQueSobeCedo()
    {
        var legacy = CpuTuningPlan.Build(CpuTuningProfile.Default(), hybrid: false, legacyPStates: true);
        Assert.Contains(legacy, s => s.Name == "PERFBOOSTPOL" && s.Value == 100);
        Assert.Contains(legacy, s => s.Name == "PERFINCTHRESHOLD");

        var modern = CpuTuningPlan.Build(CpuTuningProfile.Default(), hybrid: false);
        Assert.DoesNotContain(modern, s => s.Name == "PERFBOOSTPOL");
    }
}
