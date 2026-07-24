using System.Collections;
using System.Reflection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace HousecarlCore;

/// <summary>Reports one donor record copied into the output plugin.</summary>
/// <param name="Type">Record type without Mutagen overlay suffixes.</param>
/// <param name="EditorId">Preserved EditorID or a display placeholder when absent.</param>
/// <param name="OldKey">Original donor FormKey.</param>
/// <param name="NewKey">Allocated output-plugin FormKey.</param>
/// <param name="PulledBy">Parent record or NPC field that required this record.</param>
public sealed record InternalizedRecord(
    string Type,
    string EditorId,
    FormKey OldKey,
    FormKey NewKey,
    string PulledBy);

/// <summary>Reports the complete record-copy phase of a standalone NPC operation.</summary>
/// <param name="Success">Whether a usable patch was committed.</param>
/// <param name="Error">Pre-commit refusal or failure; null on success.</param>
/// <param name="Mode"><c>apply</c> for an existing target or <c>clone</c> for a new NPC.</param>
/// <param name="DonorKey">Source NPC FormKey.</param>
/// <param name="DonorReadFrom">Human-readable donor read provenance.</param>
/// <param name="DonorOutOfLoadOrder">Whether the donor was read directly rather than from the active order.</param>
/// <param name="NewNpcKey">Existing target key in apply mode or allocated clone key in clone mode.</param>
/// <param name="OutPath">Committed native plugin path; null on failure.</param>
/// <param name="Extended">Whether an existing output plugin was extended.</param>
/// <param name="Internalized">New donor-dependent records copied into the patch.</param>
/// <param name="Reused">Content-identical records already present and reused.</param>
/// <param name="KeptLinkCount">Resolvable shared links retained as masters instead of internalized.</param>
/// <param name="CopiedFields">Appearance fields assigned in apply mode.</param>
/// <param name="Stripped">Foreign non-appearance links removed in clone mode.</param>
/// <param name="Masters">Master filenames read back from the committed patch.</param>
/// <param name="DonorAmongMasters">Whether a donor plugin unexpectedly remains a master.</param>
/// <param name="DonorIsBaseGame">
/// Whether the donor set is empty because the source is an implicit or base-game master.
/// </param>
/// <param name="HarvestedAssetPaths">Asset paths found in new in-memory records before serialization.</param>
/// <param name="Assets">Later file-carry result, or null before the service appends that phase.</param>
/// <param name="Bytes">Committed plugin size, or zero when unavailable.</param>
/// <param name="Warning">Post-commit verification caveat; never a pre-commit refusal.</param>
public sealed record NpcCopyOutcome(
    bool Success,
    string? Error,
    string Mode,
    FormKey DonorKey,
    string DonorReadFrom,
    bool DonorOutOfLoadOrder,
    FormKey NewNpcKey,
    string? OutPath,
    bool Extended,
    IReadOnlyList<InternalizedRecord> Internalized,
    IReadOnlyList<string> Reused,
    int KeptLinkCount,
    IReadOnlyList<string> CopiedFields,
    IReadOnlyList<NpcAppearanceCopy.StripReport> Stripped,
    IReadOnlyList<string> Masters,
    bool DonorAmongMasters,
    bool DonorIsBaseGame,
    IReadOnlyList<string> HarvestedAssetPaths,
    NpcAssetOutcome? Assets,
    long Bytes,
    string? Warning)
{
    /// <summary>Creates a uniform pre-commit failure result.</summary>
    /// <param name="error">Actionable refusal or failure text.</param>
    /// <returns>An unsuccessful result with empty accounting and no output path.</returns>
    public static NpcCopyOutcome Fail(string error) => new(
        false, error, "", default, "", false, default, null, false,
        Array.Empty<InternalizedRecord>(), Array.Empty<string>(), 0, Array.Empty<string>(),
        Array.Empty<NpcAppearanceCopy.StripReport>(), Array.Empty<string>(), false, false,
        Array.Empty<string>(), null, 0, null);
}

/// <summary>Builds the record half of a standalone NPC appearance copy.</summary>
/// <remarks>
/// Donor-defined or otherwise unresolved appearance dependencies are duplicated with Mutagen and remapped to new
/// output keys. Shared active dependencies remain links. Clone mode then strips all remaining foreign links; apply
/// mode copies only appearance fields onto an existing NPC. Required foreign links cause a loud pre-write refusal.
/// Asset files are handled separately by <see cref="NpcAppearanceAssets"/>.
/// </remarks>
public static class NpcAppearanceCopy
{
    /// <summary>
    /// Maximum records in an appearance closure before the walk is treated as a runaway dependency graph.
    /// </summary>
    public const int ClosureCap = 128;

    /// <summary>Fetches a record body from the donor's read universe.</summary>
    /// <param name="fk">FormKey requested by the appearance closure.</param>
    /// <returns>The donor body, or null when it cannot be produced.</returns>
    public delegate IMajorRecordGetter? DonorFetch(FormKey fk);

    /// <summary>Tests whether a FormKey resolves in the active load order.</summary>
    /// <param name="fk">FormKey referenced by the donor graph.</param>
    /// <returns>True when the output can safely retain the link as a normal master dependency.</returns>
    public delegate bool ActiveResolve(FormKey fk);

    /// <summary>Describes a donor body selected for duplication.</summary>
    /// <param name="Body">Original donor record.</param>
    /// <param name="PulledBy">Parent record or NPC field that reached it.</param>
    public sealed record ClosureItem(IMajorRecordGetter Body, string PulledBy);

    /// <summary>Reports the bounded appearance-closure walk.</summary>
    /// <param name="Success">Whether a complete safe closure was collected.</param>
    /// <param name="Error">Named refusal; null on success.</param>
    /// <param name="ToInternalize">Donor records that must be duplicated.</param>
    /// <param name="KeptLinks">Shared active dependencies that remain links.</param>
    /// <param name="FetchMiss">
    /// Whether failure specifically means the donor reader could not supply a required body.
    /// </param>
    public sealed record ClosureResult(
        bool Success, string? Error,
        IReadOnlyList<ClosureItem> ToInternalize,
        IReadOnlyList<FormKey> KeptLinks,
        bool FetchMiss = false)
    {
        /// <summary>Creates a failed closure result with empty collections.</summary>
        /// <param name="error">Actionable refusal text.</param>
        /// <param name="fetchMiss">Whether donor read context can explain the failure.</param>
        /// <returns>A failed result.</returns>
        public static ClosureResult Fail(string error, bool fetchMiss = false)
            => new(false, error, Array.Empty<ClosureItem>(), Array.Empty<FormKey>(), fetchMiss);
    }

    /// <summary>Collects all records needed to reproduce the donor's appearance.</summary>
    /// <param name="donor">Source NPC body.</param>
    /// <param name="donorMods">Plugins being removed as standalone dependencies.</param>
    /// <param name="fetch">Reader for donor-universe record bodies.</param>
    /// <param name="resolvesActively">Active-order resolution test for shared dependencies.</param>
    /// <returns>A complete internalize/retain partition or a named refusal.</returns>
    /// <remarks>
    /// Seeds are HeadParts, HairColor, HeadTexture, and WornArmor. Expansion uses Mutagen form-link enumeration,
    /// not a per-type field list. Templated traits, donor-local custom races, missing bodies, and a closure above
    /// <see cref="ClosureCap"/> are refused before writing.
    /// </remarks>
    public static ClosureResult CollectAppearanceClosure(
        INpcGetter donor, IReadOnlySet<ModKey> donorMods, DonorFetch fetch, ActiveResolve resolvesActively)
    {
        // A donor that INHERITS its traits from a template (TemplateFlags.Traits) has EMPTY appearance fields on its
        // own record — the look lives on the template. Copying "nothing" would succeed and, in the apply lane,
        // actively wipe the target's face. Refuse with the real remedy.
        if (donor.Template is { IsNull: false } tpl
            && donor.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits))
            return ClosureResult.Fail(
                $"the donor NPC inherits its TRAITS from a template ({tpl.FormKey}) — its own record carries no " +
                "appearance to copy, and copying empty fields would blank the target's face. Pass the TEMPLATE " +
                "NPC as the donor instead (resolve the Template chain to the record that actually carries the look). " +
                "Nothing was written.");

        var seeds = new List<(FormKey Key, string Field)>();
        foreach (var hp in donor.HeadParts)
            if (!hp.FormKey.IsNull) seeds.Add((hp.FormKey, "HeadParts"));
        if (donor.HairColor is { IsNull: false } hc) seeds.Add((hc.FormKey, "HairColor"));
        if (donor.HeadTexture is { IsNull: false } ht) seeds.Add((ht.FormKey, "HeadTexture"));
        if (donor.WornArmor is { IsNull: false } wa) seeds.Add((wa.FormKey, "WornArmor"));

        // The RACE is a link-bearing appearance-adjacent field, but a race is NOT an internalizable subtree (it pulls
        // skeletons/body meshes/other races — the runaway the cap exists for). Donor-internal race → refuse UP FRONT
        // with the real remedy, not a cap message.
        if (donor.Race is { IsNull: false } race &&
            (donorMods.Contains(race.FormKey.ModKey) || !resolvesActively(race.FormKey)))
            return ClosureResult.Fail(
                $"the donor NPC's Race ({race.FormKey}) is donor-defined or absent from the active load order. " +
                "Standalone-copying a custom RACE is out of scope because it pulls skeletons, body meshes, and " +
                "sibling races. Keep the race mod installed and active as a master, or " +
                "choose a donor on a standard race. Nothing was written.");

        var toInternalize = new List<ClosureItem>();
        var kept = new List<FormKey>();
        var seen = new HashSet<FormKey>();
        var queue = new Queue<(FormKey Key, string PulledBy)>();
        foreach (var (key, field) in seeds) queue.Enqueue((key, $"Npc.{field}"));

        while (queue.Count > 0)
        {
            var (key, pulledBy) = queue.Dequeue();
            if (key.IsNull || !seen.Add(key)) continue;

            bool internalize = donorMods.Contains(key.ModKey) || !resolvesActively(key);
            if (!internalize) { kept.Add(key); continue; }

            if (toInternalize.Count >= ClosureCap)
                return ClosureResult.Fail(
                    $"the appearance closure exceeded {ClosureCap} records (last pull: {key} via {pulledBy}) — " +
                    "a real appearance subtree is typically well under 30 records, so this is a runaway donor " +
                    "dependency walk. Refusing instead of truncating. Nothing was written.");

            var body = fetch(key);
            if (body is null)
                return ClosureResult.Fail(
                    $"the donor appearance references {key} via {pulledBy}; it must be internalized because it is " +
                    "donor-defined or absent from the active order, but the donor reader cannot produce it. It is " +
                    $"defined in '{key.ModKey.FileName}': if that mod is disabled, enable it or keep it " +
                    "installed as a master) and re-run. Nothing was written.", fetchMiss: true);

            toInternalize.Add(new ClosureItem(body, pulledBy));
            var label = $"{RecordNaming.StripOverlay(body.GetType().Name)} {key} ({body.EditorID ?? "<no editorid>"})";
            if (body is IFormLinkContainerGetter flc)
                foreach (var link in flc.EnumerateFormLinks())
                    if (!link.FormKey.IsNull && !seen.Contains(link.FormKey))
                        queue.Enqueue((link.FormKey, label));
        }

        return new ClosureResult(true, null, toInternalize, kept);
    }

    // ======================================================================
    //  CLONE-MODE STRIP — remove every remaining foreign link, loudly
    // ======================================================================

    /// <summary>Reports one foreign reference removed from a standalone clone.</summary>
    /// <param name="Field">Property or indexed element from which the link was removed.</param>
    /// <param name="Removed">FormKey or key list removed.</param>
    public sealed record StripReport(string Field, string Removed);

    /// <summary>Reports clone-mode foreign-link removal.</summary>
    /// <param name="Success">Whether every foreign link was safely removed.</param>
    /// <param name="Error">Named required-link refusal; null on success.</param>
    /// <param name="Stripped">Every optional property or list entry removed.</param>
    public sealed record StripResult(bool Success, string? Error, IReadOnlyList<StripReport> Stripped)
    {
        /// <summary>Creates a failed strip result with no partial report.</summary>
        /// <param name="error">Actionable required-link refusal.</param>
        /// <returns>A failed result.</returns>
        public static StripResult Fail(string error) => new(false, error, Array.Empty<StripReport>());
    }

    /// <summary>Removes every safely removable foreign link from a cloned NPC.</summary>
    /// <param name="record">Mutable cloned record after appearance remapping.</param>
    /// <param name="isForeign">Predicate identifying donor-owned or unresolved keys.</param>
    /// <returns>Every removed link, or a refusal when a required field cannot be cleared.</returns>
    /// <remarks>
    /// Nullable links are nulled; foreign list entries are removed; optional link-bearing substructures are cleared.
    /// A required foreign link is never silently nulled because that would invent invalid record data.
    /// </remarks>
    public static StripResult StripForeignLinks(IMajorRecord record, Func<FormKey, bool> isForeign)
    {
        var stripped = new List<StripReport>();
        foreach (var prop in record.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length != 0 || prop.GetGetMethod() is null) continue;
            if (prop.Name is "FormKey" or "EditorID") continue;
            object? val;
            try { val = prop.GetValue(record); } catch { continue; }
            if (val is null) continue;

            // 1. A single FormLink property (nullable or required).
            if (val is IFormLinkGetter singleLink)
            {
                if (singleLink.FormKeyNullable is not { } fk || fk.IsNull || !isForeign(fk)) continue;
                if (TrySetLinkNull(val))
                    stripped.Add(new StripReport(prop.Name, fk.ToString()));
                else
                    return StripResult.Fail(
                        $"the clone's REQUIRED field '{prop.Name}' points at donor-internal or unresolved {fk} and " +
                        "cannot be nulled or removed — stripping it would invent data; keeping it would master " +
                        "the donor. Use the apply lane instead: scaffold your own NPC (with your own " +
                        $"{prop.Name}) and copy the donor's appearance onto it. Nothing was written.");
                continue;
            }

            // 2. A list — of FormLinks directly, or of link-bearing elements.
            if (val is IList list && val is not string)
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var el = list[i];
                    if (el is IFormLinkGetter elLink)
                    {
                        if (elLink.FormKeyNullable is { } lk && !lk.IsNull && isForeign(lk))
                        { list.RemoveAt(i); stripped.Add(new StripReport($"{prop.Name}[{i}]", lk.ToString())); }
                    }
                    else if (el is IFormLinkContainerGetter elc)
                    {
                        var foreignKeys = elc.EnumerateFormLinks()
                            .Where(l => !l.FormKey.IsNull && isForeign(l.FormKey))
                            .Select(l => l.FormKey.ToString()).Distinct().ToList();
                        if (foreignKeys.Count > 0)
                        {
                            list.RemoveAt(i);
                            stripped.Add(new StripReport(
                                $"{prop.Name}[{i}]",
                                string.Join(", ", foreignKeys)));
                        }
                    }
                }
                continue;
            }

            // Clear an optional link-bearing substructure only when any contained link is foreign.
            if (val is IFormLinkContainerGetter sub)
            {
                var foreign = sub.EnumerateFormLinks()
                    .Where(l => !l.FormKey.IsNull && isForeign(l.FormKey))
                    .Select(l => l.FormKey.ToString()).Distinct().ToList();
                if (foreign.Count == 0) continue;
                if (prop.CanWrite)
                { prop.SetValue(record, null); stripped.Add(new StripReport(prop.Name, string.Join(", ", foreign))); }
                else
                    return StripResult.Fail(
                        $"the clone's field '{prop.Name}' carries donor-internal (or unresolvable) reference(s) " +
                        $"({string.Join(", ", foreign)}) and the property cannot be cleared. Use the apply lane " +
                        "(scaffold your own NPC, copy the appearance onto it). Nothing was written.");
            }
        }
        return new StripResult(true, null, stripped);
    }

    /// <summary>Clears a FormLink only when its Mutagen contract is genuinely nullable.</summary>
    /// <param name="link">Runtime FormLink value.</param>
    /// <returns>True when a nullable link was cleared; false for required or unsupported link shapes.</returns>
    /// <remarks>
    /// Method presence is insufficient because Mutagen also exposes <c>SetToNull</c> on required links. The generic
    /// <c>IFormLinkNullable&lt;T&gt;</c> interface is the authority.
    /// </remarks>
    static bool TrySetLinkNull(object link)
    {
        bool nullable = link.GetType().GetInterfaces().Any(i =>
            i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IFormLinkNullable<>));
        if (!nullable) return false;                                      // required by the record model — never nulled
        var m = link.GetType().GetMethod("SetToNull", Type.EmptyTypes);
        if (m is null) return false;
        m.Invoke(link, null);
        return true;
    }

    // ======================================================================
    // BUILD + WRITE: the service owns Amethyst output lanes; this type owns records and serialization.
    // ======================================================================

    /// <summary>Builds and commits the record portion of an apply-mode or clone-mode NPC copy.</summary>
    /// <param name="donorNpc">Source NPC body.</param>
    /// <param name="donorMods">Plugin keys that must not remain as standalone dependencies.</param>
    /// <param name="closure">Previously validated appearance dependency closure.</param>
    /// <param name="clone">True to allocate a new NPC; false to override an existing target.</param>
    /// <param name="newEditorid">Required new clone EditorID; ignored in apply mode.</param>
    /// <param name="newName">Optional display name for a clone.</param>
    /// <param name="targetFk">Existing NPC FormKey used in apply mode.</param>
    /// <param name="targetActiveBody">Active target body when it is not already defined in the output patch.</param>
    /// <param name="resolvesActively">Resolution test used to identify remaining foreign links.</param>
    /// <param name="outPath">Native output plugin path.</param>
    /// <param name="extend">Whether to load and extend an existing output plugin.</param>
    /// <param name="mastersFor">Provides known master bodies after any conflicting read session is released.</param>
    /// <param name="donorReadFrom">Human-readable donor provenance for the report.</param>
    /// <param name="donorOutOfLoadOrder">Whether the donor came from direct disk access.</param>
    /// <returns>A committed record result or a pre-commit refusal with no usable output.</returns>
    /// <remarks>
    /// New records use the patch's 0x800-and-above allocation space. Existing content-identical internalized
    /// records are reused by type and EditorID; collisions with different content are refused. Asset links are
    /// harvested before serialization, but file copying remains the service's later phase.
    /// </remarks>
    public static NpcCopyOutcome BuildAndWrite(
        INpcGetter donorNpc, IReadOnlySet<ModKey> donorMods, ClosureResult closure,
        bool clone, string? newEditorid, string? newName,
        FormKey targetFk, INpcGetter? targetActiveBody,
        ActiveResolve resolvesActively,
        string outPath, bool extend,
        Func<string, IReadOnlyList<ISkyrimModGetter>> mastersFor,
        string donorReadFrom, bool donorOutOfLoadOrder)
    {
        var patchFileName = Path.GetFileName(outPath);
        try
        {
            SkyrimMod patchMod;
            if (extend)
            {
                try
                {
                    patchMod = SkyrimMod.CreateFromBinary(outPath, SkyrimRelease.SkyrimSE);
                }
                catch (Exception ex)
                {
                    return NpcCopyOutcome.Fail(
                        $"could not open '{patchFileName}' to extend: {ex.Message}");
                }
            }
            else
            {
                var patchKey = new ModKey(
                    Path.GetFileNameWithoutExtension(outPath),
                    ModType.Plugin);
                patchMod = new SkyrimMod(patchKey, SkyrimRelease.SkyrimSE);
            }
            WriteEngine.EnsureFormIdFloor(patchMod);

            // ---- extend-lane DEDUPE (review finding): a PRIOR run may already have internalized this donor's
            //      records into the patch. Re-copying would put TWO records with the same preserved EditorID in one
            //      plugin — ambiguous for the engine's facegeom block-name mapping, the exact invariant EditorID
            //      preservation protects. Match by type + EditorID (both preserved by design) → REUSE the existing
            //      copy: map the donor key to it and skip the duplicate. Each reuse is reported.
            var toCopy = new List<IMajorRecordGetter>();
            var dict = new Dictionary<FormKey, FormKey>();
            var reused = new List<string>();
            if (extend)
            {
                // Group patch-originating records by type + EditorID — OrdinalIgnoreCase (matching the clone
                // dup-check and the create-path upsert; review finding: an ordinal compare re-copied a case-differing
                // prior copy, minting the exact duplicate this dedupe prevents) and DUPLICATE-TOLERANT (same-named
                // records exist in the wild per the nested-create re-run semantics; a raw ToDictionary threw).
                var groups = new Dictionary<string, List<IMajorRecordGetter>>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in patchMod.EnumerateMajorRecords())
                {
                    if (r.FormKey.ModKey != patchMod.ModKey || string.IsNullOrEmpty(r.EditorID)) continue;
                    var key = RecordNaming.StripOverlay(r.GetType().Name) + "\0" + r.EditorID;
                    if (!groups.TryGetValue(key, out var l)) groups[key] = l = new List<IMajorRecordGetter>();
                    l.Add(r);
                }
                // Pass 1 — pair up name matches; the candidate map must be COMPLETE before any content check,
                // because matched records reference each other (a headpart → its texture set).
                var matches = new List<(ClosureItem Item, IMajorRecordGetter Existing)>();
                var candDict = new Dictionary<FormKey, FormKey>();
                foreach (var i in closure.ToInternalize)
                {
                    var type = RecordNaming.StripOverlay(i.Body.GetType().Name);
                    var edid = i.Body.EditorID;
                    if (string.IsNullOrEmpty(edid) || !groups.TryGetValue(type + "\0" + edid, out var cands))
                    { toCopy.Add(i.Body); continue; }
                    if (cands.Count > 1)
                        return NpcCopyOutcome.Fail(
                            $"'{patchFileName}' already contains {cands.Count} {type} records with EditorID " +
                            $"'{edid}' — matching this donor record is ambiguous, and re-copying would add another. " +
                            "Clean the duplicates up or copy into a different patch. Nothing was written.");
                    matches.Add((i, cands[0]));
                    candDict[i.Body.FormKey] = cands[0].FormKey;
                }
                // Pass 2 — CONTENT equality (review finding): a prior copy is reused only when it IS this donor's
                // record. Same-named but DIFFERENT records are a real load-order fact (KS Hairdos-derived NPCs reuse
                // headpart EditorIDs across mods) — reusing one would silently wire this NPC to the OTHER donor's
                // part; re-copying would duplicate the EditorID and break facegeom block-name identity. So: refuse
                // loud, naming the collision. Equality is judged on a probe duplicate under the existing key with
                // sibling links mapped to their candidates — i.e. "would copying produce exactly this record?"
                foreach (var (item, existing) in matches)
                {
                    var probe = item.Body.Duplicate(existing.FormKey);
                    probe.RemapLinks(candDict);
                    if (!probe.Equals(existing))
                        return NpcCopyOutcome.Fail(
                            $"'{patchFileName}' already contains " +
                            $"{RecordNaming.StripOverlay(existing.GetType().Name)} '{existing.EditorID}' " +
                            $"({existing.FormKey}) from a previous copy, but its CONTENT differs from donor record " +
                            $"{item.Body.FormKey}. Reusing it would select the wrong donor record; re-copying would " +
                            "duplicate the EditorID and " +
                            "break the facegeom block-name mapping. Copy into a DIFFERENT patch. Nothing was written.");
                    dict[item.Body.FormKey] = existing.FormKey;
                    reused.Add(
                        $"{RecordNaming.StripOverlay(existing.GetType().Name)} '{existing.EditorID}'  " +
                        $"{item.Body.FormKey} → {existing.FormKey} " +
                        "(already in the patch, content-identical — reused, not re-copied)");
                }
            }
            else toCopy.AddRange(closure.ToInternalize.Select(i => i.Body));
            if (clone)
            {
                // a clone re-run into the same patch would mint a SECOND NPC with the same EditorID — refuse with the
                // real choice instead (the dedupe above deliberately covers only the appearance subtree).
                bool cloneAlreadyExists = extend && patchMod.Npcs.Any(n =>
                    string.Equals(
                        n.EditorID,
                        newEditorid!.Trim(),
                        StringComparison.OrdinalIgnoreCase));
                if (cloneAlreadyExists)
                    return NpcCopyOutcome.Fail(
                        $"'{patchFileName}' already contains an NPC with EditorID '{newEditorid!.Trim()}' — " +
                        "cloning again would duplicate it. Pick a different new_editorid, or target the existing " +
                        "NPC via target_formid=.");
                toCopy.Add(donorNpc);
            }

            // ---- allocate NEW keys (the patch's own 0x800+ counter) for what actually gets copied ----
            uint next = patchMod.ModHeader.Stats.NextFormID;
            foreach (var rec in toCopy)
            {
                if (FormIdRange.ObjectIdSpaceExhausted(next))
                    return NpcCopyOutcome.Fail(
                        "cannot allocate a new FormID: the patch's NextObjectID counter is past " +
                        $"0x{FormIdRange.ObjectIdMax:X}.");
                dict[rec.FormKey] = new FormKey(patchMod.ModKey, next++);
            }
            patchMod.ModHeader.Stats.NextFormID = next;

            // ---- duplicate + remap in a SCRATCH mod (review finding): RenumberRecordsInto's whole-mod RemapLinks
            //      honors its fresh-target contract there, so an EXTENDED patch's PRE-EXISTING records are never
            //      silently repointed (a user record deliberately referencing the active donor stays untouched).
            //      The scratch shares the patch's ModKey, so the allocated keys are final; the remapped copies are
            //      then transplanted into the real patch. EditorIDs preserved by Duplicate (block-name identity).
            var scratch = new SkyrimMod(patchMod.ModKey, SkyrimRelease.SkyrimSE);
            var ren = RemapEngine.RenumberRecordsInto(scratch, toCopy, dict);
            if (!ren.Success) return NpcCopyOutcome.Fail(ren.Error!);
            foreach (var rec in scratch.EnumerateMajorRecords())
                if (!RemapEngine.TryAddToFlatGroup(patchMod, (IMajorRecord)rec))
                    return NpcCopyOutcome.Fail(
                        $"{RecordNaming.StripOverlay(rec.GetType().Name)} {rec.FormKey} could not be transplanted " +
                        "into the patch due to an engine inconsistency. Nothing usable was written.");

            // Build the report before serialization releases any session-backed donor overlays.
            var internalized = closure.ToInternalize
                .Where(i => dict.ContainsKey(i.Body.FormKey) && toCopy.Any(t => t.FormKey == i.Body.FormKey))
                .Select(i => new InternalizedRecord(
                    RecordNaming.StripOverlay(i.Body.GetType().Name), i.Body.EditorID ?? "<no editorid>",
                    i.Body.FormKey, dict[i.Body.FormKey], i.PulledBy))
                .ToList();

            FormKey newNpcKey;
            var copiedFields = (IReadOnlyList<string>)Array.Empty<string>();
            var strippedReports = (IReadOnlyList<StripReport>)Array.Empty<StripReport>();
            Func<FormKey, bool> isForeign = fk2 =>
                donorMods.Contains(fk2.ModKey) || (fk2.ModKey != patchMod.ModKey && !resolvesActively(fk2));

            if (clone)
            {
                newNpcKey = dict[donorNpc.FormKey];
                var cloneNpc = patchMod.Npcs.FirstOrDefault(n => n.FormKey == newNpcKey)
                    ?? throw new InvalidOperationException(
                        "the clone vanished from the patch after renumber (engine inconsistency).");
                cloneNpc.EditorID = newEditorid!.Trim();
                if (!string.IsNullOrWhiteSpace(newName)) cloneNpc.Name = newName.Trim();

                // strip every remaining donor-internal / unresolvable link — each named, or a loud refusal.
                var strip = StripForeignLinks(cloneNpc, isForeign);
                if (!strip.Success) return NpcCopyOutcome.Fail(strip.Error!);
                strippedReports = strip.Stripped;
            }
            else
            {
                // apply lane: the target NPC — an active winner the service fetched, or (not-yet-enabled patch) the
                // extend target's own record.
                Npc targetOverride;
                if (targetFk.ModKey == patchMod.ModKey)
                {
                    targetOverride = patchMod.Npcs.FirstOrDefault(n => n.FormKey == targetFk)
                        ?? throw new InvalidOperationException(
                            $"{targetFk} names this patch, but '{patchFileName}' defines no such NPC. " +
                            "Create the NPC first with housecarl_create_record or pass an active NPC FormID.");
                }
                else if (targetActiveBody is not null)
                {
                    targetOverride = (Npc)WriteEngine.GenericGetOrAddAsOverride(patchMod, targetActiveBody);
                }
                else
                    return NpcCopyOutcome.Fail(
                        "apply lane reached the build without a target body (engine inconsistency).");

                newNpcKey = targetFk;
                copiedFields = CopyAppearanceFields(donorNpc, targetOverride);
                // Repoint only the new override, never unrelated records in an extended patch.
                targetOverride.RemapLinks(dict);

                // Only donor keys are forbidden here. An unrelated pre-existing dangling target link remains outside
                // this operation's responsibility.
                var leak = ((IFormLinkContainerGetter)targetOverride).EnumerateFormLinks()
                    .FirstOrDefault(l => !l.FormKey.IsNull && donorMods.Contains(l.FormKey.ModKey));
                if (leak is not null && !leak.FormKey.IsNull)
                    return NpcCopyOutcome.Fail(
                        $"after the copy the target still references {leak.FormKey} in the donor plugin — " +
                        "refusing to write a patch that would master the donor. If a non-appearance field " +
                        "deliberately references the donor, remove it first or clone instead. Nothing was written.");
            }

            // ---- HARVEST asset paths from the IN-PATCH duplicates, pre-serialize (they are plain in-memory records;
            //      the donor-overlay bodies may be released before the service's asset carry runs — review finding).
            var newKeys = internalized.Select(i => i.NewKey).ToHashSet();
            var duplicates = patchMod.EnumerateMajorRecords()
                .Where(r => newKeys.Contains(r.FormKey))
                .Cast<IMajorRecordGetter>()
                .ToList();
            var harvested = NpcAppearanceAssets.HarvestAssetPaths(duplicates);

            // ---- serialize (multi-master; the caller's mastersFor handles the active-patch self-lock) ----
            try
            {
                WriteEngine.WritePatch(patchMod, mastersFor(patchFileName), outPath);
            }
            catch (Exception ex)
            {
                return NpcCopyOutcome.Fail(
                    $"serialize failed — {WriteEngine.Describe(ex)}. Nothing usable was written.");
            }

            // ---- post-commit read-back — the patch IS on disk from here; a read-back failure is a WARNING on a
            //      success, never a "nothing was written" (review finding: that mislabel invites a duplicate re-run).
            var masters = new List<string>();
            long bytes = 0;
            bool donorAmongMasters = false;
            string? warning = null;
            try
            {
                var back = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE);
                try
                {
                    masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
                    bytes = new FileInfo(outPath).Length;
                }
                finally { (back as IDisposable)?.Dispose(); }
                donorAmongMasters = masters.Any(m =>
                {
                    try { return donorMods.Contains(ModKey.FromFileName(m)); }
                    catch { return false; }
                });
            }
            catch (Exception ex)
            {
                warning = $"the patch was written, but post-write read-back failed ({ex.Message}) — the masters " +
                          "could not be verified. Do not re-run blindly because that may create a duplicate; " +
                          "inspect the plugin with housecarl_read_plugin_file.";
            }

            return new NpcCopyOutcome(
                true, null, clone ? "clone" : "apply",
                donorNpc.FormKey, donorReadFrom, donorOutOfLoadOrder,
                newNpcKey, outPath, extend,
                internalized, reused, closure.KeptLinks.Count,
                copiedFields, strippedReports, masters, donorAmongMasters, donorMods.Count == 0,
                harvested, null, bytes, warning);
        }
        catch (Exception ex)
        {
            return NpcCopyOutcome.Fail($"copy failed — {WriteEngine.Describe(ex)}. Nothing usable was written.");
        }
    }

    // ======================================================================
    //  APPLY-MODE FIELD COPY — donor appearance onto an EXISTING target NPC
    // ======================================================================

    /// <summary>Copies every appearance-bearing field from a donor onto an existing NPC override.</summary>
    /// <param name="donor">Source appearance body.</param>
    /// <param name="target">Mutable target override already placed in the output patch.</param>
    /// <returns>Human-readable names of fields copied or reconciled.</returns>
    /// <remarks>
    /// Complex values are deep-copied and links retain donor keys until the caller's remap pass. Race and gender
    /// are reconciled because FaceGen, headparts, and tint data are fitted to them.
    /// </remarks>
    public static IReadOnlyList<string> CopyAppearanceFields(INpcGetter donor, Npc target)
    {
        var copied = new List<string>();

        target.HeadParts.SetTo(donor.HeadParts.Select(l => new FormLink<IHeadPartGetter>(l.FormKey)));
        copied.Add($"HeadParts ({donor.HeadParts.Count})");

        target.HairColor.SetTo(donor.HairColor?.FormKeyNullable);
        copied.Add("HairColor");
        target.HeadTexture.SetTo(donor.HeadTexture?.FormKeyNullable);
        copied.Add("HeadTexture");
        target.WornArmor.SetTo(donor.WornArmor?.FormKeyNullable);
        copied.Add("WornArmor");

        target.FaceMorph = donor.FaceMorph?.DeepCopy();
        copied.Add("FaceMorph");
        target.FaceParts = donor.FaceParts?.DeepCopy();
        copied.Add("FaceParts");

        target.TintLayers.SetTo(donor.TintLayers.Select(t => t.DeepCopy()));
        copied.Add($"TintLayers ({donor.TintLayers.Count})");

        target.TextureLighting = donor.TextureLighting;
        copied.Add("TextureLighting");

        target.Weight = donor.Weight;
        target.Height = donor.Height;
        copied.Add("Weight/Height");

        if (target.Race.FormKey != donor.Race.FormKey)
        {
            target.Race.SetTo(donor.Race.FormKey);
            copied.Add($"Race (target's differed — copied {donor.Race.FormKey}; facegen is race-fitted)");
        }

        // GENDER (review finding): headparts, tint masks and the baked facegen are all gender-fitted — a female
        // donor's look on a male-flagged target hands the engine female headparts under a male body/skeleton (the
        // wrong-head/dark-face class this verb exists to kill). Match the Female flag to the donor, loudly.
        bool donorFemale = donor.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);
        bool targetFemale = target.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);
        if (donorFemale != targetFemale)
        {
            if (donorFemale) target.Configuration.Flags |= NpcConfiguration.Flag.Female;
            else target.Configuration.Flags &= ~NpcConfiguration.Flag.Female;
            copied.Add(
                "Configuration.Flags.Female " +
                "(target gender differed — matched to donor; headparts and FaceGen are gender-fitted)");
        }

        return copied;
    }
}
