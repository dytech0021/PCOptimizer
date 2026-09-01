using PCOptimizer.Services;
using Xunit;

namespace PCOptimizer.Tests;

public sealed class DisplayColorStateTests
{
    [Fact]
    public void ModernHdrPreferenceDoesNotPretendHdrIsActiveWhileWcgIsActive()
    {
        const uint value = (1u << 4) | (1u << 5) | (1u << 6) | (1u << 7);

        AdvancedColorState state = AdvancedColorState.DecodeModern(value, activeColorMode: 1);

        Assert.True(state.HdrSupported);
        Assert.True(state.HdrUserEnabled);
        Assert.False(state.HdrActive);
        Assert.True(state.WcgSupported);
        Assert.True(state.WcgUserEnabled);
        Assert.True(state.WcgActive);
    }

    [Fact]
    public void ModernActiveModeIsTheSourceOfTruthForHdr()
    {
        const uint value = (1u << 4);

        AdvancedColorState state = AdvancedColorState.DecodeModern(value, activeColorMode: 2);

        Assert.True(state.HdrSupported);
        Assert.False(state.HdrUserEnabled);
        Assert.True(state.HdrActive);
        Assert.False(state.WcgActive);
    }

    [Fact]
    public void LegacyWideColorIsNotReportedAsHdr()
    {
        const uint value = 1u | 2u | 4u;

        AdvancedColorState state = AdvancedColorState.DecodeLegacy(value);

        Assert.False(state.HdrSupported);
        Assert.False(state.HdrActive);
        Assert.True(state.WcgSupported);
        Assert.True(state.WcgActive);
    }

    [Fact]
    public void LegacyAdvancedColorWithoutWideColorIsReportedAsHdr()
    {
        const uint value = 1u | 2u;

        AdvancedColorState state = AdvancedColorState.DecodeLegacy(value);

        Assert.True(state.HdrSupported);
        Assert.True(state.HdrActive);
        Assert.False(state.WcgActive);
    }
}
