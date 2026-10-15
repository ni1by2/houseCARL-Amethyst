using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

[Trait("tier", "unit")]
public sealed class UnavailableManagerTests
{
    [Fact]
    public void UnverifiedManagerRefusesLoadOrderAndMo2Switch()
    {
        var path = Path.Combine(Path.GetTempPath(), "hc-unavailable-" + Guid.NewGuid().ToString("N"), "user.json");
        const string reason = "Amethyst filegraph currentness is unverified.";
        using var service = LoadOrderService.WithUnavailableManager(new UserConfigStore(path), reason);

        Assert.False(service.IsConfigured);
        Assert.Equal(reason, service.ConfigPromptOrNull());
        Assert.Equal(reason, Assert.Throws<InvalidOperationException>(() => service.SetInstance("/tmp/mo2")).Message);
    }
}
