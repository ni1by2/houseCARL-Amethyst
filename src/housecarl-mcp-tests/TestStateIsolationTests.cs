using System.Security.Cryptography;
using System.Text.Json;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

[Trait("tier", "unit")]
public sealed class TestStateIsolationTests
{
    [Fact]
    public void InProcessTransactionsAreWrittenUnderThePrivateRunState()
    {
        var coordinator = new AmethystTransactionCoordinator();
        Assert.Equal(Path.Combine(TestRunSetup.StateHome, "housecarl-amethyst"), coordinator.StateRoot);
        if (!OperatingSystem.IsLinux()) return;

        var journal = Begin(coordinator);
        var path = Path.Combine(coordinator.StateRoot, "transactions", journal.Id, "journal.json");
        Assert.True(File.Exists(path));
        Assert.Equal(AmethystTransactionState.Preparing, coordinator.ReadJournal(journal.Id).State);
    }

    [Fact]
    public void DefaultServerDoesNotReadTransactionsFromTheParentTestProcess()
    {
        if (!OperatingSystem.IsLinux()) return;
        var coordinator = new AmethystTransactionCoordinator();
        var journal = Begin(coordinator);
        using var server = new ServerFixture();
        var result = server.Call(ToolNames.StagingStatus, JsonSerializer.Serialize(new { transaction_id = journal.Id }));

        Assert.False(result.IsError, result.Describe());
        using var parsed = JsonDocument.Parse(result.Text);
        Assert.Empty(parsed.RootElement.GetProperty("transactions").EnumerateArray());
        Assert.Single(coordinator.Status(journal.Id));
    }

    static AmethystTransactionJournal Begin(AmethystTransactionCoordinator coordinator)
    {
        var root = TestRunSetup.StateHome;
        return coordinator.Begin(new AmethystTransactionRequest("test-state-isolation", "skyrim_se", "fixture",
            Path.Combine(root, "connection.json"), root, Path.Combine(root, "filegraph.lock"),
            1, 1, "fixture", Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant(),
            [new AmethystTransactionUnit("output")], Managed: false));
    }
}
