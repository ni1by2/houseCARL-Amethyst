namespace HousecarlCore;

/// <summary>
/// Isolates mod-manager state from the record and asset engines. Implementations must return a
/// complete, internally consistent snapshot or throw a named configuration error.
/// </summary>
public interface IModManagerLayout
{
    /// <summary>Returns a current, internally consistent manager snapshot.</summary>
    /// <remarks>Implementations refresh changing profile state before returning.</remarks>
    /// <returns>Immutable state for one resolver build.</returns>
    ManagerSnapshot Capture();

    /// <summary>Re-reads manager inputs and replaces the cached snapshot only when its identity changed.</summary>
    /// <returns>True when a new snapshot replaced the prior one; otherwise false.</returns>
    bool RefreshIfStale();
}

/// <summary>One immutable view of the manager state used by a resolver build.</summary>
/// <param name="ManifestPath">Absolute path to the validated connection manifest.</param>
/// <param name="SchemaVersion">Connection-manifest schema understood by this process.</param>
/// <param name="ActiveProfileName">Amethyst profile selected when the snapshot was captured.</param>
/// <param name="ProfileDir">Absolute directory containing the active profile's state files.</param>
/// <param name="ProfileSpecificMods">Whether staging roots live under the profile instead of the shared root.</param>
/// <param name="ModsDir">Effective absolute Amethyst mod-staging directory.</param>
/// <param name="OverwriteDir">Effective absolute Amethyst overwrite staging directory.</param>
/// <param name="FilemapPath">Authoritative loose-file winner map for the effective staging model.</param>
/// <param name="ModIndexPath">MessagePack index that recovers raw source path spelling for filemap entries.</param>
/// <param name="GamePath">Absolute native Skyrim installation root.</param>
/// <param name="VanillaDataDir">Unmerged vanilla source: Data_Core, or inactive-deployment Data fallback.</param>
/// <param name="PathsFile">Amethyst paths.json used to validate stable roots.</param>
/// <param name="DeployStateFile">Amethyst deploy_state.json used to select the live profile and deploy state.</param>
/// <param name="DeploymentActive">Whether Amethyst reports a currently deployed profile.</param>
/// <param name="LastDeploymentMode">Last Amethyst deployment mode, or null when Amethyst did not record one.</param>
/// <param name="FilemapReady">Whether filemap and mod index form a usable authoritative winner set.</param>
/// <param name="ActivePluginOrder">Active plugin names in Skyrim load order, including unresolved entries.</param>
/// <param name="ResolvedPluginSources">Active plugin names mapped to their physical winning source files.</param>
/// <param name="LooseAssetSources">Canonical Data-relative winners mapped to physical staging sources.</param>
/// <param name="Warnings">Non-fatal omissions or inconsistencies surfaced while capturing the snapshot.</param>
/// <param name="FreshnessInputs">Manager-owned input paths and mtimes used to detect later changes.</param>
public sealed record ManagerSnapshot(
    string ManifestPath,
    int SchemaVersion,
    string ActiveProfileName,
    string ProfileDir,
    bool ProfileSpecificMods,
    string ModsDir,
    string OverwriteDir,
    string FilemapPath,
    string ModIndexPath,
    string GamePath,
    string VanillaDataDir,
    string PathsFile,
    string DeployStateFile,
    bool DeploymentActive,
    string? LastDeploymentMode,
    bool FilemapReady,
    IReadOnlyList<string> ActivePluginOrder,
    IReadOnlyDictionary<string, string> ResolvedPluginSources,
    IReadOnlyDictionary<string, ManagerFileSource> LooseAssetSources,
    IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, DateTime> FreshnessInputs);
