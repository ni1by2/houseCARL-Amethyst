using System.Text.Json;
using MessagePack;

namespace HousecarlCore;

/// <summary>One authoritative Amethyst loose-file winner.</summary>
public sealed record ManagerFileSource(string Provider, string HostPath);

/// <summary>Parsed filemap state. Not-ready state is explicit and never triggers a Data scan.</summary>
public sealed record ManagerFileIndex(
    bool Ready,
    IReadOnlyDictionary<string, ManagerFileSource> Sources,
    IReadOnlyList<string> Warnings);

/// <summary>Strict reader for Amethyst filemap.txt and MessagePack modindex.bin v4.</summary>
public static class AmethystFileMap
{
    const int Version = 4;
    const long MaxIndexBytes = 256L * 1024 * 1024;
    const int MaxMods = 100_000;
    const int MaxFiles = 5_000_000;
    const string Overwrite = "[Overwrite]";

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

    static Dictionary<string, string> ChildDirectories(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return result;
        foreach (var path in Directory.EnumerateDirectories(root).OrderBy(x => x, StringComparer.Ordinal))
            if (!result.TryAdd(Path.GetFileName(path), path))
                throw Error($"mod folders differ only by case: '{result[Path.GetFileName(path)]}' and '{path}'");
        return result;
    }

    static string Provider(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0') || value.Contains('/')
            || value is "." or "..")
            throw Error($"invalid filemap provider '{value}'");
        return value;
    }

    static string Key(string path) => BethesdaPath.Normalize(path).Replace('\\', '/').ToLowerInvariant();

    static IReadOnlyDictionary<string, ManagerFileSource> Empty() =>
        new Dictionary<string, ManagerFileSource>(StringComparer.OrdinalIgnoreCase);

    static AmethystConfigurationException Error(string message) =>
        new($"Amethyst filemap error: {message}");
}
