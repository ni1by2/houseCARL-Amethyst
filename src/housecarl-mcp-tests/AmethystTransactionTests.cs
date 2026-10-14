using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HousecarlMcp;
using ModelContextProtocol.Protocol;
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
        var rolledBackError = new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "error: producer failed" }] };
        AmethystStagingWrite.AttachFailureEvidence(rolledBackError,
            new AmethystTransactionCoordinator(fixture.State).ReadJournal(journal.Id), recovered, "profile-test", managed: true);
        Assert.Equal(journal.Id, rolledBackError.StructuredContent!.Value.GetProperty("amethyst_staging").GetProperty("transaction_id").GetString());
        Assert.Equal("rolled_back", rolledBackError.StructuredContent!.Value.GetProperty("amethyst_staging").GetProperty("state").GetString());

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
        var recoveryError = new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "error: producer failed" }] };
        AmethystStagingWrite.AttachFailureEvidence(recoveryError,
            new AmethystTransactionCoordinator(fixture.State).ReadJournal(journal2.Id), conflict, "profile-test", managed: true);
        var recoveryEvidence = recoveryError.StructuredContent!.Value.GetProperty("amethyst_staging");
        Assert.Equal(journal2.Id, recoveryEvidence.GetProperty("transaction_id").GetString());
        Assert.Equal("recovery_required", recoveryEvidence.GetProperty("state").GetString());
        Assert.NotEmpty(recoveryEvidence.GetProperty("diagnostics").EnumerateArray());
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
        var rolledBack = coordinator.Manage(journal.Id, AmethystTransactionAction.Rollback,
            confirmAmethystRedeploy: true, confirmLiveDeploymentChange: true);
        Assert.True(rolledBack.Success, $"{rolledBack.Code}: {string.Join("; ", rolledBack.Diagnostics)}");
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

    [Fact]
    public void MissingDestinationParentsAreCreatedByPublicationAndRemovedOnRollback()
    {
        using var fixture = new TransactionFixture();
        var destination = Path.Combine(fixture.Library, "mods", "new-mod", "SEQ", "patch.seq");
        var coordinator = new AmethystTransactionCoordinator(fixture.State);
        var journal = coordinator.Begin(fixture.Request() with { ImmediateVisibility = true });
        using var locks = coordinator.AcquireWriteLocks(fixture.Library, fixture.ManagerLock);
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("seq bytes"));
        var artifact = coordinator.PrepareArtifact(journal.Id, "unit-1", destination, "seq", content, fixture.Library);
        Assert.Equal(new[] { "mods/new-mod", "mods/new-mod/SEQ" }, artifact.MissingParentDirectories);
        Assert.False(Directory.Exists(Path.GetDirectoryName(destination)!));

        coordinator.Publish(journal.Id, artifact.Id, locks);
        Assert.Equal("seq bytes", File.ReadAllText(destination));
        locks.ReleaseAndSeal(coordinator, journal.Id);
        var rolledBack = coordinator.Manage(journal.Id, AmethystTransactionAction.Rollback,
            confirmAmethystRedeploy: true, confirmLiveDeploymentChange: true);
        Assert.True(rolledBack.Success, $"{rolledBack.Code}: {string.Join("; ", rolledBack.Diagnostics)}");
        Assert.False(Directory.Exists(Path.Combine(fixture.Library, "mods", "new-mod")));
    }

    [Fact]
    public void RecoveryRemovesParentsCreatedBeforePublicationAndSymlinkParentsAreRejected()
    {
        using var fixture = new TransactionFixture();
        var destination = Path.Combine(fixture.Library, "mods", "new-mod", "SEQ", "patch.seq");
        var coordinator = new AmethystTransactionCoordinator(fixture.State, point =>
        { if (point == "after_destination_parent_open") throw new IOException("simulated interruption"); });
        var journal = coordinator.Begin(fixture.Request());
        using (var locks = coordinator.AcquireWriteLocks(fixture.Library, fixture.ManagerLock))
        {
            using var content = new MemoryStream(Encoding.UTF8.GetBytes("seq bytes"));
            var artifact = coordinator.PrepareArtifact(journal.Id, "unit-1", destination, "seq", content, fixture.Library);
            Assert.Throws<IOException>(() => coordinator.Publish(journal.Id, artifact.Id, locks));
        }
        var recovered = new AmethystTransactionCoordinator(fixture.State).Manage(journal.Id, AmethystTransactionAction.Recover);
        Assert.True(recovered.Success, recovered.Code);
        Assert.False(Directory.Exists(Path.Combine(fixture.Library, "mods", "new-mod")));

        var outside = Path.Combine(Path.GetTempPath(), "housecarl-tx-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        var symlink = Path.Combine(fixture.Library, "mods", "redirect");
        Directory.CreateSymbolicLink(symlink, outside);
        var rejectedJournal = new AmethystTransactionCoordinator(fixture.State).Begin(fixture.Request());
        using var rejectedLocks = new AmethystTransactionCoordinator(fixture.State).AcquireWriteLocks(fixture.Library, fixture.ManagerLock);
        using var rejectedContent = new MemoryStream(Encoding.UTF8.GetBytes("should not land"));
        Assert.Throws<IOException>(() => new AmethystTransactionCoordinator(fixture.State).PrepareArtifact(
            rejectedJournal.Id, "unit-1", Path.Combine(symlink, "nested", "patch.seq"), "seq", rejectedContent, fixture.Library));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        Directory.Delete(symlink);
        Directory.Delete(outside);
    }

    [Fact]
    public void RecoveryOfHalfPublishedPairPreservesEarlierIndependentPlacementUnit()
    {
        using var fixture = new TransactionFixture();
        var independent = fixture.Destination;
        var pairMesh = Path.Combine(fixture.Library, "mods", "Patch", "face.nif");
        var pairTint = Path.Combine(fixture.Library, "mods", "Patch", "face.dds");
        var pairVoice = Path.Combine(fixture.Library, "mods", "Patch", "face.tri");
        var publishes = 0;
        var coordinator = new AmethystTransactionCoordinator(fixture.State, point =>
        { if (point == "after_publish" && ++publishes == 3) throw new IOException("interrupted after first paired half"); });
        var request = fixture.Request() with { Units = [new AmethystTransactionUnit("place-0"), new AmethystTransactionUnit("place-1")] };
        var journal = coordinator.Begin(request);
        using (var locks = coordinator.AcquireWriteLocks(fixture.Library, fixture.ManagerLock))
        {
            using var a = new MemoryStream(Encoding.UTF8.GetBytes("independent"));
            using var mesh = new MemoryStream(Encoding.UTF8.GetBytes("mesh"));
            using var tint = new MemoryStream(Encoding.UTF8.GetBytes("tint"));
            using var voice = new MemoryStream(Encoding.UTF8.GetBytes("tri"));
            var independentArtifact = coordinator.PrepareArtifact(journal.Id, "place-0", independent, "asset", a, fixture.Library);
            var meshArtifact = coordinator.PrepareArtifact(journal.Id, "place-1", pairMesh, "asset", mesh, fixture.Library);
            var tintArtifact = coordinator.PrepareArtifact(journal.Id, "place-1", pairTint, "asset", tint, fixture.Library);
            _ = coordinator.PrepareArtifact(journal.Id, "place-1", pairVoice, "asset", voice, fixture.Library);
            coordinator.Publish(journal.Id, independentArtifact.Id, locks);
            coordinator.Publish(journal.Id, meshArtifact.Id, locks);
            Assert.Equal(AmethystTransactionState.Prepared, coordinator.ReadJournal(journal.Id).State);
            Assert.Throws<IOException>(() => coordinator.Publish(journal.Id, tintArtifact.Id, locks));
        }
        var recovered = new AmethystTransactionCoordinator(fixture.State).Manage(journal.Id, AmethystTransactionAction.Recover);
        Assert.True(recovered.Success, recovered.Code);
        Assert.Equal("independent", File.ReadAllText(independent));
        Assert.False(File.Exists(pairMesh));
        Assert.False(File.Exists(pairTint));
        var final = coordinator.ReadJournal(journal.Id);
        Assert.False(File.Exists(pairVoice));
        Assert.Equal(AmethystTransactionState.Partial, final.State);
        Assert.DoesNotContain(final.Artifacts, artifact => artifact.UnitId == "place-1" && artifact.State == "prepared");
        Assert.Equal(AmethystUnitState.RolledBack, final.Units.Single(unit => unit.Id == "place-1").State);
    }

    [Fact]
    public void ExternalExportCreatesMissingOutputParentsAndIsCompletedWithoutDeploymentClaim()
    {
        using var fixture = new TransactionFixture();
        var exportRoot = Path.Combine(Path.GetTempPath(), "housecarl-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(exportRoot);
        try
        {
            var coordinator = new AmethystTransactionCoordinator(fixture.State);
            var request = fixture.Request() with
            {
                LibraryPath = exportRoot,
                Managed = false,
                Units = [new AmethystTransactionUnit("external-output")]
            };
            var journal = coordinator.Begin(request);
            using var locks = coordinator.AcquireExternalWriteLock(exportRoot);
            using var content = new MemoryStream(Encoding.UTF8.GetBytes("export"));
            var destination = Path.Combine(exportRoot, "nested", "output.bin");
            var artifact = coordinator.PrepareArtifact(journal.Id, "external-output", destination, "asset", content, exportRoot);
            coordinator.Publish(journal.Id, artifact.Id, locks);
            var sealedJournal = locks.ReleaseAndSeal(coordinator, journal.Id);
            Assert.False(sealedJournal.Managed);
            Assert.False(sealedJournal.PendingDeployment);
            Assert.Equal(AmethystTransactionState.Completed, sealedJournal.State);
            Assert.Equal("export", File.ReadAllText(destination));
            Assert.DoesNotContain(sealedJournal.Artifacts, a => a.Role == "owner_metadata");
        }
        finally { Directory.Delete(exportRoot, recursive: true); }
    }

    [Fact]
    public void FailedPlacementUnitDoesNotPublishDeferredOwnerMetadata()
    {
        using var fixture = new TransactionFixture();
        var coordinator = new AmethystTransactionCoordinator(fixture.State);
        var request = fixture.Request() with { Units = [new AmethystTransactionUnit("place-0")] };
        var journal = coordinator.Begin(request);
        using var locks = coordinator.AcquireWriteLocks(fixture.Library, fixture.ManagerLock);
        using var scope = new AmethystTransactionWriteScope(coordinator, journal, locks, fixture.Library,
            () => { }, unitId: "place-0", metadataUnitId: "place-0");
        scope.DeferOwnerMetadata(Path.Combine(fixture.Library, "mods", "Patch", "meta.ini"), "owner data");
        scope.FailUnit("place-0", "asset source was unavailable");
        scope.PublishOwnerMetadata();
        Assert.Empty(scope.Journal.Artifacts);
        Assert.Equal(AmethystTransactionState.Partial, scope.Journal.State);
    }

    [Fact]
    public void OwnerMetadataFollowsFirstSuccessfulAssetUnitAfterEarlierPlacementRefusal()
    {
        using var fixture = new TransactionFixture();
        var coordinator = new AmethystTransactionCoordinator(fixture.State);
        var request = fixture.Request() with { Units = [new AmethystTransactionUnit("place-0"), new AmethystTransactionUnit("place-1")] };
        var journal = coordinator.Begin(request);
        using var locks = coordinator.AcquireWriteLocks(fixture.Library, fixture.ManagerLock);
        using var scope = new AmethystTransactionWriteScope(coordinator, journal, locks, fixture.Library,
            () => { }, unitId: "place-0", metadataUnitId: "managed-output");
        var metaPath = Path.Combine(fixture.Library, "mods", "Patch", "meta.ini");
        var assetPath = Path.Combine(fixture.Library, "mods", "Patch", "meshes", "placed.nif");
        var staged = Path.Combine(Path.GetDirectoryName(fixture.State)!, "placed.nif");
        File.WriteAllText(staged, "placed mesh");
        scope.DeferOwnerMetadata(metaPath, "owner data");
        scope.FailUnit("place-0", "this placement source was unavailable");
        scope.CommitArtifactGroup("place-1", [(staged, assetPath, "asset")]);
        scope.PublishOwnerMetadata();
        var records = scope.Journal.Artifacts;
        var asset = Assert.Single(records, a => a.Role == "asset");
        var metadata = Assert.Single(records, a => a.Role == "owner_metadata");
        Assert.Equal("place-1", asset.UnitId);
        Assert.Equal(asset.UnitId, metadata.UnitId);
        Assert.Equal("committed", metadata.State);
        Assert.Equal("owner data", File.ReadAllText(metaPath));
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
