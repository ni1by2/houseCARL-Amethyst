using System.Security.Cryptography;
using System.Text.Json;
using HousecarlMcp;
using ModelContextProtocol.Protocol;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Producer parity, structural refusal and source races against an independently generated catalog.</summary>
[Trait("tier", "integration")]
public sealed class AmethystSavedSnapshotTests
{
    [Fact]
    public void SavedWinnersAndProvidersMatchTheActualProducer()
    {
        using var world = new AmethystSnapshotWorld();
        var before = SHA256.HashData(File.ReadAllBytes(world.Database));
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);
        foreach (var expected in world.ExpectedWinners.EnumerateArray())
        {
            var destination = expected.GetProperty("destination").GetString()![5..];
            var sources = snapshot.ResolveAssets(destination);
            var ns = expected.GetProperty("namespace").GetString();
            var selected = sources.Where(s => ns == "archive" ? s.Kind == AssetKind.Bsa : s.Kind == AssetKind.Loose).ToList();
            Assert.NotEmpty(selected);
            Assert.Equal(expected.GetProperty("mod").GetString()!.Trim('[', ']').ToLowerInvariant(),
                (selected[0].OwningMod ?? selected[0].ProviderName).Trim('[', ']').ToLowerInvariant());
            Assert.Equal(expected.GetProperty("providers").EnumerateArray().Select(p => p.GetString()!.Trim('[', ']').ToLowerInvariant()).Reverse(),
                selected.Select(s => (s.OwningMod ?? s.ProviderName).Trim('[', ']').ToLowerInvariant()));
        }
        Assert.Single(snapshot.OrderedPluginPaths);
        Assert.Contains("/Beta/Fixture.esp", snapshot.OrderedPluginPaths[0]);
        Assert.Contains("/Gamma/Dormant.esp", snapshot.LocatePlugin("Dormant.esp"));
        snapshot.CheckUnchanged();
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(world.Database)));
    }

    [Theory]
    [InlineData("PRAGMA user_version=10", "unsupported")]
    [InlineData("UPDATE meta SET value=X'0E00000000000000' WHERE key='rules_revision'", "unsupported")]
    [InlineData("UPDATE meta SET value=X'00' WHERE key='ready'", "not ready")]
    [InlineData("UPDATE profiles SET ready=0", "incomplete")]
    [InlineData("UPDATE profiles SET inventory_generation=0", "behind")]
    [InlineData("DELETE FROM profile_mods WHERE mod_id=(SELECT mod_id FROM mods WHERE name_key='alpha')", "missing required")]
    [InlineData("UPDATE winners SET candidate_id=999999", "broken joins")]
    [InlineData("UPDATE destinations SET path_key=X'2E2E2F657363617065' WHERE destination_id=(SELECT MIN(destination_id) FROM destinations)", "unsafe")]
    [InlineData("UPDATE raw_files SET source_rel=X'2F746D702F657363617065' WHERE file_id=(SELECT MIN(file_id) FROM raw_files)", "unsafe")]
    [InlineData("UPDATE raw_files SET source_rel=X'FF' WHERE file_id=(SELECT MIN(file_id) FROM raw_files)", "cannot read")]
    [InlineData("UPDATE winners SET generation=generation+1", "inconsistent")]
    public void InconsistentCatalogsRefuse(string mutation, string error)
    {
        using var world = new AmethystSnapshotWorld();
        world.Execute(mutation);
        Assert.Contains(error, Assert.Throws<InvalidOperationException>(() => AmethystSavedSnapshot.Capture(world.Manifest)).Message);
    }

    [Fact]
    public void ProfileEditsRequireManagerRefresh()
    {
        using var world = new AmethystSnapshotWorld();
        File.AppendAllText(Path.Combine(world.Profile, "modlist.txt"), "-NewMod\n");
        Assert.Contains("intent changed", Assert.Throws<InvalidOperationException>(() => AmethystSavedSnapshot.Capture(world.Manifest)).Message);
    }

    [Fact]
    public void NewStagingFilesRemainOutsideTheWarnedSavedGeneration()
    {
        using var world = new AmethystSnapshotWorld();
        File.WriteAllText(Path.Combine(world.Root, "mods", "Beta", "Meshes", "new.nif"), "not scanned");
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);
        using var assets = AssetResolver.BuildSavedSnapshot(snapshot);
        Assert.False(assets.Resolve("Meshes/new.nif").Exists);
        Assert.DoesNotContain("meshes\\new.nif", assets.EnumerateUnder("Meshes"), StringComparer.OrdinalIgnoreCase);
        Assert.Contains("may be missing", AmethystSnapshotProvenance.Warning);
    }

    [Fact]
    public void ChangedSavedSourcesRefuse()
    {
        using var world = new AmethystSnapshotWorld();
        File.AppendAllText(Path.Combine(world.Root, "mods", "Beta", "Meshes", "example.nif"), "changed");
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);
        Assert.Contains("saved source changed", Assert.Throws<InvalidOperationException>(() => snapshot.ResolveAssets("meshes/example.nif")).Message);
    }

    [Fact]
    public void EscapingSymlinksRefuse()
    {
        using var world = new AmethystSnapshotWorld();
        var source = Path.Combine(world.Root, "mods", "Beta", "Meshes", "example.nif");
        var outside = Path.Combine(world.Root, "outside.nif");
        File.Copy(source, outside); File.Delete(source); File.CreateSymbolicLink(source, outside);
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);
        Assert.Contains("escapes", Assert.Throws<InvalidOperationException>(() => snapshot.ResolveAssets("meshes/example.nif")).Message);
    }

    [Theory]
    [InlineData("UPDATE profiles SET generation=generation+1")]
    [InlineData("UPDATE meta SET value=X'6300000000000000' WHERE key='inventory_generation'")]
    public void ProducerCommitsAfterCaptureRefuse(string mutation)
    {
        using var world = new AmethystSnapshotWorld();
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);
        world.Execute(mutation);
        Assert.Contains("changed during", Assert.Throws<InvalidOperationException>(snapshot.CheckUnchanged).Message);
    }

    [Fact]
    public void SourcesChangedAfterReadRefuse()
    {
        using var world = new AmethystSnapshotWorld();
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);
        var source = snapshot.ResolveAssets("meshes/example.nif")[0].LooseFilePath!;
        File.AppendAllText(source, "changed during call");
        Assert.Contains("source changed during", Assert.Throws<InvalidOperationException>(snapshot.CheckUnchanged).Message);
    }

    [Fact]
    public void ActiveDeploymentRequiresTheDistinctVanillaBackup()
    {
        using var world = new AmethystSnapshotWorld();
        File.WriteAllText(Path.Combine(world.Root, "manager-config", "deploy_state.json"), "{\"last_active_profile\":\"default\",\"deploy_active\":true}");
        Assert.Contains("Data_Core is missing", Assert.Throws<InvalidOperationException>(() => AmethystSavedSnapshot.Capture(world.Manifest)).Message);
        var core = Path.Combine(world.Root, "game", "Data_Core"); Directory.CreateDirectory(core);
        File.WriteAllText(Path.Combine(core, "Vanilla.nif"), "vanilla");
        File.WriteAllText(Path.Combine(world.Root, "game", "Data", "Merged.nif"), "deployed");
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);
        Assert.Equal(Path.Combine(core, "Vanilla.nif"), snapshot.ResolveAssets("vanilla.nif")[0].LooseFilePath);
        Assert.Empty(snapshot.ResolveAssets("merged.nif"));
    }

    [Fact]
    public void CoreRecordReadsUseRecordedPluginOwnership()
    {
        using var world = new AmethystSnapshotWorld();
        using var service = LoadOrderService.WithAmethystSnapshot(world.Manifest, world.Store);
        using var scope = AmethystSnapshotContext.Use(service.CaptureSavedSnapshot());
        var result = RecordsTools.Records(service, types: ["WEAP"], format: "json");
        Assert.Contains("SnapshotBeta", result);
        Assert.DoesNotContain("SnapshotAlpha", result);
        Assert.DoesNotContain("SnapshotGamma", result);
        Assert.Contains("SnapshotGamma", RecordsTools.Records(service, source: JsonSerializer.SerializeToElement("Dormant.esp"), types: ["WEAP"], format: "json"));
    }

    [Fact]
    public void ExportedResultsRetainWarningAndGeneration()
    {
        using var world = new AmethystSnapshotWorld();
        using var service = LoadOrderService.WithAmethystSnapshot(world.Manifest, world.Store);
        var snapshot = service.CaptureSavedSnapshot();
        using var scope = AmethystSnapshotContext.Use(snapshot);
        var output = Path.Combine(world.Root, "records.jsonl");
        RecordsTools.Records(service, types: ["WEAP"], to_file: output, format: "json");
        var (manifest, _, error) = ResultArtifact.ReadIdentity(output, File.ReadAllText(output));
        Assert.Null(error);
        Assert.Equal(snapshot.Provenance, manifest!.AmethystSnapshot);
        using var manifestJson = JsonDocument.Parse(File.ReadLines(output).First());
        Assert.Equal(AmethystSnapshotProvenance.Warning, manifestJson.RootElement.GetProperty("amethyst_snapshot").GetProperty("warning").GetString());
        snapshot.RemoveFailedArtifacts();
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void JsonResultsKeepTheirRowsAndStructuredWarning()
    {
        var provenance = new AmethystSnapshotProvenance("/profile", 7, 8);
        var result = new CallToolResult { Content = [new TextContentBlock { Text = "{\"rows\":[1,2]}" }] };
        AmethystSnapshotFilter.Decorate(result, provenance);
        using var inline = JsonDocument.Parse(((TextContentBlock)result.Content[0]).Text);
        Assert.Equal(2, inline.RootElement.GetProperty("rows").GetArrayLength());
        Assert.Equal("unverified", inline.RootElement.GetProperty("amethyst_snapshot").GetProperty("freshness").GetString());
        Assert.Equal(7, result.StructuredContent!.Value.GetProperty("amethyst_snapshot").GetProperty("generation").GetInt32());
    }

    [Fact]
    public void FailedReadRestoresAnExistingArtifact()
    {
        using var world = new AmethystSnapshotWorld();
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);
        using var scope = AmethystSnapshotContext.Use(snapshot);
        var path = Path.Combine(world.Root, "previous.jsonl");
        File.WriteAllText(path, "original artifact");
        using var target = ArtifactTarget.Named(path);
        using var writer = new ResultArtifact.Writer();
        var result = writer.Save(target, ToolNames.Records, [], "formid", ["formid"], "formid", 0, "epoch");
        Assert.Null(result.Error);
        world.Execute("UPDATE profiles SET generation=generation+1");
        Assert.Throws<InvalidOperationException>(snapshot.CheckUnchanged);
        snapshot.RemoveFailedArtifacts();
        Assert.Equal("original artifact", File.ReadAllText(path));
    }

    [Fact]
    public void UnindexedVanillaArchivesAreExplicitlyIncomplete()
    {
        using var world = new AmethystSnapshotWorld();
        File.Copy(Path.Combine(world.Root, "mods", "Alpha", "Fixture.bsa"), Path.Combine(world.Root, "game", "Data", "Vanilla.bsa"));
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);
        Assert.True(snapshot.Provenance.VanillaArchivesOmitted);
        using var assets = AssetResolver.BuildSavedSnapshot(snapshot);
        Assert.True(assets.ReadIncomplete);
        Assert.True(assets.Capture().ReadIncomplete);
        var result = new CallToolResult { Content = [new TextContentBlock { Text = "{}" }] };
        AmethystSnapshotFilter.Decorate(result, snapshot.Provenance);
        Assert.Contains("asset_coverage_warning", ((TextContentBlock)result.Content[0]).Text);
    }

    [Fact]
    public void SpecificLibraryAndNondefaultGameOverrideUseSelectedRoots()
    {
        using var world = new AmethystSnapshotWorld(profileSpecific: true, profileName: "other", gameOverride: true);
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);
        Assert.Equal(Path.Combine(world.Profile, "mods"), snapshot.ModsDir);
        Assert.Equal(Path.Combine(world.Root, "alternate-game"), snapshot.GameDir);
        Assert.StartsWith(Path.Combine(world.Profile, "mods", "Beta"), snapshot.OrderedPluginPaths[0]);
        Assert.Equal(Path.Combine(world.Profile, "overwrite", "Meshes", "overwrite.nif"), snapshot.ResolveAssets("meshes/overwrite.nif")[0].LooseFilePath);
    }

    [Fact]
    public void MissingCatalogIsNotCreatedByARead()
    {
        using var world = new AmethystSnapshotWorld();
        File.Delete(world.Database);
        Assert.Throws<IOException>(() => AmethystSavedSnapshot.Capture(world.Manifest));
        Assert.False(File.Exists(world.Database));
    }

    [Fact]
    public void BusyCatalogRefusesWithinTheBoundedTimeout()
    {
        using var world = new AmethystSnapshotWorld();
        using var writer = world.Open();
        using var command = writer.CreateCommand();
        command.CommandText = "BEGIN EXCLUSIVE"; command.ExecuteNonQuery();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var failure = Assert.Throws<InvalidOperationException>(() => AmethystSavedSnapshot.Capture(world.Manifest));
        Assert.Contains("locked", failure.Message);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(8), elapsed.Elapsed.ToString());
        command.CommandText = "ROLLBACK"; command.ExecuteNonQuery();
    }

    [Fact]
    public void WalReadsSeeCompleteCommittedGenerationsWithoutBlockingTheProducer()
    {
        using var world = new AmethystSnapshotWorld();
        world.Execute("PRAGMA journal_mode=WAL");
        using var writer = world.Open();
        using var command = writer.CreateCommand();
        command.CommandText = "BEGIN; UPDATE profiles SET generation=generation+1; UPDATE winners SET generation=generation+1";
        command.ExecuteNonQuery();
        var before = AmethystSavedSnapshot.Capture(world.Manifest);
        command.CommandText = "COMMIT"; command.ExecuteNonQuery();
        var after = AmethystSavedSnapshot.Capture(world.Manifest);
        Assert.Equal(before.Provenance.Generation + 1, after.Provenance.Generation);
        Assert.Throws<InvalidOperationException>(before.CheckUnchanged);
        after.CheckUnchanged();
    }
}
