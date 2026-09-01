using PCOptimizer.Services;
using Xunit;

namespace PCOptimizer.Tests;

public sealed class RecoveryPolicyTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void RecoveryIsClearedOnlyAfterSuccessfulRestoration(bool restored, bool shouldClear)
    {
        Assert.Equal(shouldClear, RecoveryPolicy.CanClear(restored));
    }
}
