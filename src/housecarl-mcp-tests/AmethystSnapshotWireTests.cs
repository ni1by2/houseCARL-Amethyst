using System.Text.Json;
using HousecarlMcp;
using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Real stdio server proof: explicit selection, restart, JSON/text warnings, exports and managed-write refusal.</summary>
[Trait("tier", "wire")]
public sealed class AmethystSnapshotWireTests
{
    /// <summary>Point only the child server at this synthetic connection and the generated full corpus.</summary>
    static Dictionary<string, string> EnvironmentFor(AmethystSnapshotWorld world, bool enabled) => new()
    {
        ["HOUSECARL_DATA_DIR"] = Path.GetDirectoryName(world.Store.FilePath)!,
        ["HouseCarl__ConnectionManifest"] = world.Manifest,
        ["HouseCarl__FilegraphMode"] = enabled ? "saved-snapshot" : "",
        ["HouseCarl__CorpusPath"] = TestCorpus.Path,
    };

    [Fact]
    public void ExplicitSelectionPersistsAndWorksAfterRestart()
    {
        using var world = new AmethystSnapshotWorld();
        var environment = EnvironmentFor(world, false);
        using (var server = new ServerFixture(environment))
        {
            Assert.Contains(ToolNames.SetAmethystConnection, server.PublishedNames);
            Assert.Equal(34, server.PublishedNames.Count);
            Assert.Contains("saved-snapshot", server.Call(ToolNames.Records, "{\"types\":[\"WEAP\"]}").Text);
            var rejected = server.Call(ToolNames.SetAmethystConnection, JsonSerializer.Serialize(new { manifest_path = world.Manifest, filegraph_mode = "automatic" }));
            Assert.True(rejected.IsError, rejected.Text);
            Assert.Contains("must explicitly", rejected.Text);
            Assert.False(File.Exists(world.Store.FilePath));
            var selection = server.Call(ToolNames.SetAmethystConnection, JsonSerializer.Serialize(new { manifest_path = world.Manifest, filegraph_mode = "saved-snapshot" }));
            Assert.False(selection.IsError, selection.Text);
            Assert.Contains(AmethystSnapshotProvenance.Warning, selection.Text);
            Assert.Equal("saved-snapshot", world.Store.Load().AmethystFilegraphMode);
            Assert.Contains("SnapshotBeta", server.Call(ToolNames.Records, "{\"types\":[\"WEAP\"]}").Text);
        }
        using var restarted = new ServerFixture(environment);
        var read = restarted.Call(ToolNames.Records, "{\"types\":[\"WEAP\"]}");
        Assert.False(read.IsError, read.Text);
        Assert.Contains("SnapshotBeta", read.Text);
        Assert.Contains(AmethystSnapshotProvenance.Warning, read.Text);
    }

    [Fact]
    public void JsonTextPagingAndExportsCarryTheSameProvenance()
    {
        using var world = new AmethystSnapshotWorld();
        using var server = new ServerFixture(EnvironmentFor(world, true));
        var text = server.Call(ToolNames.LoadOrderStatus, "{}");
        Assert.False(text.IsError, text.Text);
        Assert.Contains("Amethyst saved-snapshot mode", text.Text);
        Assert.Contains(AmethystSnapshotProvenance.Warning, text.Text);
        var asset = server.Call(ToolNames.AssetStatus, "{\"asset_paths\":[\"Meshes/example.nif\"],\"format\":\"json\"}");
        Assert.False(asset.IsError, asset.Text);
        using var assetJson = JsonDocument.Parse(asset.Text);
        Assert.Contains("Beta", asset.Text);
        Assert.Equal("unverified", assetJson.RootElement.GetProperty("amethyst_snapshot").GetProperty("freshness").GetString());
        var path = Path.Combine(world.Root, "wire-records.jsonl");
        var export = server.Call(ToolNames.Records, JsonSerializer.Serialize(new { types = new[] { "WEAP" }, format = "json", to_file = path }));
        Assert.False(export.IsError, export.Text);
        using var inline = JsonDocument.Parse(export.Text);
        using var manifest = JsonDocument.Parse(File.ReadLines(path).First());
        var stamp = inline.RootElement.GetProperty("amethyst_snapshot");
        Assert.Equal(stamp.GetRawText(), manifest.RootElement.GetProperty("amethyst_snapshot").GetRawText());
        Assert.Equal(stamp.GetProperty("generation").GetInt64(), export.Raw.GetProperty("structuredContent").GetProperty("amethyst_snapshot").GetProperty("generation").GetInt64());
        var page = server.Call(ToolNames.Records, "{\"types\":[\"WEAP\"],\"format\":\"json\",\"limit\":1,\"offset\":1}");
        using var pageJson = JsonDocument.Parse(page.Text);
        Assert.Equal("unverified", pageJson.RootElement.GetProperty("amethyst_snapshot").GetProperty("freshness").GetString());
        var spill = server.Call(ToolNames.Records, "{\"types\":[\"WEAP\"],\"format\":\"json\",\"max_chars\":200}");
        Assert.False(spill.IsError, spill.Text);
        using var spillJson = JsonDocument.Parse(spill.Text);
        Assert.True(spillJson.RootElement.TryGetProperty("spilled", out var spilled), spill.Text);
        var spillPath = spilled.GetProperty("path").GetString()!;
        using var spillManifest = JsonDocument.Parse(File.ReadLines(spillPath).First());
        Assert.Equal("unverified", spillManifest.RootElement.GetProperty("amethyst_snapshot").GetProperty("freshness").GetString());
    }

    [Fact]
    public void ExistingPluginWritesAreRefusedBeforeConsentOrStaging()
    {
        using var world = new AmethystSnapshotWorld();
        using var server = new ServerFixture(EnvironmentFor(world, true));
        var files = Directory.GetFiles(Path.Combine(world.Root, "mods"), "*", SearchOption.AllDirectories).Order().ToArray();
        foreach (var args in new[] { "{\"into\":\"Fixture.esp\"}", "{\"into\":\"Fixture.esp\",\"dry_run\":true}", "{\"in_place\":\"Fixture.esp\",\"acknowledge\":true}" })
        {
            var result = server.Call(ToolNames.Apply, args);
            Assert.True(result.IsError, result.Text);
            Assert.Contains("confirm_amethyst_redeploy=true", result.Text);
        }
        Assert.False(File.Exists(world.Store.FilePath));
        Assert.Equal(files, Directory.GetFiles(Path.Combine(world.Root, "mods"), "*", SearchOption.AllDirectories).Order());
    }

    [Fact]
    public void NewPatchWritesOnlyStagingAndReportsPendingDeployment()
    {
        using var world = new AmethystSnapshotWorld();
        var originals = Directory.GetFiles(world.Root, "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(p)));
        var source = Path.Combine(world.Root, "mods", "Beta", "Fixture.esp");
        var formid = SkyrimMod.CreateFromBinary(source, SkyrimRelease.SkyrimSE).Weapons.First().FormKey.ToString();
        File.WriteAllText(Path.Combine(world.Library, "filegraph.lock"), "");
        using var server = new ServerFixture(EnvironmentFor(world, true));
        var ops = new[] { new { formid, field_path = "EditorID", value = "PatchedWeapon" } };
        var preview = server.Call(ToolNames.Apply, JsonSerializer.Serialize(new { ops, patch = "VerifiedPatch", dry_run = true, format = "json" }));
        Assert.False(preview.IsError, preview.Text);
        Assert.False(Directory.Exists(Path.Combine(world.Root, "mods", "houseCARL - VerifiedPatch")));
        var result = server.Call(ToolNames.Apply, JsonSerializer.Serialize(new { ops, patch = "VerifiedPatch", format = "json" }));
        Assert.False(result.IsError, result.Text);
        using var json = JsonDocument.Parse(result.Text);
        var staging = json.RootElement.GetProperty("amethyst_staging");
        Assert.Equal("pending_deployment", staging.GetProperty("state").GetString());
        var output = Assert.Single(staging.GetProperty("outputs").EnumerateArray());
        var path = output.GetProperty("path").GetString()!;
        Assert.Equal(Path.Combine(world.Root, "mods", "houseCARL - VerifiedPatch", "VerifiedPatch.esp"), path);
        Assert.Equal("PatchedWeapon", Assert.Single(SkyrimMod.CreateFromBinary(path, SkyrimRelease.SkyrimSE).Weapons).EditorID);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), output.GetProperty("sha256").GetString());
        var meta = File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "meta.ini"));
        Assert.Contains("pendingDeployment=true", meta);
        Assert.Contains("patchSha256=", meta);
        Assert.True(result.Raw.GetProperty("structuredContent").TryGetProperty("amethyst_staging", out _));
        foreach (var (file, hash) in originals) Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(file)));
        Assert.False(File.Exists(world.Store.FilePath));
        Assert.Empty(Directory.GetFiles(Path.Combine(world.Root, "game", "Data")));
    }

    [Theory]
    [InlineData(ToolNames.Create)]
    [InlineData(ToolNames.Forward)]
    [InlineData(ToolNames.Copy)]
    public void OtherRecordWritersUseTheSameStagingTransaction(string tool)
    {
        using var world = new AmethystSnapshotWorld(linkedKeyword: tool == ToolNames.Copy);
        var source = Path.Combine(world.Root, "mods", "Beta", "Fixture.esp");
        var original = File.ReadAllBytes(source);
        var formid = SkyrimMod.CreateFromBinary(source, SkyrimRelease.SkyrimSE).Weapons.First().FormKey.ToString();
        File.WriteAllText(Path.Combine(world.Library, "filegraph.lock"), "");
        using var server = new ServerFixture(EnvironmentFor(world, true));
        var args = tool switch
        {
            ToolNames.Create => JsonSerializer.Serialize(new { records = new[] { new { record_type = "Keyword", editorid = "NewKeyword" } }, patch = "OtherPatch" }),
            ToolNames.Forward => JsonSerializer.Serialize(new { formids = new[] { formid }, source = "Fixture.esp", patch = "OtherPatch" }),
            _ => JsonSerializer.Serialize(new { from = formid, seed_paths = new[] { "Keywords" }, new_editorid = "ClonedWeapon", patch = "OtherPatch" })
        };
        var result = server.Call(tool, args);
        Assert.False(result.IsError, result.Text);
        Assert.Contains("pending deployment", result.Text);
        var metadata = result.Raw.GetProperty("structuredContent").GetProperty("amethyst_staging");
        Assert.Equal("pending_deployment", metadata.GetProperty("state").GetString());
        var path = Assert.Single(metadata.GetProperty("outputs").EnumerateArray()).GetProperty("path").GetString()!;
        var patch = SkyrimMod.CreateFromBinary(path, SkyrimRelease.SkyrimSE);
        if (tool == ToolNames.Create) Assert.Equal("NewKeyword", Assert.Single(patch.Keywords).EditorID);
        else Assert.Equal(tool == ToolNames.Copy ? "ClonedWeapon" : "SnapshotBeta0", Assert.Single(patch.Weapons).EditorID);
        if (tool == ToolNames.Copy)
        {
            var keyword = Assert.Single(patch.Keywords);
            Assert.Equal("SnapshotKeyword", keyword.EditorID);
            Assert.Equal(keyword.FormKey, Assert.Single(Assert.Single(patch.Weapons).Keywords!).FormKey);
        }
        Assert.Equal(original, File.ReadAllBytes(source));
    }

    [Fact]
    public void InPlaceRecordWriteRequiresBothConsentsAndCommitsTheOwnerMarkerWithThePlugin()
    {
        using var world = new AmethystSnapshotWorld();
        File.WriteAllText(Path.Combine(world.Library, "filegraph.lock"), "");
        var source = Path.Combine(world.Root, "mods", "Beta", "Fixture.esp");
        var formid = SkyrimMod.CreateFromBinary(source, SkyrimRelease.SkyrimSE).Weapons.First().FormKey.ToString();
        using var server = new ServerFixture(EnvironmentFor(world, true));
        var args = JsonSerializer.Serialize(new
        {
            ops = new[] { new { formid, field_path = "EditorID", value = "InPlaceVerified" } },
            in_place = "Fixture.esp", acknowledge = true, confirm_amethyst_redeploy = true,
            confirm_live_deployment_change = true, format = "json"
        });
        var result = server.Call(ToolNames.Apply, args);
        Assert.False(result.IsError, result.Text);
        Assert.Contains(SkyrimMod.CreateFromBinary(source, SkyrimRelease.SkyrimSE).Weapons, weapon => weapon.EditorID == "InPlaceVerified");
        var metadata = result.Raw.GetProperty("structuredContent").GetProperty("amethyst_staging");
        Assert.True(metadata.GetProperty("immediate_visibility").GetBoolean());
        Assert.Contains("editedInPlace=", File.ReadAllText(Path.Combine(world.Root, "mods", "Beta", "meta.ini")));
    }

    [Fact]
    public void RemoveUsesTheSameConsentAndTransactionLaneForExistingPlugins()
    {
        using var world = new AmethystSnapshotWorld();
        File.WriteAllText(Path.Combine(world.Library, "filegraph.lock"), "");
        var source = Path.Combine(world.Root, "mods", "Beta", "Fixture.esp");
        var plugin = SkyrimMod.CreateFromBinary(source, SkyrimRelease.SkyrimSE);
        var formid = plugin.Weapons.First().FormKey.ToString();
        using var server = new ServerFixture(EnvironmentFor(world, true));
        var args = JsonSerializer.Serialize(new
        {
            formids = new[] { formid }, in_place = "Fixture.esp", acknowledge = true,
            confirm_amethyst_redeploy = true, confirm_live_deployment_change = true, format = "json"
        });
        var result = server.Call(ToolNames.Remove, args);
        Assert.False(result.IsError, result.Text);
        Assert.DoesNotContain(SkyrimMod.CreateFromBinary(source, SkyrimRelease.SkyrimSE).Weapons,
            weapon => weapon.FormKey.ToString() == formid);
        Assert.True(result.Raw.GetProperty("structuredContent").GetProperty("amethyst_staging")
            .GetProperty("immediate_visibility").GetBoolean());
    }

    [Fact]
    public void CompactBuildsPluginAndSidecarsPrivatelyThenPublishesOneTransactionGroup()
    {
        using var world = new AmethystSnapshotWorld();
        File.WriteAllText(Path.Combine(world.Library, "filegraph.lock"), "");
        var source = Path.Combine(world.Root, "mods", "Beta", "Fixture.esp");
        var original = File.ReadAllBytes(source);
        using var server = new ServerFixture(EnvironmentFor(world, true));
        var result = server.Call(ToolNames.CompactPlugin, JsonSerializer.Serialize(new { source = "Fixture.esp", patch = "TxnCompact" }));
        Assert.False(result.IsError, result.Text);
        var output = Path.Combine(world.Root, "mods", "houseCARL - TxnCompact", "Fixture.esp");
        Assert.True(File.Exists(output));
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Contains("pendingDeployment=true", File.ReadAllText(Path.Combine(Path.GetDirectoryName(output)!, "meta.ini")));
        Assert.Contains("patchSha256=", File.ReadAllText(Path.Combine(Path.GetDirectoryName(output)!, "meta.ini")));
        Assert.True(result.Raw.GetProperty("structuredContent").GetProperty("amethyst_staging")
            .GetProperty("transaction_id").GetString() is { Length: 32 });
    }

    [Fact]
    public void MergeBuildsNewPluginAndMetadataInsideOneTransactionGroup()
    {
        using var world = new AmethystSnapshotWorld();
        File.WriteAllText(Path.Combine(world.Library, "filegraph.lock"), "");
        var source = Path.Combine(world.Root, "mods", "Beta", "Fixture.esp");
        var original = File.ReadAllBytes(source);
        using var server = new ServerFixture(EnvironmentFor(world, true));
        var result = server.Call(ToolNames.MergePlugins, "{\"plugins\":[\"Fixture.esp\"],\"patch\":\"TxnMerge\"}");
        Assert.False(result.IsError, result.Text);
        var output = Path.Combine(world.Root, "mods", "houseCARL - TxnMerge", "TxnMerge.esp");
        Assert.True(File.Exists(output));
        Assert.Equal(original, File.ReadAllBytes(source));
        var meta = File.ReadAllText(Path.Combine(Path.GetDirectoryName(output)!, "meta.ini"));
        Assert.Contains("pendingDeployment=true", meta);
        Assert.Contains("patchSha256=", meta);
        Assert.True(result.Raw.GetProperty("structuredContent").GetProperty("amethyst_staging")
            .GetProperty("transaction_id").GetString() is { Length: 32 });
    }

    [Fact]
    public void CompactInPlaceNeedsPerCallAmethystConsentsAndMarksImmediateVisibility()
    {
        using var world = new AmethystSnapshotWorld();
        File.WriteAllText(Path.Combine(world.Library, "filegraph.lock"), "");
        var source = Path.Combine(world.Root, "mods", "Beta", "Fixture.esp");
        using var server = new ServerFixture(EnvironmentFor(world, true));
        var denied = server.Call(ToolNames.CompactPlugin, "{\"source\":\"Fixture.esp\",\"in_place\":true,\"acknowledge\":true}");
        Assert.True(denied.IsError, denied.Text);
        Assert.Contains("confirm_amethyst_redeploy=true", denied.Text);

        var approved = server.Call(ToolNames.CompactPlugin,
            "{\"source\":\"Fixture.esp\",\"in_place\":true,\"acknowledge\":true,\"confirm_amethyst_redeploy\":true,\"confirm_live_deployment_change\":true}");
        Assert.False(approved.IsError, approved.Text);
        var metadata = approved.Raw.GetProperty("structuredContent").GetProperty("amethyst_staging");
        Assert.True(metadata.GetProperty("immediate_visibility").GetBoolean());
        Assert.Contains("editedInPlace=", File.ReadAllText(Path.Combine(Path.GetDirectoryName(source)!, "meta.ini")));
    }

    [Fact]
    public void HeaderOnlyPluginUsesTheSamePendingTransaction()
    {
        using var world = new AmethystSnapshotWorld();
        File.WriteAllText(Path.Combine(world.Library, "filegraph.lock"), "");
        using var server = new ServerFixture(EnvironmentFor(world, true));
        var result = server.Call(ToolNames.CreatePlugin, "{\"patch\":\"TransactionHeader\",\"esl\":true}");
        Assert.False(result.IsError, result.Text);
        var staging = result.Raw.GetProperty("structuredContent").GetProperty("amethyst_staging");
        Assert.Equal("pending_deployment", staging.GetProperty("state").GetString());
        var path = Assert.Single(staging.GetProperty("outputs").EnumerateArray()).GetProperty("path").GetString()!;
        Assert.True(SkyrimMod.CreateFromBinary(path, SkyrimRelease.SkyrimSE).IsSmallMaster);
        Assert.Contains("patchSha256=", File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "meta.ini")));
    }

    [Fact]
    public void IntoUpdatesOwnedPatchAndOwnerMetadataAsOneConsentedTransaction()
    {
        using var world = new AmethystSnapshotWorld();
        File.WriteAllText(Path.Combine(world.Library, "filegraph.lock"), "");
        var source = Path.Combine(world.Root, "mods", "Beta", "Fixture.esp");
        var formid = SkyrimMod.CreateFromBinary(source, SkyrimRelease.SkyrimSE).Weapons.First().FormKey.ToString();
        using var server = new ServerFixture(EnvironmentFor(world, true));
        var firstOps = new[] { new { formid, field_path = "EditorID", value = "BeforeInto" } };
        var first = server.Call(ToolNames.Apply, JsonSerializer.Serialize(new { ops = firstOps, patch = "IntoTransaction", format = "json" }));
        Assert.False(first.IsError, first.Text);
        var plugin = Path.Combine(world.Root, "mods", "houseCARL - IntoTransaction", "IntoTransaction.esp");
        var metaPath = Path.Combine(Path.GetDirectoryName(plugin)!, "meta.ini");
        var beforeMetaHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(metaPath))).ToLowerInvariant();
        var secondOps = new[] { new { formid, field_path = "EditorID", value = "AfterInto" } };
        var second = server.Call(ToolNames.Apply, JsonSerializer.Serialize(new
        {
            ops = secondOps, into = "IntoTransaction.esp", confirm_amethyst_redeploy = true,
            confirm_live_deployment_change = true, format = "json"
        }));
        Assert.False(second.IsError, second.Text);
        Assert.Equal("AfterInto", Assert.Single(SkyrimMod.CreateFromBinary(plugin, SkyrimRelease.SkyrimSE).Weapons).EditorID);
        var staging = second.Raw.GetProperty("structuredContent").GetProperty("amethyst_staging");
        Assert.True(staging.GetProperty("immediate_visibility").GetBoolean());
        Assert.Equal(plugin, Assert.Single(staging.GetProperty("outputs").EnumerateArray()).GetProperty("path").GetString());
        Assert.NotEqual(beforeMetaHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(metaPath))).ToLowerInvariant());
    }

    [Fact]
    public void KnownStaleInputsReturnAMcpError()
    {
        using var world = new AmethystSnapshotWorld();
        using var server = new ServerFixture(EnvironmentFor(world, true));
        File.AppendAllText(Path.Combine(world.Profile, "modlist.txt"), "-NewMod\n");
        var result = server.Call(ToolNames.Records, "{\"types\":[\"WEAP\"]}");
        Assert.True(result.IsError, result.Text);
        Assert.Contains("intent changed", result.Text);
        Assert.Contains("refresh", result.Text);
    }
}
