using System.Buffers;
using System.Text.Json;
using HousecarlCore;
using MessagePack;

namespace HousecarlGenerator;

/// <summary>Locks Amethyst v4 winners, exclusions, raw casing, and stripped-wrapper lookup.</summary>
public static class AmethystFileMapProbe
{
    public static int RunGuard(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "hc-amethyst-filemap-" + Guid.NewGuid().ToString("N"));
        try
        {
            WinnerFixture(root);
            RefusalFixtures(root);
            Console.WriteLine("PASS: Amethyst filemap and modindex.bin v4 authority");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            return 1;
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    static void WinnerFixture(string root)
    {
        var profile = Path.Combine(root, "profile");
        var staging = Path.Combine(root, "staging");
        var mods = Path.Combine(staging, "mods");
        var overwrite = Path.Combine(staging, "overwrite");
        var game = Path.Combine(root, "game");
        var vanilla = Path.Combine(game, "Data_Core");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "profile_state.json"), JsonSerializer.Serialize(new
        {
            mod_strip_prefixes = new Dictionary<string, string[]> { ["Strip Mod"] = new[] { "Wrapper/Deep" } }
        }));

        Touch(Path.Combine(mods, "Low", "Meshes", "Winner.NIF"));
        var winning = Touch(Path.Combine(mods, "HIGH", "Data", "Meshes", "winner.nif"));
        var stripped = Touch(Path.Combine(mods, "Strip Mod", "Wrapper", "Deep", "Scripts", "Ω", "Test.PEX"));
        var overwriteFile = Touch(Path.Combine(overwrite, "SKSE", "Plugins", "Loose.DLL"));
        var plugin = Touch(Path.Combine(mods, "HIGH", "Data", "Mod.esp"));
        var archive = Touch(Path.Combine(mods, "HIGH", "Data", "Mod.bsa"));
        var vanillaAsset = Touch(Path.Combine(vanilla, "Textures", "Vanilla.DDS"));
        Touch(Path.Combine(mods, "HIGH", "Data", "Excluded.txt"));

        var index = Path.Combine(staging, "modindex.bin");
        WriteIndex(index, 4,
            ("Low", new[] { ("meshes/winner.nif", "Meshes/Winner.NIF", "n") }),
            ("HIGH", new[]
            {
                ("meshes/winner.nif", "Meshes/winner.nif", "n"),
                ("excluded.txt", "Excluded.txt", "n"),
                ("mod.esp", "Mod.esp", "n"),
                ("mod.bsa", "Mod.bsa", "n")
            }),
            ("Strip Mod", new[] { ("scripts/ω/test.pex", "Scripts/Ω/Test.PEX", "n") }),
            ("[Overwrite]", new[] { ("skse/plugins/loose.dll", "SKSE/Plugins/Loose.DLL", "n") }));
        var filemap = Path.Combine(staging, "filemap.txt");
        File.WriteAllText(filemap,
            "Meshes/WINNER.nif\tHIGH\nScripts/Ω/Test.PEX\tStrip Mod\nSKSE/Plugins/Loose.DLL\t[Overwrite]\n" +
            "Mod.esp\tHIGH\nMod.bsa\tHIGH\n");
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), "+HIGH\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*Mod.esp\n");
        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "Mod.esp\n");

        var result = AmethystFileMap.Load(profile, mods, overwrite, filemap, index);
        Check(result.Ready, "valid filemap is ready");
        Equal(5, result.Sources.Count, "excluded/non-winning files absent");
        Equal(winning, result.Sources["Meshes\\WINNER.nif"].HostPath, "filemap winner");
        Equal(stripped, result.Sources["Scripts\\Ω\\Test.PEX"].HostPath, "per-mod stripped prefix");
        Equal(overwriteFile, result.Sources["SKSE\\Plugins\\Loose.DLL"].HostPath, "overwrite source");
        Equal("HIGH", result.Sources["Meshes\\winner.nif"].Provider, "provider retained");
        Equal(plugin, result.Sources["Mod.esp"].HostPath, "plugin source");

        using var assets = AssetResolver.Build(result.Sources, vanilla, Array.Empty<ActiveArchive>());
        Equal(winning, assets.ResolveForPlacement("meshes/winner.nif").Sources[0].LooseFilePath!, "asset winner");
        Equal(vanillaAsset, assets.ResolveForPlacement("textures/vanilla.dds").Sources[0].LooseFilePath!, "vanilla fallback");
        Check(!assets.Resolve("Excluded.txt").Exists, "excluded asset stays absent");
        Check(assets.EnumerateUnder("SKSE").Contains("SKSE\\Plugins\\Loose.DLL"), "authoritative subtree enumeration");

        var discovered = ArchiveDiscovery.DiscoverAmethyst(profile, game, vanilla, result);
        Equal(archive, discovered.Archives.Single(x => x.OwningPlugin == "Mod.esp").Path, "archive winner");

        const string bsaRel = @"meshes\actors\character\facegendata\facegeom\Dawnguard.esm\0001A51A.nif";
        var fixture = Path.GetFullPath("src/housecarl-generator/fixtures/asset-resolver/FixtureA.bsa");
        using (var conflict = AssetResolver.Build(
            new Dictionary<string, ManagerFileSource>(StringComparer.OrdinalIgnoreCase)
            {
                [bsaRel] = new("HIGH", winning)
            },
            vanilla,
            new[] { new ActiveArchive(fixture, "Mod.esp", 1) }))
        {
            var providers = conflict.Resolve(bsaRel).Providers;
            Check(
                providers.Count == 2
                && providers[0] is { Source: "HIGH", Kind: AssetKind.Loose }
                && providers[1].Kind == AssetKind.Bsa,
                "authoritative loose winner beats BSA");
        }

        File.Delete(winning);
        ThrowsInvalid(
            () => assets.Resolve("Meshes/Winner.nif"),
            "filemap winner",
            "a vanished indexed winner fails instead of falling back");
    }

    static void RefusalFixtures(string root)
    {
        var profile = Path.Combine(root, "refusals", "profile");
        var staging = Path.Combine(root, "refusals", "staging");
        var mods = Path.Combine(staging, "mods");
        var overwrite = Path.Combine(staging, "overwrite");
        Directory.CreateDirectory(profile);
        Touch(Path.Combine(mods, "Mod", "A.txt"));
        var index = Path.Combine(staging, "modindex.bin");
        var filemap = Path.Combine(staging, "filemap.txt");

        WriteIndex(index, 5, ("Mod", new[] { ("a.txt", "A.txt", "n") }));
        File.WriteAllText(filemap, "A.txt\tMod\n");
        Throws(() => AmethystFileMap.Load(profile, mods, overwrite, filemap, index), "version 5 is unsupported");

        WriteIndex(index, 4, ("Mod", new[] { ("a.txt", "A.txt", "n") }));
        File.SetLastWriteTimeUtc(filemap, DateTime.UtcNow.AddMinutes(-2));
        File.SetLastWriteTimeUtc(index, DateTime.UtcNow);
        Check(!AmethystFileMap.Load(profile, mods, overwrite, filemap, index).Ready, "newer index marks filemap stale");

        File.SetLastWriteTimeUtc(filemap, DateTime.UtcNow.AddMinutes(1));
        File.WriteAllText(filemap, "..\\escape.txt\tMod\n");
        Throws(() => AmethystFileMap.Load(profile, mods, overwrite, filemap, index), "parent-escaping");

        File.Delete(filemap);
        Check(!AmethystFileMap.Load(profile, mods, overwrite, filemap, index).Ready, "missing filemap is explicit");
    }

    internal static void WriteIndex(
        string path, int version,
        params (string Mod, (string Key, string Path, string Kind)[] Files)[] mods)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteMapHeader(2);
        writer.Write("v");
        writer.Write(version);
        writer.Write("mods");
        writer.WriteArrayHeader(mods.Length);
        foreach (var (mod, files) in mods)
        {
            writer.WriteArrayHeader(2);
            writer.Write(mod);
            writer.WriteArrayHeader(files.Length);
            foreach (var (key, relPath, kind) in files)
            {
                writer.WriteArrayHeader(3);
                writer.Write(key);
                writer.Write(relPath);
                writer.Write(kind);
            }
        }
        writer.Flush();
        File.WriteAllBytes(path, buffer.WrittenSpan.ToArray());
    }

    static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    static void Equal<T>(T expected, T actual, string arm) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{arm}: expected '{expected}', got '{actual}'");
    }

    static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    static void Throws(Action action, string text)
    {
        try { action(); }
        catch (AmethystConfigurationException ex) when (ex.Message.Contains(text, StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException($"expected AmethystConfigurationException containing '{text}'");
    }

    static void ThrowsInvalid(Action action, string text, string arm)
    {
        try { action(); }
        catch (InvalidOperationException ex) when (ex.Message.Contains(text, StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException(arm);
    }
}
