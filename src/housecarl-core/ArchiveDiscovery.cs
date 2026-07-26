namespace HousecarlCore;

/// <summary>Contains active archive bindings and any reason the discovery may be incomplete.</summary>
/// <param name="Archives">Physical archives with their owner and precedence rank.</param>
/// <param name="Warnings">Non-fatal completeness problems, such as missing base-archive metadata.</param>
public sealed record ArchiveDiscoveryResult(IReadOnlyList<ActiveArchive> Archives, IReadOnlyList<string> Warnings);

/// <summary>
/// Combines Skyrim.ini base archives with plugin-associated archives and assigns deterministic
/// BSA precedence from the active plugin order.
/// </summary>
/// <remarks>
/// Base archives from <c>sResourceArchiveList</c> and <c>sResourceArchiveList2</c> load first.
/// Plugin-associated <c>X.bsa</c> and <c>X - Textures.bsa</c> archives then load in plugin order.
/// Higher ranks therefore win. Amethyst product mode gets winning physical paths from its file index;
/// the root-walking overload remains only for inherited compatibility probes.
/// </remarks>
public static class ArchiveDiscovery
{
    /// <summary>The owner marker for a base archive loaded from Skyrim.ini rather than through a plugin.</summary>
    public const string IniArchiveOwner = "Skyrim.ini [Archive]";

    /// <summary>
    /// Discover archives from Amethyst's authoritative loose-file winners. Only top-level
    /// BSA files can participate in Skyrim's archive loading rules.
    /// </summary>
    /// <param name="profileDir">Active Amethyst profile containing load-order and optional Skyrim.ini files.</param>
    /// <param name="gamePath">Native game root used as the secondary Skyrim.ini location.</param>
    /// <param name="vanillaDataDir">Validated Data_Core/Data vanilla root.</param>
    /// <param name="fileIndex">Authoritative Amethyst loose-file winners.</param>
    /// <returns>Active physical archives in rank order plus non-fatal completeness warnings.</returns>
    /// <exception cref="AmethystConfigurationException">The filemap/modindex snapshot is not usable.</exception>
    public static ArchiveDiscoveryResult DiscoverAmethyst(
        string profileDir,
        string gamePath,
        string vanillaDataDir,
        ManagerFileIndex fileIndex)
    {
        if (!fileIndex.Ready)
            throw new AmethystConfigurationException(string.Join("; ", fileIndex.Warnings));

        var warnings = new List<string>();
        var composition = AmethystLoadOrder.ReadComposition(profileDir, warnings);
        var archiveMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (logicalPath, source) in fileIndex.Sources)
            if (BethesdaPath.DirectoryName(logicalPath).Length == 0
                && Path.GetExtension(logicalPath).Equals(".bsa", StringComparison.OrdinalIgnoreCase))
                archiveMap[logicalPath] = source.HostPath;
        foreach (var (name, path) in EnumerateArchives(vanillaDataDir))
            archiveMap.TryAdd(name, path);

        return Build(
            profileDir,
            gamePath,
            composition.OrderedPluginNames,
            composition.InactivePluginNames,
            archiveMap,
            warnings);
    }

    /// <summary>Discovers archives by walking legacy profile roots for inherited probes.</summary>
    /// <param name="profileDir">Legacy profile containing activation files and the preferred Skyrim.ini.</param>
    /// <param name="modsDir">Legacy staging root containing enabled mod folders.</param>
    /// <param name="dataDir">Legacy lowest-priority game Data root.</param>
    /// <param name="overwriteDir">Legacy highest-priority loose root.</param>
    /// <param name="gamePath">Fallback directory containing Skyrim.ini.</param>
    /// <returns>Active physical archives plus non-fatal completeness warnings.</returns>
    public static ArchiveDiscoveryResult Discover(
        string profileDir, string modsDir, string dataDir, string overwriteDir, string gamePath)
    {
        var warnings = new List<string>();
        var comp = AmethystLoadOrder.ReadComposition(profileDir, warnings);
        var archiveMap = BuildArchiveMap(comp.EnabledMods, modsDir, dataDir, overwriteDir);
        return Build(
            profileDir,
            gamePath,
            comp.OrderedPluginNames,
            comp.InactivePluginNames,
            archiveMap,
            warnings);
    }

    /// <summary>Builds base and plugin-associated archive bindings from a physical filename map.</summary>
    /// <param name="profileDir">Profile containing the preferred Skyrim.ini.</param>
    /// <param name="gamePath">Fallback Skyrim.ini directory.</param>
    /// <param name="orderedPlugins">Plugin names in authoritative load order.</param>
    /// <param name="inactivePlugins">Plugin names that must not load associated archives.</param>
    /// <param name="archiveMap">Case-insensitive top-level BSA filename to winning host path.</param>
    /// <param name="warnings">Shared non-fatal diagnostic collector.</param>
    /// <returns>Archive bindings with monotonically increasing precedence ranks.</returns>
    static ArchiveDiscoveryResult Build(
        string profileDir,
        string gamePath,
        IReadOnlyList<string> orderedPlugins,
        IReadOnlyList<string> inactivePlugins,
        IReadOnlyDictionary<string, string> archiveMap,
        List<string> warnings)
    {
        var inactive = new HashSet<string>(inactivePlugins, StringComparer.OrdinalIgnoreCase);
        var activeOrdered = orderedPlugins.Where(name => !inactive.Contains(name));

        var archives = new List<ActiveArchive>();
        int rank = 0;

        // (1) base archives — Skyrim.ini [Archive] sResourceArchiveList/2; loaded first → the LOW rank block.
        foreach (var fn in ReadBaseArchiveNames(profileDir, gamePath, warnings))
        {
            if (archiveMap.TryGetValue(fn, out var path))
                archives.Add(new ActiveArchive(path, IniArchiveOwner, rank));
            // Later INI entries load later even when an earlier named archive is absent.
            rank++;
        }

        // (2) plugin-associated archives — "X.bsa" + "X - Textures.bsa", in load order (winner last → higher rank).
        foreach (var name in activeOrdered)
        {
            var baseName = Path.GetFileNameWithoutExtension(name);
            foreach (var candidate in new[] { baseName + ".bsa", baseName + " - Textures.bsa" })
                if (archiveMap.TryGetValue(candidate, out var path))
                    archives.Add(new ActiveArchive(path, name, rank));
            rank++;   // both of a plugin's archives share its rank; advance once per plugin
        }

        return new ArchiveDiscoveryResult(archives, warnings);
    }

    /// <summary>Builds a winning archive map by walking legacy roots in precedence order.</summary>
    /// <param name="enabledModsByPriority">Legacy mod folder names in highest-first priority order.</param>
    /// <param name="modsDir">Legacy staging root.</param>
    /// <param name="dataDir">Legacy lowest-priority Data root.</param>
    /// <param name="overwriteDir">Legacy highest-priority overwrite root.</param>
    /// <returns>Case-insensitive BSA filename map containing only the winning physical copy.</returns>
    static Dictionary<string, string> BuildArchiveMap(
        IReadOnlyList<string> enabledModsByPriority, string modsDir, string dataDir, string overwriteDir)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (fn, full) in EnumerateArchives(overwriteDir))   // overwrite — beats every mod
            map[fn] = full;

        foreach (var mod in enabledModsByPriority)                    // highest priority first
            foreach (var (fn, full) in EnumerateArchives(Path.Combine(modsDir, mod)))
                if (!map.ContainsKey(fn)) map[fn] = full;             // first (highest-priority) wins

        foreach (var (fn, full) in EnumerateArchives(dataDir))        // base game / vanilla — lowest priority
            if (!map.ContainsKey(fn)) map[fn] = full;

        return map;
    }

    /// <summary>Enumerates immediate BSA children of one native directory.</summary>
    /// <param name="dir">Native directory whose immediate files should be inspected.</param>
    /// <returns>
    /// Lazy pairs of raw filename and full host path. A missing or inaccessible directory yields no entries.
    /// </returns>
    static IEnumerable<(string fn, string full)> EnumerateArchives(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) yield break;
        var opts = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true };
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir, "*.bsa", opts); }
        catch { yield break; }
        foreach (var f in files)
            if (Path.GetExtension(f).Equals(".bsa", StringComparison.OrdinalIgnoreCase))
                yield return (Path.GetFileName(f), f);
    }

    /// <summary>Reads always-loaded archive names from the first usable profile or game Skyrim.ini.</summary>
    /// <param name="profileDir">Profile directory containing the preferred Skyrim.ini.</param>
    /// <param name="gamePath">Game root containing the fallback Skyrim.ini.</param>
    /// <param name="warnings">Collector receiving a named warning when no usable list exists.</param>
    /// <returns>Archive filenames in engine load order, or an empty list with a warning.</returns>
    static IReadOnlyList<string> ReadBaseArchiveNames(string profileDir, string gamePath, List<string> warnings)
    {
        var candidates = new List<string>(2);
        if (profileDir.Length > 0) candidates.Add(Path.Combine(profileDir, "Skyrim.ini"));
        if (gamePath.Length > 0) candidates.Add(Path.Combine(gamePath, "Skyrim.ini"));

        foreach (var ini in candidates)
        {
            if (!File.Exists(ini)) continue;
            var names = ParseResourceArchiveList(ini);
            if (names.Count > 0) return names;   // first Skyrim.ini that actually lists archives wins
        }

        warnings.Add(
            "could not read the [Archive] sResourceArchiveList from a Skyrim.ini (looked in the profile folder" +
            (gamePath.Length > 0 ? " and the game dir" : "") + ") — the base-game BSAs (Skyrim - Textures*.bsa, " +
            "etc.) are NOT in the asset scan, so an asset present ONLY in a vanilla archive may read as absent. " +
            "If profile-specific INIs are enabled, make sure the active profile has a Skyrim.ini.");
        return Array.Empty<string>();
    }

    /// <summary>Parses both resource-archive list keys from a Skyrim.ini <c>[Archive]</c> section.</summary>
    /// <param name="iniPath">Native path to a candidate Skyrim.ini.</param>
    /// <returns>Trimmed archive filenames in file order; empty when unreadable or unspecified.</returns>
    static IReadOnlyList<string> ParseResourceArchiveList(string iniPath)
    {
        var names = new List<string>();
        string[] lines;
        try { lines = File.ReadAllLines(iniPath); } catch { return names; }

        bool inArchive = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';') continue;
            if (line[0] == '[') { inArchive = line.Equals("[Archive]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inArchive) continue;

            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq].Trim();
            if (!key.Equals("sResourceArchiveList", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("sResourceArchiveList2", StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var part in line[(eq + 1)..].Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                names.Add(part);
        }
        return names;
    }
}
