using System.Security.Cryptography;
using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Strings;
using Mutagen.Bethesda.Strings.DI;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The new traversal surface against actual schema-9/schema-10 native saved fixtures, on Linux paths.</summary>
public sealed class AmethystAssetWalkTests
{
    const string Mesh = "Meshes/Test/Armor.nif";
    const string Texture = "Textures/Test/Armor.dds";

    sealed class TestEncoding(IMutagenEncoding encoding) : IMutagenEncodingProvider
    {
        public IMutagenEncoding GetEncoding(GameRelease release, Language language) => encoding;
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    public void RecordMeshAndTextureUseNativeProvidersAndKeepSourcesUnchanged(int schema)
    {
        using var world = new AmethystSnapshotWorld(schemaVersion: schema);
        world.AddSavedFile("Beta", Mesh, NifSetMeshes.Textured([Texture]));
        world.AddSavedFile("Alpha", Texture, [1, 2, 3]);
        world.EditPlugin("Beta", "Fixture.esp", mod => mod.Weapons.First().Model = new Model { File = Mesh });
        var before = Directory.GetFiles(world.Root, "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(p)));
        using var service = LoadOrderService.WithAmethystSnapshot(world.Manifest, world.Store);
        var snapshot = service.CaptureSavedSnapshot();
        using var context = AmethystSnapshotContext.Use(snapshot);
        var weapon = SkyrimMod.CreateFromBinary(snapshot.OrderedPluginPaths.Single(), SkyrimRelease.SkyrimSE).Weapons.First().FormKey;
        var walk = service.AssetWalk([], [weapon], null, null);
        Assert.Null(walk.Refusal);
        var mesh = Assert.Single(walk.Nodes.Where(n => n.Path.Equals(Mesh.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase)));
        Assert.Equal("Beta", mesh.Copy!.ProviderName);
        var texture = Assert.Single(walk.Nodes.Where(n => n.Path.Equals(Texture.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase)));
        Assert.Equal("Alpha", texture.Copy!.ProviderName);
        Assert.Equal(mesh.Path, texture.PulledBy);
        Assert.Equal(WalkStanding.Present, texture.Standing);
        snapshot.CheckUnchanged();
        foreach (var (path, hash) in before) Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    public void ExplicitDisabledDonorUsesSavedRoutingAndRefusesChangedSource(int schema)
    {
        using var world = new AmethystSnapshotWorld(schemaVersion: schema);
        world.AddSavedFile("Gamma", Mesh, NifSetMeshes.Textured([Texture]));
        world.AddSavedFile("Gamma", Texture, [1, 3, 5]);
        using var service = LoadOrderService.WithAmethystSnapshot(world.Manifest, world.Store);
        var snapshot = service.CaptureSavedSnapshot();
        using var context = AmethystSnapshotContext.Use(snapshot);
        var walk = service.AssetWalk([Mesh], [], null, "Gamma");
        Assert.Null(walk.Refusal);
        Assert.Equal(2, walk.Nodes.Count);
        Assert.All(walk.Nodes, node =>
        {
            Assert.Equal(WalkStanding.FromSource, node.Standing);
            Assert.True(node.Copy!.OffOrder);
            Assert.False(node.Copy.OwnerEnabled);
        });
        snapshot.CheckUnchanged();
        File.AppendAllText(BethesdaPath.Under(Path.Combine(world.Library, "mods", "Gamma"), Texture), "changed");
        Assert.Contains("changed", Assert.Throws<InvalidOperationException>(snapshot.CheckUnchanged).Message);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    public void ExclusionsAndProviderSelectorsUseTheSavedViewRatherThanDiskOrMo2Rules(int schema)
    {
        using var world = new AmethystSnapshotWorld(schemaVersion: schema);
        world.AddSavedFile("Beta", "Meshes/hidden.nif", [1], included: false);
        world.AddSavedFile("Beta", "Meshes/native.nif.mohidden", [2]);
        world.AddSavedFile("Alpha", "Meshes/alpha.nif", [3]);
        var unknown = Path.Combine(world.Library, "mods", "Unknown");
        Directory.CreateDirectory(unknown);
        File.WriteAllText(Path.Combine(unknown, "unrecorded.nif"), "not in the saved graph");
        using var service = LoadOrderService.WithAmethystSnapshot(world.Manifest, world.Store);
        var snapshot = service.CaptureSavedSnapshot();
        using var context = AmethystSnapshotContext.Use(snapshot);
        using var assets = AssetResolver.BuildSavedSnapshot(snapshot);
        Assert.False(assets.Resolve("Meshes/hidden.nif").Exists);
        Assert.Null(assets.TryResolveOffOrderProvider("Beta", "Meshes/hidden.nif").Source);
        Assert.True(assets.Resolve("Meshes/native.nif.mohidden").Exists);
        var selected = assets.Capture().EnumerateUnder("Meshes", null, 0, out _, "Beta");
        Assert.Contains(@"Meshes\native.nif.mohidden", selected, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"Meshes\alpha.nif", selected, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"Meshes\hidden.nif", selected, StringComparer.OrdinalIgnoreCase);
        var rootProvider = assets.ResolveForPlacement("meshes/root.nif").Sources[0].ProviderName;
        Assert.True(assets.Capture().HasProvider(rootProvider));
        Assert.Contains(@"meshes\root.nif", assets.Capture().EnumerateUnder("meshes", null, 0, out _, rootProvider), StringComparer.OrdinalIgnoreCase);
        Assert.NotNull(service.AssetWalk([Mesh], [], null, "Unknown").Refusal);
        Assert.Equal(WalkStanding.Absent, Assert.Single(service.AssetWalk(["Meshes/missing.nif"], [], null, "Data").Nodes).Standing);
        snapshot.CheckUnchanged();
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    public void StdioWalkIncludesSavedGenerationAndRefreshWarning(int schema)
    {
        using var world = new AmethystSnapshotWorld(schemaVersion: schema);
        world.AddSavedFile("Beta", Mesh, NifSetMeshes.Textured([Texture]));
        world.AddSavedFile("Alpha", Texture, [1, 2, 3]);
        using var server = new ServerFixture(new Dictionary<string, string>
        {
            ["HOUSECARL_DATA_DIR"] = Path.GetDirectoryName(world.Store.FilePath)!,
            ["HouseCarl__ConnectionManifest"] = world.Manifest,
            ["HouseCarl__FilegraphMode"] = "saved-snapshot",
            ["HouseCarl__CorpusPath"] = TestCorpus.Path,
            ["XDG_STATE_HOME"] = Path.Combine(world.Root, "state"),
        });
        var result = server.Call(ToolNames.AssetStatus, JsonSerializer.Serialize(new { asset_paths = new[] { Mesh }, walk = true, format = "json" }));
        Assert.False(result.IsError, result.Text);
        using var json = JsonDocument.Parse(result.Text);
        Assert.Equal("unverified", json.RootElement.GetProperty("amethyst_snapshot").GetProperty("freshness").GetString());
        Assert.Contains("Alpha", result.Text);
        Assert.Contains("Beta", result.Text);
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, false)]
    [InlineData(9, true)]
    [InlineData(10, true)]
    public void CapturedCustomIniSelectsLanguageAndSourceChangesInvalidateIt(int schema, bool utf16)
    {
        using var world = new AmethystSnapshotWorld(schemaVersion: schema, voiceLine: true);
        var ini = Path.Combine(world.Root, "prefix", "drive_c", "users", "steamuser", "Documents", "My Games", "Skyrim Special Edition", "skyrimcustom.ini");
        File.WriteAllText(ini, "[General]\nsLanguage=RUSSIAN\n", utf16 ? System.Text.Encoding.Unicode : System.Text.Encoding.UTF8);
        using var call = GameLanguage.Begin();
        using var service = LoadOrderService.WithAmethystSnapshot(world.Manifest, world.Store);
        var snapshot = service.CaptureSavedSnapshot();
        Assert.Equal(Mutagen.Bethesda.Strings.Language.Russian, GameLanguage.Current.Language);
        Assert.Equal("RUSSIAN", snapshot.IniValue("General", "sLanguage").Value);
        File.WriteAllText(ini, "[General]\nsLanguage=ENGLISH\n");
        Assert.Throws<InvalidOperationException>(snapshot.CheckUnchanged);
    }

    [Fact]
    public void AVoiceTemplateLoopIsNamedBeforeMutagenCanRecurse()
    {
        using var world = new AmethystSnapshotWorld(voiceLine: true);
        Mutagen.Bethesda.Plugins.FormKey speaker = default;
        world.EditPlugin("Beta", "Fixture.esp", mod =>
        {
            var npc = mod.Npcs.AddNew("LoopSpeaker");
            npc.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits;
            npc.Template.SetTo(npc);
            speaker = npc.FormKey;
            var line = mod.DialogTopics.First().Responses.First();
            line.Speaker.SetTo(npc);
            line.Responses.Add(new DialogResponse { ResponseNumber = 1, Text = "loop" });
        });
        using var service = LoadOrderService.WithAmethystSnapshot(world.Manifest, world.Store);
        var snapshot = service.CaptureSavedSnapshot();
        using var context = AmethystSnapshotContext.Use(snapshot);
        var walk = service.AssetWalk([], [speaker], null, null);
        Assert.Contains(walk.Notes, n => n.Contains("Voice resolution refused", StringComparison.Ordinal));
        Assert.Contains(walk.Notes, n => n.Contains("template chain loops", StringComparison.Ordinal));
        snapshot.CheckUnchanged();
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    public void ArchiveMembersRemainNativeProvidersDuringAWalk(int schema)
    {
        using var world = new AmethystSnapshotWorld(schemaVersion: schema);
        using var service = LoadOrderService.WithAmethystSnapshot(world.Manifest, world.Store);
        var snapshot = service.CaptureSavedSnapshot();
        using var context = AmethystSnapshotContext.Use(snapshot);
        var walk = service.AssetWalk(["SKSE/Plugins/SkyPatcher/weapon/bsaonly.ini"], [], null, null);
        var node = Assert.Single(walk.Nodes);
        Assert.Equal(WalkStanding.Present, node.Standing);
        Assert.Equal(AssetKind.Bsa, node.Copy!.Kind);
        Assert.Equal("Alpha", node.Copy.OwningMod);
        var (bytes, error) = AssetResolver.ReadPlacementSource(node.Copy);
        Assert.Null(error);
        Assert.NotEmpty(bytes!);
        snapshot.CheckUnchanged();
    }

    [Theory]
    [InlineData(9, true)]
    [InlineData(10, true)]
    [InlineData(9, false)]
    [InlineData(10, false)]
    public void LocalizedPluginWithOnlyItsUsedTableRemainsReadable(int schema, bool included)
    {
        using var world = new AmethystSnapshotWorld(schemaVersion: schema, voiceLine: true);
        var path = Path.Combine(world.Library, "mods", "Beta", "Fixture.esp");
        var generated = Path.Combine(world.Root, "localized");
        Directory.CreateDirectory(generated);
        var plugin = new SkyrimMod(Mutagen.Bethesda.Plugins.ModKey.FromFileName("Fixture.esp"), SkyrimRelease.SkyrimSE)
            { UsingLocalization = true };
        var weapon = plugin.Weapons.AddNew();
        weapon.Name = new TranslatedString(Language.English, "Saved table weapon");
        using (var writer = new StringsWriter(GameRelease.SkyrimSE, plugin.ModKey, Path.Combine(generated, "Strings"),
                   new TestEncoding(new MutagenEncodingWrapper(System.Text.Encoding.UTF8)), new System.IO.Abstractions.FileSystem()))
            plugin.BeginWrite.ToPath(Path.Combine(generated, "Fixture.esp"))
                .WithLoadOrder(Array.Empty<ISkyrimModGetter>()).WithStringsWriter(writer).Write();
        File.Copy(Path.Combine(generated, "Fixture.esp"), path, overwrite: true);
        var stamp = LinuxFileIdentity.Of(path);
        using (var db = world.Open())
        using (var command = db.CreateCommand())
        {
            command.CommandText = "UPDATE raw_files SET size=$size,mtime_ns=$time WHERE mod_id=(SELECT mod_id FROM mods WHERE name_key='beta') AND source_display='Fixture.esp'";
            command.Parameters.AddWithValue("$size", checked((long)stamp.Size));
            command.Parameters.AddWithValue("$time", stamp.MtimeNs);
            command.ExecuteNonQuery();
        }
        world.AddSavedFile("Beta", "Strings/Fixture_English.STRINGS",
            File.ReadAllBytes(Path.Combine(generated, "Strings", "Fixture_English.STRINGS")), included: included);
        using var call = GameLanguage.Begin();
        using var service = LoadOrderService.WithAmethystSnapshot(world.Manifest, world.Store);
        var snapshot = service.CaptureSavedSnapshot();
        using var overlay = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE, PluginTextEncoding.ReadFor(path));
        var name = ReadEngine.ReadLeaf(overlay.Weapons.Single(), ["Name"]);
        if (included)
        {
            Assert.True(name.HasValue);
            Assert.Equal("Saved table weapon", name.Token);
        }
        else
        {
            Assert.False(name.HasValue);
            Assert.Equal(ReadEngine.UnresolvedStringNote, name.Note);
        }
        var folders = snapshot.StringFolders(path, Language.English);
        Assert.Equal(included ? 1 : 0, Directory.GetFiles(folders.Strings).Length);
        Assert.False(File.Exists(Path.Combine(folders.Strings, "Fixture_English.DLSTRINGS")));
        Assert.False(File.Exists(Path.Combine(folders.Strings, "Fixture_English.ILSTRINGS")));
        snapshot.CheckUnchanged();
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    public void LocalizedReadsMaterializeOnlySavedWinnersAcrossDifferentFolders(int schema)
    {
        using var world = new AmethystSnapshotWorld(schemaVersion: schema, voiceLine: true);
        var path = Path.Combine(world.Library, "mods", "Beta", "Fixture.esp");
        var plugin = File.ReadAllBytes(path);
        plugin[8] |= 0x80;
        File.WriteAllBytes(path, plugin);
        var stamp = LinuxFileIdentity.Of(path);
        using (var db = world.Open())
        using (var command = db.CreateCommand())
        {
            command.CommandText = "UPDATE raw_files SET mtime_ns=$time WHERE mod_id=(SELECT mod_id FROM mods WHERE name_key='beta') AND source_display='Fixture.esp'";
            command.Parameters.AddWithValue("$time", stamp.MtimeNs);
            command.ExecuteNonQuery();
        }
        foreach (var (kind, mod) in new[] { ("STRINGS", "Alpha"), ("DLSTRINGS", "Beta"), ("ILSTRINGS", "Beta") })
            world.AddSavedFile(mod, $"Strings/Fixture_English.{kind}", new byte[8]);
        world.AddSavedFile("Beta", "Strings/Fixture_English.STRINGS", [9, 9], included: false);
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);
        var folders = snapshot.StringFolders(path, Mutagen.Bethesda.Strings.Language.English);
        Assert.Equal(3, Directory.GetFiles(folders.Strings, "*", SearchOption.TopDirectoryOnly).Length);
        Assert.Equal(new byte[8], File.ReadAllBytes(Path.Combine(folders.Strings, "Fixture_English.STRINGS")));
        Assert.Empty(Directory.GetFiles(folders.Archives));
        Assert.NotEqual(Path.GetDirectoryName(path), folders.Strings);
        snapshot.CheckUnchanged();
        snapshot.ReleaseStringFiles();
        Assert.False(Directory.Exists(folders.Strings));
    }
}
