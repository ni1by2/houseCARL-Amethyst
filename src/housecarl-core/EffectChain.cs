using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>
/// The effect-chain resolver (housecarl_effect_chain — gap 2026-06-08): given a MagicEffect (MGEF), find every
/// record that APPLIES it and the magnitude/area/duration of the matching effect entry. It collapses the hand-trace
/// "cross_plugin_query references=&lt;MGEF&gt; type=SPEL → read each hit's Effects[].Data → keep the entry whose
/// BaseEffect is the MGEF, then repeat for ENCH/ALCH/SCRL/INGR" into one call. The inverse-by-magnitude of
/// references=: that says WHICH carriers reference the effect; this says which entry, and at what strength.
///
/// Two seams, mirroring the where= predicate (<see cref="FieldPredicateSet"/> ← <c>ValuePredicateProbe</c>):
///   • <see cref="Match"/> — PURE over an in-hand body (no resolver, no I/O), so the regression guard tests the real
///     matcher directly against a known-magnitude fixture.
///   • <see cref="Resolve"/> — the end-to-end service path (Q3 typed-match gate → winner-body scan → assemble),
///     drivable from a <see cref="LoadOrderResolver"/> built over synthetic plugins, so the guard covers the gate too.
///
/// Scope is the five records the library models with an <c>Effects</c> list — Spell/ObjectEffect/Ingestible/
/// Scroll/Ingredient (SPEL/ENCH/ALCH/SCRL/INGR). That is EVERY effect-bearing record by construction, not a chosen
/// subset (cornerstone §3): the element type is the SAME <see cref="IEffectGetter"/> across all five, so once
/// <see cref="EffectsOf"/> hands back the list, extraction is uniform and the only per-type code is the switch that
/// reaches the list.
/// </summary>
public static class EffectChain
{
    /// <summary>One matching effect entry on a carrier: its index in the carrier's Effects list and the list size
    /// (so a caller can render "[effect i/n]"), and the magnitude/area/duration of THAT entry — the trace's payload.</summary>
    /// <param name="Index">Zero-based position of the matching entry in its carrier's Effects list.</param>
    /// <param name="Count">Total number of effect entries on the carrier.</param>
    /// <param name="Magnitude">EFIT magnitude, or zero when the matching entry has no modeled data.</param>
    /// <param name="Area">EFIT area, or zero when the matching entry has no modeled data.</param>
    /// <param name="Duration">EFIT duration, or zero when the matching entry has no modeled data.</param>
    public readonly record struct EffectHit(int Index, int Count, float Magnitude, int Area, int Duration);

    /// <summary>The effect-bearing getter Types — the scan scope. Every Skyrim record the library models with an
    /// Effects list; a query type-narrow is validated against this exact set (a non-member is refused, never a
    /// silent empty scan). Listed once here so both the scan default and the narrow-validation share one source.</summary>
    public static readonly IReadOnlyList<Type> CarrierTypes = new[]
    {
        typeof(ISpellGetter), typeof(IObjectEffectGetter), typeof(IIngestibleGetter),
        typeof(IScrollGetter), typeof(IIngredientGetter),
    };

    /// <summary>The carrier's Effects list if <paramref name="body"/> is one of the five effect-bearing records, else
    /// null. An explicit switch over the known set — NOT reflection: type-safe, and a sixth effect-bearing record in a
    /// future library version is then a deliberate, surfaced boundary (the guard's MATCH-ALL arm would catch the
    /// new type's absence) rather than a silent reflective guess that might mis-read an unrelated "Effects" member.</summary>
    /// <param name="body">One winning record body from the captured load order.</param>
    /// <returns>The modeled Effects collection, or null when the record is not a supported carrier type.</returns>
    public static IReadOnlyList<IEffectGetter>? EffectsOf(IMajorRecordGetter body) => body switch
    {
        ISpellGetter s => s.Effects,
        IObjectEffectGetter e => e.Effects,
        IIngestibleGetter i => i.Effects,
        IScrollGetter c => c.Effects,
        IIngredientGetter g => g.Effects,
        _ => null,
    };

    /// <summary>The effect entries of <paramref name="body"/> whose BaseEffect is <paramref name="mgef"/>, each with
    /// its magnitude/area/duration. Empty if the body carries no effects or none reference the MGEF. An effect with an
    /// unset BaseEffect (FormKey.Null) is skipped — never matched, never throws. Pure: the guard's ground truth.</summary>
    /// <param name="body">Carrier candidate to inspect without further load-order or filesystem access.</param>
    /// <param name="mgef">Exact MagicEffect FormKey each effect entry must reference.</param>
    /// <returns>All matching entries in source-list order; never null.</returns>
    public static IReadOnlyList<EffectHit> Match(IMajorRecordGetter body, FormKey mgef)
    {
        var effects = EffectsOf(body);
        if (effects is null || effects.Count == 0) return Array.Empty<EffectHit>();
        List<EffectHit>? hits = null;
        for (int i = 0; i < effects.Count; i++)
        {
            var eff = effects[i];
            if (eff.BaseEffect.FormKey != mgef) continue;   // FormKey.Null (an unset base) never equals a real MGEF
            // Data (the EFIT magnitude/area/duration) is required in practice but modeled nullable; a matching effect
            // with absent data is still a carrier — report it at zero (honest "no magnitude data"), never an NRE that
            // the scan's fault-isolation would then mis-count as an unparseable record.
            var d = eff.Data;
            (hits ??= new List<EffectHit>()).Add(new EffectHit(i, effects.Count, d?.Magnitude ?? 0f, d?.Area ?? 0, d?.Duration ?? 0));
        }
        return hits ?? (IReadOnlyList<EffectHit>)Array.Empty<EffectHit>();
    }

    /// <summary>Resolve the effect chain for <paramref name="mgef"/> over the order <paramref name="resolver"/> holds,
    /// scanning the winner bodies of <paramref name="scope"/> (a subset of <see cref="CarrierTypes"/>). One
    /// <see cref="LoadOrderResolver.Capture"/>; per-record fault isolation (one Mutagen-unparseable body is excluded +
    /// accounted, never aborts the call, never a silent skip); holds nothing.
    ///
    /// Q3 GATE FIRST (the gap's explicit ask — never a silent "0 carriers" on a bad target): the FormID must resolve
    /// to a MagicEffect. An absent FormID or a non-MGEF (a WEAP, an NPC_, …) fails loud with the actual type named; a
    /// VALID-but-unused MGEF returns a clean zero result (Error null) so the caller can tell "no carriers" from "bad
    /// id" by the absence of an error and the resolved <see cref="EffectChainResult.MgefEditorId"/> in the header.</summary>
    /// <param name="resolver">Source of one immutable load-order snapshot and short-lived record sessions.</param>
    /// <param name="mgef">Target FormKey, which must resolve to a winning MagicEffect record.</param>
    /// <param name="scope">Supported carrier getter types to scan; normally a subset of <see cref="CarrierTypes"/>.</param>
    /// <param name="limit">Maximum number of rows retained; matching entries beyond it still contribute to the total.</param>
    /// <returns>A successful, possibly capped chain, or a recoverable named failure with no rows.</returns>
    public static EffectChainResult Resolve(LoadOrderResolver resolver, FormKey mgef, IReadOnlyList<Type> scope, int limit)
    {
        var view = resolver.Capture();

        // --- Q3 typed-match gate: the target must be an MGEF. Cheap — one winner-body fetch off this view. ---
        var w = view.ResolveWinner(mgef);
        if (w is null)
            return EffectChainResult.Fail(
                $"no record with FormID {mgef} in the load order. effect_chain needs a MagicEffect (MGEF) the active order defines.");
        string mgefEid;
        using (var session = resolver.OpenSession())
        {
            var mbody = view.GetRecord(session, w.Value.WinnerPlugin, mgef);
            if (mbody is null)
                return EffectChainResult.Fail(
                    $"winner '{w.Value.WinnerPlugin}' did not yield {mgef} on fetch — cannot confirm it is a MagicEffect.");
            if (mbody is not IMagicEffectGetter mr)
                return EffectChainResult.Fail(
                    $"{mgef} resolves to a {RecordNaming.StripOverlay(mbody.GetType().Name)}, not a MagicEffect — effect_chain " +
                    "needs an MGEF. (To find what references an arbitrary record, use cross_plugin_query references=.)");
            mgefEid = mr.EditorID ?? "<none>";
        }

        // --- scan the winner bodies of the scope; collect every matching effect entry. ---
        var rows = new List<EffectChainRow>();
        int total = 0, unscannable = 0;
        var samples = new List<string>();
        try
        {
            foreach (var (fk, _, body) in view.WinnerRecordsOfType(scope))
            {
                // PER-RECORD FAULT ISOLATION (twin of the cross_plugin_query scan): Match lazily parses subrecord
                // content, so ONE record Mutagen can't parse is excluded + accounted, not an opaque whole-call abort.
                try
                {
                    var hits = Match(body, mgef);
                    if (hits.Count == 0) continue;
                    string type = RecordNaming.StripOverlay(body.GetType().Name);
                    string winner = view.ResolveWinner(fk)?.WinnerPlugin ?? "?";
                    foreach (var h in hits)
                    {
                        total++;
                        if (rows.Count < limit)
                            rows.Add(new EffectChainRow(fk, type, body.EditorID, winner, h.Index, h.Count, h.Magnitude, h.Area, h.Duration));
                    }
                }
                catch (Exception ex)
                {
                    unscannable++;
                    if (samples.Count < 3) samples.Add($"{fk} — {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        // Anything escaping the stream itself (a bad scope type, a build fault) gets a NAMED failure — never the
        // MCP layer's generic "An error occurred invoking …" as the terminal diagnostic for a data failure (Q3).
        catch (Exception ex) { return EffectChainResult.Fail($"scan aborted: {ex.GetType().Name}: {ex.Message}"); }

        string? scanNote = unscannable == 0 ? null
            : $"note: {unscannable} record instance(s) could not be scanned (Mutagen could not parse their content) and were skipped: "
              + string.Join("; ", samples)
              + (unscannable > samples.Count ? $"; and {unscannable - samples.Count} more" : "")
              + ". Inspect one with read_record (per-field fault isolation applies).";

        return new EffectChainResult(mgef, mgefEid, rows, total, total > rows.Count, null, scanNote);
    }
}

/// <summary>One carrier-row of an effect chain: the carrier record, its catalog type + editorid + load-order winner,
/// and the matching effect entry's position (<paramref name="EffectIndex"/>/<paramref name="EffectCount"/>) and
/// magnitude/area/duration. A carrier that applies the MGEF in more than one entry yields one row per entry.</summary>
/// <param name="Carrier">FormKey of the effect-bearing record.</param>
/// <param name="Type">Catalog record type with Mutagen overlay suffixes removed.</param>
/// <param name="EditorId">Optional editor identifier from the winning carrier body.</param>
/// <param name="Winner">Filename of the plugin providing the winning carrier override.</param>
/// <param name="EffectIndex">Zero-based position of this matching effect entry.</param>
/// <param name="EffectCount">Total number of effect entries on the carrier.</param>
/// <param name="Magnitude">Modeled EFIT magnitude, or zero when EFIT data is absent.</param>
/// <param name="Area">Modeled EFIT area, or zero when EFIT data is absent.</param>
/// <param name="Duration">Modeled EFIT duration, or zero when EFIT data is absent.</param>
public sealed record EffectChainRow(
    FormKey Carrier, string Type, string? EditorId, string Winner,
    int EffectIndex, int EffectCount, float Magnitude, int Area, int Duration);

/// <summary>The result of <see cref="EffectChain.Resolve"/>: the resolved MGEF (+ its editorid, the typed-match proof
/// for the header), the carrier rows (capped at the caller's limit), the TRUE total, the capped flag, an optional Q3
/// scan note, and — on the Q3 gate — a recoverable <see cref="Error"/> with no rows.</summary>
/// <param name="Mgef">Validated target MagicEffect FormKey; default only on failure.</param>
/// <param name="MgefEditorId">Target editor identifier, <c>&lt;none&gt;</c> when unset, or empty on failure.</param>
/// <param name="Rows">Retained carrier entries in winner-scan order.</param>
/// <param name="Total">True number of matching entries, including rows omitted by the limit.</param>
/// <param name="Capped">Whether <paramref name="Total"/> exceeds the retained row count.</param>
/// <param name="Error">Recoverable validation or scan failure; null on success.</param>
/// <param name="ScanNote">Optional accounting for individual records skipped after parse faults.</param>
public sealed record EffectChainResult(
    FormKey Mgef, string MgefEditorId, IReadOnlyList<EffectChainRow> Rows,
    int Total, bool Capped, string? Error, string? ScanNote)
{
    /// <summary>Whether the typed target gate and complete carrier stream succeeded.</summary>
    public bool Success => Error is null;

    /// <summary>Constructs the uniform empty failure shape used by validation and stream-level faults.</summary>
    /// <param name="error">Actionable diagnostic suitable for returning through the MCP tool.</param>
    /// <returns>An unsuccessful result with no target identity, rows, or scan note.</returns>
    public static EffectChainResult Fail(string error) =>
        new(default, "", Array.Empty<EffectChainRow>(), 0, false, error, null);
}
