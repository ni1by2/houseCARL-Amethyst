namespace HousecarlMcp;

/// <summary>Validates and persists native diagnostic-log directories.</summary>
public sealed class ToolPathResolver
{
    /// <summary>Shared atomic user-configuration store.</summary>
    readonly UserConfigStore _store;

    /// <summary>Creates a resolver backed by the shared user configuration.</summary>
    public ToolPathResolver(UserConfigStore store) => _store = store;

    /// <summary>Returns the saved directory for a dependency, or null when unset.</summary>
    public string? Saved(ToolDependency dep)
    {
        var paths = _store.Load().ToolPaths;
        return paths is not null && paths.TryGetValue(ToolBridge.Info(dep).Key, out var path) ? path : null;
    }

    /// <summary>Reports whether the saved directory still exists without changing configuration.</summary>
    public (string? path, ToolPathSource source) Inspect(ToolDependency dep) =>
        ToolBridge.Inspect(dep, Saved(dep));

    /// <summary>Validates and atomically stores a user-supplied native directory.</summary>
    public (bool ok, string? error, bool persisted, string? persistError, string? persistNote, string resolved)
        Save(ToolDependency dep, string rawPath)
    {
        var path = (rawPath ?? "").Trim().Trim('"');
        if (path.Length == 0) return (false, "no path given.", false, null, null, "");
        try { path = Path.GetFullPath(path); }
        catch (Exception ex)
        {
            return (false, $"'{rawPath}' is not a usable path ({ex.Message}).", false, null, null, rawPath ?? "");
        }

        var (ok, error) = ToolBridge.Validate(dep, path);
        if (!ok) return (false, error, false, null, null, path);

        var (persisted, persistError, persistNote) =
            _store.Update(config => (config.ToolPaths ??= new())[ToolBridge.Info(dep).Key] = path);
        return (true, null, persisted, persistError, persistNote, path);
    }
}
