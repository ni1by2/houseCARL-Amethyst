namespace HousecarlCore;

/// <summary>
/// Locates raw plugin copies in synthetic or explicitly supplied staging roots.
/// Product Amethyst reads use <see cref="ManagerSnapshot"/> winners instead of this filesystem scan.
/// </summary>
public static class StagingPluginLocator
{
    /// <summary>Locates every copy of one plugin in provider-priority order.</summary>
    /// <param name="profileDir">Profile whose activation lists classify staging providers.</param>
    /// <param name="modsDir">Root containing one directory per staged mod.</param>
    /// <param name="dataDir">Lowest-priority vanilla test source.</param>
    /// <param name="overwriteDir">Optional highest-priority overwrite source.</param>
    /// <param name="filename">Plugin filename; directory components are discarded.</param>
    /// <returns>Overwrite, enabled, disabled, unlisted, then vanilla sightings.</returns>
    public static IReadOnlyList<PluginFileHit> Locate(
        string profileDir, string modsDir, string dataDir, string overwriteDir, string filename) =>
        Locate(
            AmethystLoadOrder.ReadComposition(profileDir),
            modsDir, dataDir, overwriteDir, filename);

    /// <summary>Locates every copy while reusing already parsed profile composition.</summary>
    /// <param name="composition">Activation state used to classify provider folders.</param>
    /// <param name="modsDir">Root containing one directory per staged mod.</param>
    /// <param name="dataDir">Lowest-priority vanilla test source.</param>
    /// <param name="overwriteDir">Optional highest-priority overwrite source.</param>
    /// <param name="filename">Plugin filename; directory components are discarded.</param>
    /// <returns>All physical sightings in deterministic provider-priority order.</returns>
    public static IReadOnlyList<PluginFileHit> Locate(
        ModComposition composition, string modsDir, string dataDir,
        string overwriteDir, string filename)
    {
        var hits = new List<PluginFileHit>();
        var name = Path.GetFileName(filename?.Trim() ?? "");
        if (name.Length == 0) return hits;

        Add(overwriteDir, "overwrite", enabled: true);
        foreach (var mod in composition.EnabledMods)
            Add(Path.Combine(modsDir, mod), $"mod '{mod}' (enabled)", enabled: true);
        foreach (var mod in composition.DisabledMods)
            Add(Path.Combine(modsDir, mod), $"mod '{mod}' (DISABLED)", enabled: false);
        foreach (var directory in UnlistedModFolders(composition, modsDir))
            Add(directory, $"mod '{Path.GetFileName(directory)}' (UNLISTED)", enabled: false);
        Add(dataDir, "game Data", enabled: true);
        return hits;

        void Add(string directory, string where, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(directory)) return;
            try
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path)) hits.Add(new PluginFileHit(path, where, enabled));
            }
            catch
            {
                // An inaccessible provider is not a valid physical sighting.
            }
        }
    }

    /// <summary>Checks whether any explicit staging provider contains one plugin filename.</summary>
    /// <param name="composition">Activation state used to enumerate known providers.</param>
    /// <param name="modsDir">Root containing staged mod directories.</param>
    /// <param name="dataDir">Lowest-priority vanilla test source.</param>
    /// <param name="overwriteDir">Optional highest-priority overwrite source.</param>
    /// <param name="filename">Plugin filename; directory components are discarded.</param>
    /// <returns>True after the first accessible physical match.</returns>
    public static bool Exists(
        ModComposition composition, string modsDir, string dataDir,
        string overwriteDir, string filename)
    {
        var name = Path.GetFileName(filename?.Trim() ?? "");
        if (name.Length == 0) return false;
        if (Has(overwriteDir)) return true;
        foreach (var mod in composition.EnabledMods)
            if (Has(Path.Combine(modsDir, mod))) return true;
        foreach (var mod in composition.DisabledMods)
            if (Has(Path.Combine(modsDir, mod))) return true;
        foreach (var directory in UnlistedModFolders(composition, modsDir))
            if (Has(directory)) return true;
        return Has(dataDir);

        bool Has(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) return false;
            try { return File.Exists(Path.Combine(directory, name)); }
            catch { return false; }
        }
    }

    /// <summary>Enumerates staging folders absent from both enabled and disabled profile lists.</summary>
    /// <param name="composition">Profile composition defining registered folders.</param>
    /// <param name="modsDir">Root whose immediate subdirectories are candidate providers.</param>
    /// <returns>Unregistered directories in ordinal path order; inaccessible roots yield no entries.</returns>
    static IEnumerable<string> UnlistedModFolders(
        ModComposition composition, string modsDir)
    {
        if (string.IsNullOrWhiteSpace(modsDir) || !Directory.Exists(modsDir)) yield break;
        var listed = new HashSet<string>(
            composition.EnabledMods, StringComparer.OrdinalIgnoreCase);
        listed.UnionWith(composition.DisabledMods);

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(modsDir)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();
        }
        catch { yield break; }

        foreach (var directory in directories)
            if (!listed.Contains(Path.GetFileName(directory)))
                yield return directory;
    }
}
