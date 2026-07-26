using System.Globalization;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Exceptions;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>Builds, edits, forwards, compacts, and merges Skyrim plugins through guarded write pipelines.</summary>
/// <remarks>
/// Patch operations resolve and validate every request before serialization, then write once and reopen the result.
/// In-place operations target staging files only and require consent in the service layer. All serializers use atomic
/// replacement so a failed write does not partially replace the prior plugin.
/// </remarks>
public static class WritePatchBuilder
{
    /// <summary>Describes one field operation against the resolved record identified by <see cref="Target"/>.</summary>
    /// <remarks>The record type is derived from the resolved record rather than supplied by the caller.</remarks>
    public sealed record PatchEdit
    {
        /// <summary>Gets the record FormKey to edit.</summary>
        public required FormKey Target { get; init; }

        /// <summary>Gets the field path within the target record.</summary>
        public required string[] Path { get; init; }

        /// <summary>Gets the write verb understood by <see cref="WriteEngine"/>.</summary>
        public required string Verb { get; init; }

        /// <summary>Gets the optional dictionary key or keyed-list selector.</summary>
        public string? Key { get; init; }

        /// <summary>Gets the optional scalar value.</summary>
        public string? Value { get; init; }

        /// <summary>Gets optional multiple scalar values.</summary>
        public string[]? Values { get; init; }

        /// <summary>Gets optional named structure entries.</summary>
        public Dictionary<string, string>? Entries { get; init; }

        /// <summary>Gets the optional single structured value.</summary>
        public StructSpec? Struct { get; init; }

        /// <summary>Gets optional structured values for batch add or replace operations.</summary>
        public IReadOnlyList<StructSpec>? Structs { get; init; }

        /// <summary>Gets the plugin whose field value supplies a <c>CopyFrom</c> operation.</summary>
        public string? FromPlugin { get; init; }
    }

    /// <summary>Reports one requested edit and its best-effort post-apply value.</summary>
    /// <param name="Target">Edited record FormKey.</param>
    /// <param name="RecordType">Resolved record type.</param>
    /// <param name="Label">Human-readable operation label.</param>
    /// <param name="Applied">Whether the operation was applied.</param>
    /// <param name="Error">Operation error, when present.</param>
    /// <param name="After">Best-effort rendered field value after the edit.</param>
    /// <param name="Landed">Compact value or list-count description used by verification output.</param>
    public sealed record OpResult(
        FormKey Target,
        string RecordType,
        string Label,
        bool Applied,
        string? Error,
        string? After,
        string? Landed = null);

    /// <summary>Reports a complete modeled record read from the written plugin.</summary>
    /// <param name="Target">Requested record FormKey.</param>
    /// <param name="Record">Rendered record fields, when found.</param>
    /// <param name="Error">Named readback failure, when the record could not be confirmed.</param>
    public sealed record FullReadback(FormKey Target, RecordFields? Record, string? Error);

    /// <summary>The call outcome. <see cref="Error"/> non-null ⇒ the whole call was refused (no patch written) with a
    /// named, recoverable reason (Q3). Otherwise the patch at <see cref="OutputPath"/> carries every op; <see cref="Masters"/>
    /// is its (lean, only-referenced) master header; <see cref="Extended"/> says whether an existing patch was grown;
    /// <see cref="ReadBack"/> is the opt-in full read-back of every record this call touched (null unless requested).</summary>
    public sealed record PatchOutcome(
        bool Success, string? Error, string OutputPath, bool Extended,
        IReadOnlyList<string> Masters, IReadOnlyList<OpResult> Ops, long Bytes)
    {
        /// <summary>Gets optional full-record verification results.</summary>
        public IReadOnlyList<FullReadback>? ReadBack { get; init; }

        /// <summary>True ⇒ this outcome came from the IN-PLACE lane (<see cref="Apply"/>'s sibling
        /// <see cref="ApplyInPlace"/>) — the edits landed in the USER's own file at <see cref="OutputPath"/>, not a new
        /// patch. Drives the distinct "edited in place" confirmation (and the "no undo; keep your own backup" note).</summary>
        public bool InPlace { get; init; }

        /// <summary>True ⇒ NOT a write and NOT an error: the server-enforced first-touch in-place CONSENT handshake. The
        /// in-place lane refused to write this plugin until the user acknowledges the trade-off; <see cref="Error"/>
        /// carries the prompt verbatim (re-call with acknowledge=true). Rendered as a confirmation prompt, never "error:"
        /// (Q3 — a required confirmation is not a failure). Nothing was written; the original is untouched.</summary>
        public bool NeedsAcknowledge { get; init; }

        /// <summary>An optional Q3 honesty note appended to a SUCCESSFUL outcome — a side effect that didn't land cleanly
        /// even though the write did (e.g. the in-place acknowledgement couldn't be persisted, or the editedInPlace audit
        /// marker couldn't be written). Null when there's nothing to add.</summary>
        public string? Note { get; init; }

        /// <summary>True ⇒ this Success came from a DRY RUN (#225): the REAL pipeline ran — winner resolve, pre-flight,
        /// every verb applied to the in-memory mod, the reference-resolution check — and STOPPED at the point of no
        /// return (the Phase-4 serialize), so NOTHING was written (no file, no folder). <see cref="Ops"/> carries what
        /// WOULD change; <see cref="Masters"/> is the EXPECTED master set (link-derived preview — the real write derives
        /// its own lean header); <see cref="Bytes"/> is 0; <see cref="ReadBack"/> (if requested) is read from the
        /// in-memory mod, not a file. Drives the distinct "DRY RUN — nothing written" confirmation.</summary>
        public bool DryRun { get; init; }

        /// <summary>Creates an unsuccessful outcome without any partial result.</summary>
        /// <param name="error">Actionable refusal or failure text.</param>
        /// <returns>The unsuccessful outcome.</returns>
        public static PatchOutcome Fail(string error) =>
            new(false, error, "", false, Array.Empty<string>(), Array.Empty<OpResult>(), 0);

        /// <summary>The first-touch in-place consent handshake: no write, no error — a required confirmation carrying the
        /// trade-off <paramref name="prompt"/> (the caller re-calls with acknowledge=true). Success=false so no
        /// downstream success path runs; <see cref="NeedsAcknowledge"/> tells the renderer to show it as a prompt.</summary>
        public static PatchOutcome NeedsAck(string prompt) =>
            new(false, prompt, "", false, Array.Empty<string>(), Array.Empty<OpResult>(), 0) { NeedsAcknowledge = true };
    }

    /// <summary>Identifies one record removed from a plugin.</summary>
    /// <param name="Target">Removed record FormKey.</param>
    /// <param name="RecordType">Catalog record type.</param>
    /// <param name="EditorId">Editor ID, when present.</param>
    public sealed record RemovedRecord(FormKey Target, string RecordType, string? EditorId);

    /// <summary>The outcome of a <see cref="RemoveRecords"/> call. <see cref="Error"/> non-null ⇒ the whole call was
    /// refused (no file written) with a named, recoverable reason (Q3 — e.g. a target the patch doesn't carry).
    /// Otherwise <see cref="Removed"/> lists every dropped record; <see cref="Masters"/> is the patch's now-lean header
    /// (a master orphaned by the removal is gone); <see cref="RemainingRecords"/>=0 means the patch is now inert.</summary>
    public sealed record RemovalOutcome(
        bool Success, string? Error, string OutputPath,
        IReadOnlyList<RemovedRecord> Removed, IReadOnlyList<string> Masters, int RemainingRecords, long Bytes)
    {
        /// <summary>True ⇒ this outcome came from the IN-PLACE remove lane (<see cref="RemoveRecords"/>'s sibling
        /// <see cref="RemoveRecordsInPlace"/>) — the records were dropped from the USER's own file at
        /// <see cref="OutputPath"/>, not a houseCARL patch. Drives the distinct "removed in place" confirmation (and the
        /// "no undo; keep your own backup" note). Mirrors <see cref="PatchOutcome.InPlace"/>.</summary>
        public bool InPlace { get; init; }

        /// <summary>True ⇒ NOT a write and NOT an error: the server-enforced first-touch in-place CONSENT handshake. The
        /// in-place lane refused to write this plugin until the user acknowledges the trade-off; <see cref="Error"/>
        /// carries the prompt verbatim (re-call with acknowledge=true). Rendered as a confirmation prompt, never "error:"
        /// (Q3 — a required confirmation is not a failure). Nothing was written; the original is untouched.</summary>
        public bool NeedsAcknowledge { get; init; }

        /// <summary>An optional Q3 honesty note appended to a SUCCESSFUL outcome — a side effect that didn't land cleanly
        /// even though the removal did (e.g. the in-place acknowledgement couldn't be persisted, or the editedInPlace
        /// audit marker couldn't be written). Null when there's nothing to add.</summary>
        public string? Note { get; init; }

        /// <summary>Creates an unsuccessful removal outcome.</summary>
        /// <param name="error">Actionable refusal or failure text.</param>
        /// <returns>The unsuccessful outcome.</returns>
        public static RemovalOutcome Fail(string error) =>
            new(false, error, "", Array.Empty<RemovedRecord>(), Array.Empty<string>(), 0, 0);

        /// <summary>The first-touch in-place consent handshake: no write, no error — a required confirmation carrying the
        /// trade-off <paramref name="prompt"/> (the caller re-calls with acknowledge=true). Success=false so no
        /// downstream success path runs; <see cref="NeedsAcknowledge"/> tells the renderer to show it as a prompt.</summary>
        public static RemovalOutcome NeedsAck(string prompt) =>
            new(false, prompt, "", Array.Empty<RemovedRecord>(), Array.Empty<string>(), 0, 0) { NeedsAcknowledge = true };
    }

    /// <summary>One brand-new record to create: its (caller-DECLARED) <see cref="RecordType"/> catalog name, the required
    /// <see cref="EditorId"/> it'll be referenced by, and the field <see cref="Edits"/> to apply to it (each a
    /// <see cref="WriteRequest"/> rooted at the create type — the same shape <see cref="WriteEngine.ApplyVerb"/> consumes).
    /// Unlike <see cref="PatchEdit"/>, RecordType is declared, not derived — there's no existing winner to read it from.</summary>
    public sealed record CreateSpec
    {
        /// <summary>Gets the catalog type of the record to create.</summary>
        public required string RecordType { get; init; }

        /// <summary>Gets the required Editor ID for the new record.</summary>
        public required string EditorId { get; init; }

        /// <summary>Gets the field operations applied to the new record.</summary>
        public required IReadOnlyList<WriteRequest> Edits { get; init; }

        /// <summary>Optional — the PARENT this record nests UNDER (nested-create, Layer A). Either an EXISTING parent's
        /// FormKey (<c>XXXXXX:Plugin.esp</c> — add a line to an existing topic, a ref to an existing cell) OR the
        /// <see cref="EditorId"/> of a record created EARLIER in this same call (the one-shot "topic + its lines" unit).
        /// A FormKey is recognised by parsing; anything else is a same-call sibling EditorId. Null ⇒ a flat top-level
        /// record (the existing create path, unchanged).</summary>
        public string? ParentRef { get; init; }

        /// <summary>Optional — which of the parent's child-collections to add into, BY NAME (the outcome-(ii)
        /// discriminator, e.g. a Cell's <c>Persistent</c>/<c>Temporary</c>). Null ⇒ the unique collection that accepts
        /// this child type (outcome (i), e.g. a DialogTopic's one <c>Responses</c> list). Ignored when
        /// <see cref="ParentRef"/> is null.</summary>
        public string? IntoCollection { get; init; }

        /// <summary>Gets optional <c>X,Y</c> coordinates for a Cell nested under a Worldspace.</summary>
        /// <remarks>A parentless Cell without a grid is created as an interior cell.</remarks>
        public string? Grid { get; init; }
    }

    /// <summary>One record created by <see cref="CreateRecords"/> — its freshly-allocated <see cref="FormKey"/> (the
    /// caller can't predict it; it's the local 0x800+ id), its type + editorid, and the per-field op results.
    /// <see cref="ReplacedExisting"/> = this create REPLACED a record the patch already defined with the same
    /// editorid (an into= re-run): same FormKey, prior contents — including any set_field edits made since the
    /// original create — discarded and rebuilt from this call's spec. MUST be surfaced to the user (Q3 — a replace
    /// is never silent).</summary>
    public sealed record CreatedRecord(FormKey FormKey, string RecordType, string EditorId, IReadOnlyList<OpResult> Ops,
        bool ReplacedExisting = false);

    /// <summary>The outcome of a <see cref="CreateRecords"/> call. <see cref="Error"/> non-null ⇒ the whole call was
    /// refused (no file written) with a named, recoverable reason (Q3 — missing editorid, an un-createable type, a rejected
    /// edit). Otherwise <see cref="Created"/> lists every new record with its allocated FormKey; <see cref="Masters"/> is
    /// the patch's (lean, derived) header; <see cref="Extended"/> says whether an existing patch was grown;
    /// <see cref="ReadBack"/> is the opt-in full read-back of every record this call created (null unless requested).</summary>
    public sealed record CreateOutcome(
        bool Success, string? Error, string OutputPath, bool Extended,
        IReadOnlyList<CreatedRecord> Created, IReadOnlyList<string> Masters, long Bytes)
    {
        /// <summary>Gets optional full-record verification results.</summary>
        public IReadOnlyList<FullReadback>? ReadBack { get; init; }

        /// <summary>True ⇒ this outcome came from the IN-PLACE create lane (<see cref="CreateRecords"/>'s sibling
        /// <see cref="CreateRecordsInPlace"/>) — the new records were allocated into the USER's own file at
        /// <see cref="OutputPath"/>, not a new patch. Drives the distinct "created in place" confirmation (and the
        /// "no undo; keep your own backup" note). Mirrors <see cref="PatchOutcome.InPlace"/>.</summary>
        public bool InPlace { get; init; }

        /// <summary>True ⇒ NOT a write and NOT an error: the server-enforced first-touch in-place CONSENT handshake.
        /// The in-place create lane refused to write this plugin until the user acknowledges the trade-off;
        /// <see cref="Error"/> carries the prompt verbatim (re-call with acknowledge=true). Rendered as a confirmation
        /// prompt, never "error:" (Q3). Nothing was written; the original is untouched. Mirrors
        /// <see cref="PatchOutcome.NeedsAcknowledge"/>.</summary>
        public bool NeedsAcknowledge { get; init; }

        /// <summary>An optional Q3 honesty note appended to a SUCCESSFUL outcome — a side effect that didn't land cleanly
        /// even though the write did (e.g. the in-place acknowledgement couldn't be persisted, or the editedInPlace audit
        /// marker couldn't be written). Null when there's nothing to add. Mirrors <see cref="PatchOutcome.Note"/>.</summary>
        public string? Note { get; init; }

        /// <summary>The voice-coverage report for the INFOs this call created (Layer B unit B) — null unless the call
        /// created ≥1 dialogue line. Filled by the SERVICE post-write (it owns the live AssetResolver), NOT by the core
        /// create path: a `with { Voice = … }` enrich on the returned outcome, so <see cref="CreateRecords"/> stays a
        /// pure record-write and the asset-layer dependency lives in the service. See <see cref="VoiceCheck"/>.</summary>
        public VoiceReport? Voice { get; init; }

        /// <summary>The result-script binding report for the INFOs this call created (Layer B unit C / per-create
        /// structural check) — null unless the call created ≥1 scripted dialogue line. Filled by the SERVICE post-write
        /// the SAME way as <see cref="Voice"/> (it owns the live AssetResolver), so <see cref="CreateRecords"/> stays a
        /// pure record-write. See <see cref="DialogueScriptCheck"/>.</summary>
        public ScriptBindingReport? ScriptBinding { get; init; }

        /// <summary>Gets the service-supplied structural report for newly created cells.</summary>
        /// <remarks>Null when the call created no Cell records.</remarks>
        public CellShellReport? CellShell { get; init; }

        /// <summary>Creates an unsuccessful record-creation outcome.</summary>
        /// <param name="error">Actionable refusal or failure text.</param>
        /// <returns>The unsuccessful outcome.</returns>
        public static CreateOutcome Fail(string error) =>
            new(false, error, "", false, Array.Empty<CreatedRecord>(), Array.Empty<string>(), 0);

        /// <summary>The first-touch in-place consent handshake: no write, no error — a required confirmation carrying the
        /// trade-off <paramref name="prompt"/> (the caller re-calls with acknowledge=true). Success=false so no downstream
        /// success path runs; <see cref="NeedsAcknowledge"/> tells the renderer to show it as a prompt. Mirrors
        /// <see cref="PatchOutcome.NeedsAck"/>.</summary>
        public static CreateOutcome NeedsAck(string prompt) =>
            new(false, prompt, "", false, Array.Empty<CreatedRecord>(), Array.Empty<string>(), 0) { NeedsAcknowledge = true };
    }

    /// <summary>Maximum modeled depth used for full post-write record verification.</summary>
    public const int FullReadbackDepth = 16;

    /// <summary>Classifies a cell-create request by its required container.</summary>
    enum CellCreate { None, Exterior, Interior }

    /// <summary>Checks whether a catalog name denotes a Cell record.</summary>
    static bool IsCellType(string recordType) =>
        string.Equals(recordType, nameof(Cell), StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses a whitespace-tolerant exterior-cell grid in <c>X,Y</c> form.</summary>
    static bool TryParseGrid(string? grid, out int x, out int y)
    {
        x = y = 0;
        if (string.IsNullOrWhiteSpace(grid)) return false;
        var parts = grid.Split(',');
        return parts.Length == 2
            && int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out x)
            && int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out y);
    }

    /// <summary>Applies validated edits to a new or existing patch and atomically serializes the result.</summary>
    /// <remarks>
    /// The method uses one load-order snapshot for the call, rejects the entire batch on any invalid edit, and reopens
    /// the written plugin for verification. A dry run executes the same in-memory pipeline but stops before serialization.
    /// </remarks>
    public static PatchOutcome Apply(
        LoadOrderResolver resolver, CorpusRulebook rulebook,
        IReadOnlyList<PatchEdit> edits, string outPath, bool extend, bool fullReadback = false,
        IReadOnlyDictionary<PatchEdit, IMajorRecordGetter>? copyFromSources = null, bool dryRun = false)
    {
        if (edits.Count == 0) return PatchOutcome.Fail("no edits supplied.");

        // Keep every source overlay and link cache inside this call so no mapped file remains open after it returns.
        using var session = resolver.OpenSession();

        // Open an extended patch before resolution because the edit may target a record defined only by that patch.
        var fileName = Path.GetFileName(outPath);
        SkyrimMod patchMod;
        if (extend)
        {
            if (!File.Exists(outPath))
                return PatchOutcome.Fail($"cannot extend: no existing patch at {outPath}. Omit into= to create it fresh.");
            try { patchMod = SkyrimMod.CreateFromBinary(outPath, SkyrimRelease.SkyrimSE); }
            catch (Exception ex) { return PatchOutcome.Fail($"cannot open patch to extend ({fileName}): {ex.GetType().Name}: {ex.Message}"); }
        }
        else
        {
            patchMod = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(outPath), ModType.Plugin), SkyrimRelease.SkyrimSE);
        }
        if (!string.Equals(patchMod.ModKey.FileName.String, fileName, StringComparison.OrdinalIgnoreCase))
            return PatchOutcome.Fail($"patch ModKey '{patchMod.ModKey.FileName}' must match output filename '{fileName}'.");

        // Resolve and validate every edit against one captured view. Collect all failures before refusing the batch.
        // Records defined only by the extended patch resolve from that named artifact, not from unrelated inactive mods.
        var view = resolver.Capture();
        var resolved = new List<(PatchEdit edit, IMajorRecordGetter? body, string? winnerPlugin, IMajorRecord? patchLocal, WriteRequest req, string label, IMajorRecordGetter? srcBody)>(edits.Count);
        var problems = new List<string>();
        // Build the patch-owned record index lazily. Carried overrides still resolve through the active load order.
        Dictionary<FormKey, IMajorRecord>? patchDefined = null;
        foreach (var e in edits)
        {
            IMajorRecordGetter? body = null; string? winnerPlugin = null; IMajorRecord? patchLocal = null;
            var w = view.ResolveWinner(e.Target);
            if (w is not null)
            {
                body = view.GetRecord(session, w.Value.WinnerPlugin, e.Target);
                if (body is null) { problems.Add($"{e.Target}: winner '{w.Value.WinnerPlugin}' did not yield it on fetch (a load-order inconsistency)."); continue; }
                winnerPlugin = w.Value.WinnerPlugin;
            }
            else
            {
                if (extend)
                {
                    if (patchDefined is null)
                    {
                        patchDefined = new Dictionary<FormKey, IMajorRecord>();
                        foreach (var r in patchMod.EnumerateMajorRecords())
                            if (r.FormKey.ModKey == patchMod.ModKey) patchDefined.TryAdd(r.FormKey, r);
                    }
                    if (patchDefined.TryGetValue(e.Target, out var own)) patchLocal = own;
                }
                if (patchLocal is null)
                {
                    problems.Add($"{e.Target}: not present in the load order ({view.PluginCount} plugins)"
                        + (extend
                            ? $", and not a record '{fileName}' (the patch being extended) itself defines — a record " +
                              "the patch merely OVERRIDES resolves via the load order, so its defining plugin must be enabled."
                            : "."));
                    continue;
                }
            }

            // CopyFrom uses a service-resolved off-order source or the named version in this captured active order.
            IMajorRecordGetter? srcBody = null;
            if (string.Equals(e.Verb, "CopyFrom", StringComparison.Ordinal))
            {
                if (copyFromSources is not null && copyFromSources.TryGetValue(e, out var offSrc))
                    srcBody = offSrc;
                else if (string.IsNullOrWhiteSpace(e.FromPlugin))
                { problems.Add($"{e.Target}: CopyFrom is missing from_plugin (internal — the mapper should have caught this)."); continue; }
                else if (string.Equals(e.FromPlugin, fileName, StringComparison.OrdinalIgnoreCase))
                { problems.Add($"{e.Target}: CopyFrom from_plugin '{e.FromPlugin}' is the output patch itself — name the OTHER plugin whose version to copy from."); continue; }
                else if (!view.ContainsPlugin(e.FromPlugin))
                // Off-order discovery already ran in the service, so this branch means no usable file was found.
                { problems.Add($"{e.Target}: CopyFrom source '{e.FromPlugin}' is not in the load order (and no plugin file by that name was located on disk) — name an active plugin, or a plugin file present on disk."); continue; }
                else if (view.ExcludedPlugins.TryGetValue(e.FromPlugin, out var why))
                { problems.Add($"{e.Target}: CopyFrom source '{e.FromPlugin}' was excluded from this session ({why}) — its records aren't resolvable."); continue; }
                else
                {
                    srcBody = view.GetRecord(session, e.FromPlugin, e.Target);
                    if (srcBody is null)
                    { problems.Add($"{e.Target}: CopyFrom source '{e.FromPlugin}' is in the load order but does NOT define or override this record — there is no version of it there to copy."); continue; }
                }
            }

            var recType = RecordNaming.StripOverlay((patchLocal ?? (object)body!).GetType().Name);
            var req = new WriteRequest
            {
                RecordType = recType, Path = e.Path, Verb = e.Verb,
                Key = e.Key, Value = e.Value, Values = e.Values, Entries = e.Entries, Struct = e.Struct, Structs = e.Structs,
            };
            var label = Label(req);
            if (rulebook.Validate(req) is { } reject) { problems.Add($"{recType} {e.Target} [{label}]: {reject}"); continue; }
            resolved.Add((e, body, winnerPlugin, patchLocal, req, label, srcBody));
        }
        if (problems.Count > 0)
            return PatchOutcome.Fail(
                $"refused — {problems.Count} of {edits.Count} edit(s) rejected by resolve/pre-flight; NO patch written:\n  - "
                + string.Join("\n  - ", problems));

        // Copy winners into the patch and apply edits. Nested records obtain a source link cache only when required.
        var ops = new List<OpResult>(resolved.Count);
        foreach (var (e, body, winnerPlugin, patchLocal, req, label, srcBody) in resolved)
        {
            try
            {
                // A record defined by the extended patch is already mutable and needs no new override.
                IMajorRecord ov;
                if (patchLocal is not null) ov = patchLocal;
                else
                {
                    ILinkCache? cache = WriteEngine.RecordNeedsSourceCache(body!) ? session.LinkCacheFor(winnerPlugin!) : null;
                    ov = WriteEngine.GenericGetOrAddAsOverride(patchMod, body!, cache);
                }
                // CopyFrom transplants a source field; other verbs mutate the destination record directly.
                if (string.Equals(req.Verb, "CopyFrom", StringComparison.Ordinal))
                    WriteEngine.CopyField(srcBody!, ov, req.Path);
                else
                    WriteEngine.ApplyVerb(ov, req);
                var (after, landed) = DescribeApplied(ov, req);
                ops.Add(new OpResult(e.Target, req.RecordType, label, true, null, after, landed));
            }
            catch (ExpectedApplyRejectionException ex)
            {
                // Some live-state conflicts, such as duplicate keys, are knowable only during application.
                return PatchOutcome.Fail(
                    $"refused applying [{label}] to {req.RecordType} {e.Target} — {ex.Message} (no patch written)");
            }
            catch (MalformedTargetDataException ex)
            {
                // Distinguish malformed source data from both invalid user input and an engine inconsistency.
                return PatchOutcome.Fail(
                    $"refused applying [{label}] to {req.RecordType} {e.Target} — {ex.Message} (no patch written)");
            }
            catch (Exception ex)
            {
                return PatchOutcome.Fail(
                    $"engine error applying [{label}] to {req.RecordType} {e.Target}: pre-flight ACCEPTED it but the apply " +
                    $"threw — a real inconsistency, surfaced not swallowed (Q3): {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Keep the dialogue SNAM marker aligned when this call changes only the modeled subtype.
        if (SyncEditedTopicMarkers(patchMod, edits, ops) is { } syncErr)
            return PatchOutcome.Fail($"refused — {syncErr} (no patch written).");

        // Dry run stops before serialization after checking that every referenced master is resolvable.
        if (dryRun)
        {
            if (DryRunMastersPreview(patchMod, resolver, patchLane: true, out var wouldMasters) is { } dryErr)
                return PatchOutcome.Fail(dryErr);
            IReadOnlyList<FullReadback>? dryBack = fullReadback
                ? ReadBackInFull(patchMod, resolved.Select(r => r.edit.Target), inMemory: true) : null;
            return new PatchOutcome(true, null, outPath, extend, wouldMasters, ops, 0) { DryRun = true, ReadBack = dryBack };
        }

        // Release any overlay on the output, exclude it from the resolution set, and serialize once with active masters.
        session.ReleaseOverlay(patchMod.ModKey.FileName.String);
        try { WriteEngine.WritePatch(patchMod, session.AllMastersExcept(patchMod.ModKey.FileName.String), outPath); }
        catch (Exception ex)
            { return PatchOutcome.Fail($"writing the patch failed (serialize or commit; the existing file is untouched): {WriteEngine.Describe(ex)}"); }

        // Reopen the written bytes to report masters and optional full-record verification, then release the mapping.
        IReadOnlyList<string> masters = Array.Empty<string>();
        IReadOnlyList<FullReadback>? readBack = null;
        long bytes = 0;
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE);
            masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
            bytes = new FileInfo(outPath).Length;
            if (fullReadback) readBack = ReadBackInFull(back, resolved.Select(r => r.edit.Target));
        }
        catch (Exception ex)
            { return PatchOutcome.Fail($"patch written but could not be re-opened to confirm masters: {ex.Message}"); }
        finally { (back as IDisposable)?.Dispose(); }

        return new PatchOutcome(true, null, outPath, extend, masters, ops, bytes) { ReadBack = readBack };
    }

    /// <summary>Synchronizes a dialogue topic's SNAM marker when this call changes only its modeled subtype.</summary>
    /// <returns>An error for an unmodeled subtype; otherwise null.</returns>
    static string? SyncEditedTopicMarkers(SkyrimMod mod, IReadOnlyList<PatchEdit> edits, List<OpResult> ops)
    {
        // Track only top-level fields because Subtype and SubtypeName are scalar leaves.
        var editedTop = new Dictionary<FormKey, HashSet<string>>();
        foreach (var e in edits)
        {
            if (e.Path.Length == 0) continue;
            if (!editedTop.TryGetValue(e.Target, out var set)) editedTop[e.Target] = set = new(StringComparer.OrdinalIgnoreCase);
            set.Add(e.Path[0]);
        }
        foreach (var (fk, set) in editedTop)
        {
            if (!set.Contains("Subtype") || set.Contains("SubtypeName")) continue;   // only Subtype-set-without-marker
            if (mod.DialogTopics.FirstOrDefault(t => t.FormKey == fk) is not { } dt) continue;   // not a DialogTopic
            switch (DialogueSubtype.SyncMarkerToSubtype(dt, out var marker))
            {
                case MarkerFill.Filled:
                    ops.Add(new OpResult(fk, "DialogTopic",
                        $"SubtypeName (SNAM subtype marker) synced to {marker}", true, null,
                        $"{marker} — you set Subtype={dt.Subtype}; the game buckets by the SNAM marker, so it was synced to match (#131 — otherwise the Subtype change is a silent no-op)"));
                    break;
                case MarkerFill.Unmodeled:
                    return $"cannot set Subtype on DialogTopic {fk}: no SNAM marker is modeled for Subtype={dt.Subtype} " +
                           $"((int){(int)dt.Subtype}, outside the known 0..{DialogueSubtype.Count - 1}). Use a valid Subtype, or set SubtypeName explicitly";
            }
        }
        return null;
    }

    /// <summary>Edits records owned by an existing staging plugin and atomically replaces that plugin.</summary>
    /// <remarks>
    /// The method reads each target from the named plugin, never from the load-order winner. It preserves the plugin's
    /// declared masters and FormID metadata, verifies touched records after writing, and leaves the prior file intact
    /// when validation or serialization fails. The service enforces consent and Amethyst redeployment confirmation.
    /// </remarks>
    public static PatchOutcome ApplyInPlace(
        LoadOrderResolver resolver, CorpusRulebook rulebook,
        IReadOnlyList<PatchEdit> edits, string targetPath, string targetName, bool fullReadback = true,
        bool dryRun = false)
    {
        if (edits.Count == 0) return PatchOutcome.Fail("no edits supplied.");

        // Scope the target overlays and nested link caches to this call.
        using var session = resolver.OpenSession();
        var fileName = Path.GetFileName(targetPath);

        // Resolve every body from the target plugin, not the winner, using one captured view for the whole batch.
        var view = resolver.Capture();
        if (!view.ContainsPlugin(targetName))
            return PatchOutcome.Fail($"in-place target '{targetName}' is not an active plugin in the load order.{view.AbsenceClause(targetName)}");
        if (view.ExcludedPlugins.TryGetValue(targetName, out var excluded))
            return PatchOutcome.Fail(
                $"cannot edit '{targetName}' in place: it was EXCLUDED from this session ({excluded}) — houseCARL won't " +
                "re-serialize a plugin it can't fully parse (that would risk dropping the record it couldn't read, Q3). The file is UNTOUCHED.");

        var resolved = new List<(PatchEdit edit, IMajorRecordGetter body, WriteRequest req, string label)>(edits.Count);
        var problems = new List<string>();
        foreach (var e in edits)
        {
            var body = view.GetRecord(session, targetName, e.Target);
            if (body is null)
            {
                problems.Add($"{e.Target}: '{targetName}' does not define or override this record — in-place edits only what the " +
                             "file OWNS. To change a record defined in another plugin, use the default patch lane (a new override) instead.");
                continue;
            }
            var recType = RecordNaming.StripOverlay(body.GetType().Name);
            var req = new WriteRequest
            {
                RecordType = recType, Path = e.Path, Verb = e.Verb,
                Key = e.Key, Value = e.Value, Values = e.Values, Entries = e.Entries, Struct = e.Struct, Structs = e.Structs,
            };
            var label = Label(req);
            if (rulebook.Validate(req) is { } reject) { problems.Add($"{recType} {e.Target} [{label}]: {reject}"); continue; }
            resolved.Add((e, body, req, label));
        }
        if (problems.Count > 0)
            return PatchOutcome.Fail(
                $"refused — {problems.Count} of {edits.Count} edit(s) rejected by resolve/pre-flight; '{fileName}' is UNTOUCHED:\n  - "
                + string.Join("\n  - ", problems));

        // Load only the target as a mutable plugin. Refuse an unreadable file instead of re-emitting incomplete data.
        if (!File.Exists(targetPath))
            return PatchOutcome.Fail($"in-place target '{fileName}' not found on disk at {targetPath} — the file is untouched.");
        SkyrimMod targetMod;
        try { targetMod = SkyrimMod.CreateFromBinary(targetPath, SkyrimRelease.SkyrimSE); }
        catch (Exception ex)
            { return PatchOutcome.Fail($"cannot open '{fileName}' to edit in place ({WriteEngine.Describe(ex)}) — a plugin Mutagen can't parse is refused, not re-emitted minus what it couldn't read (Q3). The file is UNTOUCHED."); }
        if (!string.Equals(targetMod.ModKey.FileName.String, fileName, StringComparison.OrdinalIgnoreCase))
            return PatchOutcome.Fail($"in-place ModKey '{targetMod.ModKey.FileName}' must match the target filename '{fileName}'.");
        // Capture declared masters before mutation so any required load-order change can be reported after writing.
        var mastersBefore = targetMod.ModHeader.MasterReferences
            .Select(m => m.Master.FileName.String).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Apply each verb to the target's own mutable record. Nested records obtain the target cache on demand.
        var ops = new List<OpResult>(resolved.Count);
        foreach (var (e, body, req, label) in resolved)
        {
            try
            {
                ILinkCache? cache = WriteEngine.RecordNeedsSourceCache(body) ? session.LinkCacheFor(targetName) : null;
                var ov = WriteEngine.GenericGetOrAddAsOverride(targetMod, body, cache);
                WriteEngine.ApplyVerb(ov, req);
                var (after, landed) = DescribeApplied(ov, req);
                ops.Add(new OpResult(e.Target, req.RecordType, label, true, null, after, landed));
            }
            catch (ExpectedApplyRejectionException ex)
            {
                return PatchOutcome.Fail(
                    $"refused applying [{label}] to {req.RecordType} {e.Target} — {ex.Message} (the file is untouched)");
            }
            catch (MalformedTargetDataException ex)
            {
                return PatchOutcome.Fail(
                    $"refused applying [{label}] to {req.RecordType} {e.Target} — {ex.Message} (the file is untouched)");
            }
            catch (Exception ex)
            {
                return PatchOutcome.Fail(
                    $"engine error applying [{label}] to {req.RecordType} {e.Target}: pre-flight ACCEPTED it but the apply " +
                    $"threw — a real inconsistency, surfaced not swallowed (Q3): {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Keep the dialogue SNAM marker aligned when this call changes only the modeled subtype.
        if (SyncEditedTopicMarkers(targetMod, edits, ops) is { } syncErr)
            return PatchOutcome.Fail($"refused — {syncErr} ('{fileName}' is UNTOUCHED).");

        // Dry run stops before replacement and previews only masters derived by the in-place serializer.
        if (dryRun)
        {
            if (DryRunMastersPreview(targetMod, resolver, patchLane: false, out var wouldMasters) is { } dryErr)
                return PatchOutcome.Fail(dryErr);
            var wouldGrow = wouldMasters.Where(m => !mastersBefore.Contains(m)).ToList();
            IReadOnlyList<FullReadback>? dryBack = fullReadback
                ? ReadBackInFull(targetMod, resolved.Select(r => r.edit.Target), inMemory: true) : null;
            return new PatchOutcome(true, null, targetPath, false, wouldMasters, ops, 0)
            {
                DryRun = true, InPlace = true, ReadBack = dryBack,
                Note = wouldGrow.Count == 0 ? null :
                    $"the real write would ADD {string.Join(", ", wouldGrow)} as master(s) of '{fileName}' — a plugin " +
                    "loads only if its masters load BEFORE it, so re-sort your load order in Amethyst after the real write.",
            };
        }

        // Release every mapping on the target, then atomically reserialize it against all other active plugins.
        // Mutagen derives a lean header, including newly referenced active masters.
        session.ReleaseOverlay(fileName);
        try { WriteEngine.WriteInPlace(targetMod, session.AllMastersExcept(fileName), targetPath); }
        catch (MissingModException ex)
        {
            return PatchOutcome.Fail(
                $"writing '{fileName}' in place failed: the edited records reference a plugin that is NOT active in " +
                $"the load order ({ex.Message}) — a reference into an inactive plugin can't resolve in game. " +
                "Enable that plugin in Amethyst (or reference an active one) and retry. The existing file is untouched.");
        }
        catch (Exception ex)
            { return PatchOutcome.Fail($"writing '{fileName}' in place failed (serialize or commit; the existing file is untouched): {WriteEngine.Describe(ex)}"); }

        // Reopen the replacement to report masters and verify each touched record from the on-disk bytes.
        IReadOnlyList<string> masters = Array.Empty<string>();
        IReadOnlyList<FullReadback>? readBack = null;
        long bytes = 0;
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(targetPath, SkyrimRelease.SkyrimSE);
            masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
            bytes = new FileInfo(targetPath).Length;
            if (fullReadback) readBack = ReadBackInFull(back, resolved.Select(r => r.edit.Target));
        }
        catch (Exception ex)
            { return PatchOutcome.Fail($"'{fileName}' was edited in place but could not be re-opened to verify: {ex.Message}"); }
        finally { (back as IDisposable)?.Dispose(); }

        return new PatchOutcome(true, null, targetPath, false, masters, ops, bytes)
            { ReadBack = readBack, InPlace = true, Note = MasterGrowNote(fileName, mastersBefore, masters) };
    }

    /// <summary>Builds a load-order warning when an in-place write adds declared masters.</summary>
    /// <returns>The warning, or null when no master was added.</returns>
    static string? MasterGrowNote(string fileName, HashSet<string> mastersBefore, IReadOnlyList<string> mastersAfter)
    {
        var grown = mastersAfter.Where(m => !mastersBefore.Contains(m)).ToList();
        if (grown.Count == 0) return null;
        return $"{string.Join(", ", grown)} {(grown.Count == 1 ? "was" : "were")} added as a master of '{fileName}' — " +
               "a plugin loads only if its masters load BEFORE it, so re-sort your load order in Amethyst before playing.";
    }

    /// <summary>Validates dry-run references and previews the master list a real serialization would derive.</summary>
    /// <returns>An unresolved-reference error, or null with <paramref name="masters"/> populated.</returns>
    static string? DryRunMastersPreview(SkyrimMod mod, LoadOrderResolver resolver, bool patchLane, out IReadOnlyList<string> masters)
    {
        masters = Array.Empty<string>();
        var priority = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < resolver.PluginNames.Count; i++) priority[resolver.PluginNames[i]] = i;

        var referenced = new Dictionary<ModKey, FormKey>();   // referenced plugin -> first record referencing it (for the refusal)
        foreach (var rec in mod.EnumerateMajorRecords())
        {
            if (rec.FormKey.ModKey != mod.ModKey) referenced.TryAdd(rec.FormKey.ModKey, rec.FormKey);
            try
            {
                foreach (var link in rec.EnumerateFormLinks())
                    if (!link.FormKey.IsNull && link.FormKey.ModKey != mod.ModKey)
                        referenced.TryAdd(link.FormKey.ModKey, rec.FormKey);
            }
            catch (Exception ex)
            {
                // Name required-null polymorphic fields consistently with the serializer instead of exposing a bare NRE.
                if (WriteEngine.RootNullArm(ex) is not null)
                    return $"dry run caught what the real write would fail on: {rec.FormKey} carries a required modeled " +
                           "sub-field left null (the same null-dereference Mutagen's writer refuses at serialize). The " +
                           "cause is a COMPOSED record that left a required polymorphic sub-field unset — e.g. a Condition " +
                           "composed without its Data arm, or a leveled-list / effect element missing a required part. " +
                           "Compose that sub-field too (select the arm via compose). Nothing was written.";
                return $"dry run: enumerating {rec.FormKey}'s references threw ({ex.GetType().Name}: {ex.Message}) — " +
                       "the would-be content could not be fully checked; the real write would hit the same data. Nothing was written.";
            }
        }

        var missing = referenced.Where(kv => !priority.ContainsKey(kv.Key.FileName.String))
                                .Select(kv => $"{kv.Key.FileName} (referenced by {kv.Value})").ToList();
        if (missing.Count > 0)
            return $"dry run caught what the real write would fail on: the would-be content references " +
                   $"{missing.Count} plugin(s) NOT active in the load order — a reference into an inactive plugin " +
                   $"can't resolve in game, and the real serialize refuses it (MissingModException): " +
                   $"{string.Join("; ", missing)}. Enable the plugin(s) in Amethyst (or reference active ones). " +
                   "Nothing was written.";

        var set = new HashSet<string>(referenced.Keys.Select(mk => mk.FileName.String), StringComparer.OrdinalIgnoreCase);
        if (patchLane)
            foreach (var bm in WriteEngine.BaselineMasters)
                if (priority.ContainsKey(bm.FileName.String)) set.Add(bm.FileName.String);
        masters = set.OrderBy(n => priority[n]).ToList();
        return null;
    }

    /// <summary>Opens a target plugin's declared masters in declared order for faithful reserialization.</summary>
    /// <returns>Resolved master overlays, or an empty array with <paramref name="missing"/> set.</returns>
    static ISkyrimModGetter[] ResolveOwnMasters(
        LoadOrderResolver.IndexView view, SkyrimMod targetMod, List<IDisposable> overlays, out string? missing)
    {
        missing = null;
        var resolved = new List<ISkyrimModGetter>();
        foreach (var mr in targetMod.ModHeader.MasterReferences)
        {
            var mfn = mr.Master.FileName.String;
            var mpath = view.PluginPath(mfn);
            if (mpath is null)
            {
                missing = $"cannot re-serialize '{targetMod.ModKey.FileName}' in place: its declared master '{mfn}' is not active " +
                          "in the load order, so a faithful re-serialize can't resolve the references into it. Enable that master " +
                          "(or fix the target's masters in xEdit) first. The file is UNTOUCHED.";
                return Array.Empty<ISkyrimModGetter>();
            }
            var ov = SkyrimMod.CreateFromBinaryOverlay(mpath, SkyrimRelease.SkyrimSE);
            overlays.Add((IDisposable)ov);
            resolved.Add(ov);
        }
        return resolved.ToArray();
    }

    /// <summary>Removes complete records carried by an existing houseCARL patch.</summary>
    /// <remarks>
    /// Every target is confirmed present before mutation because Mutagen removal can silently no-op. Typed removal
    /// handles flat and nested records, and serialization derives a lean master list from the surviving content.
    /// </remarks>
    public static RemovalOutcome RemoveRecords(LoadOrderResolver resolver, IReadOnlyList<FormKey> targets, string outPath)
    {
        if (targets.Count == 0) return RemovalOutcome.Fail("no records to remove supplied.");

        // Scope all master overlays to this call.
        using var session = resolver.OpenSession();

        var fileName = Path.GetFileName(outPath);
        if (!File.Exists(outPath))
            return RemovalOutcome.Fail($"cannot remove: no existing patch at {outPath}. Removal targets a patch houseCARL already created.");

        SkyrimMod patchMod;
        try { patchMod = SkyrimMod.CreateFromBinary(outPath, SkyrimRelease.SkyrimSE); }
        catch (Exception ex) { return RemovalOutcome.Fail($"cannot open patch to remove from ({fileName}): {ex.GetType().Name}: {ex.Message}"); }

        // Index carried records once. Removal uses each record's group type because a concrete subtype can no-op.
        var carried = new Dictionary<FormKey, (string type, string? edid, Type runtime)>();
        foreach (var r in patchMod.EnumerateMajorRecords())
            carried[r.FormKey] = (RecordNaming.StripOverlay(r.GetType().Name), r.EditorID, WriteEngine.RemovalTypeFor(r));

        var problems = new List<string>();
        var toRemove = new List<RemovedRecord>(targets.Count);
        var seen = new HashSet<FormKey>();
        foreach (var fk in targets)
        {
            if (!seen.Add(fk)) continue;   // de-dup repeated targets in one call
            if (!carried.TryGetValue(fk, out var info))
            {
                problems.Add(
                    $"{fk}: not carried by patch '{fileName}' — only a record the patch ITSELF defines (a created record " +
                    "or an accumulated override) can be removed; a master's record can't be literally removed, only its " +
                    "override dropped (and this patch has no override of it).");
                continue;
            }
            toRemove.Add(new RemovedRecord(fk, info.type, info.edid));
        }
        if (problems.Count > 0)
            return RemovalOutcome.Fail(
                $"refused — {problems.Count} of {targets.Count} target(s) not carried by the patch; NOTHING removed:\n  - "
                + string.Join("\n  - ", problems));

        // Remove the complete record from its typed group rather than setting the record's deleted flag.
        try
        {
            foreach (var rr in toRemove)
                ((IMajorRecordEnumerable)patchMod).Remove(rr.Target, carried[rr.Target].runtime, throwIfUnknown: true);
        }
        catch (Exception ex)
        {
            return RemovalOutcome.Fail(
                $"present-check passed but Remove threw — a real engine inconsistency, surfaced not swallowed (Q3): "
                + $"{ex.GetType().Name}: {ex.Message}");
        }
        if (RemoveSurvivors(patchMod, toRemove) is { } survived)
            return RemovalOutcome.Fail(survived + $" '{fileName}' is UNTOUCHED.");

        // Release any mapping on the patch, exclude it from the master context, and serialize surviving content once.
        session.ReleaseOverlay(patchMod.ModKey.FileName.String);
        try { WriteEngine.WritePatch(patchMod, session.AllMastersExcept(patchMod.ModKey.FileName.String), outPath); }
        catch (Exception ex) { return RemovalOutcome.Fail($"writing the patch after removal failed (serialize or commit; the existing file is untouched): {WriteEngine.Describe(ex)}"); }

        // Reopen the output to report its derived masters and surviving record count, then release the mapping.
        IReadOnlyList<string> masters = Array.Empty<string>();
        int remaining = 0; long bytes = 0;
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE);
            masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
            remaining = back.EnumerateMajorRecords().Count();
            bytes = new FileInfo(outPath).Length;
        }
        catch (Exception ex) { return RemovalOutcome.Fail($"records removed + written but the patch could not be re-opened to confirm: {ex.Message}"); }
        finally { (back as IDisposable)?.Dispose(); }

        return new RemovalOutcome(true, null, outPath, toRemove, masters, remaining, bytes);
    }

    /// <summary>Confirms in memory that every requested record was removed before any serialization.</summary>
    /// <returns>Null when all targets are absent; otherwise a named no-op failure.</returns>
    static string? RemoveSurvivors(SkyrimMod mod, IReadOnlyList<RemovedRecord> toRemove)
    {
        var mustBeGone = toRemove.Select(rr => rr.Target).ToHashSet();
        var survivors = new List<FormKey>();
        foreach (var r in mod.EnumerateMajorRecords())
            if (mustBeGone.Contains(r.FormKey)) survivors.Add(r.FormKey);
        if (survivors.Count == 0) return null;
        return $"Remove did not drop {survivors.Count} record(s) ({string.Join(", ", survivors)}) — the engine " +
               "no-op'd without throwing; a real inconsistency surfaced BEFORE any rewrite, not swallowed (Q3).";
    }

    /// <summary>Removes complete records from an existing staging plugin and atomically replaces that plugin.</summary>
    /// <remarks>
    /// The target must be active and fully readable. Every record is confirmed present, removed by typed group, and
    /// confirmed absent both before serialization and after reopening the replacement. The service owns consent and
    /// Amethyst redeployment confirmation.
    /// </remarks>
    public static RemovalOutcome RemoveRecordsInPlace(
        LoadOrderResolver resolver, IReadOnlyList<FormKey> targets, string targetPath, string targetName)
    {
        if (targets.Count == 0) return RemovalOutcome.Fail("no records to remove supplied.");

        // Scope all master overlays to this call.
        using var session = resolver.OpenSession();
        var fileName = Path.GetFileName(targetPath);

        // Refuse inactive or excluded targets before opening a mutable copy.
        var view = resolver.Capture();
        if (!view.ContainsPlugin(targetName))
            return RemovalOutcome.Fail($"in-place target '{targetName}' is not an active plugin in the load order.{view.AbsenceClause(targetName)}");
        if (view.ExcludedPlugins.TryGetValue(targetName, out var excluded))
            return RemovalOutcome.Fail(
                $"cannot remove from '{targetName}' in place: it was EXCLUDED from this session ({excluded}) — houseCARL won't " +
                "re-serialize a plugin it can't fully parse (that would risk dropping a record it couldn't read, Q3). The file is UNTOUCHED.");

        // Load only the target as a mutable plugin and refuse incomplete parses.
        if (!File.Exists(targetPath))
            return RemovalOutcome.Fail($"in-place target '{fileName}' not found on disk at {targetPath} — the file is untouched.");
        SkyrimMod targetMod;
        try { targetMod = SkyrimMod.CreateFromBinary(targetPath, SkyrimRelease.SkyrimSE); }
        catch (Exception ex)
            { return RemovalOutcome.Fail($"cannot open '{fileName}' to remove from in place ({WriteEngine.Describe(ex)}) — a plugin Mutagen can't parse is refused, not re-emitted minus what it couldn't read (Q3). The file is UNTOUCHED."); }
        if (!string.Equals(targetMod.ModKey.FileName.String, fileName, StringComparison.OrdinalIgnoreCase))
            return RemovalOutcome.Fail($"in-place ModKey '{targetMod.ModKey.FileName}' must match the target filename '{fileName}'.");

        // Index the target's carried records once and reject any target it does not own.
        var carried = new Dictionary<FormKey, (string type, string? edid, Type runtime)>();
        foreach (var r in targetMod.EnumerateMajorRecords())
            carried[r.FormKey] = (RecordNaming.StripOverlay(r.GetType().Name), r.EditorID, WriteEngine.RemovalTypeFor(r));

        var problems = new List<string>();
        var toRemove = new List<RemovedRecord>(targets.Count);
        var seen = new HashSet<FormKey>();
        foreach (var fk in targets)
        {
            if (!seen.Add(fk)) continue;   // de-dup repeated targets in one call
            if (!carried.TryGetValue(fk, out var info))
            {
                problems.Add(
                    $"{fk}: not carried by '{fileName}' — in-place removes only a record the file ITSELF defines or " +
                    "overrides. To stop ANOTHER plugin's record from winning, use the default patch lane (forward the " +
                    "master version, or override it) instead.");
                continue;
            }
            toRemove.Add(new RemovedRecord(fk, info.type, info.edid));
        }
        if (problems.Count > 0)
            return RemovalOutcome.Fail(
                $"refused — {problems.Count} of {targets.Count} target(s) not carried by '{fileName}'; NOTHING removed:\n  - "
                + string.Join("\n  - ", problems));

        // Remove each complete record from its typed group and verify the in-memory absence.
        try
        {
            foreach (var rr in toRemove)
                ((IMajorRecordEnumerable)targetMod).Remove(rr.Target, carried[rr.Target].runtime, throwIfUnknown: true);
        }
        catch (Exception ex)
        {
            return RemovalOutcome.Fail(
                $"present-check passed but Remove threw — a real engine inconsistency, surfaced not swallowed (Q3): "
                + $"{ex.GetType().Name}: {ex.Message}");
        }
        if (RemoveSurvivors(targetMod, toRemove) is { } survived)
            return RemovalOutcome.Fail(survived + $" Your original '{fileName}' is UNTOUCHED.");

        // Release target mappings and atomically reserialize against its declared masters.
        session.ReleaseOverlay(fileName);
        var masterOverlays = new List<IDisposable>();
        try
        {
            ISkyrimModGetter[] ownMasters = ResolveOwnMasters(view, targetMod, masterOverlays, out var missing);
            if (missing is not null) return RemovalOutcome.Fail(missing);
            try { WriteEngine.WriteInPlace(targetMod, ownMasters, targetPath); }
            catch (Exception ex)
                { return RemovalOutcome.Fail($"writing '{fileName}' in place after removal failed (serialize or commit; the existing file is untouched): {WriteEngine.Describe(ex)}"); }
        }
        finally { foreach (var d in masterOverlays) { try { d.Dispose(); } catch { /* best-effort; never mask the write result */ } } }

        // Reopen the replacement, report surviving records and masters, and confirm every removed key is absent.
        IReadOnlyList<string> masters = Array.Empty<string>();
        int remaining = 0; long bytes = 0;
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(targetPath, SkyrimRelease.SkyrimSE);
            var removedKeys = toRemove.Select(rr => rr.Target).ToHashSet();
            var stillThere = new List<FormKey>();
            foreach (var r in back.EnumerateMajorRecords())
            {
                remaining++;
                if (removedKeys.Contains(r.FormKey)) stillThere.Add(r.FormKey);
            }
            if (stillThere.Count > 0)
                return RemovalOutcome.Fail(
                    $"'{fileName}' was rewritten but the verify found {stillThere.Count} record(s) that should have been removed still present " +
                    $"({string.Join(", ", stillThere)}) — a real inconsistency surfaced, not swallowed (Q3). The on-disk file may differ from intent; re-check in xEdit.");
            masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
            bytes = new FileInfo(targetPath).Length;
        }
        catch (Exception ex) { return RemovalOutcome.Fail($"records removed + written but '{fileName}' could not be re-opened to verify: {ex.Message}"); }
        finally { (back as IDisposable)?.Dispose(); }

        return new RemovalOutcome(true, null, targetPath, toRemove, masters, remaining, bytes) { InPlace = true };
    }

    /// <summary>Resolves complete record bodies from each explicitly named source plugin using one captured view.</summary>
    /// <returns>Resolved sources; <paramref name="refusal"/> names all failures when any source is unusable.</returns>
    static List<(ForwardSpec spec, IMajorRecordGetter body, string priorWinner, bool wasWinner)> ResolveForwardSources(
        LoadOrderResolver.OverlaySession session, LoadOrderResolver.IndexView view,
        IReadOnlyList<ForwardSpec> specs, string fileName, bool selfIsTarget, out string? refusal)
    {
        var resolved = new List<(ForwardSpec spec, IMajorRecordGetter body, string priorWinner, bool wasWinner)>(specs.Count);
        var problems = new List<string>();
        var seen = new HashSet<FormKey>();
        // Memoize absence explanations within this batch because each explanation may inspect the profile and install.
        var absenceMemo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Absence(string plugin)
        {
            if (!absenceMemo.TryGetValue(plugin, out var clause)) absenceMemo[plugin] = clause = view.AbsenceClause(plugin);
            return clause;
        }

        foreach (var s in specs)
        {
            if (!seen.Add(s.Target))
            { problems.Add($"{s.Target}: forwarded more than once in this call — name each target once (one source per record)."); continue; }
            if (string.Equals(s.FromPlugin, fileName, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(selfIsTarget
                    ? $"{s.Target}: from_plugin '{s.FromPlugin}' is the in-place target itself — forwarding a plugin's own version into itself is a no-op; name the OTHER plugin whose version you want carried in."
                    : $"{s.Target}: from_plugin '{s.FromPlugin}' is the output patch itself — forwarding a patch's own version into itself is a no-op; name the EARLIER plugin whose version you want to re-assert.");
                continue;
            }
            if (!view.ContainsPlugin(s.FromPlugin))
            { problems.Add($"{s.Target}: source plugin '{s.FromPlugin}' is not in the load order — name an active plugin that defines or overrides this record.{Absence(s.FromPlugin)}"); continue; }
            if (view.ExcludedPlugins.TryGetValue(s.FromPlugin, out var why))
            { problems.Add($"{s.Target}: source plugin '{s.FromPlugin}' was excluded from this session ({why}) — its records aren't resolvable."); continue; }
            var body = view.GetRecord(session, s.FromPlugin, s.Target);
            if (body is null)
            { problems.Add($"{s.Target}: source plugin '{s.FromPlugin}' is in the load order but does NOT define or override this record (it doesn't touch it) — there is no version of it there to forward."); continue; }
            var w = view.ResolveWinner(s.Target);
            resolved.Add((s, body, w?.WinnerPlugin ?? "(none)",
                w is { } wi && string.Equals(wi.WinnerPlugin, s.FromPlugin, StringComparison.OrdinalIgnoreCase)));
        }
        refusal = problems.Count > 0
            ? $"refused — {problems.Count} of {specs.Count} forward(s) rejected; NOTHING written:\n  - " + string.Join("\n  - ", problems)
            : null;
        return resolved;
    }

    /// <summary>Copies named source record versions into an existing staging plugin and atomically replaces it.</summary>
    /// <remarks>
    /// Existing target records are removed and verified absent before replacement. The serializer derives masters from
    /// copied content while preserving the target's counter. The service owns consent and redeployment confirmation.
    /// </remarks>
    public static ForwardOutcome ForwardRecordsInPlace(
        LoadOrderResolver resolver, IReadOnlyList<ForwardSpec> specs, string targetPath, string targetName,
        bool fullReadback = true, bool dryRun = false)
    {
        if (specs.Count == 0) return ForwardOutcome.Fail("no records to forward supplied.");

        // Scope source overlays and link caches to this call.
        using var session = resolver.OpenSession();
        var fileName = Path.GetFileName(targetPath);

        // Validate the target and resolve every named source from one captured view.
        var view = resolver.Capture();
        if (!view.ContainsPlugin(targetName))
            return ForwardOutcome.Fail($"in-place target '{targetName}' is not an active plugin in the load order.{view.AbsenceClause(targetName)}");
        if (view.ExcludedPlugins.TryGetValue(targetName, out var excluded))
            return ForwardOutcome.Fail(
                $"cannot forward into '{targetName}' in place: it was EXCLUDED from this session ({excluded}) — houseCARL won't " +
                "re-serialize a plugin it can't fully parse (that would risk dropping the record it couldn't read, Q3). The file is UNTOUCHED.");
        var resolved = ResolveForwardSources(session, view, specs, targetName, selfIsTarget: true, out var refusal);
        if (refusal is not null) return ForwardOutcome.Fail(refusal);

        // Load only the target as a mutable plugin.
        if (!File.Exists(targetPath))
            return ForwardOutcome.Fail($"in-place target '{fileName}' not found on disk at {targetPath} — the file is untouched.");
        SkyrimMod targetMod;
        try { targetMod = SkyrimMod.CreateFromBinary(targetPath, SkyrimRelease.SkyrimSE); }
        catch (Exception ex)
            { return ForwardOutcome.Fail($"cannot open '{fileName}' to forward into in place ({WriteEngine.Describe(ex)}) — a plugin Mutagen can't parse is refused, not re-emitted minus what it couldn't read (Q3). The file is UNTOUCHED."); }
        if (!string.Equals(targetMod.ModKey.FileName.String, fileName, StringComparison.OrdinalIgnoreCase))
            return ForwardOutcome.Fail($"in-place ModKey '{targetMod.ModKey.FileName}' must match the target filename '{fileName}'.");
        // Capture declared masters so newly required load-order relationships can be reported.
        var mastersBefore = targetMod.ModHeader.MasterReferences
            .Select(m => m.Master.FileName.String).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Replace an existing carried record or copy a new override into the mutable target.
        var alreadyCarried = new Dictionary<FormKey, IMajorRecord>();
        foreach (var r in targetMod.EnumerateMajorRecords())
            alreadyCarried[r.FormKey] = r;

        var forwarded = new List<ForwardedRecord>(resolved.Count);
        foreach (var (spec, body, priorWinner, wasWinner) in resolved)
        {
            try
            {
                bool replaced = false;
                if (alreadyCarried.TryGetValue(spec.Target, out var existing))
                {
                    ((IMajorRecordEnumerable)targetMod).Remove(spec.Target, WriteEngine.RemovalTypeFor(existing), throwIfUnknown: true);
                    if (targetMod.EnumerateMajorRecords().Any(x => x.FormKey == spec.Target))
                        return ForwardOutcome.Fail(
                            $"cannot replace {spec.Target}: '{fileName}' already carries this record and its existing " +
                            "version could not be dropped before the copy (the engine no-op'd without throwing) — " +
                            "surfaced, not a silent skip (Q3); your original is UNTOUCHED.");
                    replaced = true;
                }
                ILinkCache? cache = WriteEngine.RecordNeedsSourceCache(body) ? session.LinkCacheFor(spec.FromPlugin) : null;
                WriteEngine.GenericGetOrAddAsOverride(targetMod, body, cache);
                forwarded.Add(new ForwardedRecord(
                    spec.Target, RecordNaming.StripOverlay(body.GetType().Name), body.EditorID, spec.FromPlugin, priorWinner, wasWinner,
                    ReplacedExisting: replaced));
            }
            catch (Exception ex)
            {
                return ForwardOutcome.Fail(
                    $"engine error forwarding {spec.Target} from '{spec.FromPlugin}': the source resolved but the " +
                    $"override-copy threw — a real inconsistency, surfaced not swallowed (Q3): {ex.GetType().Name}: {ex.Message}. Your original is UNTOUCHED.");
            }
        }

        // Dry run stops before replacement and previews masters derived by the in-place serializer.
        if (dryRun)
        {
            if (DryRunMastersPreview(targetMod, resolver, patchLane: false, out var wouldMasters) is { } dryErr)
                return ForwardOutcome.Fail(dryErr);
            var wouldGrow = wouldMasters.Where(m => !mastersBefore.Contains(m)).ToList();
            IReadOnlyList<FullReadback>? dryBack = fullReadback
                ? ReadBackInFull(targetMod, resolved.Select(r => r.spec.Target), inMemory: true) : null;
            return new ForwardOutcome(true, null, targetPath, false, forwarded, wouldMasters, 0)
            {
                DryRun = true, InPlace = true, ReadBack = dryBack,
                Note = wouldGrow.Count == 0 ? null :
                    $"the real write would ADD {string.Join(", ", wouldGrow)} as master(s) of '{fileName}' — a plugin " +
                    "loads only if its masters load BEFORE it, so re-sort your load order in Amethyst after the real write.",
            };
        }

        // Release target mappings and atomically serialize against all other active plugins.
        session.ReleaseOverlay(fileName);
        try { WriteEngine.WriteInPlace(targetMod, session.AllMastersExcept(fileName), targetPath); }
        catch (MissingModException ex)
        {
            return ForwardOutcome.Fail(
                $"writing '{fileName}' in place failed: the forwarded records reference a plugin that is NOT active in " +
                $"the load order ({ex.Message}) — a reference into an inactive plugin can't resolve in game. " +
                "Enable that plugin in Amethyst and retry. The existing file is untouched.");
        }
        catch (Exception ex)
            { return ForwardOutcome.Fail($"writing '{fileName}' in place failed (serialize or commit; the existing file is untouched): {WriteEngine.Describe(ex)}"); }

        // Reopen the replacement to report masters and verify every copied record from disk.
        IReadOnlyList<string> masters = Array.Empty<string>();
        IReadOnlyList<FullReadback>? readBack = null;
        long bytes = 0;
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(targetPath, SkyrimRelease.SkyrimSE);
            masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
            bytes = new FileInfo(targetPath).Length;
            if (fullReadback) readBack = ReadBackInFull(back, resolved.Select(r => r.spec.Target));
        }
        catch (Exception ex)
            { return ForwardOutcome.Fail($"'{fileName}' was rewritten but could not be re-opened to verify: {ex.Message}"); }
        finally { (back as IDisposable)?.Dispose(); }

        return new ForwardOutcome(true, null, targetPath, false, forwarded, masters, bytes)
            { ReadBack = readBack, InPlace = true, Note = MasterGrowNote(fileName, mastersBefore, masters) };
    }

    /// <summary>Identifies a complete record version to copy from a named plugin.</summary>
    public sealed record ForwardSpec
    {
        /// <summary>Gets the record FormKey to forward.</summary>
        public required FormKey Target { get; init; }

        /// <summary>Gets the plugin whose complete record version should be copied.</summary>
        public required string FromPlugin { get; init; }
    }

    /// <summary>Reports one complete record copied from a named plugin.</summary>
    /// <param name="Target">Forwarded record FormKey.</param>
    /// <param name="RecordType">Resolved record type.</param>
    /// <param name="EditorId">Editor ID, when present.</param>
    /// <param name="FromPlugin">Plugin whose record body was copied.</param>
    /// <param name="PriorWinner">Load-order winner before the output is enabled.</param>
    /// <param name="WasAlreadyWinner">Whether the copied source was already the winner.</param>
    /// <param name="ReplacedExisting">Whether an existing output record was replaced.</param>
    public sealed record ForwardedRecord(
        FormKey Target, string RecordType, string? EditorId, string FromPlugin, string PriorWinner, bool WasAlreadyWinner,
        bool ReplacedExisting = false);

    /// <summary>Reports a complete-record forwarding operation and its derived master list.</summary>
    public sealed record ForwardOutcome(
        bool Success, string? Error, string OutputPath, bool Extended,
        IReadOnlyList<ForwardedRecord> Forwarded, IReadOnlyList<string> Masters, long Bytes)
    {
        /// <summary>Gets optional full-record verification results.</summary>
        public IReadOnlyList<FullReadback>? ReadBack { get; init; }

        /// <summary>Gets whether records were written into an existing staging plugin.</summary>
        public bool InPlace { get; init; }

        /// <summary>Gets whether the service must obtain first-touch consent before writing.</summary>
        public bool NeedsAcknowledge { get; init; }

        /// <summary>Gets a successful-write warning or required follow-up note.</summary>
        public string? Note { get; init; }

        /// <summary>Gets whether the pipeline stopped before serialization and wrote nothing.</summary>
        public bool DryRun { get; init; }

        /// <summary>Creates an unsuccessful forwarding outcome.</summary>
        /// <param name="error">Actionable refusal or failure text.</param>
        /// <returns>The unsuccessful outcome.</returns>
        public static ForwardOutcome Fail(string error) =>
            new(false, error, "", false, Array.Empty<ForwardedRecord>(), Array.Empty<string>(), 0);

        /// <summary>Creates a no-write outcome requesting first-touch consent.</summary>
        /// <param name="prompt">Consent prompt shown to the caller.</param>
        /// <returns>The confirmation-required outcome.</returns>
        public static ForwardOutcome NeedsAck(string prompt) =>
            new(false, prompt, "", false, Array.Empty<ForwardedRecord>(), Array.Empty<string>(), 0) { NeedsAcknowledge = true };
    }

    /// <summary>Reports creation and verification of an empty, header-only plugin.</summary>
    public sealed record CreatePluginOutcome(
        bool Success, string? Error, string OutputPath, string PluginName, bool Esl,
        IReadOnlyList<string> Masters, int RecordCount, long Bytes)
    {
        /// <summary>Gets a successful-write warning or follow-up note.</summary>
        public string? Note { get; init; }

        /// <summary>Creates an unsuccessful plugin-creation outcome.</summary>
        /// <param name="error">Actionable refusal or failure text.</param>
        /// <returns>The unsuccessful outcome.</returns>
        public static CreatePluginOutcome Fail(string error) =>
            new(false, error, "", "", false, Array.Empty<string>(), 0, 0);
    }

    /// <summary>Reports whether one external plugin was safely repointed after compaction.</summary>
    /// <param name="Plugin">Rewritten plugin name.</param>
    /// <param name="Success">Whether every requested reference was repointed.</param>
    /// <param name="Error">Named failure when the plugin was left untouched.</param>
    public sealed record RepointReport(string Plugin, bool Success, string? Error);

    /// <summary>Reports plugin compaction, external reference handling, and FormID-keyed asset migration.</summary>
    public sealed record CompactOutcome(
        bool Success, string? Error, bool NeedsAcknowledge, string OutputPath, string PluginName, bool InPlace, bool Esl,
        IReadOnlyList<string> Masters, int RecordsCopied, int RecordsRenumbered, long Bytes,
        IReadOnlyList<string> ExternalPlugins, IReadOnlyList<RepointReport> Repointed,
        int PluginsScanned, int UnscannableRecords, IReadOnlyList<string> UnscannableSamples, string? Note = null,
        AssetRenameOutcome? AssetRename = null, IReadOnlyList<string>? ExternalOverriders = null,
        VoiceCarryOutcome? VoiceRename = null, SeqRegenOutcome? SeqRegen = null)
    {
        /// <summary>Creates an unsuccessful compaction outcome.</summary>
        /// <param name="error">Actionable refusal or failure text.</param>
        /// <returns>The unsuccessful outcome.</returns>
        public static CompactOutcome Fail(string error) =>
            new(false, error, false, "", "", false, false, Array.Empty<string>(), 0, 0, 0,
                Array.Empty<string>(), Array.Empty<RepointReport>(), 0, 0, Array.Empty<string>());

        /// <summary>Creates a no-write outcome that requests destructive-operation consent.</summary>
        /// <param name="prompt">Consent prompt shown to the caller.</param>
        /// <returns>The confirmation-required outcome.</returns>
        public static CompactOutcome Confirm(string prompt) =>
            new(false, prompt, true, "", "", false, false, Array.Empty<string>(), 0, 0, 0,
                Array.Empty<string>(), Array.Empty<RepointReport>(), 0, 0, Array.Empty<string>());
    }

    /// <summary>Reports a new merged plugin, remaps, conflicts, external dependencies, and migrated assets.</summary>
    public sealed record MergeOutcome(
        bool Success, string? Error, string OutputPath, string OutputName,
        IReadOnlyList<string> Donors, IReadOnlyList<string> Masters,
        int RecordsCopied, int RecordsRenumbered,
        IReadOnlyList<RemapEngine.MergeDonorRemap> DonorRemaps,
        IReadOnlyList<RemapEngine.MergeConflict> Conflicts,
        IReadOnlyList<string> ExternalPlugins, IReadOnlyList<string> ExternalOverriders,
        int PluginsScanned, int UnscannableRecords, IReadOnlyList<string> UnscannableSamples,
        long Bytes, string? Note = null,
        AssetRenameOutcome? AssetRename = null, VoiceCarryOutcome? VoiceRename = null, SeqRegenOutcome? SeqRegen = null)
    {
        /// <summary>Creates an unsuccessful merge outcome.</summary>
        /// <param name="error">Actionable refusal or failure text.</param>
        /// <returns>The unsuccessful outcome.</returns>
        public static MergeOutcome Fail(string error) =>
            new(false, error, "", "", Array.Empty<string>(), Array.Empty<string>(), 0, 0,
                Array.Empty<RemapEngine.MergeDonorRemap>(), Array.Empty<RemapEngine.MergeConflict>(),
                Array.Empty<string>(), Array.Empty<string>(), 0, 0, Array.Empty<string>(), 0);
    }

    /// <summary>Copies complete record versions from named plugins into a new or existing patch.</summary>
    /// <remarks>
    /// Source bodies come from the named plugins rather than current winners. Existing patch records are replaced,
    /// every source is resolved before mutation, and the written patch is reopened for optional full-record verification.
    /// </remarks>
    public static ForwardOutcome ForwardRecords(
        LoadOrderResolver resolver, IReadOnlyList<ForwardSpec> specs, string outPath, bool extend, bool fullReadback = false,
        bool dryRun = false)
    {
        if (specs.Count == 0) return ForwardOutcome.Fail("no records to forward supplied.");

        // Scope source overlays and link caches to this call.
        using var session = resolver.OpenSession();
        var fileName = Path.GetFileName(outPath);

        // Resolve every named source from one captured view before opening or changing the output patch.
        var view = resolver.Capture();
        var resolved = ResolveForwardSources(session, view, specs, fileName, selfIsTarget: false, out var refusal);
        if (refusal is not null) return ForwardOutcome.Fail(refusal);

        // Open the existing output for extension or create a fresh patch.
        SkyrimMod patchMod;
        if (extend)
        {
            if (!File.Exists(outPath))
                return ForwardOutcome.Fail($"cannot extend: no existing patch at {outPath}. Omit into= to create it fresh.");
            try { patchMod = SkyrimMod.CreateFromBinary(outPath, SkyrimRelease.SkyrimSE); }
            catch (Exception ex) { return ForwardOutcome.Fail($"cannot open patch to extend ({fileName}): {ex.GetType().Name}: {ex.Message}"); }
        }
        else
        {
            patchMod = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(outPath), ModType.Plugin), SkyrimRelease.SkyrimSE);
        }
        if (!string.Equals(patchMod.ModKey.FileName.String, fileName, StringComparison.OrdinalIgnoreCase))
            return ForwardOutcome.Fail($"patch ModKey '{patchMod.ModKey.FileName}' must match output filename '{fileName}'.");

        // Replace any carried target, verify its absence, then deep-copy the named source body into the patch.
        var alreadyCarried = new Dictionary<FormKey, IMajorRecord>();
        if (extend)
            foreach (var r in patchMod.EnumerateMajorRecords())
                alreadyCarried[r.FormKey] = r;

        var forwarded = new List<ForwardedRecord>(resolved.Count);
        foreach (var (spec, body, priorWinner, wasWinner) in resolved)
        {
            try
            {
                bool replaced = false;
                if (alreadyCarried.TryGetValue(spec.Target, out var existing))
                {
                    ((IMajorRecordEnumerable)patchMod).Remove(spec.Target, WriteEngine.RemovalTypeFor(existing), throwIfUnknown: true);
                    // Confirm the old record is absent because typed removal can return without removing it.
                    if (patchMod.EnumerateMajorRecords().Any(x => x.FormKey == spec.Target))
                        return ForwardOutcome.Fail(
                            $"cannot replace {spec.Target}: the patch already carries this record and its existing " +
                            "override could not be dropped before the copy (the engine no-op'd without throwing) — " +
                            "surfaced, not a silent skip (Q3); nothing was serialized (the extended patch's on-disk file is untouched).");
                    replaced = true;
                }
                ILinkCache? cache = WriteEngine.RecordNeedsSourceCache(body) ? session.LinkCacheFor(spec.FromPlugin) : null;
                WriteEngine.GenericGetOrAddAsOverride(patchMod, body, cache);
                forwarded.Add(new ForwardedRecord(
                    spec.Target, RecordNaming.StripOverlay(body.GetType().Name), body.EditorID, spec.FromPlugin, priorWinner, wasWinner,
                    ReplacedExisting: replaced));
            }
            catch (Exception ex)
            {
                return ForwardOutcome.Fail(
                    $"engine error forwarding {spec.Target} from '{spec.FromPlugin}': the source resolved but the " +
                    $"override-copy threw — a real inconsistency, surfaced not swallowed (Q3): {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Dry run reports the in-memory copies and predicted masters without serializing.
        if (dryRun)
        {
            if (DryRunMastersPreview(patchMod, resolver, patchLane: true, out var wouldMasters) is { } dryErr)
                return ForwardOutcome.Fail(dryErr);
            IReadOnlyList<FullReadback>? dryBack = fullReadback
                ? ReadBackInFull(patchMod, resolved.Select(r => r.spec.Target), inMemory: true) : null;
            return new ForwardOutcome(true, null, outPath, extend, forwarded, wouldMasters, 0) { DryRun = true, ReadBack = dryBack };
        }

        // Release output mappings, exclude the output from the master context, and serialize once.
        session.ReleaseOverlay(patchMod.ModKey.FileName.String);
        try { WriteEngine.WritePatch(patchMod, session.AllMastersExcept(patchMod.ModKey.FileName.String), outPath); }
        catch (Exception ex)
            { return ForwardOutcome.Fail($"writing the patch failed (serialize or commit; the existing file is untouched): {WriteEngine.Describe(ex)}"); }

        // Reopen the output to report derived masters and optional full-record verification.
        IReadOnlyList<string> masters = Array.Empty<string>();
        IReadOnlyList<FullReadback>? readBack = null;
        long bytes = 0;
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE);
            masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
            bytes = new FileInfo(outPath).Length;
            if (fullReadback) readBack = ReadBackInFull(back, resolved.Select(r => r.spec.Target));
        }
        catch (Exception ex)
            { return ForwardOutcome.Fail($"patch written but could not be re-opened to confirm masters: {ex.Message}"); }
        finally { (back as IDisposable)?.Dispose(); }

        return new ForwardOutcome(true, null, outPath, extend, forwarded, masters, bytes) { ReadBack = readBack };
    }

    /// <summary>Creates an empty, header-only plugin and verifies the serialized result.</summary>
    /// <remarks>
    /// The plugin contains no records or masters. The method reopens the output to confirm its record count, master
    /// list, and light-master flag before reporting success. An invalid or unverifiable new artifact is removed.
    /// The service supplies an exact, collision-checked <paramref name="outPath"/> because some integrations bind by
    /// plugin basename.
    /// </remarks>
    public static CreatePluginOutcome CreatePlugin(string outPath, bool esl, string? author, string? description)
    {
        var fileName = Path.GetFileName(outPath);

        SkyrimMod mod;
        try
        {
            mod = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(outPath), ModType.Plugin), SkyrimRelease.SkyrimSE)
                { IsSmallMaster = esl };
            if (!string.IsNullOrWhiteSpace(author)) mod.ModHeader.Author = author.Trim();
            if (!string.IsNullOrWhiteSpace(description)) mod.ModHeader.Description = description.Trim();
        }
        catch (Exception ex) { return CreatePluginOutcome.Fail($"could not build the plugin in memory: {ex.GetType().Name}: {ex.Message}"); }

        // Serialize atomically with an empty master context, which produces a header with no master references.
        try { WriteEngine.WritePatch(mod, Array.Empty<ISkyrimModGetter>(), outPath); }
        catch (Exception ex)
            { return CreatePluginOutcome.Fail($"writing the plugin failed (serialize or commit; nothing left on disk): {WriteEngine.Describe(ex)}"); }

        // Reopen the artifact and confirm its zero records, empty master list, requested ESL flag, and byte size.
        IReadOnlyList<string> masters = Array.Empty<string>();
        int recordCount = -1; bool eslBack = false; long bytes = 0;
        string? confirmFail = null;
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE);
            masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
            recordCount = back.EnumerateMajorRecords().Count();
            eslBack = back.IsSmallMaster;
            bytes = new FileInfo(outPath).Length;
        }
        catch (Exception ex) { confirmFail = $"plugin written but could not be re-opened to confirm it: {ex.Message}"; }
        finally { (back as IDisposable)?.Dispose(); }   // dispose the overlay BEFORE any delete below (it maps the file)

        if (confirmFail is null && recordCount != 0)
            confirmFail = $"internal error: the created plugin carries {recordCount} record(s), expected 0 (a header-only plugin) — refusing to report success on a wrong artifact (Q3).";
        if (confirmFail is null && masters.Count != 0)
            confirmFail = $"internal error: the created plugin carries {masters.Count} master(s) ({string.Join(", ", masters)}), expected 0 (a header-only plugin references nothing) — refusing to report success on a wrong artifact (Q3).";
        if (confirmFail is null && eslBack != esl)
            confirmFail = $"internal error: the created plugin's light-master (ESL) flag is {eslBack}, expected {esl} — refusing to report success on a wrong artifact (Q3).";

        if (confirmFail is not null)
        {
            // This lane always writes a fresh artifact, so remove an invalid result rather than leave it behind.
            try { File.Delete(outPath); } catch { /* best-effort; the loud refusal stands regardless */ }
            return CreatePluginOutcome.Fail(confirmFail);
        }

        return new CreatePluginOutcome(true, null, outPath, fileName, esl, masters, recordCount, bytes);
    }

    /// <summary>Reads originating record keys in document order for compaction.</summary>
    /// <remarks>Overrides retain their master keys and are not included. The source overlay is disposed before return.</remarks>
    public static bool TryReadOriginatingKeys(string srcPath, ModKey modKey, out IReadOnlyList<FormKey> keys, out string? error)
    {
        keys = Array.Empty<FormKey>(); error = null;
        ISkyrimModGetter? ov = null;
        try
        {
            ov = SkyrimMod.CreateFromBinaryOverlay(srcPath, SkyrimRelease.SkyrimSE);
            keys = ov.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == modKey).Select(r => r.FormKey).ToList();
            return true;
        }
        catch (Exception ex)
        {
            error = $"cannot parse '{modKey.FileName}' to renumber it ({WriteEngine.Describe(ex)}) — houseCARL won't renumber a " +
                    "plugin it can't fully read (it would risk dropping a record it couldn't parse, Q3).";   // op-neutral: compact AND merge surface this verbatim
            return false;
        }
        finally { (ov as IDisposable)?.Dispose(); }
    }

    /// <summary>Reports the core compaction artifact and renumbering counts.</summary>
    public sealed record CompactBuildResult(
        bool Success, string? Error, IReadOnlyList<string> Masters, int RecordsCopied, int RecordsRenumbered, long Bytes)
    {
        /// <summary>Creates an unsuccessful core-compaction result.</summary>
        /// <param name="error">Actionable build failure text.</param>
        /// <returns>The unsuccessful result.</returns>
        public static CompactBuildResult Fail(string error) => new(false, error, Array.Empty<string>(), 0, 0, 0);
    }

    /// <summary>Renumbers a plugin into a fresh mutable model and atomically writes the compacted artifact.</summary>
    /// <remarks>
    /// Originating records use <paramref name="dict"/> while overrides retain master keys. The source is closed before
    /// replacement, declared masters are resolved explicitly, and any failure leaves the destination unchanged.
    /// </remarks>
    public static CompactBuildResult CompactBuild(
        string srcPath, ModKey modKey, IReadOnlyDictionary<FormKey, FormKey> dict,
        Func<string, string?> resolveMasterPath, string outPath, bool esl, uint floor)
    {
        // Build the compacted model in memory and close the source before a possible in-place replacement.
        SkyrimMod pPrime;
        RemapEngine.RenumberResult ren;
        List<string> declaredMasters;
        ISkyrimModGetter? srcOv = null;
        try
        {
            srcOv = SkyrimMod.CreateFromBinaryOverlay(srcPath, SkyrimRelease.SkyrimSE);
            declaredMasters = srcOv.ModHeader.MasterReferences.Select(m => m.Master.FileName.String).ToList();
            pPrime = new SkyrimMod(modKey, SkyrimRelease.SkyrimSE) { IsSmallMaster = esl };
            ren = RemapEngine.RenumberModInto(pPrime, srcOv, dict);
        }
        catch (Exception ex)
        {
            return CompactBuildResult.Fail($"cannot open/renumber '{modKey.FileName}' to compact it ({WriteEngine.Describe(ex)}) — nothing written.");
        }
        finally { (srcOv as IDisposable)?.Dispose(); }

        if (!ren.Success) return CompactBuildResult.Fail(ren.Error!);

        // Set the next object ID above the compacted run. A full ESL may legitimately point one past its usable window.
        pPrime.ModHeader.Stats.NextFormID = Math.Max(floor, (uint)(floor + dict.Count));

        // Resolve the source's declared masters before atomically serializing the compacted model.
        var overlays = new List<IDisposable>();
        try
        {
            var resolved = new List<ISkyrimModGetter>();
            foreach (var mfn in declaredMasters)
            {
                var mp = resolveMasterPath(mfn);
                if (mp is null)
                    return CompactBuildResult.Fail(
                        $"cannot compact '{modKey.FileName}': its declared master '{mfn}' is not active in the load order, so a " +
                        "faithful re-serialize can't resolve the references into it. Enable that master (or fix the masters in xEdit) first. Nothing was written.");
                var mov = SkyrimMod.CreateFromBinaryOverlay(mp, SkyrimRelease.SkyrimSE);
                overlays.Add((IDisposable)mov); resolved.Add(mov);
            }
            try { WriteEngine.WriteInPlace(pPrime, resolved, outPath); }
            catch (Exception ex)
            {
                return CompactBuildResult.Fail(
                    $"writing the compacted plugin failed (serialize or commit; nothing partial left): {WriteEngine.Describe(ex)} — " +
                    $"note: a sub-0x{RemapEngine.EslFloor:X} originating record, or (for the light range) one above 0x{RemapEngine.EslCeiling:X}, is rejected by the light-/master-aware write here.");
            }
        }
        finally { foreach (var d in overlays) { try { d.Dispose(); } catch { /* best-effort; never mask the write result */ } } }

        long bytes = 0; try { bytes = new FileInfo(outPath).Length; } catch { }
        return new CompactBuildResult(true, null, declaredMasters, ren.RecordsCopied, ren.RecordsRenumbered, bytes);
    }

    /// <summary>Reports the core merge artifact, record counts, derived masters, and donor conflicts.</summary>
    public sealed record MergeBuildResult(
        bool Success, string? Error, IReadOnlyList<string> Masters, int RecordsCopied, int RecordsRenumbered,
        IReadOnlyList<RemapEngine.MergeConflict> Conflicts, long Bytes)
    {
        /// <summary>Creates an unsuccessful core-merge result.</summary>
        /// <param name="error">Actionable build failure text.</param>
        /// <returns>The unsuccessful result.</returns>
        public static MergeBuildResult Fail(string error) =>
            new(false, error, Array.Empty<string>(), 0, 0, Array.Empty<RemapEngine.MergeConflict>(), 0);
    }

    /// <summary>Merges load-ordered donors into a new plugin and atomically writes the result.</summary>
    /// <remarks>
    /// Donor records are remapped into one model. References that still point into a donor are rejected because they
    /// would keep the donor as a master. The supplied non-donor masters are resolved before serialization.
    /// </remarks>
    public static MergeBuildResult MergeBuild(
        IReadOnlyList<(string Name, string Path, ModKey Key)> donorsByLoadOrder, ModKey outKey,
        IReadOnlyDictionary<FormKey, FormKey> dict, IReadOnlyList<string> masters,
        Func<string, string?> resolveMasterPath, string outPath)
    {
        // Open donors only while building the merged model, then release every mapping before serialization.
        var m = new SkyrimMod(outKey, SkyrimRelease.SkyrimSE);
        RemapEngine.MergeResult mr;
        var donorSet = new HashSet<ModKey>(donorsByLoadOrder.Select(d => d.Key));
        var overlays = new List<IDisposable>();
        try
        {
            var mods = new List<(string, ISkyrimModGetter)>(donorsByLoadOrder.Count);
            foreach (var (name, path, _) in donorsByLoadOrder)
            {
                ISkyrimModGetter ov;
                try { ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); }
                catch (Exception ex)
                {
                    return MergeBuildResult.Fail($"cannot open donor '{name}' to merge it ({WriteEngine.Describe(ex)}) — nothing written.");
                }
                overlays.Add((IDisposable)ov);
                mods.Add((name, ov));
            }
            mr = RemapEngine.MergeModsInto(m, mods, dict);
        }
        finally { foreach (var d in overlays) { try { d.Dispose(); } catch { /* best-effort */ } } }
        if (!mr.Success) return MergeBuildResult.Fail(mr.Error!);

        // Reject links still targeting donors; such keys were not defined and therefore could not be remapped.
        var dangling = new List<string>();
        int danglingCount = 0;
        foreach (var rec in m.EnumerateMajorRecords())
            foreach (var link in rec.EnumerateFormLinks())
                if (!link.FormKey.IsNull && donorSet.Contains(link.FormKey.ModKey))
                {
                    danglingCount++;
                    if (dangling.Count < 10) dangling.Add($"{rec.FormKey} → {link.FormKey}");
                }
        if (danglingCount > 0)
            return MergeBuildResult.Fail(
                $"refused — {danglingCount} reference(s) in the merged content still point INTO a donor after the renumber. " +
                "Each targets a FormID its donor never DEFINES (a dangling reference already broken in the source), so it cannot " +
                $"be remapped, and writing it would keep the donor as a master of its own merge. Fix the source (xEdit: check for " +
                $"deleted/injected records) or drop that donor. Samples: {string.Join("; ", dangling)}. Nothing was written.");

        // Place NextObjectID above the merged range while remaining inside the 24-bit object-ID limit.
        uint maxUsed = 0;
        foreach (var nk in dict.Values) if (nk.ID > maxUsed) maxUsed = nk.ID;
        m.ModHeader.Stats.NextFormID = Math.Min(FormIdRange.ObjectIdMax, Math.Max(FormIdRange.EslWindowFloor, maxUsed + 1));

        // Resolve every non-donor master and serialize the merged model atomically.
        var masterOverlays = new List<IDisposable>();
        try
        {
            var resolved = new List<ISkyrimModGetter>(masters.Count);
            foreach (var mfn in masters)
            {
                var mp = resolveMasterPath(mfn);
                if (mp is null)
                    return MergeBuildResult.Fail(
                        $"cannot merge: donor master '{mfn}' is not active in the load order, so the references into it can't " +
                        "resolve for the serialize. Enable that master first. Nothing was written.");
                ISkyrimModGetter mov;
                try { mov = SkyrimMod.CreateFromBinaryOverlay(mp, SkyrimRelease.SkyrimSE); }
                catch (Exception ex)
                {
                    return MergeBuildResult.Fail(
                        $"cannot merge: donor master '{mfn}' could not be opened for the serialize ({WriteEngine.Describe(ex)}). Nothing was written.");
                }
                masterOverlays.Add((IDisposable)mov); resolved.Add(mov);
            }
            try { WriteEngine.WriteInPlace(m, resolved, outPath); }
            catch (Exception ex)
            {
                return MergeBuildResult.Fail(
                    $"writing the merged plugin failed (serialize or commit; nothing partial left): {WriteEngine.Describe(ex)}.");
            }
        }
        finally { foreach (var d in masterOverlays) { try { d.Dispose(); } catch { /* best-effort; never mask the write result */ } } }

        // Prefer masters read from the written header; fall back to the computed superset if readback fails.
        IReadOnlyList<string> writtenMasters = masters;
        long bytes = 0;
        try
        {
            bytes = new FileInfo(outPath).Length;
            using var wr = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE);
            writtenMasters = wr.ModHeader.MasterReferences.Select(x => x.Master.FileName.String).ToList();
        }
        catch { /* best-effort read-back; the union is a correct superset */ }
        return new MergeBuildResult(true, null, writtenMasters, mr.RecordsCopied, mr.RecordsRenumbered, mr.Conflicts, bytes);
    }

    /// <summary>Creates flat, nested, interior-cell, or exterior-cell records in a patch or staging plugin.</summary>
    /// <remarks>
    /// The method validates every specification before mutation, allocates FormKeys in specification order, resolves
    /// same-call references, applies Creation Kit parity defaults, serializes once, and reopens the artifact. A non-null
    /// <paramref name="inPlaceTarget"/> selects the consent-gated in-place lane owned by the service.
    /// </remarks>
    public static CreateOutcome CreateRecords(
        LoadOrderResolver resolver, CorpusRulebook rulebook,
        IReadOnlyList<CreateSpec> specs, string outPath, bool extend, bool fullReadback = false, string? inPlaceTarget = null)
    {
        if (specs.Count == 0) return CreateOutcome.Fail("no records to create supplied.");
        bool inPlace = inPlaceTarget is not null;

        // Scope all overlays and link caches to this call and use one captured load-order view.
        using var session = resolver.OpenSession();
        var view = resolver.Capture();

        // Open the destination before validation so parent records already owned by it can participate in nesting.
        var fileName = Path.GetFileName(outPath);
        SkyrimMod patchMod;
        if (inPlace)
        {
            // Refuse inactive, excluded, missing, or unreadable in-place targets before mutation.
            if (!view.ContainsPlugin(inPlaceTarget!))
                return CreateOutcome.Fail($"in-place target '{inPlaceTarget}' is not an active plugin in the load order.{view.AbsenceClause(inPlaceTarget!)}");
            if (view.ExcludedPlugins.TryGetValue(inPlaceTarget!, out var excluded))
                return CreateOutcome.Fail(
                    $"cannot create into '{inPlaceTarget}' in place: it was EXCLUDED from this session ({excluded}) — houseCARL won't " +
                    "re-serialize a plugin it can't fully parse (that would risk dropping a record it couldn't read, Q3). The file is UNTOUCHED.");
            if (!File.Exists(outPath))
                return CreateOutcome.Fail($"in-place target '{fileName}' not found on disk at {outPath} — the file is untouched.");
            try { patchMod = SkyrimMod.CreateFromBinary(outPath, SkyrimRelease.SkyrimSE); }
            catch (Exception ex)
                { return CreateOutcome.Fail($"cannot open '{fileName}' to create into it in place ({WriteEngine.Describe(ex)}) — a plugin Mutagen can't parse is refused, not re-emitted minus what it couldn't read (Q3). The file is UNTOUCHED."); }
        }
        else if (extend)
        {
            if (!File.Exists(outPath))
                return CreateOutcome.Fail($"cannot extend: no existing patch at {outPath}. Omit into= to create it fresh.");
            try { patchMod = SkyrimMod.CreateFromBinary(outPath, SkyrimRelease.SkyrimSE); }
            catch (Exception ex) { return CreateOutcome.Fail($"cannot open patch to extend ({fileName}): {ex.GetType().Name}: {ex.Message}"); }
        }
        else
        {
            patchMod = new SkyrimMod(new ModKey(Path.GetFileNameWithoutExtension(outPath), ModType.Plugin), SkyrimRelease.SkyrimSE);
        }
        if (!string.Equals(patchMod.ModKey.FileName.String, fileName, StringComparison.OrdinalIgnoreCase))
            return CreateOutcome.Fail($"{(inPlace ? "in-place" : "patch")} ModKey '{patchMod.ModKey.FileName}' must match {(inPlace ? "the target filename" : "output filename")} '{fileName}'.");

        // Validate all Editor IDs, types, parent routes, cell coordinates, and field edits before creating anything.
        var problems = new List<string>();
        var seenEdid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var declaredEdidType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // A record may reference itself or an earlier specification, but never a later specification.
        var priorEditorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parentPlans = new List<(IMajorRecordGetter? body, string? winnerPlugin, string? sibling, IMajorRecord? patchParent)?>(specs.Count);
        var cellKinds = new CellCreate[specs.Count];
        for (int i = 0; i < specs.Count; i++)
        {
            var s = specs[i];
            parentPlans.Add(null);   // flat default; overwritten on the nested path
            if (string.IsNullOrWhiteSpace(s.EditorId)) { problems.Add($"{s.RecordType}: an editorid is required to create a record (it's how the record is referenced)."); continue; }
            if (!seenEdid.Add(s.EditorId)) { problems.Add($"editorid '{s.EditorId}' is used by more than one record in this call — each created record needs a distinct editorid."); continue; }
            declaredEdidType[s.EditorId] = s.RecordType;
            priorEditorIds.Add(s.EditorId);   // declared ⇒ referenceable by its own edits (self) and by LATER specs

            if (s.ParentRef is null)
            {
                if (IsCellType(s.RecordType))
                {
                    // A parentless Cell without a grid is an interior cell.
                    if (s.Grid is not null) { problems.Add($"Cell '{s.EditorId}': an exterior cell (grid=) needs parent= a Worldspace; an interior cell takes no parent and no grid."); continue; }
                    cellKinds[i] = CellCreate.Interior;
                }
                else if (!WriteEngine.CanCreateType(s.RecordType, out var why)) { problems.Add($"{s.RecordType} '{s.EditorId}': {why}"); continue; }
            }
            else
            {
                Type? parentType = null;
                if (FormKey.TryFactory(s.ParentRef, out var parentFk))
                {
                    // Preserve an in-place target's own parent body rather than replacing it with the current winner.
                    if (inPlace && patchMod.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == parentFk) is { } ownParent)
                    {
                        parentType = WriteEngine.ResolveConcreteRecordType(RecordNaming.StripOverlay(ownParent.GetType().Name));
                        parentPlans[i] = (null, null, null, ownParent);
                    }
                    else if (view.ResolveWinner(parentFk) is { } w)
                    {
                        // A foreign existing parent is overridden into the destination so it can own the new child.
                        var parentBody = view.GetRecord(session, w.WinnerPlugin, parentFk);
                        if (parentBody is null) { problems.Add($"{s.RecordType} '{s.EditorId}': parent {parentFk} winner '{w.WinnerPlugin}' did not yield it on fetch (a load-order inconsistency)."); continue; }
                        parentType = WriteEngine.ResolveConcreteRecordType(RecordNaming.StripOverlay(parentBody.GetType().Name));
                        parentPlans[i] = (parentBody, w.WinnerPlugin, null, null);
                    }
                    else if (patchMod.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == parentFk) is { } patchParent)
                    {
                        // A parent defined by the destination is already mutable and needs no new override.
                        parentType = WriteEngine.ResolveConcreteRecordType(RecordNaming.StripOverlay(patchParent.GetType().Name));
                        parentPlans[i] = (null, null, null, patchParent);
                    }
                    else
                    {
                        // Name both accepted parent sources and the same-call workaround when no parent exists.
                        problems.Add($"{s.RecordType} '{s.EditorId}': parent {parentFk} is not present in the load order"
                            + (extend ? " or this patch" : "") + (inPlace ? " or the target plugin" : "") + " — name an existing parent, or create the parent and this "
                            + "child in ONE call (a same-call sibling parent, by the parent's editorid).");
                        continue;
                    }
                }
                else   // a same-call sibling parent — must be DECLARED EARLIER in this call (topic before its lines)
                {
                    if (s.ParentRef.Equals(s.EditorId, StringComparison.OrdinalIgnoreCase) || !declaredEdidType.TryGetValue(s.ParentRef, out var parentCatalog))
                    { problems.Add($"{s.RecordType} '{s.EditorId}': parent '{s.ParentRef}' is neither an existing FormID nor a record created EARLIER in this call — create the parent (e.g. the topic) before its children, in spec order."); continue; }
                    parentType = WriteEngine.ResolveConcreteRecordType(parentCatalog);
                    parentPlans[i] = (null, null, s.ParentRef, null);
                }
                if (parentType is null) { problems.Add($"{s.RecordType} '{s.EditorId}': could not resolve the parent's record type."); continue; }
                if (IsCellType(s.RecordType))
                {
                    // A cell with a Worldspace parent requires a grid and is placed in the exterior block tree.
                    if (s.Grid is null) { problems.Add($"Cell '{s.EditorId}': a Cell with parent= but no grid= is ambiguous — an exterior cell needs grid=<X,Y> under a Worldspace; an interior cell takes no parent."); continue; }
                    if (parentType != typeof(Worldspace)) { problems.Add($"Cell '{s.EditorId}': an exterior cell nests under a Worldspace, but parent '{s.ParentRef}' resolved to a {parentType.Name}."); continue; }
                    if (!TryParseGrid(s.Grid, out _, out _)) { problems.Add($"Cell '{s.EditorId}': grid '{s.Grid}' must be two integers \"X,Y\" (e.g. \"5,-12\")."); continue; }
                    cellKinds[i] = CellCreate.Exterior;
                }
                else if (!WriteEngine.CanCreateNested(s.RecordType, parentType, s.IntoCollection, out var nestedWhy)) { problems.Add($"{s.RecordType} '{s.EditorId}': {nestedWhy}"); continue; }
            }

            foreach (var req in s.Edits)
                // Validate @editorid references against this record and earlier specifications.
                if (rulebook.Validate(req, priorEditorIds) is { } reject) problems.Add($"{s.RecordType} '{s.EditorId}' [{Label(req)}]: {reject}");
        }
        if (problems.Count > 0)
            return CreateOutcome.Fail(
                $"refused — {problems.Count} problem(s) creating {specs.Count} record(s); NOTHING created:\n  - " + string.Join("\n  - ", problems));

        // Create every record in memory, then apply its validated edits. Flat records upsert by Editor ID; nested
        // records append because they lack a stable Editor ID that can identify a prior child.
        var created = new List<CreatedRecord>(specs.Count);
        var createdByEditorId = new Dictionary<string, IMajorRecord>(StringComparer.OrdinalIgnoreCase);
        var linkCacheByPlugin = new Dictionary<string, Mutagen.Bethesda.Plugins.Cache.ILinkCache>(StringComparer.OrdinalIgnoreCase);

        // Obtain a mutable parent from the destination, a source override, or an earlier same-call record.
        (IMajorRecord? parent, string? error) MakeSettableParent(int idx)
        {
            var plan = parentPlans[idx]!.Value;
            if (plan.patchParent is not null) return (plan.patchParent, null);
            if (plan.body is not null)
            {
                Mutagen.Bethesda.Plugins.Cache.ILinkCache? cache = null;
                if (WriteEngine.RecordNeedsSourceCache(plan.body))
                {
                    if (!linkCacheByPlugin.TryGetValue(plan.winnerPlugin!, out cache))
                        linkCacheByPlugin[plan.winnerPlugin!] = (cache = session.LinkCacheFor(plan.winnerPlugin!))!;
                }
                return (WriteEngine.GenericGetOrAddAsOverride(patchMod, plan.body, cache), null);
            }
            if (!createdByEditorId.TryGetValue(plan.sibling!, out var sib))
                return (null, $"internal: same-call parent '{plan.sibling}' for '{specs[idx].EditorId}' was not created before it — surfaced, not swallowed (Q3).");
            return (sib, null);
        }

        for (int i = 0; i < specs.Count; i++)
        {
            var s = specs[i];
            IMajorRecord rec; bool replaced = false;
            try
            {
                if (cellKinds[i] == CellCreate.Interior)
                {
                    // Interior cells file themselves into the destination's top-level Cells group.
                    rec = WriteEngine.AddInteriorCell(patchMod, s.EditorId);
                }
                else if (cellKinds[i] == CellCreate.Exterior)
                {
                    // Exterior cells are placed into a mutable Worldspace's block tree by validated grid coordinates.
                    var (wsParent, perr) = MakeSettableParent(i);
                    if (perr is not null) return CreateOutcome.Fail(perr);
                    TryParseGrid(s.Grid!, out var gx, out var gy);
                    rec = WriteEngine.AddExteriorCell(patchMod, (Worldspace)wsParent!, gx, gy, s.EditorId);
                }
                else if (s.ParentRef is null)
                {
                    (rec, replaced) = WriteEngine.GenericUpsertNew(patchMod, s.RecordType, s.EditorId);
                }
                else
                {
                    var (settableParent, perr) = MakeSettableParent(i);
                    if (perr is not null) return CreateOutcome.Fail(perr);
                    rec = WriteEngine.NestedAddNew(patchMod, settableParent!, s.RecordType, s.IntoCollection, s.EditorId);
                }
            }
            catch (Exception ex) { return CreateOutcome.Fail($"could not create {s.RecordType} '{s.EditorId}': {ex.Message}"); }

            createdByEditorId[s.EditorId] = rec;
            var ops = new List<OpResult>(s.Edits.Count);
            foreach (var rawReq in s.Edits)
            {
                // Replace validated @editorid tokens with the FormKeys allocated earlier in this loop.
                var (req, refErr) = ResolveSiblingRefs(rawReq, createdByEditorId, $"new {s.RecordType} '{s.EditorId}'");
                if (refErr is not null) return CreateOutcome.Fail(refErr);
                try { WriteEngine.ApplyVerb(rec, req); ops.Add(new OpResult(rec.FormKey, s.RecordType, Label(req), true, null, TryReadAfter(rec, req))); }
                catch (ExpectedApplyRejectionException ex)
                {
                    // Report live-state conflicts, such as duplicate keys, as expected refusals.
                    return CreateOutcome.Fail(
                        $"refused applying [{Label(req)}] to new {s.RecordType} '{s.EditorId}' ({rec.FormKey}) — {ex.Message} (nothing created)");
                }
                catch (MalformedTargetDataException ex)
                {
                    // Distinguish malformed target data from both invalid input and an engine inconsistency.
                    return CreateOutcome.Fail(
                        $"refused applying [{Label(req)}] to new {s.RecordType} '{s.EditorId}' ({rec.FormKey}) — {ex.Message} (nothing created)");
                }
                catch (Exception ex)
                {
                    return CreateOutcome.Fail(
                        $"engine error applying [{Label(req)}] to new {s.RecordType} '{s.EditorId}' ({rec.FormKey}): " +
                        $"pre-flight ACCEPTED it but the apply threw — a real inconsistency, surfaced not swallowed (Q3): {ex.GetType().Name}: {ex.Message}");
                }
            }
            // Fill a missing DialogTopic SNAM marker from its modeled subtype; never overwrite an explicit marker.
            if (rec is IDialogTopic dtopic)
            {
                switch (DialogueSubtype.NormalizeMarker(dtopic, out var marker))
                {
                    case MarkerFill.Filled:
                        ops.Add(new OpResult(rec.FormKey, s.RecordType,
                            $"SubtypeName (SNAM subtype marker) auto-set to {marker}", true, null,
                            $"{marker} — derived from Subtype={dtopic.Subtype}; a new topic with a blank marker is a load CTD (#131)"));
                        break;
                    case MarkerFill.Unmodeled:
                        // Reject an unmodeled subtype because a blank marker produces a malformed topic.
                        return CreateOutcome.Fail(
                            $"cannot create DialogTopic '{s.EditorId}': no SNAM subtype marker is modeled for Subtype={dtopic.Subtype} " +
                            $"((int){(int)dtopic.Subtype}, outside the known 0..{DialogueSubtype.Count - 1}). A blank marker is malformed " +
                            "(a new topic with a blank marker is a load CTD, #131). Use a valid Subtype, or set SubtypeName explicitly to the correct 4-char marker (nothing created).");
                    // AlreadySet: an explicit marker the author set — never overridden, nothing to report.
                }

                // Apply the Creation Kit topic-priority default only when the request never mentioned Priority.
                bool authorSetPriority = s.Edits.Any(e => e.Path.Length >= 1 &&
                    string.Equals(e.Path[0], "Priority", StringComparison.OrdinalIgnoreCase));
                if (DialogueCkParity.ApplyTopicPriorityDefault(dtopic, authorSetPriority) is { } pfill)
                    ops.Add(new OpResult(rec.FormKey, s.RecordType, pfill.Label, true, null, pfill.Reason));
            }
            // Fill Creation Kit-required dialogue and quest defaults while preserving every explicit value.
            else if (rec is IDialogResponses infoRec)
            {
                foreach (var fill in DialogueCkParity.ApplyInfoDefaults(infoRec))
                    ops.Add(new OpResult(rec.FormKey, s.RecordType, fill.Label, true, null, fill.Reason));
            }
            else if (rec is IDialogView viewRec)
            {
                foreach (var fill in DialogueCkParity.ApplyViewDefaults(viewRec))
                    ops.Add(new OpResult(rec.FormKey, s.RecordType, fill.Label, true, null, fill.Reason));
            }
            else if (rec is IDialogBranch branchRec)
            {
                foreach (var fill in DialogueCkParity.ApplyBranchDefaults(branchRec))
                    ops.Add(new OpResult(rec.FormKey, s.RecordType, fill.Label, true, null, fill.Reason));
            }
            else if (rec is IQuest questRec)
            {
                foreach (var fill in DialogueCkParity.ApplyQuestDefaults(questRec))
                    ops.Add(new OpResult(rec.FormKey, s.RecordType, fill.Label, true, null, fill.Reason));
            }
            created.Add(new CreatedRecord(rec.FormKey, s.RecordType, s.EditorId, ops, replaced));
        }

        // Release destination mappings and serialize once; referenced active plugins become lean derived masters.
        session.ReleaseOverlay(patchMod.ModKey.FileName.String);
        try
        {
            if (inPlace)
                // Reemit the whole target while preserving its counter and deriving masters from new references.
                WriteEngine.WriteInPlace(patchMod, session.AllMastersExcept(patchMod.ModKey.FileName.String), outPath);
            else
                WriteEngine.WritePatch(patchMod, session.AllMastersExcept(patchMod.ModKey.FileName.String), outPath);
        }
        catch (Exception ex) { return CreateOutcome.Fail($"writing {(inPlace ? $"'{fileName}' in place" : "the patch")} after create failed (serialize or commit; the existing file is untouched): {WriteEngine.Describe(ex)}"); }

        // Reopen the output to report derived masters and optional full-record verification.
        IReadOnlyList<string> masters = Array.Empty<string>();
        IReadOnlyList<FullReadback>? readBack = null;
        long bytes = 0;
        ISkyrimModGetter? back = null;
        try
        {
            back = SkyrimMod.CreateFromBinaryOverlay(outPath, SkyrimRelease.SkyrimSE);
            masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
            bytes = new FileInfo(outPath).Length;
            if (fullReadback) readBack = ReadBackInFull(back, created.Select(c => c.FormKey));
        }
        catch (Exception ex) { return CreateOutcome.Fail($"records created + written but the patch could not be re-opened to confirm: {ex.Message}"); }
        finally { (back as IDisposable)?.Dispose(); }

        return new CreateOutcome(true, null, outPath, extend, created, masters, bytes) { ReadBack = readBack, InPlace = inPlace };
    }

    /// <summary>Creates records in an existing staging plugin through the shared creation pipeline.</summary>
    /// <remarks>The service enforces consent and Amethyst redeployment confirmation before calling this entry point.</remarks>
    public static CreateOutcome CreateRecordsInPlace(
        LoadOrderResolver resolver, CorpusRulebook rulebook,
        IReadOnlyList<CreateSpec> specs, string targetPath, string targetName, bool fullReadback = true)
        => CreateRecords(resolver, rulebook, specs, targetPath, extend: false, fullReadback, inPlaceTarget: targetName);

    /// <summary>Reads each requested record in full from a written artifact or dry-run model.</summary>
    /// <remarks>
    /// Readback never throws into an already successful write. Each missing record receives a named verification error
    /// so callers do not mistake a readback problem for a lost write and repeat non-idempotent operations.
    /// </remarks>
    static IReadOnlyList<FullReadback> ReadBackInFull(ISkyrimModGetter back, IEnumerable<FormKey> targets, bool inMemory = false)
    {
        var order = new List<FormKey>();
        var want = new HashSet<FormKey>();
        foreach (var fk in targets) if (want.Add(fk)) order.Add(fk);

        var found = new Dictionary<FormKey, RecordFields>();
        string? walkError = null;
        try
        {
            foreach (var rec in back.EnumerateMajorRecords())
                if (want.Contains(rec.FormKey) && !found.ContainsKey(rec.FormKey))
                    found[rec.FormKey] = ReadEngine.ReadFields(rec, null, FullReadbackDepth);
        }
        catch (Exception ex) { walkError = $"the full read-back walk failed: {ex.GetType().Name}: {ex.Message}"; }

        var result = new List<FullReadback>(order.Count);
        foreach (var fk in order)
            result.Add(found.TryGetValue(fk, out var rf)
                ? new FullReadback(fk, rf, null)
                : new FullReadback(fk, null, walkError is not null
                    ? (inMemory
                        ? $"{walkError} — this was a DRY RUN read of the in-memory would-be content; nothing was written."
                        : $"{walkError} — the WRITE ITSELF SUCCEEDED (the patch was serialized and re-opened); inspect the patch in xEdit; do not re-issue the ops.")
                    : (inMemory
                        ? $"the in-memory would-be content did not yield {fk} — a real inconsistency, surfaced not swallowed (Q3); nothing was written."
                        : $"the written file did not yield {fk} on re-open — a real inconsistency, surfaced not swallowed (Q3); inspect the patch in xEdit.")));
        return result;
    }

    /// <summary>Formats an edit as <c>Verb path[key] = value</c>.</summary>
    static string Label(WriteRequest r) =>
        $"{r.Verb} {string.Join('.', r.Path)}{(r.Key is not null ? "[" + r.Key + "]" : "")}{(r.Value is not null ? " = " + r.Value : "")}";

    /// <summary>Replaces same-call <c>@editorid</c> tokens with allocated FormKeys throughout a write request.</summary>
    /// <remarks>The original immutable request is returned when no substitution is needed.</remarks>
    static (WriteRequest req, string? error) ResolveSiblingRefs(
        WriteRequest r, IReadOnlyDictionary<string, IMajorRecord> created, string onWhat)
    {
        string? err = null;
        string? One(string? v)
        {
            if (err is not null || !WriteEngine.IsSameCallSiblingRef(v, out var ed)) return v;
            if (created.TryGetValue(ed, out var rec)) return rec.FormKey.ToString();
            err = $"internal: same-call reference '@{ed}' on {onWhat} resolved to no record created in this call — " +
                  "pre-flight should have caught it; surfaced, not swallowed (Q3).";
            return v;
        }
        var value = One(r.Value);
        var values = r.Values;
        if (values is not null && Array.Exists(values, v => WriteEngine.IsSameCallSiblingRef(v, out _)))
            values = Array.ConvertAll(values, v => One(v)!);
        var strct = r.Struct;
        if (strct is not null)
        {
            var (rs, sErr) = ResolveStructSiblingRefs(strct, created, onWhat);
            err ??= sErr;
            strct = rs;
        }
        // Resolve every structure in a batch while cloning only requests that actually change.
        var structs = r.Structs;
        if (structs is not null)
        {
            List<StructSpec>? repl = null;
            for (int i = 0; i < structs.Count; i++)
            {
                var (rs2, e2) = ResolveStructSiblingRefs(structs[i], created, onWhat);
                err ??= e2;
                if (repl is null && !ReferenceEquals(rs2, structs[i])) repl = new List<StructSpec>(structs);
                if (repl is not null) repl[i] = rs2;
            }
            if (repl is not null) structs = repl;
        }
        if (err is not null) return (r, err);
        if (ReferenceEquals(value, r.Value) && ReferenceEquals(values, r.Values) && ReferenceEquals(strct, r.Struct)
            && ReferenceEquals(structs, r.Structs))
            return (r, null);
        return (new WriteRequest
        {
            RecordType = r.RecordType, Path = r.Path, Verb = r.Verb, Key = r.Key,
            Value = value, Values = values, Entries = r.Entries, Struct = strct, Structs = structs,
        }, null);
    }

    /// <summary>Resolves same-call references in structure fields and recursively nested writes.</summary>
    static (StructSpec spec, string? error) ResolveStructSiblingRefs(
        StructSpec sp, IReadOnlyDictionary<string, IMajorRecord> created, string onWhat)
    {
        var fields = sp.Fields;
        if (fields is not null && fields.Values.Any(v => WriteEngine.IsSameCallSiblingRef(v, out _)))
        {
            var nf = new Dictionary<string, string>(fields.Count);
            foreach (var kv in fields)
            {
                if (WriteEngine.IsSameCallSiblingRef(kv.Value, out var ed))
                {
                    if (!created.TryGetValue(ed, out var rec))
                        return (sp, $"internal: same-call reference '@{ed}' on {onWhat} resolved to no record created " +
                                    "in this call — pre-flight should have caught it; surfaced, not swallowed (Q3).");
                    nf[kv.Key] = rec.FormKey.ToString();
                }
                else nf[kv.Key] = kv.Value;
            }
            fields = nf;
        }
        var sets = sp.Sets;
        if (sets is not null)
        {
            List<WriteRequest>? ns = null;
            for (int i = 0; i < sets.Count; i++)
            {
                var (rr, e) = ResolveSiblingRefs(sets[i], created, onWhat);
                if (e is not null) return (sp, e);
                if (ns is null && !ReferenceEquals(rr, sets[i])) ns = new List<WriteRequest>(sets);
                if (ns is not null) ns[i] = rr;
            }
            if (ns is not null) sets = ns;
        }
        if (ReferenceEquals(fields, sp.Fields) && ReferenceEquals(sets, sp.Sets)) return (sp, null);
        return (new StructSpec { Type = sp.Type, Fields = fields, CtorArgs = sp.CtorArgs, Sets = sets }, null);
    }

    /// <summary>Returns a best-effort post-edit leaf value without affecting write success.</summary>
    static string? TryReadAfter(IMajorRecord ov, WriteRequest req)
    {
        try
        {
            var leaf = string.Join('.', req.Path);
            var read = ReadEngine.ReadFields(ov, new[] { leaf }, containerHint: null);
            var f = read.Fields.FirstOrDefault(x => x.Path == leaf) ?? read.Fields.FirstOrDefault();
            return f is null ? null : (f.HasValue ? f.Token : f.Note);
        }
        catch { return null; }
    }

    /// <summary>Reads an edited leaf once and derives detailed and compact verification descriptions.</summary>
    /// <remarks>Failures return null descriptions and never change write success.</remarks>
    static (string? After, string? Landed) DescribeApplied(IMajorRecord ov, WriteRequest req)
    {
        try
        {
            var leaf = string.Join('.', req.Path);
            var read = ReadEngine.ReadFields(ov, new[] { leaf }, containerHint: null);
            var f = read.Fields.FirstOrDefault(x => x.Path == leaf) ?? read.Fields.FirstOrDefault();
            if (f is null) return (null, null);
            var after = f.HasValue ? f.Token : f.Note;
            // Scalars reuse their value. Containers identify the touched element and appended count when available.
            int added = req.Verb == "Add" ? (req.Structs?.Count ?? 1) : 1;
            var landed = f.HasValue ? f.Token : (ReadEngine.TouchedElement(ov, req.Path, req.Verb, req.Key, added) ?? f.Note);
            return (after, landed);
        }
        catch { return (null, null); }
    }
}
