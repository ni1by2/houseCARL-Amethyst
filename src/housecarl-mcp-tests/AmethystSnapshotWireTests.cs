using System.Text.Json;
using HousecarlMcp;
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
            Assert.Equal(32, server.PublishedNames.Count);
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
    public void ManagedWriteAndPreviewAreRefusedBeforeConsentOrStaging()
    {
        using var world = new AmethystSnapshotWorld();
        using var server = new ServerFixture(EnvironmentFor(world, true));
        var files = Directory.GetFiles(Path.Combine(world.Root, "mods"), "*", SearchOption.AllDirectories).Order().ToArray();
        foreach (var args in new[] { "{}", "{\"dry_run\":true}", "{\"in_place\":\"Fixture.esp\",\"acknowledge\":true}" })
        {
            var result = server.Call(ToolNames.Apply, args);
            Assert.True(result.IsError, result.Text);
            Assert.Contains("supports reads and analysis only", result.Text);
        }
        Assert.False(File.Exists(world.Store.FilePath));
        Assert.Equal(files, Directory.GetFiles(Path.Combine(world.Root, "mods"), "*", SearchOption.AllDirectories).Order());
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
