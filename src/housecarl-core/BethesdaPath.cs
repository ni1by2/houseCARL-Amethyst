namespace HousecarlCore;

/// <summary>
/// Converts between Skyrim's case-insensitive, backslash-separated Data paths and
/// native host paths. Bethesda paths must never be passed directly to <see cref="File"/>,
/// <see cref="Directory"/>, or <see cref="Path.Combine(string, string)"/>.
/// </summary>
public static class BethesdaPath
{
    /// <summary>Validate and canonicalize a Data-relative path.</summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw Invalid(path, "the path is empty");
        if (path.Contains('\0')) throw Invalid(path, "the path contains NUL");
        if (path[0] is '/' or '\\') throw Invalid(path, "the path is absolute/root-prefixed");
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
            throw Invalid(path, "the path is drive-rooted");

        var segments = path.Replace('/', '\\').Split('\\');
        if (segments.Any(segment => segment.Length == 0)) throw Invalid(path, "the path contains an empty segment");
        if (segments.Any(segment => segment is "." or "..")) throw Invalid(path, "the path is parent-escaping");
        return string.Join('\\', segments);
    }

    /// <summary>Normalize a trusted archive-table entry without applying host-path rules.</summary>
    public static string NormalizeArchiveEntry(string path) => (path ?? "").Replace('/', '\\').TrimStart('\\');

    /// <summary>Return the canonical parent path, or an empty string for a top-level file.</summary>
    public static string DirectoryName(string path)
    {
        var canonical = Normalize(path);
        var separator = canonical.LastIndexOf('\\');
        return separator < 0 ? "" : canonical[..separator];
    }

    /// <summary>Return the final canonical path segment.</summary>
    public static string FileName(string path)
    {
        var canonical = Normalize(path);
        var separator = canonical.LastIndexOf('\\');
        return separator < 0 ? canonical : canonical[(separator + 1)..];
    }

    /// <summary>Convert a canonical path to a relative path using native separators.</summary>
    public static string ToHostRelative(string path) => Path.Combine(Normalize(path).Split('\\'));

    /// <summary>Place a canonical path beneath a native root.</summary>
    public static string Under(string root, string path) => Path.Combine(root, ToHostRelative(path));

    /// <summary>Convert a native relative path to canonical Skyrim form.</summary>
    public static string FromHostRelative(string path) => Normalize(path.Replace(Path.DirectorySeparatorChar, '\\'));

    /// <summary>
    /// Resolve an existing path segment-by-segment with case-insensitive Skyrim semantics,
    /// returning the actual host casing required by case-sensitive Linux filesystems.
    /// </summary>
    public static bool TryResolveExisting(string root, string path, out string resolved)
    {
        var current = root;
        foreach (var segment in Normalize(path).Split('\\'))
        {
            string? match;
            try
            {
                match = Directory.EnumerateFileSystemEntries(current)
                    .FirstOrDefault(entry => string.Equals(Path.GetFileName(entry), segment, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                resolved = Under(root, path);
                return false;
            }

            if (match is null)
            {
                resolved = Under(root, path);
                return false;
            }
            current = match;
        }

        resolved = current;
        return true;
    }

    static ArgumentException Invalid(string? path, string reason) =>
        new($"Expected a Data-relative Bethesda path, but {reason}: '{path}'", nameof(path));
}
