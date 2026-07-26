using System.Collections.Concurrent;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

namespace HousecarlCore;

/// <summary>How a provider supplies an asset.</summary>
public enum AssetKind
{
    /// <summary>A native file in staging or the vanilla Data root.</summary>
    Loose,

    /// <summary>An entry stored inside an active Bethesda archive.</summary>
    Bsa
}

/// <summary>Identifies one logical source that provides an asset.</summary>
/// <param name="Source">Mod/provider name, <c>Data</c>, overwrite, or archive filename.</param>
/// <param name="Kind">Whether the bytes are loose or archived.</param>
public sealed record AssetProvider(string Source, AssetKind Kind);

/// <summary>Describes logical winner resolution for one asset path.</summary>
/// <param name="RelPath">Canonical Data-relative Bethesda path.</param>
/// <param name="Exists">Whether at least one readable provider contains the asset.</param>
/// <param name="Winner">Highest-precedence provider, or null when <paramref name="Exists"/> is false.</param>
/// <param name="Providers">All providers in winner-first precedence order.</param>
/// <param name="Ambiguous">
/// Whether more than one provider contains the path; this signals contention, not an error.
/// </param>
public sealed record AssetHit(
    string RelPath,
    bool Exists,
    AssetProvider? Winner,
    IReadOnlyList<AssetProvider> Providers,
    bool Ambiguous);

/// <summary>Identifies the physical file or archive entry from which one provider's bytes can be read.</summary>
/// <param name="ProviderName">Logical provider shown to the user.</param>
/// <param name="Kind">Loose-file or archive-entry storage.</param>
/// <param name="LooseFilePath">Native file path for a loose source; otherwise null.</param>
/// <param name="ArchivePath">Native BSA path for an archived source; otherwise null.</param>
/// <param name="EntryPath">Canonical Data-relative path and, for a BSA, its internal entry key.</param>
public sealed record PlacementSource(
    string ProviderName,
    AssetKind Kind,
    string? LooseFilePath,
    string? ArchivePath,
    string EntryPath);

/// <summary>Describes concrete sources from which an asset-placement operation can read bytes.</summary>
/// <param name="RelPath">Canonical Data-relative Bethesda path.</param>
/// <param name="Sources">
/// Physical sources in winner-first order; empty when no scanned provider contains the path.
/// </param>
/// <param name="Ambiguous">Whether the caller must choose among multiple sources.</param>
/// <param name="ReadIncomplete">Whether an unreadable BSA prevents a conclusive absence result.</param>
public sealed record PlacementResolution(
    string RelPath,
    IReadOnlyList<PlacementSource> Sources,
    bool Ambiguous,
    bool ReadIncomplete);

/// <summary>Binds a physical BSA to its logical owner and injected precedence.</summary>
/// <param name="Path">Native path to the archive.</param>
/// <param name="OwningPlugin">Plugin or Skyrim.ini marker responsible for loading it.</param>
/// <param name="PluginRank">Archive precedence; higher values win.</param>
public sealed record ActiveArchive(string Path, string OwningPlugin, int PluginRank);

/// <summary>
/// Resolves canonical Data-relative asset paths against authoritative loose winners, vanilla
/// files, and active BSAs without retaining archive handles.
/// </summary>
/// <remarks>
/// Product mode trusts Amethyst's filemap/mod-index winner map and adds vanilla Data_Core only as the lowest
/// loose provider. Loose files beat BSAs; higher plugin ranks win among BSAs. The legacy root walker exists for
/// inherited probes only. Snapshots contain derived file tables and filename caches, never open archive handles.
/// Manager membership changes rebuild the resolver outside this class; local freshness checks cover changed archive
/// bytes and vanished authoritative winners.
/// </remarks>
public sealed class AssetResolver : IDisposable
{
    /// <summary>Legacy overwrite root, empty in authoritative Amethyst mode.</summary>
    readonly string _overwriteDir;
    /// <summary>Legacy staging root, empty in authoritative Amethyst mode.</summary>
    readonly string _modsDir;
    /// <summary>Validated vanilla Data_Core/Data host root and lowest loose precedence.</summary>
    readonly string _dataDir;
    /// <summary>Legacy enabled mod names in highest-first order.</summary>
    readonly IReadOnlyList<string> _enabledMods;
    /// <summary>Path-deduplicated active archives with injected precedence ranks.</summary>
    readonly IReadOnlyList<ActiveArchive> _archives;
    /// <summary>Legacy loose roots in precedence order, or vanilla only in authoritative mode.</summary>
    readonly IReadOnlyList<(string Name, string Dir)> _looseRoots;
    /// <summary>Authoritative Amethyst loose winners, or null in legacy root-walk mode.</summary>
    readonly IReadOnlyDictionary<string, ManagerFileSource>? _authoritativeLoose;

    /// <summary>One table-build's whole output, swapped in as a single reference write (the LoadOrderResolver
    /// snapshot discipline) so a concurrent Resolve never sees a half-rebuilt cache. Holds string sets only.
    /// internal (not private) so <see cref="AssetView"/>'s ctor can take it; never leaves the assembly.</summary>
    internal sealed class Snapshot
    {
        /// <summary>Canonical case-insensitive entry paths keyed by native archive path.</summary>
        public readonly Dictionary<string, HashSet<string>> Tables;
        /// <summary>Archive modification times captured with <see cref="Tables"/>.</summary>
        public readonly Dictionary<string, DateTime> Mtimes;
        /// <summary>Named archive-table failures that make absence results incomplete.</summary>
        public readonly List<string> Failures;
        /// <summary>Lazily warmed loose-directory caches keyed by canonical parent path.</summary>
        public readonly ConcurrentDictionary<string, LooseSubtree> LooseCache;
        /// <summary>Creates one immutable archive build with an initially empty lazy loose cache.</summary>
        /// <param name="tables">Copied archive entry tables.</param>
        /// <param name="mtimes">Parallel archive freshness baselines.</param>
        /// <param name="failures">Named archive-read failures.</param>
        public Snapshot(
            Dictionary<string, HashSet<string>> tables,
            Dictionary<string, DateTime> mtimes,
            List<string> failures)
        { Tables = tables; Mtimes = mtimes; Failures = failures; LooseCache = new(StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>
    /// Stores one lazily warmed legacy directory across roots, including files and freshness baselines.
    /// </summary>
    internal sealed class LooseSubtree
    {
        /// <summary>Directory freshness baselines parallel to the resolver's loose-root list.</summary>
        public readonly DateTime[] DirMtimes;
        /// <summary>Readable directories and case-preserving top-level filenames, in root precedence order.</summary>
        public readonly (int RootIndex, string Directory, Dictionary<string, string> Files)[] Present;
        /// <summary>Stores freshness baselines and readable providers for one canonical directory.</summary>
        /// <param name="dirMtimes">One timestamp per configured loose root.</param>
        /// <param name="present">Roots that currently provide at least one file in the directory.</param>
        public LooseSubtree(DateTime[] dirMtimes, (int, string, Dictionary<string, string>)[] present)
        { DirMtimes = dirMtimes; Present = present; }
    }

    /// <summary>Current atomically replaceable cache snapshot.</summary>
    volatile Snapshot _snap;

    /// <summary>Archives that could not be read this build, including a concise reason.</summary>
    public IReadOnlyList<string> BsaFailures => _snap.Failures;

    /// <summary>True iff this build had archive-read failures (<see cref="BsaFailures"/> non-empty). The caveat for an
    /// Exists=false answer: an asset present ONLY in an archive that failed to read is indistinguishable from a truly
    /// absent one, so a consumer acting on "no facegen → the NPC is fine" must check this — an Exists=false is
    /// authoritative only when the read was complete. Surfaced at the resolver level (like LoadOrderResolver's
    /// LoadFailures), never forced into a per-answer field that would diverge from the codebase idiom.</summary>
    public bool ReadIncomplete => _snap.Failures.Count > 0;

    /// <summary>Creates either authoritative Amethyst mode or the legacy root-walk probe mode.</summary>
    /// <param name="overwriteDir">
    /// Legacy overwrite root; ignored when <paramref name="authoritativeLoose"/> is present.
    /// </param>
    /// <param name="modsDir">Legacy mods root; ignored in authoritative mode.</param>
    /// <param name="dataDir">Vanilla Data_Core/Data root.</param>
    /// <param name="enabledMods">Legacy mod folders in highest-first priority order.</param>
    /// <param name="archives">Active archives with injected plugin ranks.</param>
    /// <param name="authoritativeLoose">Amethyst loose winners, or null to use the legacy root walker.</param>
    AssetResolver(string overwriteDir, string modsDir, string dataDir,
                  IReadOnlyList<string> enabledMods, IReadOnlyList<ActiveArchive> archives,
                  IReadOnlyDictionary<string, ManagerFileSource>? authoritativeLoose = null)
    {
        _overwriteDir = overwriteDir ?? "";
        _modsDir = modsDir ?? "";
        _dataDir = dataDir ?? "";
        _enabledMods = enabledMods;
        _archives = DedupeArchives(archives);
        _authoritativeLoose = authoritativeLoose;
        _looseRoots = authoritativeLoose is null
            ? BuildLooseRoots()
            : _dataDir.Length > 0 ? new[] { ("Data", _dataDir) } : Array.Empty<(string, string)>();
        _snap = BuildTables();
    }

    /// <summary>Builds the fixed legacy loose-root list in overwrite, mod, then Data precedence.</summary>
    /// <returns>Provider names and native roots in winner-first order.</returns>
    IReadOnlyList<(string Name, string Dir)> BuildLooseRoots()
    {
        var roots = new List<(string, string)>(_enabledMods.Count + 2);
        if (_overwriteDir.Length > 0) roots.Add(("overwrite", _overwriteDir));
        foreach (var mod in _enabledMods) roots.Add((mod, Path.Combine(_modsDir, mod)));
        if (_dataDir.Length > 0) roots.Add(("Data", _dataDir));
        return roots;
    }

    /// <summary>Collapses duplicate physical archive paths without creating false provider contention.</summary>
    /// <param name="archives">Injected bindings, which may name the same path more than once.</param>
    /// <returns>
    /// One binding per case-insensitive path, retaining the highest rank and stable first-seen path order.
    /// </returns>
    static IReadOnlyList<ActiveArchive> DedupeArchives(IReadOnlyList<ActiveArchive> archives)
    {
        var byPath = new Dictionary<string, ActiveArchive>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var a in archives)
        {
            if (byPath.TryGetValue(a.Path, out var prev))
            {
                if (a.PluginRank > prev.PluginRank) byPath[a.Path] = a;
            }
            else { byPath[a.Path] = a; order.Add(a.Path); }
        }
        return order.Select(p => byPath[p]).ToList();
    }

    /// <summary>Builds a resolver using the legacy root-walk compatibility model.</summary>
    /// <param name="overwriteDir">Highest-priority native overwrite root.</param>
    /// <param name="modsDir">Native parent of legacy mod folders.</param>
    /// <param name="dataDir">Lowest-priority native Data root.</param>
    /// <param name="enabledModsByPriority">Enabled mod folder names in highest-first order.</param>
    /// <param name="activeArchives">Active archive bindings with injected ranks.</param>
    /// <returns>A resolver whose archive tables are loaded and whose loose caches begin empty.</returns>
    public static AssetResolver Build(
        string overwriteDir,
        string modsDir,
        string dataDir,
        IReadOnlyList<string> enabledModsByPriority,
        IReadOnlyList<ActiveArchive> activeArchives)
        => new(overwriteDir, modsDir, dataDir, enabledModsByPriority, activeArchives);

    /// <summary>
    /// Build from Amethyst's authoritative loose-file winner map. The map supplies staged
    /// winners; vanilla Data remains the lowest-priority loose source.
    /// </summary>
    /// <param name="looseWinners">Canonical logical paths mapped to Amethyst's physical winning sources.</param>
    /// <param name="vanillaDataDir">Validated native Data_Core/Data root.</param>
    /// <param name="activeArchives">Active archive bindings with injected ranks.</param>
    /// <returns>An authoritative resolver with copied archive tables and no staged root walking.</returns>
    public static AssetResolver Build(
        IReadOnlyDictionary<string, ManagerFileSource> looseWinners,
        string vanillaDataDir,
        IReadOnlyList<ActiveArchive> activeArchives)
        => new("", "", vanillaDataDir, Array.Empty<string>(), activeArchives, looseWinners);

    /// <summary>Copies every active archive table into a new atomically swappable snapshot.</summary>
    /// <returns>
    /// A snapshot containing table data, freshness baselines, named failures, and an empty loose cache.
    /// </returns>
    Snapshot BuildTables()
    {
        var tables = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var mtimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        // Archives are already path-deduplicated, so each physical table is read once.
        foreach (var a in _archives)
        {
            mtimes[a.Path] = SafeMtime(a.Path);
            try { tables[a.Path] = ReadArchiveTable(a.Path); }
            catch (Exception ex)
            {
                tables[a.Path] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                failures.Add(
                    $"{Path.GetFileName(a.Path)} (loaded by {a.OwningPlugin}): " +
                    $"could not read the archive table — {Concise(ex)}");
            }
        }
        return new Snapshot(tables, mtimes, failures);
    }

    /// <summary>Copies one BSA file table into canonical case-insensitive keys.</summary>
    /// <param name="archivePath">Native path to an active BSA.</param>
    /// <returns>A detached entry set; no archive reader or handle is retained.</returns>
    /// <exception cref="Exception">Mutagen cannot open or enumerate the archive.</exception>
    /// <remarks>
    /// Mutagen 0.53.1 readers are not disposable and do not retain a mapped handle after enumeration.
    /// The defensive disposable cast supports a future implementation without changing the ownership contract.
    /// </remarks>
    static HashSet<string> ReadArchiveTable(string archivePath)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reader = Archive.CreateReader(GameRelease.SkyrimSE, archivePath);
        try
        {
            foreach (var file in reader.Files)
                set.Add(BethesdaPath.NormalizeArchiveEntry(file.Path));
        }
        finally { (reader as IDisposable)?.Dispose(); }
        return set;
    }

    /// <summary>Reads one BSA entry through Mutagen without invoking BSArch.</summary>
    /// <param name="archivePath">Native path to the BSA.</param>
    /// <param name="entryPath">Untrusted Data-relative path matched case-insensitively after normalization.</param>
    /// <returns>Fresh decompressed bytes, or null when the readable archive does not contain the entry.</returns>
    /// <exception cref="ArgumentException"><paramref name="entryPath"/> is not safely Data-relative.</exception>
    /// <exception cref="Exception">Mutagen cannot open, enumerate, or read the archive.</exception>
    public static byte[]? TryReadArchiveEntry(string archivePath, string entryPath)
    {
        var want = BethesdaPath.Normalize(entryPath);
        var reader = Archive.CreateReader(GameRelease.SkyrimSE, archivePath);
        try
        {
            foreach (var file in reader.Files)
                // Archive tables commonly lowercase paths; logical Bethesda matching is case-insensitive.
                if (string.Equals(
                    BethesdaPath.NormalizeArchiveEntry(file.Path),
                    want,
                    StringComparison.OrdinalIgnoreCase))
                    return file.GetBytes();
            return null;
        }
        finally { (reader as IDisposable)?.Dispose(); }
    }

    /// <summary>Reads multiple entries with one archive open and one table scan.</summary>
    /// <param name="archivePath">Native path to the BSA.</param>
    /// <param name="entryPaths">Untrusted Data-relative paths to request.</param>
    /// <returns>Fresh bytes keyed by each found caller-supplied path; missing entries are omitted.</returns>
    /// <exception cref="ArgumentException">Any requested path is not safely Data-relative.</exception>
    /// <exception cref="Exception">Mutagen cannot open, enumerate, or read the archive.</exception>
    public static Dictionary<string, byte[]> TryReadArchiveEntries(
        string archivePath,
        IReadOnlyCollection<string> entryPaths)
    {
        var wanted = new Dictionary<string, string>(entryPaths.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var p in entryPaths) wanted[NormalizeQueryPath(p)] = p;
        var result = new Dictionary<string, byte[]>(entryPaths.Count, StringComparer.OrdinalIgnoreCase);
        var reader = Archive.CreateReader(GameRelease.SkyrimSE, archivePath);
        try
        {
            foreach (var file in reader.Files)
            {
                if (result.Count == wanted.Count) break;
                if (wanted.TryGetValue(BethesdaPath.NormalizeArchiveEntry(file.Path), out var original)
                    && !result.ContainsKey(original))
                    result[original] = file.GetBytes();
            }
            return result;
        }
        finally { (reader as IDisposable)?.Dispose(); }
    }

    /// <summary>Reads one already-resolved loose file or BSA entry.</summary>
    /// <param name="s">Concrete source returned by placement resolution.</param>
    /// <returns>
    /// Fresh bytes and null error on success; otherwise null bytes and a named race, absence, or read error.
    /// </returns>
    public static (byte[]? Bytes, string? Error) ReadPlacementSource(PlacementSource s)
    {
        if (s.Kind == AssetKind.Loose)
        {
            var p = s.LooseFilePath;
            if (p is null || !File.Exists(p)) return (null, $"the resolved loose source '{p}' is no longer on disk");
            try { return (File.ReadAllBytes(p), null); }
            catch (Exception ex) { return (null, $"could not read resolved source '{p}': {ex.Message}"); }
        }
        try
        {
            var b = s.ArchivePath is null ? null : TryReadArchiveEntry(s.ArchivePath, s.EntryPath);
            return b is null
                ? (null, $"entry '{s.EntryPath}' not found inside '{Path.GetFileName(s.ArchivePath ?? "?")}'")
                : (b, null);
        }
        catch (Exception ex)
        {
            return (null, $"could not read archive '{Path.GetFileName(s.ArchivePath ?? "?")}': {ex.Message}");
        }
    }

    /// <summary>Resolves one Data-relative asset against the current snapshot.</summary>
    /// <param name="relPath">Untrusted slash- or backslash-separated path relative to Data.</param>
    /// <returns>Canonical path, existence, winner, and all contenders in precedence order.</returns>
    /// <exception cref="ArgumentException"><paramref name="relPath"/> is not safely Data-relative.</exception>
    /// <exception cref="InvalidOperationException">An authoritative Amethyst winner vanished from staging.</exception>
    /// <remarks>
    /// Use <see cref="Capture"/> when several answers and failure diagnostics must share one snapshot.
    /// </remarks>
    public AssetHit Resolve(string relPath) => Resolve(relPath, _snap);

    /// <summary>Resolves one path against a caller-pinned snapshot.</summary>
    /// <param name="relPath">Canonical or slash-separated path relative to Data.</param>
    /// <param name="snap">Single archive/cache build to query.</param>
    /// <returns>Winner-first provider information for the normalized path.</returns>
    AssetHit Resolve(string relPath, Snapshot snap)
    {
        var rel = NormalizeQueryPath(relPath);
        var sources = ResolveProviders(rel, snap);
        if (sources.Count == 0)
            return new AssetHit(rel, false, null, Array.Empty<AssetProvider>(), false);
        // Project the concrete sources down to the display providers — the on-disk paths are placement-only.
        var providers = sources.Select(s => new AssetProvider(s.ProviderName, s.Kind)).ToList();
        // Ambiguous when >1 source provides it (contention), or a loose copy coexists with a BSA copy (the edge the
        // common-rule model cannot promise exactly for manager-controlled archives).
        return new AssetHit(rel, true, providers[0], providers, providers.Count > 1);
    }

    /// <summary>Applies the single precedence algorithm shared by display and placement resolution.</summary>
    /// <param name="rel">Already normalized canonical Data-relative path.</param>
    /// <param name="snap">Pinned archive and legacy loose-cache snapshot.</param>
    /// <returns>Concrete sources in winner-first order: loose providers, then ranked BSAs.</returns>
    /// <exception cref="InvalidOperationException">An authoritative Amethyst winner vanished from staging.</exception>
    List<PlacementSource> ResolveProviders(string rel, Snapshot snap)
    {
        var loose = new List<PlacementSource>();
        if (_authoritativeLoose is not null)
        {
            if (_authoritativeLoose.TryGetValue(rel, out var source))
            {
                if (!File.Exists(source.HostPath))
                    throw new InvalidOperationException(
                        $"Amethyst filemap winner '{rel}' from '{source.Provider}' is missing at " +
                        $"'{source.HostPath}'; refresh Amethyst and rebuild the filemap");
                loose.Add(new PlacementSource(source.Provider, AssetKind.Loose,
                    LooseFilePath: source.HostPath, ArchivePath: null, EntryPath: rel));
            }
            if (_dataDir.Length > 0
                && BethesdaPath.TryResolveExisting(_dataDir, rel, out var vanillaPath)
                && File.Exists(vanillaPath)
                && !loose.Any(x => string.Equals(x.LooseFilePath, vanillaPath, StringComparison.Ordinal)))
                loose.Add(new PlacementSource("Data", AssetKind.Loose,
                    LooseFilePath: vanillaPath, ArchivePath: null, EntryPath: rel));
        }
        else
        {
            // Legacy root walk: overwrite > enabled mods > Data.
            var subtreeDir = BethesdaPath.DirectoryName(rel);
            var fname = BethesdaPath.FileName(rel);
            var st = snap.LooseCache.GetOrAdd(subtreeDir, WarmSubtree);
            foreach (var (rootIndex, directory, files) in st.Present)
                if (files.TryGetValue(fname, out var rawName))
                    loose.Add(new PlacementSource(_looseRoots[rootIndex].Name, AssetKind.Loose,
                        LooseFilePath: Path.Combine(directory, rawName), ArchivePath: null, EntryPath: rel));
        }

        // ---- BSA, highest plugin rank first ----
        var bsa = new List<(PlacementSource source, int rank)>();
        foreach (var a in _archives)
            if (snap.Tables.TryGetValue(a.Path, out var t) && t.Contains(rel))
                bsa.Add((new PlacementSource(Path.GetFileName(a.Path), AssetKind.Bsa,
                    LooseFilePath: null, ArchivePath: a.Path, EntryPath: rel), a.PluginRank));
        // Higher plugin rank wins; the archive filename is a DETERMINISTIC tie-break so equal-rank BSAs (a plugin can
        // ship more than one) order stably across runs rather than by hash/enumeration order.
        var bsaOrdered = bsa.OrderByDescending(b => b.rank)
                            .ThenBy(b => b.source.ProviderName, StringComparer.OrdinalIgnoreCase)
                            .Select(b => b.source);

        // winner: loose beats BSA; the list is winner first, then the rest in precedence.
        var providers = new List<PlacementSource>(loose);
        providers.AddRange(bsaOrdered);
        return providers;
    }

    /// <summary>Resolves one asset to concrete sources that a placement operation can read.</summary>
    /// <param name="relPath">Untrusted slash- or backslash-separated path relative to Data.</param>
    /// <returns>Physical sources in winner-first order plus ambiguity and incomplete-read caveats.</returns>
    /// <exception cref="ArgumentException"><paramref name="relPath"/> is not safely Data-relative.</exception>
    /// <exception cref="InvalidOperationException">An authoritative Amethyst winner vanished from staging.</exception>
    public PlacementResolution ResolveForPlacement(string relPath) => ResolveForPlacement(relPath, _snap);

    /// <summary>Resolves concrete sources against a caller-pinned snapshot.</summary>
    /// <param name="relPath">Untrusted Data-relative path.</param>
    /// <param name="snap">Snapshot shared with the caller's other asset reads.</param>
    /// <returns>Physical sources and caveats from that exact snapshot.</returns>
    internal PlacementResolution ResolveForPlacement(string relPath, Snapshot snap)
    {
        var rel = NormalizeQueryPath(relPath);
        var sources = ResolveProviders(rel, snap);
        return new PlacementResolution(rel, sources, sources.Count > 1, snap.Failures.Count > 0);
    }

    /// <summary>Resolves several paths against one internally pinned current snapshot.</summary>
    /// <param name="relPaths">Untrusted Data-relative paths in desired result order.</param>
    /// <returns>One result per input path in the same order.</returns>
    /// <remarks>Use <see cref="Capture"/> to pair results with the same snapshot's failure list.</remarks>
    public IReadOnlyList<AssetHit> ResolveMany(IEnumerable<string> relPaths)
    {
        var snap = _snap;                                         // pin ONE build for the whole scan
        return relPaths.Select(p => Resolve(p, snap)).ToList();
    }

    /// <summary>Enumerates distinct paths supplied by any source below one Data-relative directory.</summary>
    /// <param name="prefix">Untrusted Data-relative directory prefix.</param>
    /// <returns>Case-insensitively distinct canonical paths; winner resolution remains a separate operation.</returns>
    /// <exception cref="ArgumentException"><paramref name="prefix"/> is not safely Data-relative.</exception>
    public IReadOnlyCollection<string> EnumerateUnder(string prefix) => EnumerateUnder(prefix, _snap);

    /// <summary>Enumerates distinct paths below a prefix using one pinned snapshot.</summary>
    /// <param name="prefix">Safe directory path relative to Data.</param>
    /// <param name="snap">Single archive/cache build to query.</param>
    /// <returns>Case-insensitively distinct canonical Bethesda paths.</returns>
    IReadOnlyCollection<string> EnumerateUnder(string prefix, Snapshot snap)
    {
        var pre = NormalizeQueryPath(prefix);
        // The trailing separator prevents a prefix such as "Sound" from matching a sibling named "Sound2".
        var withSep = pre + "\\";
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_authoritativeLoose is not null)
            foreach (var path in _authoritativeLoose.Keys)
                if (path.StartsWith(withSep, StringComparison.OrdinalIgnoreCase))
                    found.Add(path);

        // Legacy roots, or vanilla Data in authoritative mode.
        foreach (var (_, rootDir) in _looseRoots)
        {
            if (!BethesdaPath.TryResolveExisting(rootDir, pre, out var baseDir) || !Directory.Exists(baseDir)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories))
                    found.Add(BethesdaPath.FromHostRelative(Path.GetRelativePath(rootDir, f)));
            }
            catch { /* a root that won't enumerate contributes nothing; not silently trusted (see the summary) */ }
        }

        // Cached BSA tables are detached and therefore safe to enumerate without archive handles.
        foreach (var t in snap.Tables.Values)
            foreach (var entry in t)
                if (entry.StartsWith(withSep, StringComparison.OrdinalIgnoreCase))
                    found.Add(entry);

        return found;
    }

    /// <summary>Captures the current snapshot for a logically consistent multi-read operation.</summary>
    /// <returns>A handle-free value view that remains valid if the resolver later refreshes.</returns>
    public AssetView Capture() => new(this, _snap);

    /// <summary>A read view pinned to ONE captured build (see <see cref="Capture"/>). Resolve / ResolveMany and
    /// BsaFailures / ReadIncomplete all answer from the SAME snapshot, so they can never disagree about which build
    /// they describe. No handles — safe to hold for a call.</summary>
    public readonly struct AssetView
    {
        /// <summary>Owning resolver whose pure query helpers interpret the snapshot.</summary>
        readonly AssetResolver _r;
        /// <summary>Exact immutable snapshot captured for this view.</summary>
        readonly Snapshot _s;
        /// <summary>Creates a view over one resolver snapshot; only <see cref="Capture"/> calls this.</summary>
        /// <param name="r">Owning resolver.</param>
        /// <param name="s">Snapshot to pin.</param>
        internal AssetView(AssetResolver r, Snapshot s) { _r = r; _s = s; }

        /// <summary>Archives that could not be read in this snapshot.</summary>
        public IReadOnlyList<string> BsaFailures => _s.Failures;

        /// <summary>The Exists=false caveat for THIS build — see <see cref="AssetResolver.ReadIncomplete"/>.</summary>
        public bool ReadIncomplete => _s.Failures.Count > 0;

        /// <summary>Resolves one asset against this view's pinned build.</summary>
        /// <param name="relPath">Untrusted Data-relative path.</param>
        /// <returns>Logical winner and contender information from the pinned snapshot.</returns>
        public AssetHit Resolve(string relPath) => _r.Resolve(relPath, _s);

        /// <summary>Resolves concrete placement sources against this view's pinned build.</summary>
        /// <param name="relPath">Untrusted Data-relative path.</param>
        /// <returns>Physical sources and caveats from the pinned snapshot.</returns>
        public PlacementResolution ResolveForPlacement(string relPath) => _r.ResolveForPlacement(relPath, _s);

        /// <summary>Resolves every supplied path against this view's single pinned build.</summary>
        /// <param name="relPaths">Untrusted Data-relative paths in desired result order.</param>
        /// <returns>One logical resolution per input path.</returns>
        public IReadOnlyList<AssetHit> ResolveMany(IEnumerable<string> relPaths)
        {
            var r = _r; var s = _s;                              // locals — a struct's lambda can't capture 'this'
            return relPaths.Select(p => r.Resolve(p, s)).ToList();
        }

        /// <summary>Enumerates every supplied path below a prefix using this view's pinned build.</summary>
        /// <param name="prefix">Untrusted Data-relative directory prefix.</param>
        /// <returns>Case-insensitively distinct canonical paths from any provider.</returns>
        public IReadOnlyCollection<string> EnumerateUnder(string prefix) => _r.EnumerateUnder(prefix, _s);
    }

    /// <summary>
    /// Refreshes derived tables when an archive, warmed legacy subtree, or authoritative source changed.
    /// </summary>
    /// <returns>True when a new snapshot replaced the old one; false when all tracked inputs were unchanged.</returns>
    /// <remarks>
    /// Manager membership/order changes rebuild the entire resolver outside this method. Existing views keep their
    /// pinned snapshot while the replacement starts with an empty lazy loose cache.
    /// </remarks>
    public bool RefreshIfStale()
    {
        var snap = _snap;
        bool stale = false;
        foreach (var a in _archives)
            if (!snap.Mtimes.TryGetValue(a.Path, out var m) || SafeMtime(a.Path) != m) { stale = true; break; }
        if (!stale)
        {
            if (_authoritativeLoose is not null)
            {
                // Filemap/index membership changes rebuild the whole resolver in the manager layout.
                // Here only a vanished staged winner requires a local refresh signal.
                stale = _authoritativeLoose.Values.Any(x => !File.Exists(x.HostPath));
            }
            else
                foreach (var kv in snap.LooseCache)
                    if (LooseSubtreeStale(kv.Key, kv.Value)) { stale = true; break; }
        }
        if (!stale) return false;
        _snap = BuildTables();
        return true;
    }

    /// <summary>Checks whether any root's copy of one warmed legacy directory changed.</summary>
    /// <param name="subtreeDir">Canonical parent path used as the loose-cache key.</param>
    /// <param name="st">Cached directories and parallel timestamp baselines.</param>
    /// <returns>True when a directory changed, appeared, disappeared, or became unreadable.</returns>
    bool LooseSubtreeStale(string subtreeDir, LooseSubtree st)
    {
        var roots = _looseRoots;
        for (int i = 0; i < roots.Count; i++)
        {
            var dir = ResolveSubtree(roots[i].Dir, subtreeDir);
            if (SafeMtime(dir) != st.DirMtimes[i]) return true;
        }
        return false;
    }

    /// <summary>
    /// Reads one legacy parent directory across every loose root and captures its freshness baseline.
    /// </summary>
    /// <param name="subtreeDir">Canonical Data-relative parent path.</param>
    /// <returns>Parallel timestamps and case-preserving top-level filename maps in precedence order.</returns>
    LooseSubtree WarmSubtree(string subtreeDir)
    {
        var roots = _looseRoots;
        var mtimes = new DateTime[roots.Count];
        var present = new List<(int, string, Dictionary<string, string>)>();
        for (int i = 0; i < roots.Count; i++)
        {
            var dir = ResolveSubtree(roots[i].Dir, subtreeDir);
            mtimes[i] = SafeMtime(dir);
            var files = SafeListFilenames(dir);
            if (files is { Count: > 0 }) present.Add((i, dir, files));
        }
        return new LooseSubtree(mtimes, present.ToArray());
    }

    /// <summary>Reads case-preserving immediate filenames from one native directory.</summary>
    /// <param name="dir">Native directory to enumerate without recursion.</param>
    /// <returns>A case-insensitive logical-name map, or null when absent or unreadable.</returns>
    static Dictionary<string, string>? SafeListFilenames(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            var set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(dir)) set.TryAdd(Path.GetFileName(f), Path.GetFileName(f));
            return set;
        }
        catch { return null; }
    }

    /// <summary>Applies the shared canonical Data-path validation used by resolution and placement.</summary>
    /// <param name="relPath">Untrusted slash- or backslash-separated Data-relative path.</param>
    /// <returns>Canonical backslash-separated Bethesda path.</returns>
    /// <exception cref="ArgumentException"><paramref name="relPath"/> violates the Data-relative contract.</exception>
    public static string ValidateRelPath(string relPath) => NormalizeQueryPath(relPath);

    /// <summary>Rejects any query path that is not safely relative to Data.</summary>
    /// <param name="relPath">Untrusted path.</param>
    /// <returns>Canonical Bethesda path.</returns>
    static string NormalizeQueryPath(string relPath) => BethesdaPath.Normalize(relPath);

    /// <summary>
    /// Maps a canonical Bethesda directory below a native root using real Linux casing when available.
    /// </summary>
    /// <param name="root">Trusted native root.</param>
    /// <param name="subtree">Canonical Data-relative directory, or empty for the root itself.</param>
    /// <returns>Existing case-preserved path, or a safely combined expected path when absent.</returns>
    static string ResolveSubtree(string root, string subtree) => subtree.Length == 0
        ? root
        : BethesdaPath.TryResolveExisting(root, subtree, out var resolved)
            ? resolved
            : BethesdaPath.Under(root, subtree);

    /// <summary>Reads a UTC modification time without turning an inaccessible path into a refresh exception.</summary>
    /// <param name="path">Native file or directory path.</param>
    /// <returns>UTC modification time, or <see cref="DateTime.MinValue"/> when absent or unreadable.</returns>
    static DateTime SafeMtime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); } catch { return DateTime.MinValue; }
    }

    /// <summary>Flattens and bounds an archive exception for a user-visible failure list.</summary>
    /// <param name="ex">Exception to summarize without a stack trace.</param>
    /// <returns>Single-line message capped at 200 characters plus an ellipsis.</returns>
    static string Concise(Exception ex)
    {
        var s = ex.Message.Replace("\r", "").Replace("\n", " ").Trim();
        return s.Length > 200 ? s.Substring(0, 200) + "…" : s;
    }

    /// <summary>
    /// Releases no resources because snapshots retain only managed derived data;
    /// retained for uniform service ownership.
    /// </summary>
    public void Dispose() { }
}
