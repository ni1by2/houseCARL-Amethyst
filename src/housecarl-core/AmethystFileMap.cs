using System.Text.Json;
using MessagePack;

namespace HousecarlCore;

/// <summary>One authoritative Amethyst loose-file winner.</summary>
/// <param name="Provider">Amethyst mod name, or the reserved <c>[Overwrite]</c> provider.</param>
/// <param name="HostPath">Absolute actual-cased source path in staging.</param>
public sealed record ManagerFileSource(string Provider, string HostPath);

/// <summary>Parsed filemap state. Not-ready state is explicit and never triggers a Data scan.</summary>
/// <param name="Ready">Whether Sources is a complete authoritative winner set.</param>
/// <param name="Sources">Canonical Data-relative paths mapped to physical staging winners.</param>
/// <param name="Warnings">Actionable reasons the index is unavailable or incomplete.</param>
public sealed record ManagerFileIndex(
    bool Ready,
    IReadOnlyDictionary<string, ManagerFileSource> Sources,
    IReadOnlyList<string> Warnings);

/// <summary>Strict reader for Amethyst filemap.txt and MessagePack modindex.bin v4.</summary>
public static class AmethystFileMap
{
    /// <summary>Private modindex.bin schema emitted by the supported Amethyst release.</summary>
    const int Version = 4;

    /// <summary>Allocation guard for an untrusted or corrupt binary index.</summary>
    const long MaxIndexBytes = 256L * 1024 * 1024;

    /// <summary>Maximum mod entries accepted before refusing a suspicious index.</summary>
    const int MaxMods = 100_000;

    /// <summary>Maximum cumulative file entries accepted before refusing a suspicious index.</summary>
    const int MaxFiles = 5_000_000;

    /// <summary>Reserved filemap provider whose files live directly under overwrite staging.</summary>
    const string Overwrite = "[Overwrite]";

    /// <summary>Parses and cross-validates Amethyst's loose-file winner map and raw-path index.</summary>
    /// <param name="profileDir">Active profile containing optional strip-prefix configuration.</param>
    /// <param name="modsDir">Effective mod-staging root.</param>
    /// <param name="overwriteDir">Effective overwrite staging root.</param>
    /// <param name="filemapPath">Authoritative tab-separated logical winner map.</param>
    /// <param name="indexPath">MessagePack v4 provider/path index.</param>
    /// <returns>
    /// A ready authoritative index, or an explicit not-ready result when either file is missing or
    /// filemap predates modindex. Structural disagreement throws rather than guessing winners.
    /// </returns>
    public static ManagerFileIndex Load(
        string profileDir, string modsDir, string overwriteDir,
        string filemapPath, string indexPath)
    {
        var hasMap = File.Exists(filemapPath);
        var hasIndex = File.Exists(indexPath);
        if (!hasMap || !hasIndex)
        {
            var missing = new List<string>();
            if (!hasMap) missing.Add($"filemap.txt is missing: '{filemapPath}'");
            if (!hasIndex) missing.Add($"modindex.bin is missing: '{indexPath}'");
            missing.Add("refresh Amethyst and rebuild the filemap before resolving plugins or loose assets");
            return new ManagerFileIndex(false, Empty(), missing);
        }

        if (File.GetLastWriteTimeUtc(filemapPath) < File.GetLastWriteTimeUtc(indexPath))
            return new ManagerFileIndex(false, Empty(), new[]
            {
                $"filemap.txt is older than modindex.bin: '{filemapPath}'",
                "rebuild the Amethyst filemap; houseCARL will not guess winners from stale state"
            });

        var index = ReadIndex(indexPath);
        var modRoots = ChildDirectories(modsDir);
        var prefixes = ReadStripPrefixes(profileDir);
        var sources = new Dictionary<string, ManagerFileSource>(StringComparer.OrdinalIgnoreCase);

        foreach (var (logicalPath, provider) in ReadFilemap(filemapPath))
        {
            var key = Key(logicalPath);
            if (!index.TryGetValue(provider, out var files))
                throw Error($"filemap provider '{provider}' has no modindex.bin entry; rebuild Amethyst's filemap and index");
            if (!files.TryGetValue(key, out var indexedPath))
                throw Error($"filemap path '{logicalPath}' is absent from provider '{provider}' in modindex.bin; rebuild Amethyst's filemap and index");

            var root = provider == Overwrite
                ? overwriteDir
                : modRoots.TryGetValue(provider, out var modRoot)
                    ? modRoot
                    : throw Error($"filemap provider folder is missing: '{Path.Combine(modsDir, provider)}'");
            var hostPath = ResolveSource(root, indexedPath, provider, prefixes);
            if (!sources.TryAdd(logicalPath, new ManagerFileSource(provider, hostPath)))
                throw Error($"filemap contains duplicate logical path '{logicalPath}'");
        }

        return new ManagerFileIndex(true, sources, Array.Empty<string>());
    }

    /// <summary>Reads the bounded MessagePack v4 provider-to-raw-path index.</summary>
    /// <param name="path">Existing modindex.bin path.</param>
    /// <returns>Case-insensitive providers containing normalized-key to raw-cased Bethesda paths.</returns>
    /// <exception cref="AmethystConfigurationException">The binary shape, version, counts, or keys are invalid.</exception>
    static Dictionary<string, Dictionary<string, string>> ReadIndex(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxIndexBytes)
            throw Error($"modindex.bin exceeds the {MaxIndexBytes / 1024 / 1024} MiB safety limit: '{path}'");

        try
        {
            var reader = new MessagePackReader(File.ReadAllBytes(path));
            var rootCount = reader.ReadMapHeader();
            int? version = null;
            Dictionary<string, Dictionary<string, string>>? mods = null;
            for (var i = 0; i < rootCount; i++)
            {
                var name = reader.ReadString() ?? throw Error("modindex.bin contains a null root key");
                switch (name)
                {
                    case "v":
                        if (version is not null) throw Error("modindex.bin contains duplicate 'v' fields");
                        version = reader.ReadInt32();
                        break;
                    case "mods":
                        if (mods is not null) throw Error("modindex.bin contains duplicate 'mods' fields");
                        mods = ReadMods(ref reader);
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            if (version != Version)
                throw Error($"modindex.bin version {version?.ToString() ?? "missing"} is unsupported; update houseCARL-Amethyst or rebuild with a compatible Amethyst version");
            if (mods is null) throw Error("modindex.bin has no 'mods' collection");
            if (!reader.End) throw Error("modindex.bin contains trailing data");
            return mods;
        }
        catch (AmethystConfigurationException) { throw; }
        catch (Exception ex) { throw Error($"could not parse modindex.bin at '{path}': {ex.Message}"); }
    }

    /// <summary>Reads the v4 <c>mods</c> array from the current MessagePack reader position.</summary>
    /// <param name="reader">Reader positioned immediately before the mods array header.</param>
    /// <returns>Validated providers and their raw-cased paths.</returns>
    static Dictionary<string, Dictionary<string, string>> ReadMods(ref MessagePackReader reader)
    {
        var count = reader.ReadArrayHeader();
        if (count > MaxMods) throw Error($"modindex.bin contains too many mods ({count})");
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var totalFiles = 0;
        for (var i = 0; i < count; i++)
        {
            if (reader.ReadArrayHeader() != 2) throw Error("a modindex.bin mod entry is not a two-item array");
            var mod = Provider(reader.ReadString());
            var fileCount = reader.ReadArrayHeader();
            totalFiles = checked(totalFiles + fileCount);
            if (totalFiles > MaxFiles) throw Error($"modindex.bin contains too many files ({totalFiles})");
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var j = 0; j < fileCount; j++)
            {
                if (reader.ReadArrayHeader() != 3) throw Error($"modindex.bin entry for '{mod}' is not a three-item array");
                var relKey = reader.ReadString() ?? throw Error($"modindex.bin entry for '{mod}' has a null key");
                var relPath = BethesdaPath.Normalize(
                    reader.ReadString() ?? throw Error($"modindex.bin entry for '{mod}' has a null path"));
                var kind = reader.ReadString();
                if (kind is not ("n" or "r")) throw Error($"modindex.bin entry for '{mod}' has unknown kind '{kind}'");
                var normalizedKey = Key(relPath);
                if (!string.Equals(relKey, normalizedKey, StringComparison.Ordinal))
                    throw Error($"modindex.bin key '{relKey}' does not match path '{relPath}' for '{mod}'");
                if (!files.TryAdd(relKey, relPath))
                    throw Error($"modindex.bin contains duplicate path '{relKey}' for '{mod}'");
            }
            if (!result.TryAdd(mod, files))
                throw Error($"modindex.bin contains duplicate mod '{mod}'");
        }
        return result;
    }

    /// <summary>Streams validated logical winner/provider pairs from filemap.txt.</summary>
    /// <param name="path">Existing native filemap path.</param>
    /// <returns>Canonical Bethesda paths and validated provider names in file order.</returns>
    static IEnumerable<(string Path, string Provider)> ReadFilemap(string path)
    {
        var lineNumber = 0;
        foreach (var raw in File.ReadLines(path))
        {
            lineNumber++;
            var line = raw.TrimEnd('\r', '\n');
            if (line.Length == 0) continue;
            var tab = line.IndexOf('\t');
            if (tab <= 0 || tab == line.Length - 1 || line.IndexOf('\t', tab + 1) >= 0)
                throw Error($"malformed filemap.txt line {lineNumber}: expected '<path>\\t<provider>'");
            string logicalPath;
            try { logicalPath = BethesdaPath.Normalize(line[..tab]); }
            catch (ArgumentException ex) { throw Error($"invalid filemap.txt path on line {lineNumber}: {ex.Message}"); }
            yield return (logicalPath, Provider(line[(tab + 1)..]));
        }
    }

    /// <summary>Finds the actual-cased staged source represented by one indexed output path.</summary>
    /// <param name="root">Provider's physical staging root.</param>
    /// <param name="indexedPath">Raw-cased relative path recorded by modindex.bin.</param>
    /// <param name="provider">Validated provider name used to select strip-prefix rules.</param>
    /// <param name="prefixes">Per-mod prefixes Amethyst stripped while producing the logical output.</param>
    /// <returns>The existing actual-cased native source path.</returns>
    /// <remarks>
    /// Candidate order mirrors Amethyst's supported Data wrapping and strip-prefix layouts. Each
    /// candidate is resolved segment-by-segment; Linux casing is never guessed.
    /// </remarks>
    static string ResolveSource(
        string root, string indexedPath, string provider,
        IReadOnlyDictionary<string, IReadOnlyList<string>> prefixes)
    {
        var candidates = new List<string> { indexedPath, $"Data\\{indexedPath}", $"Data\\Data\\{indexedPath}" };
        if (provider != Overwrite && prefixes.TryGetValue(provider, out var configured))
        {
            candidates.AddRange(configured.Where(x => x.Contains('\\')).Select(x => $"{x}\\{indexedPath}"));
            var segments = configured.Where(x => !x.Contains('\\'));
            var prefix = "";
            foreach (var segment in segments)
            {
                prefix = prefix.Length == 0 ? segment : $"{prefix}\\{segment}";
                candidates.Add($"{prefix}\\{indexedPath}");
            }
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            if (BethesdaPath.TryResolveExisting(root, candidate, out var resolved) && File.Exists(resolved))
                return resolved;
        throw Error($"indexed source '{indexedPath}' for '{provider}' is missing under '{root}'; refresh Amethyst's mod index");
    }

    /// <summary>Reads per-mod strip-prefix history from the active profile state.</summary>
    /// <param name="profileDir">Active Amethyst profile directory.</param>
    /// <returns>Case-insensitive provider names mapped to validated canonical prefixes.</returns>
    /// <remarks>An absent file/property means no prefixes; malformed present state fails loudly.</remarks>
    static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadStripPrefixes(string profileDir)
    {
        var path = Path.Combine(profileDir, "profile_state.json");
        if (!File.Exists(path)) return new Dictionary<string, IReadOnlyList<string>>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("mod_strip_prefixes", out var value)
                || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return new Dictionary<string, IReadOnlyList<string>>();
            if (value.ValueKind != JsonValueKind.Object)
                throw Error("profile_state.json.mod_strip_prefixes must be an object");
            var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Array)
                    throw Error($"profile_state.json.mod_strip_prefixes['{property.Name}'] must be an array");
                result[property.Name] = property.Value.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => BethesdaPath.Normalize(x.GetString()!))
                    .ToList();
            }
            return result;
        }
        catch (AmethystConfigurationException) { throw; }
        catch (Exception ex) { throw Error($"could not read mod strip prefixes from '{path}': {ex.Message}"); }
    }

    /// <summary>Indexes immediate child directories using Amethyst's case-insensitive mod-name semantics.</summary>
    /// <param name="root">Effective mods staging root.</param>
    /// <returns>Actual mod folder names and native paths.</returns>
    /// <exception cref="AmethystConfigurationException">Two Linux folders differ only by case.</exception>
    static Dictionary<string, string> ChildDirectories(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return result;
        foreach (var path in Directory.EnumerateDirectories(root).OrderBy(x => x, StringComparer.Ordinal))
            if (!result.TryAdd(Path.GetFileName(path), path))
                throw Error($"mod folders differ only by case: '{result[Path.GetFileName(path)]}' and '{path}'");
        return result;
    }

    /// <summary>Validates a filemap/modindex provider as a single staging-folder name.</summary>
    /// <param name="value">Untrusted provider text.</param>
    /// <returns>The unchanged provider name after validation.</returns>
    static string Provider(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0') || value.Contains('/')
            || value is "." or "..")
            throw Error($"invalid filemap provider '{value}'");
        return value;
    }

    /// <summary>Converts a Bethesda path to Amethyst modindex's normalized lookup-key form.</summary>
    /// <param name="path">Validated or untrusted Data-relative path.</param>
    /// <returns>Lower-case, forward-slash key used by modindex.bin.</returns>
    static string Key(string path) => BethesdaPath.Normalize(path).Replace('\\', '/').ToLowerInvariant();

    /// <summary>Creates an empty source map with the same case-insensitive semantics as a ready index.</summary>
    /// <returns>An empty immutable-facing dictionary.</returns>
    static IReadOnlyDictionary<string, ManagerFileSource> Empty() =>
        new Dictionary<string, ManagerFileSource>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a consistently prefixed actionable filemap configuration error.</summary>
    /// <param name="message">Specific mismatch and corrective guidance.</param>
    /// <returns>A user-safe named configuration exception.</returns>
    static AmethystConfigurationException Error(string message) =>
        new($"Amethyst filemap error: {message}");
}
