namespace HousecarlCore;

/// <summary>The fields Amethyst's Nexus updater stores in a managed mod's [General] meta.ini section.</summary>
public sealed record AmethystNexusMeta(string? GameName, int ModId, int FileId, string? Version,
    int LatestFileId, string? LatestVersion, bool? HasUpdate, bool? IgnoreUpdate, string? IgnoredVersion);

/// <summary>Read Amethyst's installed-file update cache without applying MO2's different meta.ini schema.</summary>
public static class AmethystNexusMetaReader
{
    public static AmethystNexusMeta? Read(string path) => Parse(File.ReadLines(path));

    public static AmethystNexusMeta? ReadFromBytes(byte[] bytes) =>
        Parse(new System.Text.UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF')
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None));

    static AmethystNexusMeta? Parse(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inGeneral = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line[0] == '[')
            {
                inGeneral = line.Equals("[General]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inGeneral) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var key = line[..separator].Trim();
            if (!IsKnownKey(key)) continue;
            values[key] = QtIniEscapes.Clean(line[(separator + 1)..]) ?? "";
        }
        if (values.Count == 0) return null;
        return new(
            Get("gameName"), PositiveInt("modid"), PositiveInt("fileid"), Get("version"),
            PositiveInt("latestFileId"), Get("latestVersion"), Boolean("hasUpdate"), Boolean("ignoreUpdate"), Get("ignoredVersion"));

        string? Get(string key) => values.TryGetValue(key, out var value) && value.Length > 0 ? value : null;
        int PositiveInt(string key) => int.TryParse(Get(key), out var value) && value > 0 ? value : 0;
        bool? Boolean(string key) => Get(key)?.Trim().ToLowerInvariant() switch
        { "true" or "1" or "yes" => true, "false" or "0" or "no" => false, _ => null };
    }

    static bool IsKnownKey(string key) => key.Equals("gameName", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("modid", StringComparison.OrdinalIgnoreCase) || key.Equals("fileid", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("version", StringComparison.OrdinalIgnoreCase) || key.Equals("latestFileId", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("latestVersion", StringComparison.OrdinalIgnoreCase) || key.Equals("hasUpdate", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("ignoreUpdate", StringComparison.OrdinalIgnoreCase) || key.Equals("ignoredVersion", StringComparison.OrdinalIgnoreCase);
}
