namespace HousecarlCore;

/// <summary>
/// Reads Amethyst's profile files and resolves plugin winners from native staging paths.
/// File order is significant: the first enabled mod has the highest priority.
/// </summary>
public static class AmethystLoadOrder
{
    static readonly string[] PluginExts = PluginFile.Extensions;

    public static ModOrderResult Build(
        string profileDir, string modsDir, string vanillaDataDir, string overwriteDir)
    {
        var warnings = new List<string>();
        var composition = ReadComposition(profileDir, warnings);
        var winners = PluginWinners(
            composition.EnabledMods, modsDir, vanillaDataDir, overwriteDir, warnings);
        return Resolve(composition, winners, warnings);
    }

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
