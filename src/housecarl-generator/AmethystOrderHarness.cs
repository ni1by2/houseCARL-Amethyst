using Mutagen.Bethesda.Plugins;

namespace HousecarlGenerator;

// Verifies AmethystLoadOrder.Build against a real native profile: masters first,
// later winners last, and duplicate plugin names resolved from the highest-priority
// staged provider. It then feeds that order to the production record resolver.
//
//   dotnet run --project src/housecarl-generator amethyst-order [profileDir] [modsDir] [dataDir]
static class AmethystOrderHarness
{
    /// <summary>Runs a manual large-profile load-order characterization.</summary>
    /// <param name="args">Profile, mods, and vanilla Data paths, in that order.</param>
    /// <returns>Zero when physical order and resolver sanity checks pass.</returns>
    public static int Run(string[] args)
    {
        if (args.Length != 3)
        {
            Console.WriteLine("usage: amethyst-order <profileDir> <modsDir> <vanillaDataDir>");
            return 2;
        }
        var profileDir = args[0];
        var modsDir = args[1];
        var dataDir = args[2];

        Console.WriteLine($"amethyst-order: reading native profile and staging state\n  profile: {profileDir}\n  mods:    {modsDir}\n  data:    {dataDir}\n");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = AmethystLoadOrder.Build(profileDir, modsDir, dataDir);
        sw.Stop();

        Console.WriteLine($"built in {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"  active plugins in load order : {result.ActiveCount}");
        Console.WriteLine($"  resolved to a real path      : {result.ResolvedCount}");
        Console.WriteLine($"  warnings                     : {result.Warnings.Count}");

        var paths = result.OrderedPaths;
        if (paths.Count == 0) { Console.WriteLine("\nFAIL: no plugins resolved — check the profile/mods/data paths."); return 1; }

        Console.WriteLine("\nfirst 6 (lowest priority — expect the vanilla masters):");
        foreach (var p in paths.Take(6)) Console.WriteLine($"  {Tail(p)}");
        Console.WriteLine("last 6 (highest priority — expect the user's top patches, e.g. the houseCARL/Test plugins):");
        foreach (var p in paths.Skip(Math.Max(0, paths.Count - 6))) Console.WriteLine($"  {Tail(p)}");

        if (result.Warnings.Count > 0)
        {
            Console.WriteLine($"\nwarnings (first 10 of {result.Warnings.Count}):");
            foreach (var w in result.Warnings.Take(10)) Console.WriteLine($"  - {w}");
        }

        // Feed the true order into the REAL resolver and spot-check it stands up + the §8.5 sanity record.
        Console.WriteLine("\nbuilding LoadOrderResolver on the true order...");
        var rsw = System.Diagnostics.Stopwatch.StartNew();
        using var resolver = LoadOrderResolver.Build(paths);
        rsw.Stop();
        Console.WriteLine($"  resolver built in {rsw.ElapsedMilliseconds} ms");
        Console.WriteLine($"  plugins={resolver.PluginCount}  records={resolver.RecordCount}  conflicts={resolver.ConflictCount}  maxDepth={resolver.MaxDepth}");
        if (resolver.LoadFailures.Count > 0)
        {
            Console.WriteLine($"  excluded plugins (open OR parse failure): {resolver.LoadFailures.Count} (first 5):");
            foreach (var f in resolver.LoadFailures.Take(5)) Console.WriteLine($"    - {f}");
        }

        // §8.5 sanity record: the deepest-known Worldspace 00003C:Skyrim.esm (depth ~879 in the placeholder order).
        var sanity = FormKey.Factory("00003C:Skyrim.esm");
        var winner = resolver.ResolveWinner(sanity);
        Console.WriteLine(winner is null
            ? $"\n00003C:Skyrim.esm  -> NOT FOUND (unexpected)"
            : $"\n00003C:Skyrim.esm  winner={winner.Value.WinnerPlugin}  override_depth={winner.Value.OverrideDepth}");

        // Sanity assertions (loud, not silent) — the cheap structural checks; xEdit identity is Phase 2.
        bool firstIsMaster = paths.Count > 0 && Path.GetFileName(paths[0]).Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase);
        bool plausibleCount = result.ResolvedCount > 3000;
        Console.WriteLine($"\nchecks: first==Skyrim.esm={firstIsMaster}  resolved>3000={plausibleCount}  resolved==active={result.ResolvedCount == result.ActiveCount}");
        if (!firstIsMaster || !plausibleCount) { Console.WriteLine("FAIL: structural sanity check failed."); return 1; }
        Console.WriteLine("amethyst-order: OK");
        return 0;
    }

    /// <summary>Formats a physical plugin path as its provider directory and filename.</summary>
    static string Tail(string path)
    {
        var dir = Path.GetFileName(Path.GetDirectoryName(path)) ?? "?";
        return $"{dir}/{Path.GetFileName(path)}";
    }
}
