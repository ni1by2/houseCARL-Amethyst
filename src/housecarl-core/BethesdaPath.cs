namespace HousecarlCore;

/// <summary>
/// Converts between Skyrim's case-insensitive, backslash-separated Data paths and
/// native host paths. Bethesda paths must never be passed directly to <see cref="File"/>,
/// <see cref="Directory"/>, or <see cref="Path.Combine(string, string)"/>.
/// </summary>
public static class BethesdaPath
{
    /// <summary>Validates and canonicalizes an untrusted Data-relative path.</summary>
    /// <param name="path">Slash- or backslash-separated logical path supplied by a caller or manager file.</param>
    /// <returns>The same logical segments joined with Bethesda backslashes.</returns>
    /// <exception cref="ArgumentException">
    /// The path is empty, absolute, drive-rooted, NUL-containing, contains empty segments, or can escape its root.
    /// </exception>
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

    /// <summary>Normalizes a trusted archive-table entry without applying host-filesystem safety rules.</summary>
    /// <param name="path">
    /// Path read from an already parsed BSA directory table. A null value represents an empty archive key.
    /// </param>
    /// <returns>A backslash-separated archive lookup key without leading separators.</returns>
    /// <remarks>
    /// BSA readers need their original internal namespace. Use <see cref="Normalize"/> for any path
    /// that may reach the host filesystem; this helper is deliberately not a traversal validator.
    /// </remarks>
    public static string NormalizeArchiveEntry(string? path) => (path ?? "").Replace('/', '\\').TrimStart('\\');

    /// <summary>Returns the canonical parent path, or an empty string for a top-level file.</summary>
    /// <param name="path">Validated or untrusted Data-relative Bethesda path.</param>
    /// <returns>The canonical parent portion without a trailing separator, or empty for a top-level path.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> violates the Data-relative path contract.
    /// </exception>
    public static string DirectoryName(string path)
    {
        var canonical = Normalize(path);
        var separator = canonical.LastIndexOf('\\');
        return separator < 0 ? "" : canonical[..separator];
    }

    /// <summary>Returns the final canonical path segment.</summary>
    /// <param name="path">Validated or untrusted Data-relative Bethesda path.</param>
    /// <returns>The file or directory name after the last Bethesda separator.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> violates the Data-relative path contract.
    /// </exception>
    public static string FileName(string path)
    {
        var canonical = Normalize(path);
        var separator = canonical.LastIndexOf('\\');
        return separator < 0 ? canonical : canonical[(separator + 1)..];
    }

    /// <summary>Converts a canonical path to a relative path using native host separators.</summary>
    /// <param name="path">Validated or untrusted Data-relative Bethesda path.</param>
    /// <returns>A relative host path that remains safe to place beneath a chosen root.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> violates the Data-relative path contract.
    /// </exception>
    public static string ToHostRelative(string path) => Path.Combine(Normalize(path).Split('\\'));

    /// <summary>Places a canonical Bethesda path beneath a native root.</summary>
    /// <param name="root">Trusted absolute or relative host root chosen by the caller.</param>
    /// <param name="path">Untrusted Data-relative Bethesda path.</param>
    /// <returns>The combined native host path.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> violates the Data-relative path contract.
    /// </exception>
    public static string Under(string root, string path) => Path.Combine(root, ToHostRelative(path));

    /// <summary>Converts a native relative path to canonical Skyrim form.</summary>
    /// <param name="path">
    /// Relative path produced by host filesystem enumeration. It must not contain a root, traversal, or empty segment.
    /// </param>
    /// <returns>The same segments joined by Bethesda backslashes.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> violates the Data-relative path contract.
    /// </exception>
    public static string FromHostRelative(string path) => Normalize(path.Replace(Path.DirectorySeparatorChar, '\\'));

    /// <summary>
    /// Resolve an existing path segment-by-segment with case-insensitive Skyrim semantics,
    /// returning the actual host casing required by case-sensitive Linux filesystems.
    /// </summary>
    /// <param name="root">Trusted existing directory from which resolution begins.</param>
    /// <param name="path">Untrusted Data-relative Bethesda path to resolve.</param>
    /// <param name="resolved">
    /// Actual-cased existing path on success; the safely combined expected path on failure for diagnostics.
    /// </param>
    /// <returns>True only when every segment was enumerated and matched case-insensitively.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> violates the Data-relative path contract.
    /// </exception>
    /// <remarks>
    /// Enumeration errors fail closed. The method never substitutes a differently cased guessed path as
    /// proof that the source exists on Linux.
    /// </remarks>
    public static bool TryResolveExisting(string root, string path, out string resolved)
    {
        var current = root;
        foreach (var segment in Normalize(path).Split('\\'))
        {
            string? match;
            try
            {
                match = Directory.EnumerateFileSystemEntries(current)
                    .FirstOrDefault(entry =>
                        string.Equals(Path.GetFileName(entry), segment, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>Creates the consistent caller-facing error used by every Bethesda-path guard.</summary>
    /// <param name="path">Rejected caller value.</param>
    /// <param name="reason">Specific violated path rule.</param>
    /// <returns>An argument exception naming the Data-relative contract.</returns>
    static ArgumentException Invalid(string? path, string reason) =>
        new($"Expected a Data-relative Bethesda path, but {reason}: '{path}'", nameof(path));
}
