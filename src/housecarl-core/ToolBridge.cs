namespace HousecarlCore;

/// <summary>Identifies a user-configurable diagnostic-log directory.</summary>
public enum ToolDependency
{
    /// <summary>Papyrus script-log directory.</summary>
    PapyrusLogs,

    /// <summary>SKSE crash-log directory.</summary>
    CrashLogs,
}

/// <summary>Describes whether a diagnostic path is usable.</summary>
public enum ToolPathSource
{
    /// <summary>A valid user-saved directory.</summary>
    Saved,

    /// <summary>No usable directory is configured.</summary>
    Unset,
}

/// <summary>Describes one configurable diagnostic-log directory.</summary>
/// <param name="Dep">Stable dependency enum.</param>
/// <param name="Key">Wire key accepted by the setup tool.</param>
/// <param name="Display">Human-readable directory name.</param>
public sealed record ToolInfo(ToolDependency Dep, string Key, string Display);

/// <summary>Catalogs the native log directories that houseCARL may read.</summary>
/// <remarks>
/// External executable configuration is deliberately absent. PapyrusCompiler and BSArch need a
/// structured Proton command contract and remain deferred until after native v1.
/// </remarks>
public static class ToolBridge
{
    /// <summary>Complete native directory catalog.</summary>
    static readonly ToolInfo[] All =
    {
        new(ToolDependency.PapyrusLogs, "papyrus_logs", "the Papyrus script-log folder"),
        new(ToolDependency.CrashLogs, "crash_logs", "the SKSE crash-log folder"),
    };

    /// <summary>Catalog entries keyed by enum.</summary>
    static readonly Dictionary<ToolDependency, ToolInfo> ByDep = All.ToDictionary(info => info.Dep);

    /// <summary>Dependency enums keyed by case-insensitive wire name.</summary>
    static readonly Dictionary<string, ToolDependency> ByKey =
        All.ToDictionary(info => info.Key, info => info.Dep, StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets metadata for one diagnostic directory.</summary>
    public static ToolInfo Info(ToolDependency dep) => ByDep[dep];

    /// <summary>Gets the comma-separated valid setup keys.</summary>
    public static string WireKeys => string.Join(", ", All.Select(info => info.Key));

    /// <summary>Parses a case-insensitive setup key.</summary>
    public static bool TryParse(string? wire, out ToolDependency dep)
    {
        if (!string.IsNullOrWhiteSpace(wire) && ByKey.TryGetValue(wire.Trim(), out dep)) return true;
        dep = default;
        return false;
    }

    /// <summary>Validates that a configured native log directory exists.</summary>
    public static (bool ok, string? error) Validate(ToolDependency dep, string path)
    {
        var display = Info(dep).Display;
        if (string.IsNullOrWhiteSpace(path)) return (false, "no path given.");
        return Directory.Exists(path)
            ? (true, null)
            : (false, $"no such folder: '{path}'. Give the {display}.");
    }

    /// <summary>Reports a saved directory as usable only while it still exists.</summary>
    public static (string? path, ToolPathSource source) Inspect(ToolDependency dep, string? savedPath) =>
        savedPath is not null && Validate(dep, savedPath).ok
            ? (savedPath, ToolPathSource.Saved)
            : (null, ToolPathSource.Unset);
}
