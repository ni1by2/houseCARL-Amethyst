using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

[Trait("tier", "unit")]
public sealed class AmethystNexusMetaTests
{
    [Fact]
    public void AmethystUpdateCacheReadsSnapshotMetaOfflineAndMarksDisabledMods()
    {
        using var world = new AmethystSnapshotWorld();
        Directory.CreateDirectory(Path.Combine(world.Library, "mods", "Beta"));
        Directory.CreateDirectory(Path.Combine(world.Library, "mods", "Gamma"));
        File.WriteAllText(Path.Combine(world.Library, "mods", "Beta", "meta.ini"), """
            [General]
            gameName=Skyrim Special Edition
            modid=10
            fileid=101
            version=1.0
            latestFileId=102
            latestVersion=2.0
            hasUpdate=true
            ignoreUpdate=false
            """);
        File.WriteAllText(Path.Combine(world.Library, "mods", "Gamma", "meta.ini"), """
            [General]
            modid=20
            fileid=202
            version=2.0
            latestVersion=2.0
            hasUpdate=false
            ignoreUpdate=false
            """);
        using var service = LoadOrderService.WithAmethystSnapshot(world.Manifest, world.Store);

        var data = service.UpdateCache();

        Assert.Equal("Amethyst", data.Manager);
        var beta = Assert.Single(data.Entries, entry => entry.ModId == 10);
        Assert.True(beta.Enabled);
        Assert.True(beta.CachedHasUpdate);
        Assert.Equal([101], beta.InstalledFileIds);
        var gamma = Assert.Single(data.Entries, entry => entry.ModId == 20);
        Assert.False(gamma.Enabled);
        Assert.False(gamma.CachedHasUpdate);
        Assert.Empty(data.Problems);
    }

    [Fact]
    public void ReadsOnlyAmethystsGeneralFieldsAndSingularInstalledFileId()
    {
        var meta = AmethystNexusMetaReader.ReadFromBytes(System.Text.Encoding.UTF8.GetBytes("""
            [installedFiles]
            fileid=999
            newestVersion=MO2
            [General]
            gameName=Skyrim Special Edition
            modid=42
            fileid=1234
            version=1.0
            latestFileId=1235
            latestVersion=2.0
            hasUpdate=true
            ignoreUpdate=false
            ignoredVersion=1.5
            """));

        Assert.NotNull(meta);
        Assert.Equal(42, meta.ModId);
        Assert.Equal(1234, meta.FileId);
        Assert.Equal("2.0", meta.LatestVersion);
        Assert.True(meta.HasUpdate);
        Assert.False(meta.IgnoreUpdate);
    }

    [Fact]
    public void AmethystStatusUsesCachedFlagsAndPreservesFileIdTokenAndDisabledState()
    {
        var data = new UpdateCacheData("/mods", null,
        [
            new("update", true, 10, "1.0", "2.0", null, null, [101], true, false),
            new("ignored", true, 20, "1.0", "2.0", "2.0", null, [202], true, true),
            new("current", true, 30, "2.0", "2.0", null, null, [303], false, false),
            new("disabled", false, 40, "1.0", "2.0", null, null, [404], true, false),
            new("manual-file", true, 50, "1.0", "2.0", null, null, [], true, false)
        ], [], 0, "Amethyst");

        var text = UpdateStatusWire.Render(data, 40000);
        Assert.Contains("Amethyst marks an update available (3)", text);
        Assert.Contains("Amethyst marks the available update ignored (1)", text);
        Assert.Contains("verify: 10#101", text);
        Assert.Contains("verify: 40#404", text);
        Assert.Contains("disabled  [disabled]  [id 40]", text);
        Assert.Contains("manual-file", text);
        Assert.Contains("no installed fileid", text);
        Assert.DoesNotContain("MO2 cached", text);
    }

    [Fact]
    public void AmethystInstalledFileTokenParsesAsAnExactNexusFileCheck()
    {
        var (pairs, bad) = NexusTools.ParseUpdatePairs("10#101");
        Assert.Empty(bad);
        var pair = Assert.Single(pairs);
        Assert.Equal(10, pair.modId);
        Assert.Equal([101], pair.fileIds);
    }
}
