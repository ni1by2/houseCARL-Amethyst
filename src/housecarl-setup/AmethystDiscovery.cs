using System.Text.Json;
using System.Text.Json.Nodes;

namespace HousecarlSetup;

/// <summary>Discovers one native Amethyst Skyrim SE configuration and writes a stable connection manifest.</summary>
public static class AmethystDiscovery
{
    private const string AppDirectory = "AmethystModManager";
    private const string FlatpakId = "io.github.Amethyst.ModManager";
    private const string GameName = "Skyrim Special Edition";

    /// <summary>Explicit filesystem inputs used by discovery and its hermetic tests.</summary>
    /// <param name="Home">Native user home.</param>
    /// <param name="XdgConfigHome">Native XDG configuration root.</param>
    /// <param name="ExplicitConfig">Optional Amethyst root or Skyrim game-config directory.</param>
    /// <param name="ProfilesBaseOverride">Optional value of Amethyst's MOD_MANAGER_PROFILES_DIR.</param>
    public sealed record DiscoveryContext(
        string Home,
        string XdgConfigHome,
        string? ExplicitConfig = null,
        string? ProfilesBaseOverride = null);

    /// <summary>Validated connection written by <see cref="CreateConnection"/>.</summary>
    /// <param name="ManifestPath">Atomic schema-v1 manifest destination.</param>
    /// <param name="ProfileRoot">Amethyst staging root containing profiles.</param>
    /// <param name="GameConfigDir">Amethyst Skyrim SE configuration directory.</param>
    /// <param name="ActiveProfile">Profile selected by deploy_state.json.</param>
    public sealed record ConnectionResult(
        string ManifestPath,
        string ProfileRoot,
        string GameConfigDir,
        string ActiveProfile);

    /// <summary>Finds one unambiguous Skyrim configuration, validates it, and atomically writes connection.json.</summary>
    public static ConnectionResult CreateConnection(DiscoveryContext context)
    {
        string home = ExistingDirectory(context.Home, "home");
        string configHome = Absolute(context.XdgConfigHome, "XDG_CONFIG_HOME");
        List<string> candidates = CandidateGameConfigs(home, configHome, context.ExplicitConfig);
        List<(ConnectionResult Result, JsonObject Manifest)> valid = [];
        List<string> errors = [];

        foreach (string candidate in candidates)
            try { valid.Add(Validate(candidate, home, context.ProfilesBaseOverride)); }
            catch (InvalidOperationException ex) { errors.Add($"{candidate}: {ex.Message}"); }

        if (valid.Count == 0)
            throw new InvalidOperationException(
                "no usable Amethyst Skyrim Special Edition configuration was found. " +
                "Open the profile in Amethyst, or pass --amethyst-config PATH. Checked:\n  " +
                string.Join("\n  ", errors.Count > 0 ? errors : candidates));
        if (valid.Count > 1)
            throw new InvalidOperationException(
                "multiple usable Amethyst Skyrim configurations were found; pass --amethyst-config PATH:\n  " +
                string.Join("\n  ", valid.Select(item => item.Result.GameConfigDir)));

        var selected = valid[0];
        AtomicJson(selected.Result.ManifestPath, selected.Manifest);
        return selected.Result;
    }

    /// <summary>Builds distinct native/AppImage/AUR and Flatpak game-config candidates.</summary>
    private static List<string> CandidateGameConfigs(string home, string configHome, string? explicitConfig)
    {
        if (!string.IsNullOrWhiteSpace(explicitConfig))
            return [NormalizeGameConfig(explicitConfig)];
        return new[]
        {
            Path.Combine(configHome, AppDirectory, "games", GameName),
            Path.Combine(home, ".var", "app", FlatpakId, "config", AppDirectory, "games", GameName),
        }.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Accepts either an Amethyst config root or the Skyrim game-config directory itself.</summary>
    private static string NormalizeGameConfig(string value)
    {
        string path = Absolute(value, "--amethyst-config");
        if (File.Exists(Path.Combine(path, "paths.json")))
            return path;
        return Path.Combine(path, "games", GameName);
    }

    /// <summary>Validates one candidate and constructs its manifest entirely in memory.</summary>
    private static (ConnectionResult Result, JsonObject Manifest) Validate(
        string gameConfigDir,
        string home,
        string? profilesBaseOverride)
    {
        string config = ExistingDirectory(gameConfigDir, "game config");
        string pathsFile = ExistingFile(Path.Combine(config, "paths.json"), "paths.json");
        string deployFile = ExistingFile(Path.Combine(config, "deploy_state.json"), "deploy_state.json");
        using JsonDocument paths = JsonObjectFile(pathsFile, "paths.json");
        using JsonDocument deploy = JsonObjectFile(deployFile, "deploy_state.json");

        string profileRoot = ProfileRoot(
            paths.RootElement,
            config,
            home,
            profilesBaseOverride);
        string active = OptionalString(deploy.RootElement, "last_active_profile") ?? "default";
        SimpleName(active, "last_active_profile");
        string profile = ExistingDirectory(
            Path.Combine(profileRoot, "profiles", active),
            $"active profile '{active}'");
        ExistingFile(Path.Combine(profile, "modlist.txt"), "active profile modlist.txt");
        ExistingFile(Path.Combine(profile, "plugins.txt"), "active profile plugins.txt");

        string? game = ProfileGamePath(profile) ?? OptionalString(paths.RootElement, "game_path");
        if (game is null)
            throw new InvalidOperationException("game_path is not configured globally or for the active profile.");
        string gamePath = ExistingDirectory(game, "game_path");
        bool activeDeploy = OptionalBool(deploy.RootElement, "deploy_active") ?? false;
        if (activeDeploy && !Directory.Exists(Path.Combine(gamePath, "Data_Core")))
            throw new InvalidOperationException("deployment is active but game_path/Data_Core is missing.");
        if (!activeDeploy &&
            !Directory.Exists(Path.Combine(gamePath, "Data_Core")) &&
            !Directory.Exists(Path.Combine(gamePath, "Data")))
            throw new InvalidOperationException("game_path contains neither Data_Core nor Data.");

        string manifestPath = Path.Combine(profileRoot, ".housecarl-amethyst", "connection.json");
        JsonObject manifest = new()
        {
            ["schemaVersion"] = 1,
            ["manager"] = "amethyst",
            ["gameId"] = "skyrim_se",
            ["gameName"] = GameName,
            ["profileRoot"] = profileRoot,
            ["gameConfigDir"] = config,
            ["pathsFile"] = pathsFile,
            ["deployStateFile"] = deployFile,
            ["createdBy"] = "housecarl-amethyst-setup",
            ["setupVersion"] = "1.0.0",
        };
        return (new(manifestPath, profileRoot, config, active), manifest);
    }

    /// <summary>Derives custom or legacy/default Amethyst staging without modifying manager state.</summary>
    private static string ProfileRoot(
        JsonElement paths,
        string gameConfigDir,
        string home,
        string? profilesBaseOverride)
    {
        string? staging = OptionalBlankString(paths, "staging_path");
        if (staging is not null)
            return ExistingDirectory(staging, "staging_path");

        string configRoot = Path.GetFullPath(Path.Combine(gameConfigDir, "..", ".."));
        string profilesBase;
        if (!string.IsNullOrWhiteSpace(profilesBaseOverride))
            profilesBase = ExistingDirectory(profilesBaseOverride, "MOD_MANAGER_PROFILES_DIR");
        else
        {
            string legacy = Path.Combine(configRoot, "Profiles");
            profilesBase = Directory.Exists(legacy)
                ? legacy
                : Path.Combine(home, "Games", "Amethyst", "Profiles");
        }
        return ExistingDirectory(Path.Combine(profilesBase, GameName), "default staging root");
    }

    /// <summary>Reads the active profile's optional game_path override.</summary>
    private static string? ProfileGamePath(string profileDir)
    {
        string path = Path.Combine(profileDir, "profile_state.json");
        if (!File.Exists(path)) return null;
        using JsonDocument state = JsonObjectFile(path, "profile_state.json");
        if (!state.RootElement.TryGetProperty("profile_settings", out JsonElement settings) ||
            settings.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (settings.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("profile_state.json.profile_settings must be an object.");
        return OptionalString(settings, "game_path");
    }

    /// <summary>Writes indented UTF-8 JSON by same-directory temporary file and atomic replacement.</summary>
    private static void AtomicJson(string path, JsonObject value)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is null) throw new InvalidOperationException("connection manifest has no parent directory.");
        Directory.CreateDirectory(directory);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(
                temporary,
                value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>Reads a JSON object and gives malformed manager state a source-specific error.</summary>
    private static JsonDocument JsonObjectFile(string path, string label)
    {
        try
        {
            JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw new InvalidOperationException($"{label} must contain a JSON object.");
            }
            return document;
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception ex) { throw new InvalidOperationException($"{label} is invalid: {ex.Message}"); }
    }

    /// <summary>Reads an optional nonblank string and rejects wrong JSON types.</summary>
    private static string? OptionalString(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidOperationException($"{property} must be a non-empty string.");
        return value.GetString()!.Trim();
    }

    /// <summary>Treats an absent or blank Amethyst staging_path as the manager's default staging convention.</summary>
    private static string? OptionalBlankString(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException($"{property} must be a string.");
        string text = value.GetString()!.Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>Reads an optional strict JSON boolean.</summary>
    private static bool? OptionalBool(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidOperationException($"{property} must be a boolean.");
        return value.GetBoolean();
    }

    /// <summary>Requires one safe profile-name segment rather than a relative path.</summary>
    private static void SimpleName(string value, string label)
    {
        if (value is "." or ".." || value.IndexOfAny(['/', '\\', '\0']) >= 0)
            throw new InvalidOperationException($"{label} must be one profile name.");
    }

    /// <summary>Normalizes an absolute native path and rejects relative, blank, or NUL input.</summary>
    private static string Absolute(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0') || !Path.IsPathFullyQualified(value))
            throw new InvalidOperationException($"{label} must be an absolute native Linux path.");
        return Path.GetFullPath(value);
    }

    /// <summary>Requires an existing absolute directory.</summary>
    private static string ExistingDirectory(string value, string label)
    {
        string path = Absolute(value, label);
        if (!Directory.Exists(path))
            throw new InvalidOperationException($"{label} does not exist: '{path}'.");
        return path;
    }

    /// <summary>Requires an existing absolute regular file.</summary>
    private static string ExistingFile(string value, string label)
    {
        string path = Absolute(value, label);
        if (!File.Exists(path))
            throw new InvalidOperationException($"{label} does not exist: '{path}'.");
        return path;
    }
}
