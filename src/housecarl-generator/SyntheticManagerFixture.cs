using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// Opens manager-neutral test services from the generator's explicit native directory convention.
/// No manager configuration file is read: a fixture states its roots through its directory structure
/// and optional method arguments.
/// </summary>
internal static class SyntheticManagerFixture
{
    /// <summary>Opens one native fixture without enabling any manager-specific product mode.</summary>
    /// <param name="instanceDir">Fixture root, containing mods and profiles unless <paramref name="stagingDir"/> overrides it.</param>
    /// <param name="maxPlugins">Optional positive resolver cap; zero means unlimited.</param>
    /// <param name="store">Isolated configuration store for the probe.</param>
    /// <param name="profileName">Profile directory selected under the fixture's profiles root.</param>
    /// <param name="stagingDir">Optional root containing mods, overwrite, and profiles.</param>
    /// <param name="gameDataDir">Optional vanilla Data root; otherwise the fixture's local or sibling game/Data is used.</param>
    /// <returns>A service configured only with derived native roots.</returns>
    internal static LoadOrderService Open(
        string instanceDir, int maxPlugins, UserConfigStore store,
        string profileName = "Default", string? stagingDir = null, string? gameDataDir = null)
    {
        var paths = Resolve(instanceDir, profileName, stagingDir, gameDataDir);
        return LoadOrderService.WithFixturePaths(
            paths.DataDir, paths.ModsDir, paths.ProfileDir, paths.OverwriteDir,
            Path.GetDirectoryName(paths.ProfileDir), maxPlugins, store);
    }

    /// <summary>Repoints a running service to another native synthetic fixture.</summary>
    /// <param name="service">Fixture service whose caches must be invalidated.</param>
    /// <param name="instanceDir">Replacement fixture root.</param>
    /// <param name="profileName">Profile directory selected under the fixture's profiles root.</param>
    /// <param name="stagingDir">Optional root containing mods, overwrite, and profiles.</param>
    /// <param name="gameDataDir">Optional vanilla Data root.</param>
    internal static void Switch(
        LoadOrderService service, string instanceDir,
        string profileName = "Default", string? stagingDir = null, string? gameDataDir = null)
    {
        var paths = Resolve(instanceDir, profileName, stagingDir, gameDataDir);
        service.SwitchFixturePaths(
            paths.DataDir, paths.ModsDir, paths.ProfileDir, paths.OverwriteDir,
            Path.GetDirectoryName(paths.ProfileDir));
    }

    /// <summary>Reads the real active Amethyst order used by an opt-in manual proof.</summary>
    /// <param name="manifestPath">Schema-v1 connection manifest exported by houseCARL-Amethyst setup.</param>
    /// <returns>The active profile name and its resolved plugin files in load-order order.</returns>
    internal static ConnectedOrder ReadConnectedOrder(string manifestPath)
    {
        var snapshot = new AmethystLayout(manifestPath).Capture();
        var unresolved = snapshot.ActivePluginOrder
            .Where(name => !snapshot.ResolvedPluginSources.ContainsKey(name))
            .ToList();
        if (unresolved.Count > 0)
            throw new InvalidOperationException(
                $"active Amethyst order has {unresolved.Count} unresolved plugin(s): {string.Join(", ", unresolved)}");
        var paths = snapshot.ActivePluginOrder
            .Select(name => snapshot.ResolvedPluginSources[name])
            .ToList();
        return new ConnectedOrder(snapshot.ActiveProfileName, paths);
    }

    /// <summary>Resolves and validates the native roots used by one synthetic fixture.</summary>
    static FixturePaths Resolve(
        string instanceDir, string profileName, string? stagingDir, string? gameDataDir)
    {
        var root = Path.GetFullPath(stagingDir ?? instanceDir);
        var profileDir = Path.Combine(root, "profiles", profileName);
        var localData = Path.Combine(instanceDir, "game", "Data");
        var siblingData = Path.Combine(Path.GetDirectoryName(instanceDir) ?? instanceDir, "game", "Data");
        var dataDir = Path.GetFullPath(gameDataDir ??
            (Directory.Exists(localData) ? localData : siblingData));
        var paths = new FixturePaths(
            profileDir, Path.Combine(root, "mods"), dataDir, Path.Combine(root, "overwrite"));

        foreach (var (path, label) in new[]
        {
            (paths.ProfileDir, "profile"), (paths.ModsDir, "mods"), (paths.DataDir, "vanilla Data")
        })
            if (!Directory.Exists(path))
                throw new InvalidOperationException($"synthetic fixture {label} directory is missing: '{path}'");
        if (!File.Exists(Path.Combine(paths.ProfileDir, "loadorder.txt")))
            throw new InvalidOperationException($"synthetic fixture profile has no loadorder.txt: '{paths.ProfileDir}'");
        return paths;
    }

    /// <summary>Native roots passed into the manager-neutral service fixture seam.</summary>
    sealed record FixturePaths(string ProfileDir, string ModsDir, string DataDir, string OverwriteDir);

    /// <summary>Resolved real Amethyst inputs used only by manual data-dependent proofs.</summary>
    internal sealed record ConnectedOrder(string ProfileName, IReadOnlyList<string> OrderedPaths);
}
