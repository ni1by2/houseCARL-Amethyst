using System.Text.Json;

namespace HousecarlCore;

/// <summary>Stable schema-v1 Amethyst connection paths; this does not read filegraph winners.</summary>
public sealed record AmethystConnection(
    string ManifestPath, string ProfileRoot, string GameConfigDir, string PathsFile, string DeployStateFile)
{
    public static AmethystConnection Open(string manifestPath)
    {
        var manifest = ExistingFile(manifestPath, "connection manifest");
        using var doc = JsonFile(manifest, "connection manifest");
        var root = doc.RootElement;
        if (!root.TryGetProperty("schemaVersion", out var version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schema))
            throw Error("connection manifest.schemaVersion must be an integer");
        if (schema != 1)
            throw Error($"connection manifest schemaVersion {schema} is unsupported; run compatible setup");
        if (RequiredString(root, "manager") != "amethyst")
            throw Error("connection manifest.manager must be 'amethyst'");
        if (RequiredString(root, "gameId") != "skyrim_se")
            throw Error("connection manifest.gameId must be 'skyrim_se'");

        var profileRoot = ExistingDirectory(RequiredString(root, "profileRoot"), "profileRoot");
        var configDir = ExistingDirectory(RequiredString(root, "gameConfigDir"), "gameConfigDir");
        var pathsFile = ExistingFile(RequiredString(root, "pathsFile"), "pathsFile");
        var deployFile = ExistingFile(RequiredString(root, "deployStateFile"), "deployStateFile");
        if (!SamePath(Path.GetDirectoryName(pathsFile)!, configDir) ||
            !SamePath(Path.GetDirectoryName(deployFile)!, configDir))
            throw Error("pathsFile and deployStateFile must be inside gameConfigDir; run setup again");

        using var paths = JsonFile(pathsFile, "paths.json");
        if (paths.RootElement.TryGetProperty("staging_path", out var staging))
        {
            if (staging.ValueKind != JsonValueKind.String)
                throw Error("paths.json.staging_path must be a string");
            var configured = staging.GetString()?.Trim();
            if (!string.IsNullOrEmpty(configured) &&
                !SamePath(profileRoot, ExistingDirectory(configured, "paths.json.staging_path")))
                throw Error("connection manifest profileRoot disagrees with paths.json staging_path; run setup again");
        }
        using var _ = JsonFile(deployFile, "deploy_state.json");
        return new(manifest, profileRoot, configDir, pathsFile, deployFile);
    }

    static JsonDocument JsonFile(string path, string label)
    {
        try
        {
            var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
            throw Error($"{label} must contain a JSON object: '{path}'");
        }
        catch (AmethystConfigurationException) { throw; }
        catch (Exception ex) { throw Error($"could not read {label} at '{path}': {ex.Message}"); }
    }

    static string RequiredString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw Error($"connection manifest.{property} must be a non-empty string");
        return value.GetString()!.Trim();
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
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0') || !Path.IsPathFullyQualified(value))
            throw Error($"{label} must be an absolute native path: '{value}'");
        try { return Path.GetFullPath(value); }
        catch (Exception ex) { throw Error($"{label} is not a valid native path: {ex.Message}"); }
    }

    static bool SamePath(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.Ordinal);

    static AmethystConfigurationException Error(string message) => new(message);
}

public sealed class AmethystConfigurationException(string message)
    : InvalidOperationException($"Amethyst configuration error: {message}");
