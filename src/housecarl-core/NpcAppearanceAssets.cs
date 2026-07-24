using System.Collections;
using System.Reflection;
using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Records;

namespace HousecarlCore;

/// <summary>Describes one asset successfully copied into the output mod.</summary>
/// <param name="OldRelPath">Canonical Data-relative source path.</param>
/// <param name="NewRelPath">Canonical Data-relative destination path.</param>
/// <param name="Bytes">Number of bytes written.</param>
/// <param name="From">Human-readable winning source from which the bytes were read.</param>
public sealed record CarriedAsset(
    string OldRelPath,
    string NewRelPath,
    long Bytes,
    string From);

/// <summary>Reports the complete best-effort asset-carry result for one NPC copy.</summary>
/// <param name="Carried">Files successfully placed in the output mod.</param>
/// <param name="SkippedStillProvided">References left alone because another active provider will retain them.</param>
/// <param name="Missing">Referenced paths that no readable active or donor source supplied.</param>
/// <param name="Warnings">Precedence or verification caveats that do not represent a failed write.</param>
/// <param name="Failures">Named read or write failures.</param>
/// <param name="FaceGenMeshCarried">Whether the donor mesh was written at the new NPC's path.</param>
/// <param name="FaceGenTintCarried">Whether the donor tint was written at the new NPC's path.</param>
public sealed record NpcAssetOutcome(
    IReadOnlyList<CarriedAsset> Carried,
    IReadOnlyList<string> SkippedStillProvided,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Failures,
    bool FaceGenMeshCarried,
    bool FaceGenTintCarried);

/// <summary>Copies the file assets required by a standalone NPC appearance copy.</summary>
/// <remarks>
/// FaceGen files move to paths derived from the new FormKey. Other referenced files are copied only when removing
/// the donor would remove their winning bytes. Writes are atomic and best-effort because the record patch has
/// already been committed by the time this phase runs.
/// </remarks>
public static class NpcAppearanceAssets
{
    /// <summary>Harvests every asset link reachable in the supplied record graphs.</summary>
    /// <param name="records">In-memory copied records to inspect before their backing session is released.</param>
    /// <returns>Unique Data-relative paths in first-seen order.</returns>
    /// <remarks>
    /// The bounded reflection walk recognizes <see cref="IAssetLinkGetter"/> rather than maintaining a per-record
    /// field list, so lists and nested Mutagen structures are covered automatically.
    /// </remarks>
    public static IReadOnlyList<string> HarvestAssetPaths(IEnumerable<IMajorRecordGetter> records)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rec in records)
            HarvestFrom(rec, paths, seen, new HashSet<object>(ReferenceEqualityComparer.Instance), depth: 0);
        return paths;
    }

    /// <summary>Walks one bounded Mutagen object graph and appends newly discovered asset paths.</summary>
    /// <param name="node">Current graph value.</param>
    /// <param name="paths">Ordered result collector.</param>
    /// <param name="seen">Case-insensitive path de-duplication set.</param>
    /// <param name="visited">Reference-identity cycle guard.</param>
    /// <param name="depth">Current nesting depth; values deeper than six are not expanded.</param>
    static void HarvestFrom(
        object node,
        List<string> paths,
        HashSet<string> seen,
        HashSet<object> visited,
        int depth)
    {
        if (depth > 6 || node is string || !visited.Add(node)) return;

        if (node is IAssetLinkGetter asset)
        {
            string? rel = null;
            try { rel = asset.DataRelativePath.Path; } catch { /* an unset link — nothing to harvest */ }
            if (!string.IsNullOrWhiteSpace(rel) && seen.Add(rel)) paths.Add(rel);
            return;
        }

        if (node is IEnumerable en and not IFormLinkGetter)
        {
            foreach (var el in en)
                if (el is not null) HarvestFrom(el, paths, seen, visited, depth + 1);
            return;
        }

        // Only descend Mutagen model types (their namespace), never arbitrary BCL values — keeps the walk cheap + safe.
        var t = node.GetType();
        if (t.Namespace is null || !t.Namespace.StartsWith("Mutagen.Bethesda", StringComparison.Ordinal)) return;
        // A form link is an identity, not an asset container.
        if (node is IFormLinkGetter) return;

        foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length != 0 || prop.GetGetMethod() is null) continue;
            object? val;
            try { val = prop.GetValue(node); } catch { continue; }
            if (val is null) continue;
            HarvestFrom(val, paths, seen, visited, depth + 1);
        }
    }

    /// <summary>Finds printable DDS paths embedded in a FaceGen NIF.</summary>
    /// <param name="nif">Raw NIF bytes.</param>
    /// <returns>Unique canonical texture paths in first-seen order.</returns>
    /// <remarks>
    /// This deliberately conservative scan accepts ASCII runs beginning with <c>textures\</c> or
    /// <c>textures/</c> and ending at the first case-insensitive <c>.dds</c>. It does not interpret NIF structure.
    /// A false positive becomes a named missing-path note and cannot cause a wrong-source write.
    /// </remarks>
    public static IReadOnlyList<string> ScrapeNifTexturePaths(byte[] nif)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var prefix = "textures"u8.ToArray();
        // Five extra bytes cover the separator plus the shortest possible "a.dds".
        for (int i = 0; i + prefix.Length + 5 <= nif.Length; i++)
        {
            int j = 0;
            while (j < prefix.Length && (nif[i + j] | 0x20) == prefix[j]) j++;
            if (j != prefix.Length) continue;
            int k = i + j;
            if (k >= nif.Length || (nif[k] != (byte)'\\' && nif[k] != (byte)'/')) continue;

            // Extend through printable path chars, stopping at the FIRST '.dds' (case-insensitive).
            int end = k + 1, ddsEnd = -1;
            while (end < nif.Length && end - i < 260)
            {
                byte b = nif[end];
                if (b < 0x20 || b > 0x7E) break;
                end++;
                if (end - i >= 4
                    && nif[end - 4] == (byte)'.'
                    && (nif[end - 3] | 0x20) == (byte)'d'
                    && (nif[end - 2] | 0x20) == (byte)'d'
                    && (nif[end - 1] | 0x20) == (byte)'s')
                { ddsEnd = end; break; }
            }
            // No extension in this run: resume at the first non-printable byte.
            if (ddsEnd < 0) { i = end - 1; continue; }

            var path = Encoding.ASCII.GetString(nif, i, ddsEnd - i).Replace('/', '\\');
            if (seen.Add(path)) found.Add(path);
            // The loop increment resumes immediately after this match.
            i = ddsEnd - 1;
        }
        return found;
    }

    /// <summary>Provides direct access to one donor's staging folder and top-level archives.</summary>
    /// <param name="Folder">Native staging folder containing the donor's Data-root files.</param>
    /// <param name="Bsas">Native top-level BSA paths belonging to that donor.</param>
    /// <remarks>
    /// This fallback supports an installed but disabled donor that is absent from the active Amethyst winner map.
    /// Callers must not construct it for vanilla Data because that would misclassify all vanilla archives as donor
    /// content.
    /// </remarks>
    public sealed record DonorDisk(string Folder, IReadOnlyList<string> Bsas)
    {
        /// <summary>Builds a donor-disk view from the donor plugin's staging path.</summary>
        /// <param name="donorPluginPath">Native plugin path inside a donor staging folder.</param>
        /// <returns>The containing folder and any accessible immediate BSA children.</returns>
        public static DonorDisk For(string donorPluginPath)
        {
            var dir = Path.GetDirectoryName(donorPluginPath)!;
            List<string> bsas;
            try { bsas = Directory.EnumerateFiles(dir, "*.bsa", SearchOption.TopDirectoryOnly).ToList(); }
            catch { bsas = new List<string>(); }
            return new DonorDisk(dir, bsas);
        }

        /// <summary>Reads a Data-relative path directly from this donor.</summary>
        /// <param name="relPath">Canonical Data-relative path.</param>
        /// <returns>Bytes and provenance, or two null values when no readable copy exists.</returns>
        /// <remarks>Loose content wins over the donor's BSA entries, matching Skyrim asset precedence.</remarks>
        public (byte[]? Bytes, string? From) Read(string relPath)
        {
            var loose = BethesdaPath.TryResolveExisting(Folder, relPath, out var resolved)
                ? resolved
                : BethesdaPath.Under(Folder, relPath);
            if (File.Exists(loose))
            {
                try { return (File.ReadAllBytes(loose), $"donor folder (loose)"); }
                catch { /* fall through to BSAs */ }
            }
            foreach (var bsa in Bsas)
            {
                byte[]? b;
                try { b = AssetResolver.TryReadArchiveEntry(bsa, relPath); }
                catch { continue; }
                if (b is not null) return (b, $"donor archive '{Path.GetFileName(bsa)}'");
            }
            return (null, null);
        }
    }

    /// <summary>Resolves one path under the donor-removal carry rule.</summary>
    /// <param name="relPath">Canonical source path.</param>
    /// <param name="view">Pinned active asset snapshot.</param>
    /// <param name="donors">Direct-disk fallbacks for installed donor mods.</param>
    /// <param name="donorModFolderNames">Provider names whose winning copies disappear with the donor.</param>
    /// <param name="alwaysCarry">True for FaceGen renames whose destination is necessarily new.</param>
    /// <returns>
    /// Bytes and provenance when copying is required; a skip note when another provider remains; otherwise a
    /// missing flag and, when applicable, the active winner's read error.
    /// </returns>
    static (byte[]? Bytes, string? From, string? SkipNote, bool Missing, string? ReadError) ResolveForCarry(
        string relPath,
        AssetResolver.AssetView view,
        IReadOnlyList<DonorDisk> donors,
        IReadOnlyList<string> donorModFolderNames,
        bool alwaysCarry)
    {
        string? readError = null;
        var res = view.ResolveForPlacement(relPath);
        if (res.Sources.Count > 0)
        {
            var winner = res.Sources[0];
            if (!alwaysCarry)
            {
                bool donorProvides = donorModFolderNames.Any(n =>
                        string.Equals(winner.ProviderName, n, StringComparison.OrdinalIgnoreCase))
                    || (winner.Kind == AssetKind.Bsa && winner.ArchivePath is not null
                        && donors.Any(d => string.Equals(
                            Path.GetDirectoryName(winner.ArchivePath),
                            d.Folder,
                            StringComparison.OrdinalIgnoreCase)));
                if (!donorProvides)
                    return (
                        null,
                        null,
                        $"'{relPath}' — still provided by '{winner.ProviderName}' after donor removal; not carried.",
                        false,
                        null);
            }
            (var bytes, readError) = AssetResolver.ReadPlacementSource(winner);
            if (bytes is not null)
            {
                var from = winner.Kind == AssetKind.Loose
                    ? $"'{winner.ProviderName}' (loose)"
                    : $"'{Path.GetFileName(winner.ArchivePath!)}'";
                return (bytes, from, null, false, null);
            }
            // the winner could not be read — fall through to the donor-disk lane, keeping the named cause
        }

        foreach (var donor in donors)
        {
            var (bytes, from) = donor.Read(relPath);
            if (bytes is not null) return (bytes, from, null, false, null);
        }
        return (null, null, null, true, readError);
    }

    /// <summary>Carries all files needed by one copied NPC into its output mod.</summary>
    /// <param name="donorNpc">Original NPC key used to locate existing FaceGen.</param>
    /// <param name="newNpc">Destination NPC key used to name copied FaceGen.</param>
    /// <param name="harvestedPaths">Asset links captured from the in-patch record duplicates.</param>
    /// <param name="view">Pinned active asset snapshot used for consistent winner reads.</param>
    /// <param name="donors">Direct-disk donor fallbacks.</param>
    /// <param name="donorModFolderNames">Active provider names owned by the donor.</param>
    /// <param name="outDir">Native output-mod root under which Data-relative paths are placed.</param>
    /// <returns>Complete copy, skip, missing, warning, and failure accounting; this method does not throw.</returns>
    /// <remarks>
    /// The FaceGen pair is renamed first. Embedded NIF textures then join the harvested record assets, and each
    /// path is copied only when it would stop resolving after donor removal. Every write is atomic.
    /// </remarks>
    public static NpcAssetOutcome CarryAll(
        FormKey donorNpc,
        FormKey newNpc,
        IReadOnlyList<string> harvestedPaths,
        AssetResolver.AssetView view,
        IReadOnlyList<DonorDisk> donors,
        IReadOnlyList<string> donorModFolderNames,
        string outDir)
    {
        var carried = new List<CarriedAsset>();
        var skipped = new List<string>();
        var missing = new List<string>();
        var warnings = new List<string>();
        var failures = new List<string>();
        bool meshCarried = false, tintCarried = false;
        byte[]? geomBytes = null;

        // Records an active provider that will outrank this new destination until Amethyst priority is adjusted.
        void WarnIfDestinationContested(string newRel)
        {
            var already = view.ResolveForPlacement(newRel);
            if (already.Sources.Count > 0)
                warnings.Add(
                    $"'{newRel}' is ALREADY provided by '{already.Sources[0].ProviderName}' — the carried copy does " +
                    "not win until the patch mod has higher Amethyst file priority.");
        }

        // ---- 1. the facegen pair: donor path → NEW path (a rename — alwaysCarry from the winning copy) ----
        foreach (var (slot, oldRel) in FaceGenPath.Both(donorNpc))
        {
            try
            {
                var newRel = FaceGenPath.For(newNpc, slot);
                var (bytes, from, _, isMissing, readErr) = ResolveForCarry(
                    oldRel,
                    view,
                    donors,
                    donorModFolderNames,
                    alwaysCarry: true);
                if (isMissing || bytes is null)
                {
                    if (readErr is not null)
                        failures.Add(
                            $"facegen {slot} '{oldRel}': the winning copy could not be read — {readErr}. " +
                            "Re-run once the file is free, or carry it with housecarl_place_asset.");
                    else
                        missing.Add(
                            $"facegen {slot} '{oldRel}' — found in neither the active file index nor donor staging. " +
                            "The engine may regenerate the head at runtime; verify in-game.");
                    continue;
                }
                WarnIfDestinationContested(newRel);
                if (WriteCarried(outDir, newRel, bytes, failures))
                {
                    carried.Add(new CarriedAsset(oldRel, newRel, bytes.Length, from ?? "?"));
                    if (slot == FaceGenSlot.Mesh) { meshCarried = true; geomBytes = bytes; }
                    else tintCarried = true;
                }
            }
            catch (Exception ex) { failures.Add($"facegen {slot} '{oldRel}': {ex.Message}"); }
        }

        // ---- 2. harvest: the caller's record-link harvest + the geom's embedded textures ----
        var wanted = new List<string>(harvestedPaths);
        var wantedSet = new HashSet<string>(harvestedPaths, StringComparer.OrdinalIgnoreCase);
        if (geomBytes is not null)
            foreach (var p in ScrapeNifTexturePaths(geomBytes))
                if (wantedSet.Add(p)) wanted.Add(p);

        // ---- 3. carry each harvested path under the rule (same relpath — a keep-resolving move, not a rename).
        //         Per-path fault isolation (review finding): ONE dirty path (a '..' segment surviving in a mod
        //         author's data, a scrape false-positive) is ONE named failure — never an abort that silently
        //         drops every path after it while reporting "asset carry skipped". ----
        foreach (var rel in wanted)
        {
            try
            {
                var (bytes, from, skipNote, isMissing, readErr) = ResolveForCarry(
                    rel,
                    view,
                    donors,
                    donorModFolderNames,
                    alwaysCarry: false);
                if (skipNote is not null) { skipped.Add(skipNote); continue; }
                if (isMissing)
                {
                    if (readErr is not null)
                        failures.Add($"'{rel}': the winning copy exists but could not be read — {readErr}.");
                    else
                        missing.Add(
                            $"'{rel}' — referenced by copied records or geometry but absent from the active file " +
                            "index and donor staging; verify in-game.");
                    continue;
                }
                if (bytes is null) continue;
                if (WriteCarried(outDir, rel, bytes, failures))
                    carried.Add(new CarriedAsset(rel, rel, bytes.Length, from ?? "?"));
            }
            catch (Exception ex) { failures.Add($"'{rel}': {ex.Message}"); }
        }

        return new NpcAssetOutcome(carried, skipped, missing, warnings, failures, meshCarried, tintCarried);
    }

    /// <summary>Atomically writes one carried file beneath the output-mod root.</summary>
    /// <param name="outDir">Native output-mod root.</param>
    /// <param name="relPath">Validated Data-relative destination.</param>
    /// <param name="bytes">Exact source bytes.</param>
    /// <param name="failures">Collector receiving a named failure instead of an exception.</param>
    /// <returns>True only when the bytes were committed successfully.</returns>
    static bool WriteCarried(string outDir, string relPath, byte[] bytes, List<string> failures)
    {
        try
        {
            var final = BethesdaPath.Under(outDir, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            AtomicFile.WriteAllBytes(final, bytes);
            return true;
        }
        catch (Exception ex)
        {
            failures.Add($"could not write '{relPath}' — {ex.Message}");
            return false;
        }
    }
}
