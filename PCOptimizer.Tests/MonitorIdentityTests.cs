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
}
