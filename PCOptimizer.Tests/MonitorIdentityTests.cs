using PCOptimizer.Services;
using Xunit;

namespace PCOptimizer.Tests;

public sealed class MonitorIdentityTests
{
    [Fact]
    public void TargetIdentityIncludesTheAdapterLuid()
    {
        var firstAdapter = new HdrTargetKey(1, 0, 7);
        var secondAdapter = new HdrTargetKey(2, 0, 7);

        Assert.NotEqual(firstAdapter, secondAdapter);
    }

    [Fact]
    public void InterfaceDiscriminatorIsStableAndFilesystemSafe()
    {
        const string path = @"\\?\DISPLAY#MG900#4&123&UID256#{guid}";

        string first = MonitorIdentity.StableDiscriminator(path);
        string second = MonitorIdentity.StableDiscriminator(path.ToLowerInvariant());

        Assert.Equal(first, second);
        Assert.Matches("^[0-9a-f]{16}$", first);
    }

    [Fact]
    public void SavedStateBaseSobreviveAMudancaDeFormatoDoDiscriminador()
    {
        // O discriminador dentro do id já mudou de formato (GetHashCode de 8 hex
        // para SHA-256 de 16). Sem colapsar os dois na mesma base, o estado do
        // modo 16:9 salvo por uma versão anterior fica órfão e o usuário perde
        // o botão de reverter.
        Assert.Equal(
            MonitorIdentity.SavedStateBase("ddc_MG900_a1b2c3d4"),
            MonitorIdentity.SavedStateBase("ddc_MG900_a1b2c3d4e5f60718"));
    }

    [Fact]
    public void SavedStateBaseIgnoraOsSufixosDeDesempate()
    {
        const string expected = "ddc_mg900";

        Assert.Equal(expected, MonitorIdentity.SavedStateBase("ddc_MG900_a1b2c3d4#2"));
        Assert.Equal(expected, MonitorIdentity.SavedStateBase("ddc_MG900_a1b2c3d4#i0"));
        Assert.Equal(expected, MonitorIdentity.SavedStateBase("ddc_MG900_a1b2c3d4@ff00ff00"));
    }

    [Fact]
    public void SavedStateBaseNaoConfundeMonitoresDiferentes()
    {
        Assert.NotEqual(
            MonitorIdentity.SavedStateBase("ddc_MG900_a1b2c3d4"),
            MonitorIdentity.SavedStateBase("ddc_MG800_a1b2c3d4"));
    }
}
