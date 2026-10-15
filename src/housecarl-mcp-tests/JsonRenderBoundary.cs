using Xunit;

namespace HousecarlMcpTests;

/// <summary>Find a render transition from actual fixture content instead of pinning yesterday's byte count.</summary>
static class JsonRenderBoundary
{
    public static int First(Func<int, bool> admits, int upper = 80_000)
    {
        Assert.True(admits(upper), "the wide render must admit the content being tested");
        var low = 1;
        var high = upper;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (admits(mid)) high = mid;
            else low = mid + 1;
        }
        Assert.True(admits(low));
        Assert.False(admits(low - 1), "the fixture must exercise both sides of the transition");
        return low;
    }
}
