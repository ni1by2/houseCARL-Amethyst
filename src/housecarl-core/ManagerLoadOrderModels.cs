namespace HousecarlCore;

/// <summary>Resolved active plugin order plus every non-fatal source warning.</summary>
/// <param name="OrderedPaths">Physical plugin paths in manager load order, with the winning record last.</param>
/// <param name="Warnings">Missing sources or incomplete manager state that callers must surface.</param>
/// <param name="ActiveCount">Number of active plugins requested by manager state.</param>
public sealed record ModOrderResult(
    IReadOnlyList<string> OrderedPaths,
    IReadOnlyList<string> Warnings,
    int ActiveCount)
{
    /// <summary>Number of requested active plugins that resolved to physical files.</summary>
    public int ResolvedCount => OrderedPaths.Count;

    /// <summary>Case-insensitive plugin filename to winning physical source.</summary>
    public IReadOnlyDictionary<string, string> ResolvedSources { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Manager-neutral profile activation and load-order composition.</summary>
/// <param name="EnabledMods">Enabled staging layers in highest-first priority order.</param>
/// <param name="DisabledMods">Known staging layers excluded from deployment.</param>
/// <param name="LockedMods">Enabled layers the manager prevents users from toggling.</param>
/// <param name="OrderedPluginNames">Plugin filenames in authoritative load order.</param>
/// <param name="ActivePluginNames">Plugin filenames explicitly marked active.</param>
/// <param name="InactivePluginNames">Known plugin filenames explicitly marked inactive.</param>
/// <param name="ImplicitPluginNames">Ordered plugins absent from explicit activation state, such as force-loaded masters.</param>
public sealed record ModComposition(
    IReadOnlyList<string> EnabledMods,
    IReadOnlyList<string> DisabledMods,
    IReadOnlyList<string> LockedMods,
    IReadOnlyList<string> OrderedPluginNames,
    IReadOnlySet<string> ActivePluginNames,
    IReadOnlyList<string> InactivePluginNames,
    IReadOnlyList<string> ImplicitPluginNames);

/// <summary>One physical plugin copy and the manager layer that provides it.</summary>
/// <param name="Path">Absolute native path to the plugin file.</param>
/// <param name="Where">Human-readable provider label.</param>
/// <param name="Enabled">Whether the provider currently participates in the active profile.</param>
public sealed record PluginFileHit(string Path, string Where, bool Enabled);
