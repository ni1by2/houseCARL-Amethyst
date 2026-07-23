using System.Text.Json;

namespace HousecarlCore;

/// <summary>
/// Reads the setup manifest and Amethyst's native profile, deployment, load-order, and filemap state
/// into the manager-neutral snapshot consumed by the record and asset engines.
/// </summary>
public sealed class AmethystLayout : IModManagerLayout
{
    /// <summary>Only connection-manifest schema whose required fields this build understands.</summary>
    const int SupportedSchema = 1;

    /// <summary>Validated absolute path to the stable setup-generated connection manifest.</summary>
    readonly string _manifestPath;

    /// <summary>Last complete snapshot returned to consumers.</summary>
    ManagerSnapshot _snapshot;

    /// <summary>Deterministic profile/path/mtime identity paired with <see cref="_snapshot"/>.</summary>
    string _identity;

    /// <summary>Opens and validates an Amethyst connection and captures its initial state.</summary>
    /// <param name="manifestPath">Absolute native path to a schema-v1 connection manifest.</param>
    /// <exception cref="AmethystConfigurationException">Any required manifest or manager input is invalid.</exception>
    public AmethystLayout(string manifestPath)
    {
        _manifestPath = ExistingFile(manifestPath, "Amethyst connection manifest");
        (_snapshot, _identity) = Read();
    }

    /// <summary>Returns a current complete snapshot, refreshing manager inputs first when necessary.</summary>
    /// <returns>Immutable manager state for one resolver build.</returns>
    public ManagerSnapshot Capture()
    {
        RefreshIfStale();
        return _snapshot;
    }

    /// <summary>Rebuilds the snapshot and installs it only when its semantic/mtime identity changed.</summary>
    /// <returns>True when consumers must rebuild resolver state; otherwise false.</returns>
    /// <remarks>
    /// The full read happens before comparison so malformed new manager state fails loudly rather than
    /// leaving callers unknowingly attached to an older valid profile.
    /// </remarks>
    public bool RefreshIfStale()
    {
        var (snapshot, identity) = Read();
        if (identity == _identity) return false;
        (_snapshot, _identity) = (snapshot, identity);
        return true;
    }

    /// <summary>Reads and cross-validates every manager input needed for one coherent snapshot.</summary>
    /// <returns>The new snapshot and its deterministic freshness identity.</returns>
    /// <exception cref="AmethystConfigurationException">Inputs disagree, are stale, or violate the schema.</exception>
    (ManagerSnapshot Snapshot, string Identity) Read()
    {
        using var manifest = JsonFile(_manifestPath, "connection manifest");
        var root = manifest.RootElement;
        var schema = RequiredInt(root, "schemaVersion", "connection manifest");
        if (schema != SupportedSchema)
            throw Error($"connection manifest schemaVersion {schema} is unsupported; run setup from a compatible houseCARL-Amethyst release");
        if (RequiredString(root, "manager", "connection manifest") != "amethyst")
            throw Error("connection manifest manager must be 'amethyst'");
        if (RequiredString(root, "gameId", "connection manifest") != "skyrim_se")
            throw Error("connection manifest must describe Skyrim Special Edition (gameId 'skyrim_se')");

        var profileRoot = ExistingDirectory(RequiredString(root, "profileRoot", "connection manifest"), "profileRoot");
        var gameConfigDir = ExistingDirectory(RequiredString(root, "gameConfigDir", "connection manifest"), "gameConfigDir");
        var pathsFile = ExistingFile(RequiredString(root, "pathsFile", "connection manifest"), "pathsFile");
        var deployStateFile = ExistingFile(RequiredString(root, "deployStateFile", "connection manifest"), "deployStateFile");
        if (!SamePath(gameConfigDir, Path.GetDirectoryName(pathsFile)!))
            throw Error($"pathsFile is outside gameConfigDir: '{pathsFile}'");
        if (!SamePath(gameConfigDir, Path.GetDirectoryName(deployStateFile)!))
            throw Error($"deployStateFile is outside gameConfigDir: '{deployStateFile}'");

        using var paths = JsonFile(pathsFile, "paths.json");
        var configuredRoot = ExistingDirectory(RequiredString(paths.RootElement, "staging_path", "paths.json"), "paths.json.staging_path");
        if (!SamePath(profileRoot, configuredRoot))
            throw Error($"connection manifest is stale: profileRoot '{profileRoot}' does not match paths.json staging_path '{configuredRoot}'");

        using var deploy = JsonFile(deployStateFile, "deploy_state.json");
        var activeProfile = OptionalString(deploy.RootElement, "last_active_profile") ?? "default";
        ValidateName(activeProfile, "deploy_state.json.last_active_profile");
        var deploymentActive = OptionalBool(deploy.RootElement, "deploy_active") ?? false;
        var lastMode = OptionalString(deploy.RootElement, "last_deploy_mode");

        var profileDir = ExistingDirectory(Path.Combine(profileRoot, "profiles", activeProfile), $"active profile '{activeProfile}'");
        var profileStateFile = Path.Combine(profileDir, "profile_state.json");
        JsonDocument? profileState = File.Exists(profileStateFile) ? JsonFile(profileStateFile, "profile_state.json") : null;
        try
        {
            var settings = Settings(profileState);
            var specific = OptionalBool(settings, "profile_specific_mods") ?? false;
            var gameValue = OptionalString(settings, "game_path") ?? OptionalString(paths.RootElement, "game_path");
            if (gameValue is null) throw Error("game_path is not configured in the active profile or paths.json");
            var gamePath = ExistingDirectory(gameValue, "game_path");
            var dataCore = Path.Combine(gamePath, "Data_Core");
            var data = Path.Combine(gamePath, "Data");
            var vanillaData = Directory.Exists(dataCore)
                ? Path.GetFullPath(dataCore)
                : !deploymentActive && Directory.Exists(data)
                    ? Path.GetFullPath(data)
                    : deploymentActive
                        ? throw Error($"deployment is active but Data_Core is missing; refusing to treat merged Data as vanilla: '{dataCore}'")
                        : throw Error($"vanilla Data directory does not exist: '{data}'");

            var staging = specific ? profileDir : profileRoot;
            var modsDir = Path.Combine(staging, "mods");
            var overwriteDir = Path.Combine(staging, "overwrite");
            var filemapPath = Path.Combine(staging, "filemap.txt");
            var modIndexPath = Path.Combine(staging, "modindex.bin");
            var fileIndex = AmethystFileMap.Load(
                profileDir, modsDir, overwriteDir, filemapPath, modIndexPath);
            var composition = AmethystLoadOrder.ReadComposition(profileDir);
            var activeNames = composition.OrderedPluginNames
                .Where(name => !composition.InactivePluginNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                .ToList();
            var order = fileIndex.Ready
                ? AmethystLoadOrder.Build(profileDir, vanillaData, fileIndex)
                : new ModOrderResult(Array.Empty<string>(), fileIndex.Warnings, activeNames.Count);
            var freshness = Freshness(_manifestPath, pathsFile, deployStateFile, profileStateFile,
                Path.Combine(profileDir, "modlist.txt"), Path.Combine(profileDir, "plugins.txt"),
                Path.Combine(profileDir, "loadorder.txt"), filemapPath, modIndexPath);
            var snapshot = new ManagerSnapshot(
                _manifestPath, schema, activeProfile, profileDir, specific,
                modsDir, overwriteDir,
                filemapPath, modIndexPath,
                gamePath, vanillaData, pathsFile, deployStateFile, deploymentActive, lastMode,
                fileIndex.Ready, activeNames, order.ResolvedSources, fileIndex.Sources,
                order.Warnings, freshness);
            return (snapshot, Identity(snapshot, freshness));
        }
        finally { profileState?.Dispose(); }
    }

    /// <summary>Returns the optional profile_settings object from profile_state.json.</summary>
    /// <param name="state">Owned profile-state document, or null when the optional file is absent.</param>
    /// <returns>Undefined/default when the file or property is absent; a JSON object when present.</returns>
    static JsonElement Settings(JsonDocument? state)
    {
        if (state is null) return default;
        var root = state.RootElement;
        if (!root.TryGetProperty("profile_settings", out var settings) || settings.ValueKind == JsonValueKind.Null)
            return default;
        if (settings.ValueKind != JsonValueKind.Object)
            throw Error("profile_state.json.profile_settings must be an object");
        return settings;
    }

    /// <summary>Captures the UTC mtime of every distinct manager input, including absent inputs.</summary>
    /// <param name="paths">Manager-owned files whose changes can alter effective state.</param>
    /// <remarks>Absent files use <see cref="DateTime.MinValue"/> so their later creation changes identity.</remarks>
    static Dictionary<string, DateTime> Freshness(params string[] paths) => paths
        .Distinct(StringComparer.Ordinal)
        .ToDictionary(p => p, p => File.Exists(p) ? File.GetLastWriteTimeUtc(p) : DateTime.MinValue, StringComparer.Ordinal);

    /// <summary>Builds the exact comparison token used by <see cref="RefreshIfStale"/>.</summary>
    /// <param name="s">Freshly captured semantic manager state.</param>
    /// <param name="freshness">Input paths and mtimes captured with that state.</param>
    /// <remarks>
    /// It includes semantic roots and deployment state as well as mtimes. A profile switch therefore
    /// refreshes even if files happen to carry identical timestamps.
    /// </remarks>
    static string Identity(ManagerSnapshot s, IReadOnlyDictionary<string, DateTime> freshness) => string.Join('\n', new[]
    {
        s.ActiveProfileName, s.ProfileDir, s.ProfileSpecificMods.ToString(), s.ModsDir, s.GamePath,
        s.VanillaDataDir, s.DeploymentActive.ToString(), s.LastDeploymentMode ?? ""
    }.Concat(freshness.Select(x => $"{x.Key}\0{x.Value.Ticks}")));

    /// <summary>Reads a required JSON file whose root must be an object.</summary>
    /// <param name="path">Absolute manager-owned file path.</param>
    /// <param name="label">Human-readable source name used in errors.</param>
    /// <returns>An owned document that the caller must dispose.</returns>
    /// <exception cref="AmethystConfigurationException">The file is unreadable, invalid, or not an object.</exception>
    static JsonDocument JsonFile(string path, string label)
    {
        try
        {
            var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw Error($"{label} must contain a JSON object: '{path}'");
            }
            return document;
        }
        catch (AmethystConfigurationException) { throw; }
        catch (Exception ex) { throw Error($"could not read {label} at '{path}': {ex.Message}"); }
    }

    /// <summary>Reads a required non-empty string property.</summary>
    /// <param name="value">JSON object containing the property.</param>
    /// <param name="property">Exact property name.</param>
    /// <param name="label">Parent source label used to qualify errors.</param>
    /// <returns>The trimmed string.</returns>
    static string RequiredString(JsonElement value, string property, string label) =>
        OptionalString(value, property) ?? throw Error($"{label}.{property} must be a non-empty string");

    /// <summary>Reads an optional string, distinguishing absence/null from malformed or blank content.</summary>
    /// <param name="value">JSON object that may contain the property.</param>
    /// <param name="property">Exact property name.</param>
    /// <returns>The trimmed value, or null when the object/property is absent or explicitly null.</returns>
    static string? OptionalString(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var item) || item.ValueKind == JsonValueKind.Null)
            return null;
        if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            throw Error($"{property} must be a non-empty string");
        return item.GetString()!.Trim();
    }

    /// <summary>Reads a required signed 32-bit integer property.</summary>
    /// <param name="value">JSON object containing the property.</param>
    /// <param name="property">Exact property name.</param>
    /// <param name="label">Parent source label used to qualify errors.</param>
    /// <returns>The parsed integer.</returns>
    static int RequiredInt(JsonElement value, string property, string label)
    {
        if (!value.TryGetProperty(property, out var item) || !item.TryGetInt32(out var result))
            throw Error($"{label}.{property} must be an integer");
        return result;
    }

    /// <summary>Reads an optional JSON boolean without accepting truthy strings or numbers.</summary>
    /// <param name="value">JSON object that may contain the property.</param>
    /// <param name="property">Exact property name.</param>
    /// <returns>True/false when present, or null when absent or explicitly null.</returns>
    static bool? OptionalBool(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var item) || item.ValueKind == JsonValueKind.Null)
            return null;
        if (item.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Error($"{property} must be a boolean");
        return item.GetBoolean();
    }

    /// <summary>Validates an absolute native path and requires an existing regular file entry.</summary>
    /// <param name="value">Untrusted path text from configuration.</param>
    /// <param name="label">Human-readable path role used in errors.</param>
    /// <returns>The normalized absolute path.</returns>
    static string ExistingFile(string value, string label)
    {
        var path = Absolute(value, label);
        if (!File.Exists(path)) throw Error($"{label} does not exist: '{path}'");
        return path;
    }

    /// <summary>Validates an absolute native path and requires an existing directory.</summary>
    /// <param name="value">Untrusted path text from configuration.</param>
    /// <param name="label">Human-readable path role used in errors.</param>
    /// <returns>The normalized absolute path.</returns>
    static string ExistingDirectory(string value, string label)
    {
        var path = Absolute(value, label);
        if (!Directory.Exists(path)) throw Error($"{label} directory does not exist: '{path}'");
        return path;
    }

    /// <summary>Validates and normalizes a manifest-supplied native Linux path.</summary>
    /// <param name="value">Untrusted path text from configuration.</param>
    /// <param name="label">Human-readable path role used in errors.</param>
    /// <returns>The normalized absolute host path.</returns>
    /// <remarks>Relative paths and NUL are rejected before any filesystem lookup.</remarks>
    static string Absolute(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0'))
            throw Error($"{label} must be an absolute native Linux path: '{value}'");
        try
        {
            if (!Path.IsPathFullyQualified(value))
                throw Error($"{label} must be an absolute native Linux path: '{value}'");
            return Path.GetFullPath(value);
        }
        catch (AmethystConfigurationException) { throw; }
        catch (Exception ex) { throw Error($"{label} is not a valid native Linux path: {ex.Message}"); }
    }

    /// <summary>Ensures a profile identifier is one directory name rather than a path.</summary>
    /// <param name="value">Profile name read from deployment state.</param>
    /// <param name="label">Qualified field name used in errors.</param>
    static void ValidateName(string value, string label)
    {
        if (value is "." or ".." || value.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0)
            throw Error($"{label} must be a profile name, not a path");
    }

    /// <summary>Compares normalized Linux paths using case-sensitive host semantics.</summary>
    /// <param name="left">First native path.</param>
    /// <param name="right">Second native path.</param>
    /// <returns>True when both normalize to the same host spelling.</returns>
    static bool SamePath(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.Ordinal);

    /// <summary>Creates the named configuration exception used by this layout boundary.</summary>
    /// <param name="message">Specific invalid input and corrective guidance.</param>
    /// <returns>A user-safe named configuration exception.</returns>
    static AmethystConfigurationException Error(string message) => new(message);
}

/// <summary>A setup-manifest or Amethyst-state error safe to return to the user.</summary>
/// <param name="message">Specific invalid input and, where possible, the corrective action.</param>
public sealed class AmethystConfigurationException(string message)
    : InvalidOperationException($"Amethyst configuration error: {message}");
