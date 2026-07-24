using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>Reports a FormID-driven FaceGen carry pass.</summary>
/// <param name="NpcCount">Renumbered NPCs considered.</param>
/// <param name="FacegenNpcsCarried">NPCs for which at least one FaceGen file was written.</param>
/// <param name="FacegenFilesCarried">Total mesh and tint files written.</param>
/// <param name="Failures">Assets found but not readable, stageable, or committable.</param>
/// <param name="ReadIncomplete">Whether an unreadable archive makes absence results inconclusive.</param>
public sealed record AssetRenameOutcome(
    int NpcCount,
    int FacegenNpcsCarried,
    int FacegenFilesCarried,
    IReadOnlyList<string> Failures,
    bool ReadIncomplete)
{
    /// <summary>Creates a clean no-work result while preserving read completeness.</summary>
    /// <param name="readIncomplete">Whether archive discovery was incomplete.</param>
    /// <returns>A zero-count outcome.</returns>
    public static AssetRenameOutcome None(bool readIncomplete = false) =>
        new(0, 0, 0, Array.Empty<string>(), readIncomplete);
}

/// <summary>Reports a FormID-driven voice-file carry pass.</summary>
/// <param name="FilesScanned">Voice files found below the source plugin's voice prefix.</param>
/// <param name="FilesCarried">Files whose embedded INFO identifier changed and whose bytes were written.</param>
/// <param name="LinesCarried">Distinct renumbered INFO records for which at least one file was written.</param>
/// <param name="Failures">Files found but not readable, stageable, or committable.</param>
/// <param name="ReadIncomplete">Whether an unreadable archive makes absence results inconclusive.</param>
public sealed record VoiceCarryOutcome(
    int FilesScanned,
    int FilesCarried,
    int LinesCarried,
    IReadOnlyList<string> Failures,
    bool ReadIncomplete)
{
    /// <summary>Creates a clean no-work result while preserving read completeness.</summary>
    /// <param name="readIncomplete">Whether archive discovery was incomplete.</param>
    /// <returns>A zero-count outcome.</returns>
    public static VoiceCarryOutcome None(bool readIncomplete = false) =>
        new(0, 0, 0, Array.Empty<string>(), readIncomplete);
}

/// <summary>Reports refresh of a renumbered plugin's start-game-enabled quest index.</summary>
/// <param name="SgeQuestCount">Start-game-enabled quests in the written plugin.</param>
/// <param name="Written">Whether a replacement SEQ file was committed.</param>
/// <param name="SeqPath">Native destination path when written; otherwise null.</param>
/// <param name="Failures">Named build, write, or missing-source advisories.</param>
public sealed record SeqRegenOutcome(
    int SgeQuestCount,
    bool Written,
    string? SeqPath,
    IReadOnlyList<string> Failures)
{
    /// <summary>No start-game-enabled quests — nothing to write (no <c>.seq</c> cut), a clean no-op.</summary>
    /// <returns>A zero-count no-work result.</returns>
    public static SeqRegenOutcome None() => new(0, false, null, Array.Empty<string>());
}

/// <summary>Carries FormID-keyed assets after compacting or merging plugin records.</summary>
/// <remarks>
/// FaceGen and voice files are copied from old paths to new paths derived from the renumber map. SEQ files are
/// rebuilt from the written plugin because their contents contain renumbered IDs. Old files are never deleted.
/// FaceGen and voice share a two-phase disk-staging algorithm so an in-place destination cannot overwrite another
/// record's not-yet-read source. Failures are reported without invalidating the already-written record patch.
/// </remarks>
public static class AssetRenameService
{
    /// <summary>Carries FaceGen for each NPC renumbered into the written plugin.</summary>
    /// <param name="pPrimePath">Native path to the already-written compacted or merged plugin.</param>
    /// <param name="map">Complete old-to-new FormKey map from the record renumbering pass.</param>
    /// <param name="assets">Pinned asset view used to read consistent old-path winners.</param>
    /// <param name="outDir">Native output-mod root for the new-path copies.</param>
    /// <returns>NPC/file counts, named failures, and resolver completeness.</returns>
    /// <remarks>
    /// Only NPCs whose new key appears in the map are considered. Existing overrides that retained a master key
    /// require no asset rename. A missing FaceGen pair is normal; a found but unreadable or unwritable file is not.
    /// </remarks>
    public static AssetRenameOutcome CarryFaceGen(
        string pPrimePath, IReadOnlyDictionary<FormKey, FormKey> map, AssetResolver.AssetView assets, string outDir)
    {
        // Read the written plugin and intersect its NPCs with the map's new keys. The transient overlay is disposed
        // immediately, preserving the zero-handle-at-rest rule.
        List<(FormKey Old, FormKey New)> npcs;
        try
        {
            using var pp = SkyrimMod.CreateFromBinaryOverlay(pPrimePath, SkyrimRelease.SkyrimSE);
            var reverse = new Dictionary<FormKey, FormKey>(map.Count);
            foreach (var kv in map) reverse[kv.Value] = kv.Key;            // new → old
            npcs = pp.Npcs.Where(n => reverse.ContainsKey(n.FormKey))
                          .Select(n => (Old: reverse[n.FormKey], New: n.FormKey))
                          .ToList();
        }
        catch (Exception ex)
        {
            // The record operation already succeeded, so report an incomplete asset phase instead of throwing.
            var failure =
                $"could not read '{Path.GetFileName(pPrimePath)}' back to find its NPCs for FaceGen carry " +
                $"({ex.Message}) — verify NPC faces in-game.";
            return new AssetRenameOutcome(0, 0, 0,
                new[] { failure },
                assets.ReadIncomplete);
        }

        if (npcs.Count == 0) return AssetRenameOutcome.None(assets.ReadIncomplete);

        // Build old-to-new mesh and tint pairs. CarryItems stages all sources before any destination commit.
        var items = new List<CarryItem>();
        foreach (var (oldKey, newKey) in npcs)
            foreach (var (slot, oldPath) in FaceGenPath.Both(oldKey))      // (Mesh, …), (Tint, …) — the dark-face pair
                items.Add(new CarryItem(
                    oldPath,
                    FaceGenPath.For(newKey, slot),
                    newKey,
                    $"{oldKey.ID:X6}→{newKey.ID:X6} {slot}"));

        var failures = new List<string>();
        var (files, carried) = CarryItems(items, assets, outDir, failures);
        return new AssetRenameOutcome(npcs.Count, carried.Count, files, failures, assets.ReadIncomplete);
    }

    /// <summary>Carries voice files whose embedded INFO identifiers were renumbered.</summary>
    /// <param name="pPrimePath">Native path to the already-written output plugin.</param>
    /// <param name="map">Complete old-to-new FormKey map from the record renumbering pass.</param>
    /// <param name="assets">Pinned asset view used to enumerate and read old-path winners.</param>
    /// <param name="outDir">Native output-mod root for the new-path copies.</param>
    /// <param name="sourcePlugin">
    /// Original donor filename during a merge; null during compaction, where the plugin name is unchanged.
    /// </param>
    /// <returns>Scanned/file/line counts, named failures, and resolver completeness.</returns>
    /// <remarks>
    /// Discovery scans actual files under <c>Sound\Voice\plugin\</c> instead of reconstructing dialogue graphs.
    /// Compaction changes only the embedded local ID. Merge also changes the defining-plugin folder segment.
    /// </remarks>
    public static VoiceCarryOutcome CarryVoice(
        string pPrimePath, IReadOnlyDictionary<FormKey, FormKey> map, AssetResolver.AssetView assets, string outDir,
        string? sourcePlugin = null)
    {
        // Compact keeps the same folder name; merge rewrites from the donor folder to the output folder.
        var targetBasename = Path.GetFileName(pPrimePath);
        var sourceBasename = sourcePlugin ?? targetBasename;

        // local-id → new-local-id for the SOURCE plugin's renumbered records — the only ones whose voice lives under
        // Sound\Voice\<source>\ (an override kept at a master key has its voice under the MASTER's folder, untouched).
        var idMap = new Dictionary<uint, uint>();
        foreach (var kv in map)
            if (string.Equals(kv.Key.ModKey.FileName.ToString(), sourceBasename, StringComparison.OrdinalIgnoreCase))
                idMap[kv.Key.ID] = kv.Value.ID;
        if (idMap.Count == 0) return VoiceCarryOutcome.None(assets.ReadIncomplete);

        // The new INFO FormKeys need a ModKey for the distinct-line accounting; the target basename came from a real
        // plugin path, so a malformed name is surfaced instead of silently producing a zero pass.
        ModKey modKey;
        try { modKey = ModKey.FromFileName(targetBasename); }
        catch (Exception ex)
        {
            var failure =
                $"'{targetBasename}' is not a valid plugin filename for voice carry ({ex.Message}) — " +
                "verify voiced lines in-game.";
            return new VoiceCarryOutcome(
                0,
                0,
                0,
                new[] { failure },
                assets.ReadIncomplete);
        }

        var srcPrefix = $@"Sound\Voice\{sourceBasename}";
        var tgtPrefix = $@"Sound\Voice\{targetBasename}";
        IReadOnlyCollection<string> files;
        try { files = assets.EnumerateUnder(srcPrefix); }
        catch (Exception ex)
        {
            return new VoiceCarryOutcome(0, 0, 0,
                new[] { $"could not scan '{srcPrefix}' for voice files ({ex.Message}) — verify voiced lines in-game." },
                assets.ReadIncomplete);
        }
        if (files.Count == 0) return VoiceCarryOutcome.None(assets.ReadIncomplete);

        // Rewrite only files with a recognized embedded ID that appears in the renumber map.
        var items = new List<CarryItem>();
        foreach (var oldRel in files)
        {
            var fname = BethesdaPath.FileName(oldRel);
            var m = VoiceIdRx.Match(fname);
            if (!m.Success) continue;
            uint full;
            try { full = Convert.ToUInt32(m.Groups[1].Value, 16); } catch { continue; }
            uint oldLocal = full & FormIdRange.ObjectIdMask;
            if (!idMap.TryGetValue(oldLocal, out var newLocal)) continue;

            var newId = "00" + newLocal.ToString("X6");
            var newFname = fname.Substring(0, m.Groups[1].Index) +
                           newId +
                           fname.Substring(m.Groups[1].Index + m.Groups[1].Length);
            var dir = BethesdaPath.DirectoryName(oldRel);
            var newDir = dir.Length >= srcPrefix.Length ? tgtPrefix + dir.Substring(srcPrefix.Length) : dir;
            var newRel = newDir.Length == 0 ? newFname : newDir + "\\" + newFname;
            items.Add(new CarryItem(
                oldRel,
                newRel,
                new FormKey(modKey, newLocal),
                $"{oldLocal:X6}→{newLocal:X6} {fname}"));
        }

        var failures = new List<string>();
        var (carriedFiles, carriedLines) = CarryItems(items, assets, outDir, failures);
        return new VoiceCarryOutcome(files.Count, carriedFiles, carriedLines.Count, failures, assets.ReadIncomplete);
    }

    /// <summary>Refreshes a source-supplied SEQ file from the renumbered plugin.</summary>
    /// <param name="pPrimePath">Native path to the already-written output plugin.</param>
    /// <param name="outDir">Native output-mod root that will contain the <c>SEQ</c> directory.</param>
    /// <param name="sourceHadSeq">Whether the source shipped a SEQ file and therefore requires refresh.</param>
    /// <returns>Quest count, committed destination, and any named advisory or failure.</returns>
    /// <remarks>
    /// SEQ contains master-relative on-disk FormIDs, so it must be rebuilt rather than renamed. This operation is
    /// refresh-only: it never invents a SEQ file that the source did not ship. No start-game-enabled quests is a
    /// clean no-op.
    /// </remarks>
    public static SeqRegenOutcome RegenerateSeq(string pPrimePath, string outDir, bool sourceHadSeq)
    {
        SeqFile.SeqBuild built;
        try { built = SeqFile.Build(pPrimePath); }
        catch (Exception ex)
        {
            var failure =
                $"could not read '{Path.GetFileName(pPrimePath)}' back to rebuild its .seq ({ex.Message}) — " +
                "if it has start-game-enabled quests, run housecarl_write_seq on the compacted plugin.";
            return new SeqRegenOutcome(0, false, null, new[] { failure });
        }

        // A renumber never clears quest flags, so no start-game-enabled quests means no SEQ is needed.
        if (built.Quests.Count == 0) return SeqRegenOutcome.None();

        if (!sourceHadSeq)
        {
            var failure =
                $"'{Path.GetFileName(pPrimePath)}' has {built.Quests.Count} start-game-enabled quest(s) but no " +
                ".seq — they likely were not starting before compaction; run housecarl_write_seq to add one.";
            return new SeqRegenOutcome(built.Quests.Count, false, null, new[] { failure });
        }

        var dest = Path.Combine(outDir, "SEQ", Path.GetFileNameWithoutExtension(pPrimePath) + ".seq");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            AtomicFile.WriteAllBytes(dest, built.Bytes);
            long size; try { size = new FileInfo(dest).Length; } catch { size = -1; }
            if (size != built.Bytes.Length)
            {
                var failure =
                    $"wrote {size} byte(s) to '{Path.GetFileName(dest)}', expected {built.Bytes.Length} — " +
                    "verify the .seq.";
                return new SeqRegenOutcome(built.Quests.Count, false, null, new[] { failure });
            }
        }
        catch (Exception ex)
        {
            var failure =
                $"could not write '{Path.GetFileName(dest)}' ({ex.Message}) — its start-game-enabled quests may " +
                "not start; run housecarl_write_seq on the compacted plugin.";
            return new SeqRegenOutcome(built.Quests.Count, false, null, new[] { failure });
        }
        return new SeqRegenOutcome(built.Quests.Count, true, dest, Array.Empty<string>());
    }

    /// <summary>Describes one old-path winner that must be copied to a new FormID-keyed path.</summary>
    /// <param name="OldPath">Canonical source path.</param>
    /// <param name="NewPath">Canonical destination path.</param>
    /// <param name="Owner">Renumbered record used for distinct-owner accounting.</param>
    /// <param name="Label">Human-readable context prepended to failures.</param>
    readonly record struct CarryItem(string OldPath, string NewPath, FormKey Owner, string Label);

    /// <summary>Stages every source read before committing any destination.</summary>
    /// <param name="items">Old/new path pairs to carry.</param>
    /// <param name="assets">Pinned view that resolves each old-path winner.</param>
    /// <param name="outDir">Native output-mod root.</param>
    /// <param name="failures">Collector receiving found-but-unreadable or unwritable paths.</param>
    /// <returns>Committed file count and distinct owners with at least one committed file.</returns>
    /// <remarks>
    /// Phase one writes sibling temporary files only. Phase two atomically commits them after all reads finish.
    /// This prevents an in-place new path from overwriting a different record's not-yet-read old path. Missing
    /// paths are ignored because absence is normal for some records; all found-source failures are named.
    /// </remarks>
    static (int Files, HashSet<FormKey> Owners) CarryItems(
        IReadOnlyList<CarryItem> items, AssetResolver.AssetView assets, string outDir, List<string> failures)
    {
        var pending = new List<(string Staged, string Final, FormKey Owner)>();

        // Phase one reads every old asset and writes only destination-side temporary files.
        foreach (var it in items)
        {
            var res = assets.ResolveForPlacement(it.OldPath);
            if (res.Sources.Count == 0) continue;

            var (bytes, err) = ReadWinner(res.Sources[0]);
            if (err is not null) { failures.Add($"{it.Label}: {err}"); continue; }

            var final = BethesdaPath.Under(outDir, it.NewPath);
            var staged = final + ".houseCARL-tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(final)!);
                try { if (File.Exists(staged)) File.Delete(staged); }
                catch { /* a stuck temp surfaces on the write below */ }
                File.WriteAllBytes(staged, bytes!);
                // Atomic rename cannot truncate; verify the temporary bytes before committing them.
                long size; try { size = new FileInfo(staged).Length; } catch { size = -1; }
                if (size != bytes!.Length)
                {
                    failures.Add($"{it.Label}: staged {size} byte(s), expected {bytes.Length} — verify.");
                    try { File.Delete(staged); } catch { }
                    continue;
                }
                pending.Add((staged, final, it.Owner));
            }
            catch (Exception ex)
            {
                failures.Add($"{it.Label}: could not stage '{it.NewPath}' — {ex.Message}");
                try { if (File.Exists(staged)) File.Delete(staged); } catch { }
            }
        }

        // Phase two starts only after every old path has been read.
        int files = 0;
        var owners = new HashSet<FormKey>();
        foreach (var (staged, final, owner) in pending)
        {
            try { AtomicFile.Commit(staged, final); files++; owners.Add(owner); }
            catch (Exception ex)
            {
                failures.Add($"could not commit '{Path.GetFileName(final)}' — {ex.Message}");
                try { if (File.Exists(staged)) File.Delete(staged); } catch { }
            }
        }
        return (files, owners);
    }

    /// <summary>
    /// Matches the terminal <c>_8hex_response.extension</c> portion of an INFO-keyed voice filename.
    /// </summary>
    /// <remarks>
    /// Group one is the eight-digit FormID. End anchoring prevents underscores or hexadecimal text in preceding
    /// EditorID segments from being mistaken for the identifier.
    /// </remarks>
    static readonly System.Text.RegularExpressions.Regex VoiceIdRx =
        new(@"_([0-9A-Fa-f]{8})_\d+\.[A-Za-z0-9]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Reads the exact bytes of one already-resolved winning provider.</summary>
    /// <param name="s">Loose-file or BSA-entry descriptor.</param>
    /// <returns>Bytes on success, otherwise a named error.</returns>
    static (byte[]? bytes, string? error) ReadWinner(PlacementSource s) => AssetResolver.ReadPlacementSource(s);
}
