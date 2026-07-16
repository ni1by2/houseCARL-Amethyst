namespace HousecarlCore;

/// <summary>
/// Isolates mod-manager state from the record and asset engines. Implementations must return a
/// complete, internally consistent snapshot or throw a named configuration error.
/// </summary>
public interface IModManagerLayout
{
    ManagerSnapshot Capture();
    bool RefreshIfStale();
}

/// <summary>One immutable view of the manager state used by a resolver build.</summary>
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
    IReadOnlyList<string> ActivePluginOrder,
    IReadOnlyDictionary<string, string> ResolvedPluginSources,
    IReadOnlyDictionary<string, string> LooseAssetSources,
    IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, DateTime> FreshnessInputs);
