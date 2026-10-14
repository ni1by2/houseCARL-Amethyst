using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace HousecarlMcpTests;

[Trait("tier", "unit")]
[Collection("server")]
public sealed class AmethystTransactionTests
{
    readonly ServerFixture _server;
    public AmethystTransactionTests(ServerFixture server) => _server = server;
    [Fact]
    public void NewArtifactIsPreparedJournaledAndPublishedWithReceipt()
    {
        using var fixture = new TransactionFixture();
        var coordinator = new AmethystTransactionCoordinator(fixture.State);
        var journal = coordinator.Begin(fixture.Request());
        var invalid = coordinator.Manage(journal.Id, AmethystTransactionAction.Resume);
        Assert.Equal("invalid_transition", invalid.Code);
        Assert.Equal(AmethystTransactionState.Preparing, coordinator.ReadJournal(journal.Id).State);
        using var locks = coordinator.AcquireWriteLocks(fixture.Library, fixture.ManagerLock);
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("prepared output"));
        var artifact = coordinator.PrepareArtifact(journal.Id, "unit-1", fixture.Destination, "plugin", content, fixture.Library);
        Assert.False(artifact.OriginalExisted);
        var published = coordinator.Publish(journal.Id, artifact.Id, locks);
        Assert.Equal("prepared output", File.ReadAllText(fixture.Destination));
        Assert.Equal(AmethystTransactionState.PendingDeployment, published.State);
        Assert.False(published.SafeToRestartAmethyst);
        Assert.True(File.Exists(Path.Combine(coordinator.StateRoot, "transactions", journal.Id, "receipt.json")));
        Assert.True(locks.ReleaseAndSeal(coordinator, journal.Id).SafeToRestartAmethyst);
    }

    [Fact]
    public void InterruptedPublishRecoversOriginalAndExternalConflictIsPreserved()
    {
        using var fixture = new TransactionFixture();
        File.WriteAllText(fixture.Destination, "original");
        var failAfterReplace = new AmethystTransactionCoordinator(fixture.State, point =>
        { if (point == "after_publish") throw new IOException("simulated process loss"); });
        var journal = failAfterReplace.Begin(fixture.Request());
        using (var locks = failAfterReplace.AcquireWriteLocks(fixture.Library, fixture.ManagerLock))
        {
            using var content = new MemoryStream(Encoding.UTF8.GetBytes("prepared output"));
            var artifact = failAfterReplace.PrepareArtifact(journal.Id, "unit-1", fixture.Destination, "plugin", content, fixture.Library);
            Assert.Throws<IOException>(() => failAfterReplace.Publish(journal.Id, artifact.Id, locks));
        }
        var recovered = new AmethystTransactionCoordinator(fixture.State).Manage(journal.Id, AmethystTransactionAction.Recover);
        Assert.True(recovered.Success);
        Assert.Equal("original", File.ReadAllText(fixture.Destination));

        File.WriteAllText(fixture.Destination, "original");
        var second = new AmethystTransactionCoordinator(fixture.State, point =>
        { if (point == "after_publish") throw new IOException("simulated process loss"); });
        var journal2 = second.Begin(fixture.Request());
        using (var locks = second.AcquireWriteLocks(fixture.Library, fixture.ManagerLock))
        {
            using var content = new MemoryStream(Encoding.UTF8.GetBytes("prepared output"));
            var artifact = second.PrepareArtifact(journal2.Id, "unit-1", fixture.Destination, "plugin", content, fixture.Library);
            Assert.Throws<IOException>(() => second.Publish(journal2.Id, artifact.Id, locks));
        }
        File.WriteAllText(fixture.Destination, "someone else's edit");
        var conflict = new AmethystTransactionCoordinator(fixture.State).Manage(journal2.Id, AmethystTransactionAction.Recover);
        Assert.Equal("external_conflict", conflict.Code);
        Assert.Equal("someone else's edit", File.ReadAllText(fixture.Destination));
        Assert.Equal("recovery_required", conflict.State);
    }

    [Fact]
    public void ManagerLockMustExistAndBusyLockReturnsTypedCloseRequired()
    {
        using var fixture = new TransactionFixture();
        var missing = Path.Combine(fixture.Library, "missing.lock");
        var coordinator = new AmethystTransactionCoordinator(fixture.State);
        Assert.Throws<AmethystLockContractException>(() => coordinator.AcquireWriteLocks(fixture.Library, missing));

        using var holder = Process.Start(new ProcessStartInfo("/usr/bin/flock")
        { ArgumentList = { "-x", fixture.ManagerLock, "/bin/sleep", "4" }, UseShellExecute = false })!;
        try
        {
            Thread.Sleep(250);
            var error = Assert.Throws<AmethystManagerCloseRequiredException>(() => coordinator.AcquireWriteLocks(
                fixture.Library, fixture.ManagerLock, TimeSpan.FromMilliseconds(150)));
            Assert.Equal("manager_close_required", error.Code);
            Assert.True(holder.HasExited is false);
        }
        finally { if (!holder.HasExited) { holder.Kill(); holder.WaitForExit(); } }
    }

    [Fact]
    public void UnknownJournalSchemaIsListedAsCorruptAndNeverMutated()
    {
        using var fixture = new TransactionFixture();
        var coordinator = new AmethystTransactionCoordinator(fixture.State);
        var journal = coordinator.Begin(fixture.Request());
        var path = Path.Combine(coordinator.StateRoot, "transactions", journal.Id, "journal.json");
        var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var json = document.RootElement.GetRawText().Replace("\"SchemaVersion\": 1", "\"SchemaVersion\": 999", StringComparison.Ordinal);
        File.WriteAllText(path, json);
        Assert.Equal("corrupt", Assert.Single(coordinator.Status(journal.Id)).State);
        Assert.Throws<InvalidOperationException>(() => coordinator.Manage(journal.Id, AmethystTransactionAction.Recover));
        Assert.Contains("999", File.ReadAllText(path));
    }

    [Fact]
    public void StatusAndManageToolsArePublishedWithExpectedInputs()
    {
        Assert.Contains(ToolNames.StagingStatus, _server.PublishedTools.Keys);
        Assert.Contains(ToolNames.StagingManage, _server.PublishedTools.Keys);
        var manageSchema = _server.PublishedTools[ToolNames.StagingManage].GetProperty("inputSchema");
        var required = manageSchema.GetProperty("required").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Contains("transaction_id", required);
        Assert.Contains("action", required);
    }

    [Theory]
    [InlineData("after_begin_journal")]
    [InlineData("after_payload_flush")]
    [InlineData("after_prepared_journal")]
    [InlineData("after_committing_journal")]
    [InlineData("after_publish")]
    public void FaultAtEveryPreCompletionTransitionIsRecoverable(string failAt)
    {
        using var fixture = new TransactionFixture();
        File.WriteAllText(fixture.Destination, "original");
        var fired = false;
        var coordinator = new AmethystTransactionCoordinator(fixture.State, point =>
        { if (!fired && point == failAt) { fired = true; throw new IOException("injected fault"); } });
        AmethystTransactionJournal? journal = null;
        Assert.Throws<IOException>(() =>
        {
            journal = coordinator.Begin(fixture.Request());
            using var locks = coordinator.AcquireWriteLocks(fixture.Library, fixture.ManagerLock);
            using var content = new MemoryStream(Encoding.UTF8.GetBytes("prepared output"));
            var artifact = coordinator.PrepareArtifact(journal.Id, "unit-1", fixture.Destination, "plugin", content, fixture.Library);
            coordinator.Publish(journal.Id, artifact.Id, locks);
        });
        Assert.True(fired);
        var transactionId = journal?.Id ?? Path.GetFileName(Assert.Single(Directory.GetDirectories(Path.Combine(fixture.State, "transactions"))));
        var recovered = new AmethystTransactionCoordinator(fixture.State).Manage(transactionId, AmethystTransactionAction.Recover);
        Assert.True(recovered.Success, recovered.Code);
        Assert.Equal("original", File.ReadAllText(fixture.Destination));
        Assert.True(recovered.SafeToRestartAmethyst);
    }

    [Fact]
    public void SharedLibraryLockSerializesIndependentProcesses()
    {
        using var fixture = new TransactionFixture();
        var coordinator = new AmethystTransactionCoordinator(fixture.State);
        using (coordinator.AcquireWriteLocks(fixture.Library, fixture.ManagerLock)) { }
        var lockHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(fixture.Library)))).ToLowerInvariant();
        var libraryLock = Path.Combine(coordinator.StateRoot, "locks", lockHash + ".lock");
        using var holder = Process.Start(new ProcessStartInfo("/usr/bin/flock")
        { ArgumentList = { "-x", libraryLock, "/bin/sleep", "4" }, UseShellExecute = false })!;
        try
        {
            Thread.Sleep(250);
            Assert.Throws<TimeoutException>(() => coordinator.AcquireWriteLocks(fixture.Library, fixture.ManagerLock, TimeSpan.FromMilliseconds(150)));
        }
        finally { if (!holder.HasExited) { holder.Kill(); holder.WaitForExit(); } }
    }

    [Fact]
    public void LostResponseAfterDurableCompletionIsVisibleWithoutReplay()
    {
        using var fixture = new TransactionFixture();
        var coordinator = new AmethystTransactionCoordinator(fixture.State, point =>
        { if (point == "after_completed_journal") throw new IOException("response lost after durable commit"); });
        var journal = coordinator.Begin(fixture.Request());
        using (var locks = coordinator.AcquireWriteLocks(fixture.Library, fixture.ManagerLock))
        {
            using var content = new MemoryStream(Encoding.UTF8.GetBytes("prepared output"));
            var artifact = coordinator.PrepareArtifact(journal.Id, "unit-1", fixture.Destination, "plugin", content, fixture.Library);
            Assert.Throws<IOException>(() => coordinator.Publish(journal.Id, artifact.Id, locks));
        }
        var status = Assert.Single(new AmethystTransactionCoordinator(fixture.State).Status(journal.Id));
        Assert.Equal("pending_deployment", status.State);
        Assert.Equal("prepared output", File.ReadAllText(fixture.Destination));
        Assert.True(File.Exists(Path.Combine(coordinator.StateRoot, "transactions", journal.Id, "receipt.json")));
    }

    [Fact]
    public void ParentSymlinkSwapAfterAnchoringCannotRedirectPublication()
    {
        using var fixture = new TransactionFixture();
        var parent = Path.GetDirectoryName(fixture.Destination)!;
        var moved = parent + ".held";
        var outside = Path.Combine(fixture.Library, "outside");
        Directory.CreateDirectory(outside);
        var coordinator = new AmethystTransactionCoordinator(fixture.State, point =>
        {
            if (point != "after_destination_parent_open") return;
            Directory.Move(parent, moved);
            Directory.CreateSymbolicLink(parent, outside);
        });
        var journal = coordinator.Begin(fixture.Request());
        using var locks = coordinator.AcquireWriteLocks(fixture.Library, fixture.ManagerLock);
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("prepared output"));
        var artifact = coordinator.PrepareArtifact(journal.Id, "unit-1", fixture.Destination, "plugin", content, fixture.Library);

        Assert.Throws<IOException>(() => coordinator.Publish(journal.Id, artifact.Id, locks));
        Assert.False(File.Exists(Path.Combine(outside, "patch.esp")));
        Assert.False(File.Exists(Path.Combine(moved, "patch.esp")));
    }

    [Fact]
    public void LeafReplacementAfterFinalCheckIsPreserved()
    {
        using var fixture = new TransactionFixture();
        File.WriteAllText(fixture.Destination, "original");
        var injected = false;
        var coordinator = new AmethystTransactionCoordinator(fixture.State, point =>
        {
            if (point != "after_final_destination_check" || injected) return;
            injected = true;
            File.Move(fixture.Destination, fixture.Destination + ".old");
            File.WriteAllText(fixture.Destination, "external replacement");
        });
        var journal = coordinator.Begin(fixture.Request());
        using var locks = coordinator.AcquireWriteLocks(fixture.Library, fixture.ManagerLock);
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("prepared output"));
        var artifact = coordinator.PrepareArtifact(journal.Id, "unit-1", fixture.Destination, "plugin", content, fixture.Library);

        Assert.Throws<IOException>(() => coordinator.Publish(journal.Id, artifact.Id, locks));
        Assert.Equal("external replacement", File.ReadAllText(fixture.Destination));
        Assert.Equal("original", File.ReadAllText(fixture.Destination + ".old"));
    }

    [Fact]
    public void RollbackRequiresRedeployAcknowledgementAndRestoresIndependentBackup()
    {
        using var fixture = new TransactionFixture();
        File.WriteAllText(fixture.Destination, "original");
        var coordinator = new AmethystTransactionCoordinator(fixture.State);
        var journal = coordinator.Begin(fixture.Request());
        using (var locks = coordinator.AcquireWriteLocks(fixture.Library, fixture.ManagerLock))
        {
            using var content = new MemoryStream(Encoding.UTF8.GetBytes("replacement"));
            var artifact = coordinator.PrepareArtifact(journal.Id, "unit-1", fixture.Destination, "plugin", content, fixture.Library);
            Assert.NotNull(artifact.BackupRelativePath);
            Assert.NotEqual(fixture.Destination, Path.Combine(coordinator.StateRoot, "transactions", journal.Id, artifact.BackupRelativePath!));
            coordinator.Publish(journal.Id, artifact.Id, locks);
            locks.ReleaseAndSeal(coordinator, journal.Id);
        }
        Assert.Equal("amethyst_redeploy_confirmation_required", coordinator.Manage(journal.Id, AmethystTransactionAction.Rollback).Code);
        var rolledBack = coordinator.Manage(journal.Id, AmethystTransactionAction.Rollback, confirmAmethystRedeploy: true);
        Assert.True(rolledBack.Success);
        Assert.Equal("original", File.ReadAllText(fixture.Destination));
        Assert.Equal("rolled_back", rolledBack.State);
    }

    [Fact]
    public void RecoveryRollsBackEveryArtifactInAnInterruptedLogicalUnit()
    {
        using var fixture = new TransactionFixture();
        File.WriteAllText(fixture.Destination, "plugin-original");
        var paired = Path.Combine(fixture.Library, "mods", "Patch", "meta.ini");
        File.WriteAllText(paired, "metadata-original");
        var publishCount = 0;
        var coordinator = new AmethystTransactionCoordinator(fixture.State, point =>
        { if (point == "after_publish" && ++publishCount == 2) throw new IOException("interrupted after second rename"); });
        var journal = coordinator.Begin(fixture.Request());
        using (var locks = coordinator.AcquireWriteLocks(fixture.Library, fixture.ManagerLock))
        {
            using var plugin = new MemoryStream(Encoding.UTF8.GetBytes("plugin-new"));
            using var meta = new MemoryStream(Encoding.UTF8.GetBytes("metadata-new"));
            var a = coordinator.PrepareArtifact(journal.Id, "unit-1", fixture.Destination, "plugin", plugin, fixture.Library);
            var b = coordinator.PrepareArtifact(journal.Id, "unit-1", paired, "metadata", meta, fixture.Library);
            coordinator.Publish(journal.Id, a.Id, locks);
            Assert.Throws<IOException>(() => coordinator.Publish(journal.Id, b.Id, locks));
        }
        var recovered = new AmethystTransactionCoordinator(fixture.State).Manage(journal.Id, AmethystTransactionAction.Recover);
        Assert.True(recovered.Success);
        Assert.Equal("plugin-original", File.ReadAllText(fixture.Destination));
        Assert.Equal("metadata-original", File.ReadAllText(paired));
    }

    sealed class TransactionFixture : IDisposable
    {
        readonly string _root = Path.Combine(Path.GetTempPath(), "housecarl-tx-test-" + Guid.NewGuid().ToString("N"));
        public string State => Path.Combine(_root, "state");
        public string Library => Path.Combine(_root, "library");
        public string ManagerLock => Path.Combine(Library, "filegraph.lock");
        public string Destination => Path.Combine(Library, "mods", "Patch", "patch.esp");
        public TransactionFixture()
        {
            Directory.CreateDirectory(Path.Combine(Library, "mods", "Patch"));
            File.WriteAllText(ManagerLock, "fixture lock; never Amethyst's live lock");
        }
        public AmethystTransactionRequest Request() => new("test", "skyrimse", "profile-test", "/fixture/manifest.json",
            Library, ManagerLock, 1, 1, "fixture-v1", Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant(),
            [new AmethystTransactionUnit("unit-1")]);
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    }
}
