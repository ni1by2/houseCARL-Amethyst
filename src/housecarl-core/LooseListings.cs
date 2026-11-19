using System.Collections.Concurrent;
using System.IO.Enumeration;

namespace HousecarlCore;

// LooseListings — one listing per loose directory per call, so every loose read in a call (resolve, walk, root files,
// the freshness check, an enabled folder's off-order probe) answers off the same read; and the one join of a Data
// path onto a disk folder. Contract in docs/architecture/assets.md.

/// <summary>One call's loose directory listings: each directory is listed at its first touch and kept until the call
/// ends, a directory that will not list keeps its failure for the call, and nothing outlives the object. Outside a
/// call, one <see cref="AssetResolver.Capture"/> pass holds one.</summary>
public sealed class LooseListings
{
    /// <summary>One directory's listing: every name in enumeration order, and the folders among them; the files are
    /// the rest, made into a set only where a folder is resolved from, since most listed folders are only walked.</summary>
    internal sealed class Listing(HashSet<string> names, HashSet<string> dirs, HashSet<string> ambiguous)
    {
        HashSet<string>? _files;
        public HashSet<string> Names { get; } = names;
        public bool IsAmbiguous(string name) => ambiguous.Contains(name);
        public bool IsFile(string name) => Names.Contains(name) && !dirs.Contains(name);
        public HashSet<string> Files => LazyInitializer.EnsureInitialized(ref _files,
            () => new HashSet<string>(Names.Where(n => !dirs.Contains(n)), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>A listing, or why the directory would not list.</summary>
    internal readonly record struct Read(Listing? Listing, Exception? Failure);

    readonly ConcurrentDictionary<string, Lazy<Read>> _reads = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    int _count;

    /// <summary>The loose subtrees this call has checked against its own listings, or warmed from them.</summary>
    internal readonly ConcurrentDictionary<string, AssetResolver.LooseSubtree> Subtrees = new(StringComparer.OrdinalIgnoreCase);

    AssetResolver.Snapshot? _checked;
    int _checkedWarms;

    /// <summary>The freshness check found <paramref name="build"/> fresh, off these listings, for every subtree among
    /// its first <paramref name="warms"/> warms.</summary>
    internal void Checked(AssetResolver.Snapshot build, int warms) { _checkedWarms = warms; Volatile.Write(ref _checked, build); }

    /// <summary>Did this call's freshness check cover warm number <paramref name="warm"/> of <paramref name="build"/>?</summary>
    internal bool CheckedUpTo(AssetResolver.Snapshot build, int warm) => ReferenceEquals(Volatile.Read(ref _checked), build) && warm <= _checkedWarms;

    /// <summary>How many directories this call has listed; a test seam.</summary>
    internal int ListingCount => Volatile.Read(ref _count);

    /// <summary>The one join of a Data-relative path, or a folder name, onto a disk folder. On Windows it is the
    /// plain join; Linux validates the virtual path and resolves existing segments using Skyrim case rules.</summary>
    public static string OnDisk(string folder, string logical)
    {
        if (OperatingSystem.IsWindows()) return Path.Combine(folder, logical);
        if (logical.Length == 0) return folder;
        var canonical = BethesdaPath.Normalize(logical);
        try { if (BethesdaPath.TryResolveExisting(folder, canonical, out var existing)) return existing; }
        catch (UnauthorizedAccessException) { return BethesdaPath.Under(folder, canonical); } // join only; the caller names the unreadable root
        // A new output still needs the actual casing of any existing parent directories.
        var parent = BethesdaPath.DirectoryName(canonical);
        return parent.Length == 0 ? BethesdaPath.Under(folder, canonical)
            : Path.Combine(OnDisk(folder, parent), BethesdaPath.FileName(canonical));
    }

    /// <summary>As <see cref="OnDisk(string, string)"/> over two segments.</summary>
    public static string OnDisk(string folder, string logical, string more) => OnDisk(OnDisk(folder, logical), more);

    /// <summary>This call's listing of <paramref name="dir"/>, taken now if it is the first touch;
    /// <paramref name="fresh"/> runs only when this read took it.</summary>
    internal Read Get(string dir, Action? fresh = null) =>
        _reads.TryGetValue(dir, out var held) ? held.Value : Take(dir, fresh);

    Read Take(string dir, Action? fresh) =>
        _reads.GetOrAdd(dir, d => new Lazy<Read>(() =>
        {
            Interlocked.Increment(ref _count);
            fresh?.Invoke();
            try { return new Read(List(d), null); }
            catch (Exception ex) { return new Read(null, ex); }
        })).Value;

    /// <summary>This call's listing of <paramref name="dir"/>, or null when it would not list.</summary>
    internal Listing? TryGet(string dir, Action? fresh = null) => Get(dir, fresh).Listing;

    /// <summary>Where a descent ended: <see cref="Listing"/> at the folder itself; <see cref="Missing"/>, the first name
    /// a listing on the way lacks or <see cref="FileHeld"/> as a file, in <see cref="Dir"/>; or neither, where
    /// <see cref="Dir"/> would not list and <see cref="Failure"/> says why.</summary>
    internal readonly record struct Descent(string Dir, Listing? Listing, string? Missing, bool FileHeld, Exception? Failure);

    /// <summary>The one way into a loose folder in a call: from a root's folder down through <paramref name="segments"/>,
    /// each folder off this call's listing of it, so a folder the call listed as absent stays absent below it.</summary>
    internal Descent Descend(string rootDir, IReadOnlyList<string> segments, Action? fresh = null)
    {
        var dir = rootDir;
        for (int i = 0; ; i++)
        {
            var read = Get(dir, fresh);
            if (read.Listing is not { } listing) return new(dir, null, null, false, read.Failure);
            if (i == segments.Count) return new(dir, listing, null, false, null);
            var name = segments[i];
            if (listing.IsAmbiguous(name))
                throw new AmbiguousBethesdaPathException($"ambiguous case-insensitive Bethesda path segment '{name}' under '{dir}'");
            if (!listing.Names.Contains(name)) return new(dir, null, name, false, null);
            if (listing.IsFile(name)) return new(dir, null, name, true, null);
            listing.Names.TryGetValue(name, out var actualName);
            dir = Path.Combine(dir, actualName!);
        }
    }

    /// <summary>A Data-relative folder's segments, none for the root itself.</summary>
    internal static string[] Segments(string rel) => rel.Length == 0 ? [] : rel.Split('\\');

    /// <summary>The one enumeration every listing rides; throws when the directory will not list.</summary>
    internal static Listing List(string dir)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Only FILES may resolve: a directory reported as an asset is the silently wrong answer. Names only, no
        // FileSystemInfo per entry: a call can list every folder under meshes\.
        foreach (var (name, isDir) in new FileSystemEnumerable<(string, bool)>(dir,
                     (ref FileSystemEntry e) => (e.FileName.ToString(), e.IsDirectory), Everything))
        {
            if (!OperatingSystem.IsWindows() && !isDir && new FileInfo(Path.Combine(dir, name)).LinkTarget is not null
                && new FileInfo(Path.Combine(dir, name)).ResolveLinkTarget(returnFinalTarget: true) is not { Exists: true }) continue;
            if (!names.Add(name)) ambiguous.Add(name);
            if (isDir) dirs.Add(name);
        }
        return new Listing(names, dirs, ambiguous);
    }

    /// <summary>What <see cref="DirectoryInfo.EnumerateFileSystemInfos()"/> lists: hidden and system entries too, and
    /// a folder that refuses throws.</summary>
    static readonly EnumerationOptions Everything = new() { AttributesToSkip = 0, IgnoreInaccessible = false };
}
