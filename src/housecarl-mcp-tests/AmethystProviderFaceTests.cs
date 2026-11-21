using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

public sealed class AmethystProviderFaceTests
{
    static FormKey AddNpc(AmethystSnapshotWorld world, string provider, string plugin)
    {
        FormKey key = default;
        world.EditPlugin(provider, plugin, mod =>
        {
            var race = mod.Races.AddNew("HcSavedFaceRace");
            race.Flags = Race.Flag.Playable | Race.Flag.FaceGenHead;
            var npc = mod.Npcs.AddNew("HcSavedFaceDonor");
            npc.Race.SetTo(race);
            key = npc.FormKey;
        });
        return key;
    }

    [Theory]
    [InlineData("*winner")]
    [InlineData("Data")]
    public void NonModFaceSelectorsKeepReadingTheActiveNpc(string provider)
    {
        using var fixture = new AmethystWireFixture();
        var world = fixture.World;
        var key = AddNpc(world, "Beta", "Fixture.esp");
        var mesh = FaceGenPath.For(key, FaceGenSlot.Mesh);
        var disk = BethesdaPath.Under(Path.Combine(world.Root, "game", "Data"), mesh);
        Directory.CreateDirectory(Path.GetDirectoryName(disk)!);
        File.WriteAllBytes(disk, NifSetMeshes.Textured([]));
        var result = fixture.Server.Call(ToolNames.NifInspect, JsonSerializer.Serialize(new
        {
            formids = new[] { $"{key.ID:X6}:{key.ModKey.FileName}" }, source_provider = provider, format = "json"
        }));
        Assert.False(result.IsError, result.Describe());
        using var body = JsonDocument.Parse(result.Text);
        var row = Assert.Single(body.RootElement.GetProperty("rows").EnumerateArray());
        Assert.Equal("ok", row.GetProperty("status").GetString());
        Assert.Equal(mesh, row.GetProperty("path").GetString(), ignoreCase: true);
    }

    [Fact]
    public void ACaseDistinctDonorPluginIsNotReplacedByTheActiveCopy()
    {
        using var world = new AmethystSnapshotWorld(schemaVersion: 10, rulesRevision: 19);
        var key = AddNpc(world, "beta", "Fixture.esp");
        var mesh = FaceGenPath.For(key, FaceGenSlot.Mesh);
        world.AddSavedFile("beta", mesh.Replace('\\', '/'), NifSetMeshes.Textured([]));
        using var service = LoadOrderService.WithAmethystSnapshot(world.Manifest, world.Store);
        var snapshot = service.CaptureSavedSnapshot();
        Assert.Contains(Path.Combine("Beta", "Fixture.esp"), Assert.Single(snapshot.OrderedPluginPaths));
        using var context = AmethystSnapshotContext.Use(snapshot);
        var result = NifTools.NifInspect(service, null, formids: new[] { $"{key.ID:X6}:{key.ModKey.FileName}" }, source_provider: "beta", format: "json");
        using var body = JsonDocument.Parse(result);
        var row = Assert.Single(body.RootElement.GetProperty("rows").EnumerateArray());
        Assert.Equal("ok", row.GetProperty("status").GetString());
        Assert.Equal(mesh, row.GetProperty("path").GetString(), ignoreCase: true);
        snapshot.CheckUnchanged();
    }

    [Theory]
    [InlineData(9, 13)]
    [InlineData(10, 14)]
    [InlineData(10, 15)]
    [InlineData(10, 18)]
    [InlineData(10, 19)]
    public void DonorPluginsUseSavedInventoryAndRefuseChangedSources(int schema, int rules)
    {
        using var world = new AmethystSnapshotWorld(schemaVersion: schema, rulesRevision: rules);
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);
        var path = Assert.Single(snapshot.ProviderPluginPaths("Gamma"));
        Assert.Equal(Path.Combine(world.Library, "mods", "Gamma", "Dormant.esp"), path);
        File.WriteAllText(Path.Combine(world.Library, "mods", "Gamma", "Unsaved.esp"), "not in saved inventory");
        Assert.Equal(path, Assert.Single(snapshot.ProviderPluginPaths("Gamma")));
        File.AppendAllText(path, "changed");
        Assert.Contains("changed", Assert.Throws<InvalidOperationException>(() => snapshot.ProviderPluginPaths("Gamma")).Message);
    }

    [Fact]
    public void DonorPluginsKeepCaseDistinctProviderIdentities()
    {
        using var world = new AmethystSnapshotWorld(schemaVersion: 10, rulesRevision: 19);
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);
        var upper = Assert.Single(snapshot.ProviderPluginPaths("Beta"));
        var lower = Assert.Single(snapshot.ProviderPluginPaths("beta"));
        Assert.Equal(Path.Combine(world.Library, "mods", "Beta", "Fixture.esp"), upper);
        Assert.Equal(Path.Combine(world.Library, "mods", "beta", "Fixture.esp"), lower);
        Assert.Contains("case-distinct", Assert.Throws<InvalidOperationException>(() => snapshot.ProviderPluginPaths("BETA")).Message);
    }

    [Fact]
    public void NifInspectReadsADisabledDonorsFaceThroughTheSavedSnapshot()
    {
        using var fixture = new AmethystWireFixture();
        var world = fixture.World;
        var key = AddNpc(world, "Gamma", "Dormant.esp");
        var mesh = FaceGenPath.For(key, FaceGenSlot.Mesh);
        world.AddSavedFile("Gamma", mesh.Replace('\\', '/'), NifSetMeshes.Textured([]));
        var before = Directory.GetFiles(world.Profile).ToDictionary(p => p, File.ReadAllBytes);
        var result = fixture.Server.Call(ToolNames.NifInspect, JsonSerializer.Serialize(new
        {
            formids = new[] { $"{key.ID:X6}:{key.ModKey.FileName}" }, source_provider = "Gamma", format = "json"
        }));
        Assert.False(result.IsError, result.Describe());
        using var body = JsonDocument.Parse(result.Text);
        var row = Assert.Single(body.RootElement.GetProperty("rows").EnumerateArray());
        Assert.Equal("ok", row.GetProperty("status").GetString());
        Assert.Equal(mesh, row.GetProperty("path").GetString(), ignoreCase: true);
        Assert.Contains("Gamma", result.Text);
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
    }
}
