using System.Text.Json;

namespace HousecarlCore;

/// <summary>
/// Reads the connector manifest and Amethyst's native profile state. This class resolves roots only;
/// load order, filemap, and mod-index parsing are layered onto the snapshot in later milestones.
/// </summary>
public sealed class AmethystLayout : IModManagerLayout
{
    const int SupportedSchema = 1;
    readonly string _manifestPath;
    ManagerSnapshot _snapshot;
    string _identity;

    public AmethystLayout(string manifestPath)
    {
        _manifestPath = ExistingFile(manifestPath, "Amethyst connection manifest");
        (_snapshot, _identity) = Read();
    }

    public ManagerSnapshot Capture()
    {
        RefreshIfStale();
        return _snapshot;
    }

    public bool RefreshIfStale()
    {
        var (snapshot, identity) = Read();
        if (identity == _identity) return false;
        (_snapshot, _identity) = (snapshot, identity);
        return true;
    }

    (ManagerSnapshot Snapshot, string Identity) Read()
    {
        using var manifest = JsonFile(_manifestPath, "connection manifest");
        var root = manifest.RootElement;
        var schema = RequiredInt(root, "schemaVersion", "connection manifest");
        if (schema != SupportedSchema)
            throw Error($"connection manifest schemaVersion {schema} is unsupported; install a compatible houseCARL-Amethyst connector");
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
            var freshness = Freshness(_manifestPath, pathsFile, deployStateFile, profileStateFile,
                Path.Combine(profileDir, "modlist.txt"), Path.Combine(profileDir, "plugins.txt"),
                Path.Combine(profileDir, "loadorder.txt"), Path.Combine(staging, "filemap.txt"),
                Path.Combine(staging, "modindex.bin"));
            var snapshot = new ManagerSnapshot(
                _manifestPath, schema, activeProfile, profileDir, specific,
                Path.Combine(staging, "mods"), Path.Combine(staging, "overwrite"),
                Path.Combine(staging, "filemap.txt"), Path.Combine(staging, "modindex.bin"),
                gamePath, vanillaData, pathsFile, deployStateFile, deploymentActive, lastMode,
                Array.Empty<string>(), new Dictionary<string, string>(), new Dictionary<string, string>(),
                Array.Empty<string>(), freshness);
            return (snapshot, Identity(snapshot, freshness));
        }
        finally { profileState?.Dispose(); }
    }

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

    static Dictionary<string, DateTime> Freshness(params string[] paths) => paths
        .Distinct(StringComparer.Ordinal)
        .ToDictionary(p => p, p => File.Exists(p) ? File.GetLastWriteTimeUtc(p) : DateTime.MinValue, StringComparer.Ordinal);

    static string Identity(ManagerSnapshot s, IReadOnlyDictionary<string, DateTime> freshness) => string.Join('\n', new[]
    {
        s.ActiveProfileName, s.ProfileDir, s.ProfileSpecificMods.ToString(), s.ModsDir, s.GamePath,
        s.VanillaDataDir, s.DeploymentActive.ToString(), s.LastDeploymentMode ?? ""
    }.Concat(freshness.Select(x => $"{x.Key}\0{x.Value.Ticks}")));

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

    static string RequiredString(JsonElement value, string property, string label) =>
        OptionalString(value, property) ?? throw Error($"{label}.{property} must be a non-empty string");

    static string? OptionalString(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var item) || item.ValueKind == JsonValueKind.Null)
            return null;
        if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            throw Error($"{property} must be a non-empty string");
        return item.GetString()!.Trim();
    }

    static int RequiredInt(JsonElement value, string property, string label)
    {
        if (!value.TryGetProperty(property, out var item) || !item.TryGetInt32(out var result))
            throw Error($"{label}.{property} must be an integer");
        return result;
    }

    static bool? OptionalBool(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var item) || item.ValueKind == JsonValueKind.Null)
            return null;
        if (item.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Error($"{property} must be a boolean");
        return item.GetBoolean();
    }

    static string ExistingFile(string value, string label)
    {
        var path = Absolute(value, label);
        if (!File.Exists(path)) throw Error($"{label} does not exist: '{path}'");
        return path;
    }

    static string ExistingDirectory(string value, string label)
    {
        var path = Absolute(value, label);
        if (!Directory.Exists(path)) throw Error($"{label} directory does not exist: '{path}'");
        return path;
    }

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

    static void ValidateName(string value, string label)
    {
        if (value is "." or ".." || value.IndexOfAny(new[] { '/', '\\', '\0' }) >= 0)
            throw Error($"{label} must be a profile name, not a path");
    }

    static bool SamePath(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.Ordinal);

    static AmethystConfigurationException Error(string message) => new(message);
}

/// <summary>A connector or Amethyst state error that is safe to return directly to the user.</summary>
public sealed class AmethystConfigurationException(string message)
    : InvalidOperationException($"Amethyst configuration error: {message}");
