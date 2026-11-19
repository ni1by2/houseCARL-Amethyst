using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Upstream 2.2 selection and refusal contracts through the real Linux snapshot and transaction filters.</summary>
public sealed class Amethyst220IntegrationTests
{
    [Fact]
    public void BatchDecompilePublishesCapturedScriptsAsOneStagedGroup()
    {
        using var fixture = new AmethystWireFixture();
        var world = fixture.World;
        foreach (var name in new[] { "HcBatchOne", "HcBatchTwo" })
        {
            var path = Path.Combine(world.Root, name + ".pex");
            PexWriter.WritePex(path, name, null);
            world.AddSavedFile("Beta", "Scripts/" + name + ".pex", File.ReadAllBytes(path));
        }
        var profile = Directory.GetFiles(world.Profile).ToDictionary(p => p, File.ReadAllBytes);
        var result = fixture.Server.Call(ToolNames.DecompileScript, JsonSerializer.Serialize(new
        {
            paths = new[] { "scripts/hcbatchone.pex", "scripts/hcbatchtwo.pex" }, patch = "BatchScripts", format = "json"
        }));
        Assert.False(result.IsError, result.Describe());
        using var body = JsonDocument.Parse(result.Text);
        Assert.Equal(2, body.RootElement.GetProperty("rows").GetArrayLength());
        var folder = Path.Combine(world.Library, "mods", "houseCARL - BatchScripts", "Source", "Scripts");
        Assert.Equal(2, Directory.GetFiles(folder, "*.psc").Length);
        foreach (var (path, bytes) in profile) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Contains("pending", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BatchBackendRefusalIsAnMcpErrorAndCreatesNoOutputFolder()
    {
        using var fixture = new AmethystWireFixture();
        var output = Path.Combine(fixture.World.Library, "mods", "houseCARL - RefusedBatch");
        var result = fixture.Server.Call(ToolNames.DecompileScript, JsonSerializer.Serialize(new
        {
            paths = new[] { "scripts/one.pex", "scripts/two.pex" }, patch = "RefusedBatch",
            decompiler_backend = "not-a-backend", format = "json"
        }));
        Assert.True(result.IsError, result.Describe());
        Assert.Contains("decompiler_backend", result.Text);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void PapyrusFreshnessAllowsOnlyTheTransactionsOwnNewEntries()
    {
        using var world = new AmethystSnapshotWorld();
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);
        _ = snapshot.CapturePapyrusTree(snapshot.ModsDir, ".psc");
        var output = Path.Combine(snapshot.ModsDir, "FreshOutput", "Source", "Scripts");
        var directories = new HashSet<string>(StringComparer.Ordinal);
        for (var at = output; at != snapshot.ModsDir; at = Path.GetDirectoryName(at)!) directories.Add(at);
        Directory.CreateDirectory(output);
        snapshot.CheckUnchanged(null, directories);
        var ownFile = Path.Combine(output, "Own.psc");
        File.WriteAllText(ownFile, "Scriptname Own");
        var outputs = new HashSet<string>(StringComparer.Ordinal) { ownFile };
        // The new folder was never an input subtree, but its new siblings still must be transaction-owned.
        var unexpected = Path.Combine(snapshot.ModsDir, "Unrelated.psc");
        File.WriteAllText(unexpected, "Scriptname Unrelated");
        Assert.Throws<InvalidOperationException>(() => snapshot.CheckUnchanged(outputs, directories));
        File.Delete(unexpected);
        snapshot.CheckUnchanged(outputs, directories);
    }

    [Fact]
    public void BatchDecompileRetainsTheNativeChampollionBackend()
    {
        if (!OperatingSystem.IsLinux()) return;
        var caprica = Environment.GetEnvironmentVariable("HOUSECARL_TEST_CAPRICA");
        if (string.IsNullOrWhiteSpace(caprica)) return; // native acceptance runs provide the packaged utilities
        var executable = Path.Combine(Path.GetDirectoryName(caprica)!, "champollion");
        Assert.True(File.Exists(executable), executable);
        using var fixture = new AmethystWireFixture();
        var world = fixture.World;
        var inputs = new List<string>();
        foreach (var name in new[] { "HcNativeBatchOne", "HcNativeBatchTwo" })
        {
            var path = Path.Combine(world.Root, name + ".pex");
            PexWriter.WritePex(path, name, null);
            inputs.Add(path);
        }
        var configured = fixture.Server.Call(ToolNames.SetToolPath, JsonSerializer.Serialize(new { tool = "champollion", path = executable }));
        Assert.False(configured.IsError, configured.Describe());
        var outputRoot = Directory.CreateTempSubdirectory("hc-native-batch-").FullName;
        try
        {
            var output = Path.Combine(outputRoot, "sources");
            var result = fixture.Server.Call(ToolNames.DecompileScript, JsonSerializer.Serialize(new
            {
                paths = inputs, out_path = output, decompiler_backend = "champollion", format = "json"
            }));
            Assert.False(result.IsError, result.Describe());
            Assert.True(Directory.Exists(output), result.Describe());
            Assert.Equal(2, Directory.GetFiles(output, "*.psc").Length);
            Assert.Contains("Champollion reconstruction is unverified", result.Text);
        }
        finally { Directory.Delete(outputRoot, true); }
    }

    [Fact]
    public void AmbiguousLinuxAssetDirectoriesRefuseInsteadOfChoosingOne()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("hc-linux-ambiguous-").FullName;
        try
        {
            foreach (var name in new[] { "Textures", "textures" })
            {
                Directory.CreateDirectory(Path.Combine(root, "Mod", name));
                File.WriteAllText(Path.Combine(root, "Mod", name, "same.dds"), name);
            }
            using var resolver = AssetResolver.Build("", root, "", ["Mod"], []);
            Assert.Throws<AmbiguousBethesdaPathException>(() => resolver.Resolve(@"textures\same.dds"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void LinuxJoinKeepsExistingParentCasingForANewOutputAndRejectsTraversal()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("hc-linux-join-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Meshes", "Armor"));
            Assert.Equal(Path.Combine(root, "Meshes", "Armor", "new.nif"), LooseListings.OnDisk(root, @"meshes\armor\new.nif"));
            Assert.Equal(root, LooseListings.OnDisk(root, ""));
            Assert.Throws<ArgumentException>(() => LooseListings.OnDisk(root, @"..\escape.nif"));
        }
        finally { Directory.Delete(root, true); }
    }
}
