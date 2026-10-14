using System.Security.Cryptography;
using System.Text.Json;
using HousecarlMcp;
using ModelContextProtocol.Protocol;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

sealed class NativeFilegraphFactAttribute : FactAttribute
{
    public NativeFilegraphFactAttribute()
    {
        var extension = Environment.GetEnvironmentVariable("AMETHYST_FILEGRAPH_EXTENSION")
            ?? Path.Combine(AppContext.BaseDirectory, "amethyst_filegraph.abi3.so");
        if (!File.Exists(extension)) Skip = "native Amethyst filegraph extension is not present in this test environment";
    }
}

/// <summary>Producer parity, structural refusal and source races against an independently generated catalog.</summary>
[Trait("tier", "integration")]
public sealed class AmethystSavedSnapshotTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AProfileRaceRollsBackTheNewStagedPluginAndMetadata(bool unrelatedFile)
    {
        using var world = new AmethystSnapshotWorld();
        var source = Path.Combine(world.Root, "mods", "Beta", "Fixture.esp");
        var original = File.ReadAllBytes(source);
        var formid = SkyrimMod.CreateFromBinary(source, SkyrimRelease.SkyrimSE).Weapons.First().FormKey.ToString();
        using var service = LoadOrderService.WithAmethystSnapshot(world.Manifest, world.Store);
        var snapshot = service.CaptureSavedSnapshot();
        using var scope = AmethystSnapshotContext.Use(snapshot);
        File.WriteAllText(Path.Combine(world.Library, "filegraph.lock"), "");
        using var staging = new AmethystStagingWrite(snapshot, ToolNames.Apply, "fixture request");
        var ops = JsonSerializer.SerializeToElement(new[] { new { formid, field_path = "EditorID", value = "RacedPatch" } });
        var reply = ApplyTools.Apply(service, ops: ops, patch: "RacedPatch", format: "json");
        using var json = JsonDocument.Parse(reply);
        Assert.False(json.RootElement.TryGetProperty("error", out _), reply);
        var folder = Path.Combine(world.Root, "mods", "houseCARL - RacedPatch");
        Assert.True(File.Exists(Path.Combine(folder, "RacedPatch.esp")), reply);
        if (unrelatedFile) File.WriteAllText(Path.Combine(folder, "External.txt"), "preserve this file");
        world.Execute("UPDATE profiles SET generation=generation+1");
        Assert.Contains("changed during", Assert.Throws<InvalidOperationException>(snapshot.CheckUnchanged).Message);
        staging.Abort();
        Assert.False(File.Exists(Path.Combine(folder, "RacedPatch.esp")));
        Assert.False(File.Exists(Path.Combine(folder, "meta.ini")));
        Assert.Equal(unrelatedFile, Directory.Exists(folder));
        if (unrelatedFile) Assert.Equal("preserve this file", File.ReadAllText(Path.Combine(folder, "External.txt")));
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Empty(Directory.GetFiles(Path.Combine(world.Root, "game", "Data")));
    }

    [Fact]
    public void ReadCallersCannotAllocateAmethystOutputFolders()
    {
        using var world = new AmethystSnapshotWorld();
        using var service = LoadOrderService.WithAmethystSnapshot(world.Manifest, world.Store);
        using var scope = AmethystSnapshotContext.Use(service.CaptureSavedSnapshot());
        Assert.Contains("remain unavailable", Assert.Throws<InvalidOperationException>(() => service.ConfiguredRoots()).Message);
    }

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
    public void InconsistentCatalogsRefuse(string mutation, string error)
    {
        using var world = new AmethystSnapshotWorld();
        world.Execute(mutation);
        Assert.Contains(error, Assert.Throws<InvalidOperationException>(() => AmethystSavedSnapshot.Capture(world.Manifest)).Message);
    }

    [Theory]
    [InlineData("DELETE FROM winners WHERE destination_id IN (SELECT destination_id FROM destinations WHERE lower(path_display) LIKE '%.esp')")]
    [InlineData("UPDATE winners SET generation=0")]
    public void SqlWinnerProjectionDoesNotReplaceAmethystsOwnSnapshot(string projectionMutation)
    {
        using var world = new AmethystSnapshotWorld();
        world.Execute(projectionMutation);
        var before = SHA256.HashData(File.ReadAllBytes(world.Database));
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);
        Assert.Single(snapshot.OrderedPluginPaths);
        Assert.Contains("/Beta/Fixture.esp", snapshot.OrderedPluginPaths[0]);
        Assert.Equal("Beta", snapshot.ResolveAssets("Meshes/example.nif")[0].ProviderName);
        snapshot.CheckUnchanged();
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(world.Database)));
        Assert.False(File.Exists(Path.Combine(world.Library, "filegraph.cache")));
        Assert.False(File.Exists(Path.Combine(world.Library, "filegraph.lock")));
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

    [NativeFilegraphFact]
    public void InPlaceNifAssetOutputIsExcludedFromCapturedSourceDriftAfterJournaledCommit()
    {
        using var world = new AmethystSnapshotWorld();
        var source = Path.Combine(world.Root, "mods", "Beta", "Meshes", "example.nif");
        var original = File.ReadAllBytes(source);
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);
        _ = snapshot.ResolveAssets("meshes/example.nif");
        File.WriteAllText(Path.Combine(world.Library, "filegraph.lock"), "");
        using var scope = new AmethystStagingWrite(snapshot, ToolNames.NifSet, "nif in-place fixture", immediateVisibility: true);
        var staged = Path.Combine(world.Root, "edited.nif");
        var edited = original.Concat(new byte[] { 1, 2, 3 }).ToArray();
        File.WriteAllBytes(staged, edited);
        AmethystStagingWrite.RegisterOutputPath(source);
        AmethystStagingWrite.PublishManagedArtifact(staged, source, "asset");

        scope.ValidateSnapshotInputs();
        Assert.Equal(edited, File.ReadAllBytes(source));
        scope.Abort();
        Assert.Equal(original, File.ReadAllBytes(source));
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
    public void ShadowTreeUsesValidatedViewAndOriginalGameDataAsVanilla()
    {
        using var world = new AmethystSnapshotWorld();
        var game = Path.Combine(world.Root, "game");
        var state = Path.Combine(world.Profile, ".amethyst-vfs");
        var view = Path.Combine(state, "view");
        var rootUpper = Path.Combine(state, "root-upper");
        Directory.CreateDirectory(Path.Combine(view, "Data"));
        Directory.CreateDirectory(rootUpper);
        File.WriteAllText(Path.Combine(game, "Data", "Vanilla.nif"), "vanilla source");
        File.WriteAllText(Path.Combine(view, "Data", "Deployed.nif"), "deployed view");
        File.WriteAllText(Path.Combine(state, "manifest.json"), JsonSerializer.Serialize(new
        {
            version = 1, profile = "default", backend = "shadow-tree", game_root = game,
            data_root = Path.Combine(game, "Data"), view_root = view, root_layer = view,
            data_layer = Path.Combine(view, "Data"), root_upper = rootUpper,
            data_upper = Path.Combine(world.Library, "overwrite"), bind_hidden_source_symlinks = false
        }));

        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);
        Assert.True(snapshot.UsesShadowTreeVfs);
        Assert.False(snapshot.DeploymentTargetVerified, "the synthetic profile has no native committed deployment record");
        Assert.Equal(Path.Combine(view, "Data"), snapshot.DeploymentDataDir);
        Assert.Equal(Path.Combine(game, "Data"), snapshot.VanillaDataDir);
        Assert.Equal(Path.Combine(game, "Data", "Vanilla.nif"), snapshot.ResolveAssets("vanilla.nif")[0].LooseFilePath);
        Assert.Empty(snapshot.ResolveAssets("deployed.nif"));
        snapshot.CheckUnchanged();
        File.WriteAllText(Path.Combine(state, "pending"), "default\n");
        Assert.Contains("changed during the call", Assert.Throws<InvalidOperationException>(snapshot.CheckUnchanged).Message);
    }

    [Fact]
    public void EffectiveProfileIniAndCccDriveVanillaArchiveAndImplicitPluginCoverage()
    {
        using var world = new AmethystSnapshotWorld();
        var iniDir = Path.Combine(world.Profile, "ini files");
        Directory.CreateDirectory(iniDir);
        File.WriteAllText(Path.Combine(iniDir, "Skyrim.ini"), "[Archive]\nsResourceArchiveList=Vanilla.bsa\n");
        File.WriteAllText(Path.Combine(world.Root, "game", "Skyrim.ccc"), "cc-test.esl\n");
        File.WriteAllText(Path.Combine(world.Root, "game", "Data", "cc-test.esl"), "implicit plugin source");
        foreach (var name in new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" })
            File.WriteAllText(Path.Combine(world.Root, "game", "Data", name), "implicit master source");
        File.Copy(Path.Combine(world.Root, "mods", "Alpha", "Fixture.bsa"), Path.Combine(world.Root, "game", "Data", "Vanilla.bsa"));
        File.WriteAllText(Path.Combine(world.Root, "manager-config", "paths.json"), JsonSerializer.Serialize(new
        {
            staging_path = world.Root, game_path = Path.Combine(world.Root, "game"), profile_ini_files = true
        }));

        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);
        Assert.Equal(7, snapshot.OrderedPluginPaths.Count);
        Assert.Contains("cc-test.esl", snapshot.OrderedPluginPaths[^1]);
        Assert.Contains(snapshot.VanillaArchives, a => Path.GetFileName(a.Path) == "Vanilla.bsa");
        using var assets = AssetResolver.BuildSavedSnapshot(snapshot);
        var hit = assets.Resolve("skse/plugins/skypatcher/weapon/bsaonly.ini");
        Assert.True(hit.Exists);
        Assert.Contains(hit.Providers, p => p.Source == "Vanilla.bsa");
        Assert.False(assets.ReadIncomplete);
        snapshot.CheckUnchanged();
        File.WriteAllText(Path.Combine(iniDir, "new.ini"), "changed directory listing");
        Assert.Contains("effective INI directory changed", Assert.Throws<InvalidOperationException>(snapshot.CheckUnchanged).Message);
    }

    [Fact]
    public void MissingProfileIniFallsBackToPrefixAndTracksBothCandidates()
    {
        using var world = new AmethystSnapshotWorld();
        var profileIniDir = Path.Combine(world.Profile, "ini files");
        var prefixIniDir = Path.Combine(world.Root, "prefix", "drive_c", "users", "steamuser", "Documents", "My Games", "Skyrim Special Edition");
        Directory.CreateDirectory(profileIniDir);
        Directory.CreateDirectory(prefixIniDir);
        File.WriteAllText(Path.Combine(prefixIniDir, "Skyrim.ini"), "[Archive]\nsResourceArchiveList=Prefix.bsa\n");
        File.Copy(Path.Combine(world.Root, "mods", "Alpha", "Fixture.bsa"), Path.Combine(world.Root, "game", "Data", "Prefix.bsa"));
        File.WriteAllText(Path.Combine(world.Root, "manager-config", "paths.json"), JsonSerializer.Serialize(new
        {
            staging_path = world.Root, game_path = Path.Combine(world.Root, "game"),
            prefix_path = Path.Combine(world.Root, "prefix"), profile_ini_files = true
        }));

        var fallback = AmethystSavedSnapshot.Capture(world.Manifest);
        Assert.Equal(prefixIniDir, fallback.EffectiveIniDir);
        Assert.Contains(fallback.VanillaArchives, a => Path.GetFileName(a.Path) == "Prefix.bsa");
        fallback.CheckUnchanged();
        File.WriteAllText(Path.Combine(profileIniDir, "Skyrim.ini"), "[Archive]\nsResourceArchiveList=Profile.bsa\n");
        Assert.Contains("configuration or profile changed", Assert.Throws<InvalidOperationException>(fallback.CheckUnchanged).Message);

        var selectedProfile = AmethystSavedSnapshot.Capture(world.Manifest);
        Assert.Equal(profileIniDir, selectedProfile.EffectiveIniDir);
        File.Delete(Path.Combine(profileIniDir, "Skyrim.ini"));
        Assert.Contains("configuration or profile changed", Assert.Throws<InvalidOperationException>(selectedProfile.CheckUnchanged).Message);
    }

    [Fact]
    public void SkyrimCccEntriesWithoutAnInstalledPluginDoNotActivateArchives()
    {
        using var world = new AmethystSnapshotWorld();
        var iniDir = Path.Combine(world.Profile, "ini files");
        Directory.CreateDirectory(iniDir);
        File.WriteAllText(Path.Combine(iniDir, "Skyrim.ini"), "[Archive]\nsResourceArchiveList=Vanilla.bsa\n");
        File.WriteAllText(Path.Combine(world.Root, "game", "Skyrim.ccc"), "cc-not-installed.esl\n");
        File.WriteAllText(Path.Combine(world.Root, "game", "Data", "cc-not-installed.bsa"), "not active");
        File.WriteAllText(Path.Combine(world.Root, "manager-config", "paths.json"), JsonSerializer.Serialize(new
        {
            staging_path = world.Root, game_path = Path.Combine(world.Root, "game"), profile_ini_files = true
        }));

        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);
        Assert.DoesNotContain(snapshot.VanillaArchives, a => Path.GetFileName(a.Path) == "cc-not-installed.bsa");
    }

    [Fact]
    public void UnknownVfsBackendAndMalformedShadowRootsRefuseInsteadOfFallingBack()
    {
        using var world = new AmethystSnapshotWorld();
        var state = Path.Combine(world.Profile, ".amethyst-vfs");
        Directory.CreateDirectory(state);
        var manifest = Path.Combine(state, "manifest.json");
        File.WriteAllText(manifest, "{\"version\":1,\"profile\":\"default\",\"backend\":\"kernel-overlayfs\"}");
        Assert.Contains("backend or manifest version is unsupported", Assert.Throws<InvalidOperationException>(() => AmethystSavedSnapshot.Capture(world.Manifest)).Message);
        File.Delete(manifest);
        Assert.Contains("without a manifest", Assert.Throws<InvalidOperationException>(() => AmethystSavedSnapshot.Capture(world.Manifest)).Message);
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
