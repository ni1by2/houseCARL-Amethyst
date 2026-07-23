namespace HousecarlCore;

/// <summary>
/// Reads Amethyst's profile files and resolves plugin winners from native staging paths.
/// File order is significant: the first enabled mod has the highest priority.
/// </summary>
public static class AmethystLoadOrder
{
    /// <summary>Plugin filename extensions recognized by the shared plugin reader.</summary>
    static readonly string[] PluginExts = PluginFile.Extensions;

    /// <summary>Builds a plugin order by scanning staging roots in Amethyst priority order.</summary>
    /// <param name="profileDir">Active profile containing modlist/plugins/loadorder files.</param>
    /// <param name="modsDir">Effective mod-staging root.</param>
    /// <param name="vanillaDataDir">Unmerged vanilla plugin source.</param>
    /// <param name="overwriteDir">Highest-priority overwrite staging root.</param>
    /// <returns>Resolved physical plugin order, warnings, and the requested active count.</returns>
    /// <remarks>
    /// This overload supports layout characterization and compatibility probes. Runtime Amethyst
    /// resolution uses the authoritative <see cref="ManagerFileIndex"/> overload below.
    /// </remarks>
    public static ModOrderResult Build(
        string profileDir, string modsDir, string vanillaDataDir, string overwriteDir)
    {
        var warnings = new List<string>();
        var composition = ReadComposition(profileDir, warnings);
        var winners = PluginWinners(
            composition.EnabledMods, modsDir, vanillaDataDir, overwriteDir, warnings);
        return Resolve(composition, winners, warnings);
    }

    /// <summary>Builds the active plugin order from Amethyst's authoritative filemap winner set.</summary>
    /// <param name="profileDir">Active profile containing modlist/plugins/loadorder files.</param>
    /// <param name="vanillaDataDir">Unmerged vanilla plugin source.</param>
    /// <param name="fileIndex">Validated loose-file winners parsed from filemap and modindex.</param>
    /// <returns>Resolved physical plugin order, warnings, and the requested active count.</returns>
    /// <exception cref="AmethystConfigurationException">The winner index is explicitly not ready.</exception>
    public static ModOrderResult Build(
        string profileDir, string vanillaDataDir, ManagerFileIndex fileIndex)
    {
        if (!fileIndex.Ready)
            throw new AmethystConfigurationException(string.Join("; ", fileIndex.Warnings));

        var warnings = new List<string>();
        var composition = ReadComposition(profileDir, warnings);
        var winners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (logicalPath, source) in fileIndex.Sources)
            if (BethesdaPath.DirectoryName(logicalPath).Length == 0
                && PluginExts.Contains(Path.GetExtension(logicalPath), StringComparer.OrdinalIgnoreCase))
                winners[logicalPath] = source.HostPath;
        AddPlugins(vanillaDataDir, winners, warnings);
        return Resolve(composition, winners, warnings);
    }

    /// <summary>Maps active ordered plugin names to their physical winning files.</summary>
    /// <param name="composition">Parsed profile activation and ordering state.</param>
    /// <param name="winners">Case-insensitive plugin filenames mapped to physical sources.</param>
    /// <param name="warnings">Mutable warning sink returned with the result.</param>
    /// <returns>Only resolved active source paths, while preserving the full requested active count.</returns>
    /// <remarks>Missing winners are reported and omitted; no deployed Data scan or guessed source is attempted.</remarks>
    static ModOrderResult Resolve(
        ModComposition composition, Dictionary<string, string> winners, List<string> warnings)
    {
        var inactive = new HashSet<string>(
            composition.InactivePluginNames, StringComparer.OrdinalIgnoreCase);
        var paths = new List<string>();
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var activeCount = 0;

        foreach (var name in composition.OrderedPluginNames)
        {
            if (inactive.Contains(name)) continue;
            activeCount++;
            if (winners.TryGetValue(name, out var path))
            {
                paths.Add(path);
                sources[name] = path;
                continue;
            }

            warnings.Add(
                $"load order lists '{name}' but no Amethyst filemap or vanilla Data source provides it; " +
                "refresh Amethyst and rebuild its load order");
        }

        return new ModOrderResult(paths, warnings, activeCount) { ResolvedSources = sources };
    }

    /// <summary>Parses Amethyst's three profile lists into manager-neutral composition state.</summary>
    /// <param name="profileDir">Active profile directory.</param>
    /// <param name="warnings">Optional sink for missing order/mod files.</param>
    /// <returns>Enabled, disabled, locked, ordered, active, inactive, and implicit plugin sets.</returns>
    public static ModComposition ReadComposition(string profileDir, List<string>? warnings = null)
    {
        var enabled = new List<string>();
        var disabled = new List<string>();
        var locked = new List<string>();
        ReadMods(Path.Combine(profileDir, "modlist.txt"), enabled, disabled, locked, warnings);

        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inactive = new List<string>();
        ReadPlugins(Path.Combine(profileDir, "plugins.txt"), active, inactive);
        var inactiveSet = new HashSet<string>(inactive, StringComparer.OrdinalIgnoreCase);

        var ordered = ReadOrder(Path.Combine(profileDir, "loadorder.txt"), warnings);
        var implicitNames = ordered
            .Where(name => !active.Contains(name) && !inactiveSet.Contains(name))
            .ToList();

        return new ModComposition(
            enabled, disabled, locked, ordered, active, inactive, implicitNames);
    }

    /// <summary>Parses Amethyst mod priority and activation markers from modlist.txt.</summary>
    /// <param name="path">Native modlist path.</param>
    /// <param name="enabled">Receives both <c>+</c> enabled and <c>*</c> locked mods in file priority order.</param>
    /// <param name="disabled">Receives <c>-</c> mods except separator pseudo-mods.</param>
    /// <param name="locked">Receives the enabled subset marked <c>*</c>.</param>
    /// <param name="warnings">Optional sink for an absent modlist.</param>
    static void ReadMods(
        string path, List<string> enabled, List<string> disabled,
        List<string> locked, List<string>? warnings)
    {
        if (!File.Exists(path))
        {
            warnings?.Add($"modlist.txt not found at '{path}'");
            return;
        }

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length < 2 || line[0] is not ('+' or '-' or '*')) continue;
            var name = line[1..].Trim();
            if (name.Length == 0 || name.EndsWith("_separator", StringComparison.Ordinal)) continue;
            if (line[0] == '-')
            {
                disabled.Add(name);
                continue;
            }

            enabled.Add(name);
            if (line[0] == '*') locked.Add(name);
        }
    }

    /// <summary>Parses Skyrim star-prefixed activation from plugins.txt.</summary>
    /// <param name="path">Native plugins file path.</param>
    /// <param name="active">Receives unique star-prefixed plugin names.</param>
    /// <param name="inactive">Receives non-comment, non-star plugin names.</param>
    static void ReadPlugins(string path, HashSet<string> active, List<string> inactive)
    {
        if (!File.Exists(path)) return;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '*')
            {
                var name = line[1..].Trim();
                if (name.Length > 0) active.Add(name);
            }
            else inactive.Add(line);
        }
    }

    /// <summary>Reads authoritative plugin order from loadorder.txt.</summary>
    /// <param name="path">Native load-order file path.</param>
    /// <param name="warnings">Optional sink for an absent file.</param>
    /// <returns>Non-empty, non-comment names in file order.</returns>
    static List<string> ReadOrder(string path, List<string>? warnings)
    {
        if (!File.Exists(path))
        {
            warnings?.Add($"loadorder.txt not found at '{path}'");
            return new List<string>();
        }

        return File.ReadLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && line[0] != '#')
            .ToList();
    }

    /// <summary>Characterizes plugin winners by scanning roots from highest to lowest priority.</summary>
    /// <param name="enabledMods">Enabled mod names in Amethyst priority order, highest first.</param>
    /// <param name="modsDir">Effective mods staging root.</param>
    /// <param name="dataDir">Unmerged vanilla Data source.</param>
    /// <param name="overwriteDir">Highest-priority overwrite source.</param>
    /// <param name="warnings">Receives missing folders and enumeration failures.</param>
    /// <returns>Case-insensitive plugin filename-to-source map; first source wins.</returns>
    static Dictionary<string, string> PluginWinners(
        IReadOnlyList<string> enabledMods, string modsDir, string dataDir,
        string overwriteDir, List<string> warnings)
    {
        var winners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddPlugins(overwriteDir, winners, warnings);

        var modFolders = DirectoriesByName(modsDir, warnings);
        foreach (var mod in enabledMods)
            if (modFolders.TryGetValue(mod, out var path))
                AddPlugins(path, winners, warnings);
            else
                warnings.Add($"enabled Amethyst mod folder not found: '{Path.Combine(modsDir, mod)}'");

        AddPlugins(dataDir, winners, warnings);
        return winners;
    }

    /// <summary>Indexes immediate mod directories with case-insensitive Amethyst name matching.</summary>
    /// <param name="root">Effective mods staging root.</param>
    /// <param name="warnings">Receives enumeration and case-collision diagnostics.</param>
    /// <returns>Mod folder names mapped to their actual-cased native paths.</returns>
    static Dictionary<string, string> DirectoriesByName(string root, List<string> warnings)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return result;
        try
        {
            foreach (var path in Directory.EnumerateDirectories(root).OrderBy(x => x, StringComparer.Ordinal))
                if (!result.TryAdd(Path.GetFileName(path), path))
                    warnings.Add($"mod folders differ only by case; using '{result[Path.GetFileName(path)]}' instead of '{path}'");
        }
        catch (Exception ex) { warnings.Add($"cannot enumerate Amethyst mods directory '{root}': {ex.Message}"); }
        return result;
    }

    /// <summary>Adds top-level plugin files from one source without replacing higher-priority winners.</summary>
    /// <param name="root">Physical directory to enumerate.</param>
    /// <param name="winners">Winner map already populated by higher-priority sources.</param>
    /// <param name="warnings">Receives non-fatal enumeration failures.</param>
    static void AddPlugins(
        string root, Dictionary<string, string> winners, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
        try
        {
            foreach (var path in Directory.EnumerateFiles(root).OrderBy(x => x, StringComparer.Ordinal))
            {
                var extension = Path.GetExtension(path);
                if (PluginExts.Any(x => x.Equals(extension, StringComparison.OrdinalIgnoreCase)))
                    winners.TryAdd(Path.GetFileName(path), path);
            }
        }
        catch (Exception ex) { warnings.Add($"cannot enumerate plugin source '{root}': {ex.Message}"); }
    }
}
