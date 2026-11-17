using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

[Trait("tier", "integration")]
public sealed class AmethystPluginActivationTests
{
    [Theory]
    [InlineData(9, 13)]
    [InlineData(10, 14)]
    [InlineData(10, 15)]
    [InlineData(10, 18)]
    [InlineData(10, 19)]
    public void DisabledModOrderEntriesAreInactiveWithoutAnExplicitUncheckedLine(int schema, int rules)
    {
        using var world = new AmethystSnapshotWorld(schemaVersion: schema, rulesRevision: rules,
            pluginsText: "*Fixture.esp\n", loadorderText: "Fixture.esp\nDormant.esp\nRemoved.esp\n");
        var before = File.ReadAllBytes(Path.Combine(world.Profile, "loadorder.txt"));
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);
        Assert.Equal("Fixture.esp", Path.GetFileName(Assert.Single(snapshot.OrderedPluginPaths)));
        Assert.Contains("Dormant.esp", snapshot.Composition.InactivePluginNames);
        Assert.Contains("Removed.esp", snapshot.Composition.InactivePluginNames);
        Assert.DoesNotContain("Dormant.esp", snapshot.Composition.ImplicitPluginNames);
        Assert.Contains("/Gamma/Dormant.esp", snapshot.LocatePlugin("Dormant.esp"));
        snapshot.CheckUnchanged();
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(world.Profile, "loadorder.txt")));
    }

    [Theory]
    [InlineData(9, 13)]
    [InlineData(10, 14)]
    [InlineData(10, 15)]
    [InlineData(10, 18)]
    [InlineData(10, 19)]
    public void AnEnabledSourceDoesNotActivateAnOrderOnlyPluginOrItsVanillaArchive(int schema, int rules)
    {
        using var world = new AmethystSnapshotWorld(schemaVersion: schema, rulesRevision: rules,
            pluginsText: "*Fixture.esp\n", loadorderText: "Fixture.esp\nHistorical.esp\n");
        world.AddSavedFile("Beta", "Historical.esp", File.ReadAllBytes(Path.Combine(world.Library, "mods", "Beta", "Fixture.esp")));
        File.Copy(Path.Combine(world.Library, "mods", "Alpha", "Fixture.bsa"), Path.Combine(world.Root, "game", "Data", "Historical.bsa"));
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);
        Assert.Equal("Fixture.esp", Path.GetFileName(Assert.Single(snapshot.OrderedPluginPaths)));
        Assert.DoesNotContain("Historical.esp", snapshot.Composition.ImplicitPluginNames);
        Assert.Contains("Historical.esp", snapshot.Composition.InactivePluginNames);
        Assert.DoesNotContain(snapshot.SavedArchives(), a => a.OwningPlugin == "Historical.esp");
        Assert.Contains(snapshot.SavedArchives(), a => a.OwningPlugin == "Fixture.esp");
        snapshot.CheckUnchanged();
        File.AppendAllText(Path.Combine(world.Profile, "plugins.txt"), "*Historical.esp\n");
        Assert.Contains("changed", Assert.Throws<InvalidOperationException>(snapshot.CheckUnchanged).Message);
    }

    [Theory]
    [InlineData(9, 13, true)]
    [InlineData(10, 14, true)]
    [InlineData(10, 15, true)]
    [InlineData(10, 18, true)]
    [InlineData(10, 19, true)]
    [InlineData(9, 13, false)]
    [InlineData(10, 19, false)]
    public void ExplicitlyActiveMissingPluginsRefuseEvenWithoutAnOrderEntry(int schema, int rules, bool ordered)
    {
        using var world = new AmethystSnapshotWorld(schemaVersion: schema, rulesRevision: rules,
            pluginsText: "*Fixture.esp\n*Missing.esp\n", loadorderText: "Fixture.esp\n" + (ordered ? "Missing.esp\n" : ""));
        Assert.Contains("active plugin 'Missing.esp'", Assert.Throws<InvalidOperationException>(() => AmethystSavedSnapshot.Capture(world.Manifest)).Message);
    }

    [Theory]
    [InlineData(9, 13)]
    [InlineData(10, 14)]
    [InlineData(10, 15)]
    [InlineData(10, 18)]
    [InlineData(10, 19)]
    public void ForcedMastersAndInstalledCccEntriesStayActiveWithoutPromotingOtherEsls(int schema, int rules)
    {
        using var world = new AmethystSnapshotWorld(schemaVersion: schema, rulesRevision: rules,
            pluginsText: "*Fixture.esp\nUnchecked.esp\n",
            loadorderText: "Skyrim.esm\ncc-test.esl\nFixture.esp\nHistorical.esl\nUnchecked.esp\n");
        var masters = new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" };
        foreach (var name in masters.Append("cc-test.esl").Append("Historical.esl").Append("Unchecked.esp"))
            File.WriteAllText(Path.Combine(world.Root, "game", "Data", name), "synthetic source");
        File.WriteAllText(Path.Combine(world.Root, "game", "Skyrim.ccc"), "cc-test.esl\ncc-missing.esl\n");
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);
        Assert.Equal(masters.Concat(new[] { "cc-test.esl", "Fixture.esp" }), snapshot.OrderedPluginPaths.Select(Path.GetFileName));
        Assert.Equal(masters.Append("cc-test.esl").ToHashSet(), snapshot.Composition.ImplicitPluginNames.ToHashSet());
        Assert.Contains("Historical.esl", snapshot.Composition.InactivePluginNames);
        Assert.Contains("Unchecked.esp", snapshot.Composition.InactivePluginNames);
        snapshot.CheckUnchanged();
    }

    [Theory]
    [InlineData(9, 13)]
    [InlineData(10, 14)]
    [InlineData(10, 15)]
    [InlineData(10, 18)]
    [InlineData(10, 19)]
    public void AnUncheckedPluginDoesNotActivateItsManagedArchiveMembers(int schema, int rules)
    {
        using var world = new AmethystSnapshotWorld(schemaVersion: schema, rulesRevision: rules,
            pluginsText: "Fixture.esp\n", loadorderText: "Fixture.esp\nDormant.esp\n");
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);
        const string member = "skse/plugins/skypatcher/weapon/bsaonly.ini";
        Assert.Empty(snapshot.OrderedPluginPaths);
        Assert.Empty(snapshot.SavedArchives());
        Assert.Empty(snapshot.ResolveAssets(member));
        Assert.DoesNotContain(snapshot.AssetPaths, path => path.Replace('\\', '/').Equals(member, StringComparison.OrdinalIgnoreCase));
        using var resolver = AssetResolver.BuildSavedSnapshot(snapshot);
        Assert.False(resolver.Resolve(member).Exists);
        Assert.False(snapshot.HasActiveProvider("Fixture.bsa"));
        Assert.True(snapshot.HasSavedProvider("Fixture.bsa"));
        snapshot.CheckUnchanged();
    }

    [Fact]
    public void RemovingAnInactiveArchiveWinnerRevealsTheNextActiveNativeContender()
    {
        using var world = new AmethystSnapshotWorld(schemaVersion: 10, rulesRevision: 19,
            fixtureName: "schema10-rules19-activation-snapshot.json", pluginsText: "*Active.esp\n");
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);
        const string member = "skse/plugins/skypatcher/weapon/bsaonly.ini";
        Assert.Equal("Active.esp", Path.GetFileName(Assert.Single(snapshot.OrderedPluginPaths)));
        var source = Assert.Single(snapshot.ResolveAssets(member));
        Assert.Equal("Active.bsa", source.ProviderName);
        Assert.Equal("Alpha", source.OwningMod);
        Assert.Equal("Active.bsa", Path.GetFileName(Assert.Single(snapshot.SavedArchives()).Path));
        using var resolver = AssetResolver.BuildSavedSnapshot(snapshot);
        Assert.Equal("Active.bsa", resolver.Resolve(member).Winner!.Source);
        snapshot.CheckUnchanged();
    }

    [Fact]
    public void AnIniListedManagedArchiveLoadsEvenWhenItsPluginIsInactive()
    {
        using var world = new AmethystSnapshotWorld(pluginsText: "Fixture.esp\n");
        WriteArchiveInputs(world, "[Archive]\nsResourceArchiveList=Fixture.bsa\n", null);
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);
        Assert.Empty(snapshot.OrderedPluginPaths);
        Assert.Equal(ArchiveDiscovery.IniArchiveOwner, Assert.Single(snapshot.SavedArchives()).OwningPlugin);
        Assert.Single(snapshot.ResolveAssets("skse/plugins/skypatcher/weapon/bsaonly.ini"));
        snapshot.CheckUnchanged();
    }

    [Theory]
    [InlineData("sResourceArchiveList")]
    [InlineData("sResourceArchiveList2")]
    public void CustomIniCanActivateAnArchiveWithoutDiscardingTheOtherBaseList(string customKey)
    {
        using var world = new AmethystSnapshotWorld(pluginsText: "Fixture.esp\n");
        var otherKey = customKey == "sResourceArchiveList" ? "sResourceArchiveList2" : "sResourceArchiveList";
        var data = Path.Combine(world.Root, "game", "Data");
        File.Copy(Path.Combine(world.Library, "mods", "Alpha", "Fixture.bsa"), Path.Combine(data, "Other.bsa"));
        WriteArchiveInputs(world, $"[Archive]\n{customKey}=\n{otherKey}=Other.bsa\n",
            $"[Archive]\n{customKey}=Fixture.bsa\n");
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);
        Assert.Empty(snapshot.OrderedPluginPaths);
        Assert.Equal(ArchiveDiscovery.IniArchiveOwner, Assert.Single(snapshot.SavedArchives(), a => Path.GetFileName(a.Path) == "Fixture.bsa").OwningPlugin);
        Assert.Contains(snapshot.SavedArchives(), a => Path.GetFileName(a.Path) == "Other.bsa");
        Assert.True(snapshot.HasActiveProvider("Fixture.bsa"));
        Assert.Contains(snapshot.ResolveAssets("skse/plugins/skypatcher/weapon/bsaonly.ini"), source => source.ProviderName == "Fixture.bsa");
        snapshot.CheckUnchanged();
    }

    [Theory]
    [InlineData("sResourceArchiveList", "Other.bsa")]
    [InlineData("sResourceArchiveList2", "Other.bsa")]
    [InlineData("sResourceArchiveList", "")]
    [InlineData("sResourceArchiveList2", "")]
    public void CustomIniReplacementOrEmptyListStopsLoadingAnInheritedArchive(string key, string replacement)
    {
        using var world = new AmethystSnapshotWorld(pluginsText: "Fixture.esp\n");
        WriteArchiveInputs(world, $"[Archive]\n{key}=Fixture.bsa\n", $"[Archive]\n{key}={replacement}\n");
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);
        Assert.DoesNotContain(snapshot.SavedArchives(), a => Path.GetFileName(a.Path) == "Fixture.bsa");
        Assert.Empty(snapshot.ResolveAssets("skse/plugins/skypatcher/weapon/bsaonly.ini"));
        Assert.False(snapshot.HasActiveProvider("Fixture.bsa"));
        snapshot.CheckUnchanged();
    }

    [Fact]
    public void CustomIniAloneLoadsItsArchiveButRetainsTheMissingBaseIniWarning()
    {
        using var world = new AmethystSnapshotWorld(pluginsText: "Fixture.esp\n");
        foreach (var name in new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" })
            File.WriteAllText(Path.Combine(world.Root, "game", "Data", name), "synthetic source");
        WriteArchiveInputs(world, "", "[Archive]\nsResourceArchiveList=Fixture.bsa\n");
        File.Delete(Path.Combine(world.Root, "prefix", "drive_c", "users", "steamuser", "Documents", "My Games", "Skyrim Special Edition", "Skyrim.ini"));
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);
        Assert.Single(snapshot.ResolveAssets("skse/plugins/skypatcher/weapon/bsaonly.ini"));
        Assert.True(snapshot.Provenance.VanillaArchivesOmitted);
        snapshot.CheckUnchanged();
    }

    static void WriteArchiveInputs(AmethystSnapshotWorld world, string skyrim, string? custom)
    {
        var ini = Path.Combine(world.Root, "prefix", "drive_c", "users", "steamuser", "Documents", "My Games", "Skyrim Special Edition");
        Directory.CreateDirectory(ini);
        File.WriteAllText(Path.Combine(ini, "Skyrim.ini"), skyrim);
        if (custom is not null) File.WriteAllText(Path.Combine(ini, "SkyrimCustom.ini"), custom);
        File.WriteAllText(Path.Combine(world.Root, "manager-config", "paths.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            staging_path = world.Root, game_path = Path.Combine(world.Root, "game"), prefix_path = Path.Combine(world.Root, "prefix")
        }));
    }
}
