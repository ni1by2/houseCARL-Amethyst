using System.Globalization;
using System.Reflection;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>One field read off a record: a round-trippable <see cref="Token"/> when <see cref="HasValue"/> is
/// true, else a <see cref="Note"/> explaining why there is no value (absent optional, no such field, a non-leaf
/// container, or an isolated read fault). The public, structured form of the internal <c>LeafRead</c> — what the
/// MCP server's read tools emit.
///
/// <para><see cref="Display"/> is a DISPLAY-ONLY annotation the render layer appends in parentheses after the
/// value — NOT part of the round-trip token (the write surface reads <see cref="Token"/>, the round-trip oracle
/// drives the internal <c>LeafRead</c>, and <c>FieldsDiff</c> compares Token/Note), so it is invisible to write,
/// read-proof, and diff. Used to decode a value that is correct but opaque — a biped-slot bitmask into its slot
/// numbers (HCBR-2026-07-12) — without disturbing the token that must round-trip. Null on every leaf that needs
/// no annotation.</para>
///
/// <para><see cref="Link"/> is the resolve_names annotation (Wave 2 / P7): when a leaf's <see cref="Token"/> is a
/// form reference (a token that round-trips to a FormKey), the SERVICE layer resolves its target's identity
/// (editorid/name) against the load order and hangs it here. Like <see cref="Display"/> it is DISPLAY-ONLY — never
/// part of the round-trip <see cref="Token"/>, so it is invisible to write, read-proof, and diff. Populated by the
/// service (which holds the resolver), never by the core read (which reads one record's bytes and cannot resolve a
/// target). Null unless resolve_names was requested and the leaf carries a resolvable FormKey.</para></summary>
/// <param name="Path">The canonical dotted/bracketed record-field path that was requested or discovered.</param>
/// <param name="HasValue">True only when <paramref name="Token"/> contains a writable round-trip value.</param>
/// <param name="Token">The write-compatible value text, or null when the field has no readable scalar value.</param>
/// <param name="Note">A human-readable absence, container, or read-failure explanation when no token exists.</param>
/// <param name="Display">Optional human-only decoding that never changes the token.</param>
/// <param name="Link">Optional resolved identity for a FormKey token, populated outside the core reader.</param>
public sealed record FieldValue(string Path, bool HasValue, string? Token, string? Note, string? Display = null, ResolvedRef? Link = null);

/// <summary>The resolved identity of a form reference — the shared contract behind housecarl_resolve (P3, a full
/// row) and the resolve_names field annotation (P7). <see cref="Resolved"/> false ⇒ the FormKey is valid but not
/// present in the active order (a dangling target — named, never dropped or guessed, Q3). <see cref="Error"/> is set
/// only on the housecarl_resolve path when the INPUT string is not a legal FormID at all. When used for the P7
/// annotation it is DISPLAY-ONLY: it never replaces the leaf's round-trip <see cref="FieldValue.Token"/>.</summary>
/// <param name="Token">The original FormKey-like text being resolved.</param>
/// <param name="Resolved">True when the FormKey exists in the active load-order snapshot.</param>
/// <param name="Type">The resolved record type, or null for a dangling/invalid reference.</param>
/// <param name="EditorId">The target record's optional EditorID.</param>
/// <param name="Name">The target record's optional translated display name.</param>
/// <param name="Winner">The plugin filename supplying the resolved winning record.</param>
/// <param name="Error">An input-validation error; null for valid-but-dangling FormKeys.</param>
public sealed record ResolvedRef(
    string Token, bool Resolved, string? Type = null, string? EditorId = null,
    string? Name = null, string? Winner = null, string? Error = null);

/// <summary>A located record read out as structured fields — the public (params)->(result) result the MCP
/// server's read tools return (the §8.4 read cleave). Identity (<see cref="Type"/> / <see cref="FormKey"/> /
/// <see cref="EditorId"/>) plus the requested (or all modeled) field reads.</summary>
/// <param name="Type">The normalized Mutagen record type.</param>
/// <param name="FormKey">The canonical record identifier.</param>
/// <param name="EditorId">The record's optional EditorID.</param>
/// <param name="Fields">Field results in requested or modeled enumeration order.</param>
public sealed record RecordFields(string Type, string FormKey, string? EditorId, IReadOnlyList<FieldValue> Fields);

/// <summary>
/// Step 6 — the reflection-driven READ surface. The symmetric partner to <see cref="WriteEngine"/>.
///
/// Where the write engine coerces a string token INTO a typed value and Sets it
/// (<c>WriteEngine.Coerce</c>), the read engine reflects a record's modeled field OUT to a string
/// token that is the faithful <b>inverse of Coerce</b> — so reading a value and writing that exact
/// token straight back is a byte-level no-op. That inverse is enforced by construction, not by
/// inspection: <c>ReadProof</c>'s round-trip oracle drives every coercible leaf the write surface
/// drives and asserts read→write-back is byte-identical, so any drift between this emitter and
/// Coerce fails loud (the read analog of write-proof).
///
/// Scope (Aaron 2026-06-01): PER-PLUGIN read — "read record R exactly as plugin P defines it", the
/// mirror of <c>set_field</c>. Load-order winning-record resolution waits for the load-order
/// simulation layer (a later wave; the conflict tree builds on it).
///
/// Navigation is REUSED from the write engine (<c>ParseSegment</c> / <c>ResolveProperty</c> /
/// <c>StepIntoElement</c>) so read and write can never disagree on how a path resolves — but the
/// read walk never materialises an absent substruct (reading must not mutate; an absent optional
/// is surfaced, not created). Per-record/per-leaf fault isolation: a Mutagen-unparseable field
/// names itself loud and never crashes the read (Q3; the product-read-path requirement logged at
/// the write-surface completion sweep).
///
/// Modes: <c>read</c> (resolve a record + emit its fields as round-trippable tokens).
/// </summary>
public static class ReadEngine
{
    /// <summary>The outcome of reading one leaf. <see cref="HasValue"/> ⇒ <see cref="Token"/> is a
    /// round-trippable value (the inverse of Coerce). Otherwise <see cref="Note"/> explains why there
    /// is no value to round-trip (absent optional, no such field, a non-leaf container, or an
    /// isolated read fault). The round-trip oracle drives ONLY <see cref="HasValue"/> reads.
    ///
    /// <para><see cref="Flags"/> is additive METADATA carried only for a <c>[Flags]</c> enum leaf: the underlying
    /// bit pattern + enum type, so the query predicate (<c>where=</c>) can bit-test (<c>has</c>) and compare
    /// numerically WITHOUT tripping over the name/number rendering split that <c>[Flags].ToString()</c> produces
    /// (named bits → "Body"; an unnamed modder slot → "8388608"). The <see cref="Token"/> is unchanged — the
    /// round-trip oracle still drives the same display token — so this is invisible to read/write/diff.</para></summary>
    /// <param name="HasValue">True only for a scalar token the write engine accepts.</param>
    /// <param name="Token">The round-trip token, or an empty string for a no-value result.</param>
    /// <param name="Note">The no-value explanation, or null when a token exists.</param>
    /// <param name="Flags">Optional numeric metadata for a flags-enum token.</param>
    /// <param name="ContainerCount">List/dictionary element count; null for scalars and substructs.</param>
    internal readonly record struct LeafRead(bool HasValue, string Token, string? Note, FlagBits? Flags = null, int? ContainerCount = null)
    {
        /// <summary>Creates a successful scalar leaf from its write-compatible token.</summary>
        /// <param name="token">The exact text accepted by the corresponding write coercion.</param>
        public static LeafRead Value(string token) => new(true, token, null);

        /// <summary>Creates a successful flags-enum leaf while retaining its bit-test metadata.</summary>
        /// <param name="token">The unchanged enum token accepted by the write engine.</param>
        /// <param name="bits">The numeric/type view used only by predicates and display annotations.</param>
        public static LeafRead FlagsValue(string token, FlagBits bits) => new(true, token, null, bits);

        /// <summary>Creates a no-value leaf whose note explains the absence or read limitation.</summary>
        /// <param name="note">A display-ready explanation; it is never treated as a writable token.</param>
        public static LeafRead None(string note) => new(false, "", note);
        /// <summary>A no-value CONTAINER/substruct summary carrying its element <paramref name="count"/>: null for a
        /// substruct (present by being non-null — no element count), a number for a list/dict (0 = present-but-EMPTY).
        /// The count is additive metadata for the presence predicate (<c>exists</c>/<c>missing</c>), which must tell
        /// an EMPTY list from a carried one WITHOUT re-parsing the display note. The Token is empty and the oracle
        /// never drives a no-value read, so this is invisible to read/write/diff, exactly like <see cref="Flags"/>.</summary>
        /// <param name="note">The load-bearing container/substructure summary.</param>
        /// <param name="count">Collection size, or null for a non-collection substructure.</param>
        public static LeafRead Container(string note, int? count) => new(false, "", note, null, count);

        /// <summary>Returns the token when present, otherwise the explanatory note.</summary>
        public override string ToString() => HasValue ? Token : Note ?? "(none)";
    }

    /// <summary>The bit-test view of a <c>[Flags]</c> enum leaf — the unsigned bit pattern plus the enum
    /// <see cref="Type"/> (so a predicate's operand given as a flag NAME, e.g. <c>has Body</c>, resolves against
    /// the same enum). Populated by <see cref="EmitToken"/> for flags enums only; null for every other leaf.</summary>
    /// <param name="Bits">The enum's unsigned two's-complement bit pattern.</param>
    /// <param name="EnumType">The concrete enum type used to resolve named operands.</param>
    internal readonly record struct FlagBits(ulong Bits, Type EnumType);

    /// <summary>A modeled leaf that exists but holds no value on this record (absent optional substruct,
    /// empty optional). Distinct from a real token; the oracle skips it — write-proof owns the absent
    /// surface.</summary>
    internal const string AbsentNote = "(absent)";

    /// <summary>A present-but-null FormLink (FormKey.Null) — modeled, but carrying no target. Not a
    /// round-trippable token (the write surface sets links to a real FormKey, never "Null"), so surfaced as
    /// a note. Distinct from <see cref="AbsentNote"/> (a wholly absent optional); the conflict diff treats
    /// both as "no value here" (see <c>FieldsDiff.IsAbsentSentinel</c>).</summary>
    internal const string NullLinkNote = "(null link)";

    /// <summary>A present <c>TranslatedString</c> (FULL/DESC/…) whose <c>.String</c> resolves to null — a localized
    /// string whose <c>.STRINGS</c> entry for the target language is not in the workspace. After the
    /// <see cref="LoadOrderResolver.OpenOverlay"/> strings-source fix this is the genuinely-absent residue (no
    /// strings anywhere for it), NOT the cleaned-masters case that fix resolves. Surfaced as a no-value NOTE — never
    /// a blank token — so a value predicate's Q3 accounting fires on it (a `where Name contains …` can't silently
    /// treat it as a real non-matching value → false "0 matches") and a read renders it loud, not as an empty Name
    /// indistinguishable from a record that truly has none (HCBR-2026-06-24). Like <see cref="AbsentNote"/> /
    /// <see cref="NullLinkNote"/>, the conflict diff treats it as "no value here" (<c>FieldsDiff.IsAbsentSentinel</c>).</summary>
    internal const string UnresolvedStringNote = "(unresolved localized string)";

    // ======================================================================
    //  `read` MODE — resolve a record in one plugin and emit its fields.
    //    dotnet run --project src/housecarl-generator read \
    //        --source "<plugin>" [--type Weapon] (--formkey 0F1AC1:Skyrim.esm | --editorid X) \
    //        [--path BasicStats.Damage]...
    //  With one or more --path: emit exactly those leaves (the get_field primitive + the oracle's
    //  read). With no --path: a one-level dump of every modeled field on the record.
    // ======================================================================
    /// <summary>Runs the developer CLI read command. It opens one supplied plugin, locates one record, prints requested
    /// or modeled fields, and returns a process exit code without changing the plugin.</summary>
    /// <param name="args">Command arguments after <c>read</c>; paths are native host paths.</param>
    /// <returns>Zero on a completed read; one after an actionable validation or lookup error.</returns>
    public static int RunRead(string[] args)
    {
        var f = WriteEngine.ParseFlags(args);
        var source = f.GetValueOrDefault("source");
        if (source is null) { Console.Error.WriteLine("error: --source is required"); return 1; }
        if (!File.Exists(source)) { Console.Error.WriteLine($"error: source plugin not found: {source}"); return 1; }
        var type = f.GetValueOrDefault("type");
        var editorid = f.GetValueOrDefault("editorid");
        var formkeyRaw = f.GetValueOrDefault("formkey");
        if (editorid is null && formkeyRaw is null) { Console.Error.WriteLine("error: locate the record with --editorid or --formkey"); return 1; }

        // --path repeats (ParseFlags keeps only the last of a repeated flag) — scan the raw args.
        var paths = new List<string>();
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], "--path", StringComparison.OrdinalIgnoreCase)) paths.Add(args[i + 1]);

        var sourceMod = SkyrimMod.CreateFromBinaryOverlay(source, SkyrimRelease.SkyrimSE);
        Type? iface = type is null ? null : typeof(SkyrimMod).Assembly.GetType("Mutagen.Bethesda.Skyrim.I" + type + "Getter");
        if (type is not null && iface is null) { Console.Error.WriteLine($"error: unknown record type '{type}'"); return 1; }
        FormKey? wantFk = null;
        if (formkeyRaw is not null) { try { wantFk = FormKey.Factory(formkeyRaw); } catch (Exception ex) { Console.Error.WriteLine($"error: bad --formkey '{formkeyRaw}': {ex.Message}"); return 1; } }

        var target = sourceMod.EnumerateMajorRecords()
            .FirstOrDefault(r => (iface is null || iface.IsInstanceOfType(r))
                && (wantFk is { } fk ? r.FormKey == fk : string.Equals(r.EditorID, editorid, StringComparison.OrdinalIgnoreCase)));
        if (target is null) { Console.Error.WriteLine($"error: not found in {Path.GetFileName(source)}"); return 1; }

        var typeName = RecordNaming.StripGetterInterface(WriteEngine.PrimaryGetter(target.GetType())?.Name ?? "I?Getter");
        Console.WriteLine($"{typeName}  {target.FormKey}  ({target.EditorID ?? "<no editorid>"})");

        // --depth N (default 1): depth>=2 expands list/dict/substruct contents (descendable reads). With
        // --path it expands those targets; without, the whole-record dump. Routes through the SAME ReadFields
        // the MCP read tools call, so the harness and the product stay in lockstep.
        var depth = int.TryParse(f.GetValueOrDefault("depth"), out var dN) && dN > 0 ? dN : 1;
        var rf = ReadFields(target, paths.Count > 0 ? paths : null, depth);
        foreach (var fv in rf.Fields)
            Console.WriteLine($"  {fv.Path} = {(fv.HasValue ? fv.Token : fv.Note)}{(fv.Display is null ? "" : $"   ({fv.Display})")}");
        return 0;
    }

    /// <summary>The depth-1 container hint (HCBR-2026-07-12): appended to an unexpanded container/substruct summary so
    /// an agent turns the depth= knob instead of inventing a param or hand-rolling a parser. It names <c>depth=2</c>,
    /// which is only honest on a surface that HAS a depth= parameter (read_record / batch_record_detail /
    /// read_plugin_file / cross_plugin_query text+json (#231) / the CLI) — a caller whose surface refuses depth
    /// passes its own redirect via <c>containerHint</c> (cross_plugin_query's DENSE render names the text/json
    /// format hop — its positional cells refuse depth&gt;1) or null to suppress (write read-backs, where the count
    /// IS the confirmation and there is no knob to turn).</summary>
    public const string DepthExpandHint = " — pass depth=2 to expand";

    /// <summary>Read a located record's fields as round-trippable tokens — the public, structured entry the MCP
    /// server consumes (the §8.4 read cleave; <c>RunRead</c> is the CLI sibling). With <paramref name="paths"/>:
    /// exactly those leaves (the get_field primitive). Without: a one-level dump of every modeled field. Wraps the
    /// proven internal <see cref="ReadLeaf"/> (the round-trip oracle drives it), so the server's reads inherit the
    /// read-proof by construction. Per-leaf fault isolation (Q3): an unreadable field names itself in its
    /// <see cref="FieldValue.Note"/>, never throws out of the record read.</summary>
    /// <param name="record">The live Mutagen record getter to inspect.</param>
    /// <param name="paths">Specific dotted/bracketed paths, or null/empty to enumerate modeled top-level fields.</param>
    /// <param name="depth">Expansion levels. Values at or below one retain the scalar/container-summary surface.</param>
    /// <param name="containerHint">Optional text appended to unexpanded container summaries; null suppresses it.</param>
    /// <returns>The record identity and ordered, fault-isolated field results.</returns>
    public static RecordFields ReadFields(IMajorRecordGetter record, IReadOnlyList<string>? paths = null, int depth = 1,
                                          string? containerHint = DepthExpandHint)
    {
        var typeName = RecordNaming.StripGetterInterface(WriteEngine.PrimaryGetter(record.GetType())?.Name ?? "I?Getter");
        var targets = paths is { Count: > 0 } ? (IEnumerable<string>)paths : ModeledFieldNames(typeName, record.GetType());
        var fields = new List<FieldValue>();
        if (depth <= 1)
        {
            // depth 1 (default) — UNCHANGED one-level read: the proven get_field/dump path the round-trip
            // oracle drives (ReadLeaf). Descendable expansion (depth>=2) is an additive sibling below, so
            // the oracle-critical leaf path is never touched.
            foreach (var p in targets)
            {
                var seg = p.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var r = ReadLeaf(record, seg);
                string? note = r.HasValue ? null : r.Note;
                // An UNEXPANDED container / substruct leaf self-documents the lever that opens it: at the depth-1
                // default it renders as a count/summary ("[list: 2 item(s)]", "[BodyTemplate]"), and an agent that
                // doesn't know to raise depth= invents a param and hand-rolls a parser instead of turning the knob
                // (HCBR-2026-07-12). No-value NOTES are parenthesized ("(absent)", "(null link)"), so the leading-'['
                // test targets exactly the container/substruct summaries. Depth-1 only (this branch) — the deep read
                // FieldsDiff runs never sees the hint (it reads at expansion depth, a different summary path).
                // The hint text is the caller's (containerHint): depth=2 is only a real knob on some surfaces.
                if (note is { Length: > 0 } && note[0] == '[' && !string.IsNullOrEmpty(containerHint)) note += containerHint;
                fields.Add(new FieldValue(p, r.HasValue, r.HasValue ? r.Token : null, note, FlagDisplay(r)));
            }
        }
        else
        {
            int budget = MaxExpandNodes;
            foreach (var p in targets) EmitWithDepth(record, p, depth, fields, ref budget);
        }
        return new RecordFields(typeName, record.FormKey.ToString(), record.EditorID, fields);
    }

    // ======================================================================
    //  THE READ PRIMITIVE — navigate a path read-only, emit the leaf token.
    // ======================================================================

    /// <summary>Read one leaf path off a located record and return its round-trippable token (or a
    /// sentinel). Navigation mirrors the write engine's path walk (same <c>ResolveProperty</c> /
    /// <c>StepIntoElement</c>) but is READ-ONLY — an absent optional substruct is surfaced, never
    /// materialised. Per-leaf fault isolation (Q3): any refl/parse failure names itself and never
    /// throws out, so one Mutagen-unparseable field can't crash a record read.</summary>
    /// <param name="record">The record or nested object at which the path begins.</param>
    /// <param name="path">Pre-split dotted/bracketed segments; callers provide at least one segment.</param>
    /// <returns>A scalar token or a named no-value result. Reflection and parse faults are captured in the note.</returns>
    internal static LeafRead ReadLeaf(object record, string[] path)
    {
        try
        {
            object? current = record;
            for (int i = 0; i < path.Length - 1; i++)
            {
                var (segName, segKey) = WriteEngine.ParseSegment(path[i]);
                var p = WriteEngine.ResolveProperty(current!.GetType(), segName);
                if (p is null) return LeafRead.None(NoFieldNote(current, segName, i > 0 ? WriteEngine.ParseSegment(path[i - 1]).name : null));
                current = segKey is null
                    ? p.GetValue(current)                                  // descend a substruct (read-only)
                    : WriteEngine.StepIntoElement(current, p, segName, segKey); // collnav (handles IReadOnly*)
                if (current is null) return LeafRead.None(AbsentNote);     // absent optional substruct
            }

            var (leafName, leafKey) = WriteEngine.ParseSegment(path[^1]);
            var leaf = WriteEngine.ResolveProperty(current!.GetType(), leafName);
            if (leaf is null) return LeafRead.None(NoFieldNote(current, leafName, path.Length >= 2 ? WriteEngine.ParseSegment(path[^2]).name : null));
            if (leafKey is not null)
            {
                // The leaf brackets a collection element (Keywords[0]) — step in and emit the element.
                var elem = WriteEngine.StepIntoElement(current, leaf, leafName, leafKey);
                return EmitToken(elem, elem.GetType(), current);
            }
            return EmitToken(leaf.GetValue(current), leaf.PropertyType, current);
        }
        catch (Exception ex) { return LeafRead.None($"(unreadable: {ex.Message})"); }
    }

    /// <summary>The FormKeys on a record's <c>Keywords</c> list — the ONE keyword walk (previously
    /// triplicated across the SkyPatcher overlay, the post-state service resolver, and the show CLI,
    /// with diverging property resolution and non-formlink handling). Resolves the property the same
    /// way the engines do (<see cref="WriteEngine.ResolveProperty"/>), so explicit-interface getters
    /// can't hide it. An ABSENT (null) list honestly reads as EMPTY — a record with no keyword list
    /// carries no keywords. Null is reserved for "no such property / not a formlink list" (surfaced
    /// loud by callers, never guessed).</summary>
    /// <param name="record">A Mutagen record getter or mutable record.</param>
    /// <returns>Keyword FormKeys, an empty list for an absent list, or null when the property is incompatible.</returns>
    public static IReadOnlyList<FormKey>? KeywordKeys(object record)
    {
        var p = WriteEngine.ResolveProperty(record.GetType(), "Keywords");
        if (p is null) return null;
        if (p.GetValue(record) is not System.Collections.IEnumerable list) return new List<FormKey>();   // absent list reads as empty
        return FormLinkKeys(list);
    }

    /// <summary>The FormKeys of one formlink-list value — the ONE enumerable→FormKey walk
    /// (<see cref="KeywordKeys"/> and the SkyPatcher overlay's list ops both ride it, so link-reading
    /// can't drift between them). Null the moment an element isn't a formlink (loud upstream).</summary>
    /// <param name="list">The already resolved enumerable keyword/link value.</param>
    /// <returns>All FormKeys in enumeration order, or null if any element is not a FormLink.</returns>
    public static List<FormKey>? FormLinkKeys(System.Collections.IEnumerable list)
    {
        var keys = new List<FormKey>();
        foreach (var item in list)
        {
            if (item is IFormLinkGetter link) keys.Add(link.FormKey);
            else return null;
        }
        return keys;
    }

    /// <summary>A "no such field" note that, when the owner is a collection, points the caller at bracket
    /// indexing — the common <c>.0</c>-vs-<c>[0]</c> confusion (the read analog of the write pre-flight's
    /// bracket hint in <c>CorpusRulebook</c>). Brackets are how you step into a list/dict element mid-path;
    /// a bare dotted <c>.0</c> is parsed as a field name and dead-ends here.</summary>
    /// <param name="owner">The object on which property resolution failed.</param>
    /// <param name="segName">The unresolved segment text.</param>
    /// <param name="precedingField">The previous path field, used only to construct a bracket example.</param>
    /// <returns>A display-ready, parenthesized failure note.</returns>
    static string NoFieldNote(object owner, string segName, string? precedingField)
    {
        bool ownerIsCollection = owner is System.Collections.IDictionary
            || (owner is System.Collections.IEnumerable && owner is not string);
        if (ownerIsCollection)
        {
            var pf = precedingField ?? "<field>";
            return $"(no field '{segName}': '{pf}' is a list/dict — index an element with brackets, " +
                   $"e.g. '{pf}[{segName}]', not '{pf}.{segName}')";
        }
        return $"(no field {segName})";
    }

    // ======================================================================
    //  DESCENDABLE READS (depth>=2) — enumerate list/dict/substruct CONTENTS so element indices and
    //  sub-fields are discoverable without hand-probing each [i]. Additive: navigation reuses the same
    //  engine walk (ParseSegment/ResolveProperty/StepIntoElement) and EmitToken as the leaf path, but the
    //  proven depth-1 ReadLeaf is untouched. Bounded by MaxExpandNodes (Q3 — explicit truncation note).
    // ======================================================================

    /// <summary>Max FieldValue lines one descendable read will GENERATE (separate from the renderer's char
    /// cap) — a guard so depth-expanding a huge container can't build a runaway result. Over it, a single
    /// truncation note is emitted (Q3 — bounded, never silent).</summary>
    internal const int MaxExpandNodes = 2000;

    /// <summary>Preferred representative fields for compact anonymous element summaries, in priority order.</summary>
    static readonly string[] IdentityFieldNames = { "Name", "EditorID", "Title" };

    /// <summary>Emit one target path, expanding container/substruct contents up to <paramref name="depth"/>
    /// levels. A miss surfaces the same bracket-aware note the leaf read uses. The body is wrapped in the same
    /// per-field fault isolation depth-1 <see cref="ReadLeaf"/> gives (Q3): a throw while navigating OR expanding
    /// this one target (an unparseable nested getter, an enumerator that faults mid-list, an ambiguous identity
    /// reflection) names itself "(unreadable …)" and never escapes the record read — so one bad field can't crash
    /// a whole-record depth dump. Lines already emitted before a mid-expansion fault are real reads and are kept.</summary>
    /// <param name="record">The record at which <paramref name="path"/> begins.</param>
    /// <param name="path">One requested dotted/bracketed field path.</param>
    /// <param name="depth">Remaining caller-requested expansion levels.</param>
    /// <param name="sink">The ordered result list receiving emitted lines.</param>
    /// <param name="budget">Shared remaining line budget; negative means truncation has already been reported.</param>
    static void EmitWithDepth(object record, string path, int depth, List<FieldValue> sink, ref int budget)
    {
        try
        {
            var seg = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var nav = NavigateValue(record, seg);
            if (!nav.ok) { Emit(sink, ref budget, new FieldValue(path, false, null, nav.note)); return; }
            Expand(nav.val, nav.type, nav.parent, path, depth, sink, ref budget);
        }
        catch (Exception ex) { Emit(sink, ref budget, new FieldValue(path, false, null, $"(unreadable: {ex.Message})")); }
    }

    /// <summary>Recursively emit <paramref name="val"/> at <paramref name="path"/>: a value leaf → its token;
    /// a link → its note (not opened); a container/substruct → an identity-enriched summary line, then (while
    /// depth allows) one child line per element (bracketed) or sub-field (dotted), each recursed at depth-1.</summary>
    /// <param name="val">The live value being classified and possibly expanded; null is an absent leaf.</param>
    /// <param name="declaredType">The property's declared type, retained when the runtime value is null or polymorphic.</param>
    /// <param name="parent">The owning object needed for parent-discriminated values such as FormLinkOrIndex.</param>
    /// <param name="path">The canonical display/read path for this value.</param>
    /// <param name="depth">Expansion levels remaining at this value.</param>
    /// <param name="sink">The ordered result list receiving summaries and leaves.</param>
    /// <param name="budget">Shared remaining line budget.</param>
    static void Expand(object? val, Type declaredType, object parent, string path, int depth, List<FieldValue> sink, ref int budget)
    {
        if (budget < 0) return;
        var leaf = EmitToken(val, declaredType, parent);
        if (leaf.HasValue) { Emit(sink, ref budget, new FieldValue(path, true, leaf.Token, null, FlagDisplay(leaf))); return; }
        if (val is null) { Emit(sink, ref budget, new FieldValue(path, false, null, leaf.Note)); return; }
        // a link (incl. a null FormKey, or an FLOI) is a note, not an openable container/substruct.
        if (val is IFormLinkGetter || WriteEngine.IsFormLinkOrIndex(Nullable.GetUnderlyingType(declaredType) ?? declaredType))
        { Emit(sink, ref budget, new FieldValue(path, false, null, leaf.Note)); return; }

        // Classify dict-vs-list the SAME way the navigation does (StepIntoElement) — by the GENERIC dictionary
        // interfaces via ClosedInterface, not a separate non-generic System.Collections.IDictionary cast — so the
        // browse view and the read/write path can't drift (a getter dict need not expose the non-generic interface).
        // A generic dict enumerates as KeyValuePair<,>; Key/Value come off each pair. Classified BEFORE the
        // summary line so the summary can carry the dict marker ("pair(s)" vs "item(s)") — the in-band signal
        // FieldsDiff uses to keep numeric-KEYED dicts (Package.Data) out of positional-list comparison, where
        // a key rebinding would wrongly compare "identical" (PR #28 review).
        bool isDict = WriteEngine.ClosedInterface(val.GetType(), typeof(IDictionary<,>)) is not null
                   || WriteEngine.ClosedInterface(val.GetType(), typeof(IReadOnlyDictionary<,>)) is not null;

        // a container or substruct — summarise (with an element identity where we can), then maybe open it.
        if (!Emit(sink, ref budget, new FieldValue(path, false, null, ElementSummary(val, isDict)))) return;

        // Two POLYMORPHIC-ARM families normally stop here at their identity summary, hiding their VALUE, and both
        // surface it ONE bounded level deeper even at the depth floor so a read reaches parity with the write
        // surface and with a direct per-arm path:
        //   * a VMAD script property (e.g. "[ScriptObjectProperty] Name=DAK_HorseBuyPerk") — its Object FormLink
        //     (incl. a declared-but-null link, the signal the quest-fragment linter keys on), Data scalar, Alias
        //     (1.3.1 item 2);
        //   * a Conditions[].Data arm (e.g. "[GetFactionRankConditionData]") — its parameter fields (Faction,
        //     Global, Reference, RunOnType…), which otherwise appear ONLY when Data is addressed directly, never
        //     via a Conditions-list dump (#258 — the arm consumed a "summary-only" level, so depth=3 stopped at the
        //     bare arm type and you needed depth=4 or a per-row path).
        // Each family's direct members are leaves/links (a VMAD *ListProperty arm shows as a count at the floor;
        // raise depth= to enumerate it), so this opens exactly one level and never unbounded-descends. EVERY OTHER
        // substruct still stops at the floor, byte-for-byte unchanged — the exception is these two arm families
        // only, matched by their shared getter interface (no per-arm list).
        int childDepth = depth - 1;
        if (depth <= 1)
        {
            if (!IsScriptProperty(val.GetType()) && !IsConditionData(val.GetType())) return;
            childDepth = 1;
        }

        if (isDict)
        {
            foreach (var entry in (System.Collections.IEnumerable)val)
            {
                if (budget < 0) return;
                if (entry is null) continue;
                var et = entry.GetType();
                var key = et.GetProperty("Key", BindingFlags.Public | BindingFlags.Instance)?.GetValue(entry);
                var ev = et.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)?.GetValue(entry);
                Expand(ev, ev?.GetType() ?? typeof(object), val, $"{path}[{key}]", childDepth, sink, ref budget);
            }
        }
        else if (WriteEngine.GenderedInterface(val.GetType()) is not null)
        {
            // Gendered pair ([0]=male, [1]=female): render via the SAME index→arm mapping navigation uses
            // (WriteEngine.GenderedArmNames), NOT raw enumeration order — so the [0]/[1] paths a depth-read SHOWS are
            // exactly the ones a write/read ACCEPTS, by construction (no drift if Mutagen's enumerator order shifts).
            // Numeric [0]/[1] keeps this root a positional list to FieldsDiff (gendered halves never reorder), so the
            // conflict-diff comparison is unchanged (the HCBR PR-H render decision: keep numeric indices).
            for (int g = 0; g < WriteEngine.GenderedArmNames.Length; g++)
            {
                if (budget < 0) return;
                var armProp = WriteEngine.ResolveProperty(val.GetType(), WriteEngine.GenderedArmNames[g]);
                object? arm; try { arm = armProp?.GetValue(val); } catch { continue; }
                Expand(arm, armProp?.PropertyType ?? typeof(object), val, $"{path}[{g}]", childDepth, sink, ref budget);
            }
        }
        else if (val is System.Collections.IEnumerable seq and not string)
        {
            int i = 0;
            foreach (var item in seq)
            {
                if (budget < 0) return;
                Expand(item, item?.GetType() ?? typeof(object), val, $"{path}[{i}]", childDepth, sink, ref budget);
                i++;
            }
        }
        else
        {
            // substruct — open its modeled (Loqui-filtered) fields. Reflection, not the corpus: display-only,
            // and substructs aren't corpus-keyed by a name we hold here.
            //
            // GATE — the expansion boundary is the modeled corpus (cornerstone). Only DESCEND into Mutagen/
            // Noggog record content; a value that reaches here but is NOT modeled is .NET plumbing — in
            // practice a System.Type (RuntimeType): a ConditionData arm's Parameter1Type/Parameter2Type are
            // typed System.Type, and a naive recurse walks .Assembly.DefinedTypes (the whole ~17,900-type
            // Mutagen assembly), the BaseType→Enum→ValueType chain, StructLayoutAttribute, Module, the cyclic
            // UnderlyingSystemType — ~50 KB of reflection internals that is never record data (HCBR-2026-06-08-01).
            // The summary token was already emitted above (e.g. `Parameter1Type = [RuntimeType] Name=ActorValue`,
            // exactly the clean depth<=4 rendering); we keep it and STOP, regardless of remaining depth budget.
            if (!IsModeledContent(val.GetType())) return;
            foreach (var fname in ReflectedFieldNames(val.GetType()))
            {
                if (budget < 0) return;
                var prop = WriteEngine.ResolveProperty(val.GetType(), fname);
                object? fv; try { fv = prop?.GetValue(val); } catch { continue; }
                Expand(fv, prop?.PropertyType ?? typeof(object), val, $"{path}.{fname}", childDepth, sink, ref budget);
            }
        }
    }

    /// <summary>Append a line, decrementing the generation budget; at exhaustion emit ONE truncation note and
    /// stop (returns false thereafter so callers unwind). Q3: the cut is named, never silent.</summary>
    /// <param name="sink">The result list receiving the field or truncation marker.</param>
    /// <param name="budget">Remaining lines. Zero emits the marker; negative means it was already emitted.</param>
    /// <param name="fv">The next field result.</param>
    /// <returns>True when the supplied field was appended; false when expansion must stop.</returns>
    static bool Emit(List<FieldValue> sink, ref int budget, FieldValue fv)
    {
        if (budget < 0) return false;
        if (budget == 0)
        {
            sink.Add(new FieldValue("…", false, null,
                $"(expansion truncated at {MaxExpandNodes} lines — narrow with a field path or a lower depth)"));
            budget = -1;
            return false;
        }
        sink.Add(fv); budget--; return true;
    }

    /// <summary>Navigate a path READ-ONLY to its target, returning the live value object (+ declared type +
    /// owning parent) for recursion, or a miss note. Same walk as <see cref="ReadLeaf"/> but yields the object
    /// instead of a token, so the expander can descend into it. Fault-isolated (Q3).</summary>
    /// <param name="record">The record or nested root at which navigation begins.</param>
    /// <param name="path">Pre-split dotted/bracketed segments; callers provide at least one segment.</param>
    /// <returns>Success plus value/type/owner, or failure plus a display-ready note.</returns>
    static (bool ok, object? val, Type type, object parent, string? note) NavigateValue(object record, string[] path)
    {
        try
        {
            object current = record;
            for (int i = 0; i < path.Length - 1; i++)
            {
                var (segName, segKey) = WriteEngine.ParseSegment(path[i]);
                var p = WriteEngine.ResolveProperty(current.GetType(), segName);
                if (p is null) return (false, null, typeof(object), current,
                    NoFieldNote(current, segName, i > 0 ? WriteEngine.ParseSegment(path[i - 1]).name : null));
                var next = segKey is null ? p.GetValue(current) : WriteEngine.StepIntoElement(current, p, segName, segKey);
                if (next is null) return (false, null, typeof(object), record, AbsentNote);
                current = next;
            }
            var (leafName, leafKey) = WriteEngine.ParseSegment(path[^1]);
            var leaf = WriteEngine.ResolveProperty(current.GetType(), leafName);
            if (leaf is null) return (false, null, typeof(object), current,
                NoFieldNote(current, leafName, path.Length >= 2 ? WriteEngine.ParseSegment(path[^2]).name : null));
            if (leafKey is not null)
            {
                var elem = WriteEngine.StepIntoElement(current, leaf, leafName, leafKey);
                return (true, elem, elem.GetType(), current, null);
            }
            return (true, leaf.GetValue(current), leaf.PropertyType, current, null);
        }
        catch (Exception ex) { return (false, null, typeof(object), record, $"(unreadable: {ex.Message})"); }
    }

    /// <summary>Best-effort COMPACT identity of the element a list/dict verb just acted on — the write-verify's
    /// "what landed" line (HCBR-2026-06-28-01, the compact in-place readback). For a single list <c>Add</c>, the new
    /// last element + the new count (<c>now 29 (+1), new [28] = …</c>); for a batch <c>composes=</c> Add of N, the
    /// whole appended run (<c>now 34 (+6), new [28..33]</c> — <paramref name="added"/> carries how many the op
    /// appended, #259); for a keyed <c>SetAtIndex</c>/<c>Remove</c>, the touched key + new count; else the new count.
    /// Names the element as specifically as the model allows — a
    /// formlink element renders its FormKey, an identity-bearing struct its Name/EditorID, an anonymous struct (a
    /// condition) its <c>[Type]</c>. Read-only; NEVER throws (null on any difficulty) — a display nicety on an
    /// ALREADY-succeeded write, never load-bearing. <paramref name="leafPath"/> is the verb's path to the collection
    /// (the engine's <see cref="WriteRequest.Path"/>); <paramref name="key"/> its list index / dict key, if any.</summary>
    /// <param name="record">The already modified in-memory record.</param>
    /// <param name="leafPath">Pre-split path to the affected collection.</param>
    /// <param name="verb">The completed collection verb.</param>
    /// <param name="key">Optional list index or dictionary key used by the verb.</param>
    /// <param name="added">Number of elements appended by an Add operation.</param>
    /// <returns>A compact verification phrase, or null when it cannot be derived safely.</returns>
    internal static string? TouchedElement(object record, string[] leafPath, string verb, string? key, int added = 1)
    {
        try
        {
            var nav = NavigateValue(record, leafPath);
            if (!nav.ok || nav.val is not System.Collections.IEnumerable en || nav.val is string) return null;
            int count = 0; object? last = null;
            foreach (var e in en) { count++; last = e; }
            return verb switch
            {
                "Add"        => AddLanded(record, count, last, added),
                "ReplaceAll" => $"now {count} item(s) (replaced)",
                "SetAtIndex" => key is not null ? $"now {count} item(s), set [{key}]" : $"now {count} item(s)",
                "Remove"     => key is not null ? $"now {count} item(s), removed [{key}]" : $"now {count} item(s) (-1)",
                _            => $"now {count} item(s)",
            };
        }
        catch { return null; }
    }

    /// <summary>The list-<c>Add</c> "what landed" line, honest about the appended count. A SINGLE append names the
    /// new element (<c>now 29 (+1), new [28] = …</c>); a BATCH <c>composes=</c> Add of N names the whole appended run
    /// of indices (<c>now 34 (+6), new [28..33]</c>) instead of reporting only the last element as a "(+1)" — the
    /// #259 misleading-output bug, where a 6-element compose read as "(+1), new [36]" and cost real mid-session doubt
    /// that all six landed. <paramref name="added"/> is the op's appended count (composes.Count, else 1), clamped to
    /// the live count so a display nicety on an already-succeeded write can never throw or under-run the range.</summary>
    /// <param name="record">The owning record, used to render a single appended element's identity.</param>
    /// <param name="count">Collection size after the write.</param>
    /// <param name="last">The final element after the write, or null for an empty/unreadable collection.</param>
    /// <param name="added">Reported append count before defensive clamping.</param>
    /// <returns>The display-only post-write summary.</returns>
    static string AddLanded(object record, int count, object? last, int added)
    {
        if (last is null) return $"now {count} item(s)";
        int n = Math.Clamp(added, 1, count);
        return n <= 1
            ? $"now {count} (+1), new [{count - 1}] = {ElementId(last, record)}"
            : $"now {count} (+{n}), new [{count - n}..{count - 1}]";
    }

    /// <summary>The compact identity of ONE element for <see cref="TouchedElement"/>: a value/formlink element via its
    /// own round-trip token (a keyword → its FormKey), an identity-bearing or anonymous struct via
    /// <see cref="ElementSummary"/> (<c>[Type] Name=…</c> / <c>[Type]</c>).</summary>
    /// <param name="elem">The collection element to identify.</param>
    /// <param name="parent">Its owning record/container for parent-aware token emission.</param>
    /// <returns>A token or compact structural summary.</returns>
    static string ElementId(object elem, object parent)
    {
        var lr = EmitToken(elem, elem.GetType(), parent);
        return lr.HasValue ? lr.Token : ElementSummary(elem);
    }

    /// <summary>A compact summary for a container/struct value: for a collection, the count form
    /// (<see cref="SummariseContainer(object,bool)"/>); for a struct, <c>[TypeName]</c> plus a representative identity
    /// field (Name/EditorID/Title) where present — so a list line like
    /// <c>Properties[5] = [ScriptObjectProperty] Name=DAK_HorseBuyPerk</c> reveals which element is which.</summary>
    /// <param name="val">The collection, owned record, or substructure to summarize.</param>
    /// <param name="isDict">Whether an enumerable is logically key/value rather than positional.</param>
    /// <returns>A display-only identity/count summary.</returns>
    static string ElementSummary(object val, bool isDict = false)
    {
        if (val is System.Collections.IEnumerable && val is not string) return SummariseContainer(val, isDict);
        var t = val.GetType();
        var typeName = RecordNaming.StripGetterInterface(RecordNaming.StripOverlay(t.Name));
        // An owned child RECORD element (an IMajorRecordGetter — a DIAL's Responses hold DialogResponses/INFO
        // records, a CELL's references hold placed records; each owns its own FormKey) leads with its FormKey the
        // way a top-level read does — #252, the #198 family carried to records. Checked BEFORE the Name/EditorID/
        // Title scan: for an owned record the FormKey IS the canonical identity (an INFO has no Name and usually no
        // EditorID — the exact case #198's lone-FormLink path can't reach), and EditorID rides along when present,
        // so a depth=2 owned-record list reads "[DialogResponses 4D9A74:Plugin.esp editorid=…]" — its own id — not
        // a bare opaque [Type] (or an EditorID-only line) one level longer than the depth "index + identity" implies.
        if (val is IMajorRecordGetter mr)
            return $"[{typeName} {mr.FormKey}{(string.IsNullOrEmpty(mr.EditorID) ? "" : $" editorid={mr.EditorID}")}]";
        foreach (var idName in IdentityFieldNames)
        {
            var p = t.GetProperty(idName, BindingFlags.Public | BindingFlags.Instance);
            if (p is null || p.GetIndexParameters().Length != 0) continue;
            object? iv; try { iv = p.GetValue(val); } catch { continue; }
            var s = iv switch { null => null, string str => str, IFormLinkGetter fl => fl.FormKey.ToString(), _ => iv.ToString() };
            if (!string.IsNullOrEmpty(s)) return $"[{typeName}] {idName}={s}";
        }
        // No Name/EditorID/Title identity. If the struct carries EXACTLY ONE FormLink field, that link IS its
        // identity (PerkPlacement.Perk, and any other single-link struct) — surface it so a depth=2 element line
        // reveals which record it points at, the way a Name= identity does, instead of a bare [Type] that reads as
        // "the FormID isn't surfaced" one level longer than the depth contract implies (#198). Exactly one link
        // only — 2+ are ambiguous and we don't guess which is the identity (Q3).
        if (LoneFormLinkIdentity(val, t) is { } linkId) return $"[{typeName}] {linkId}";
        return $"[{typeName}]";
    }

    /// <summary>The <c>Field=FormKey</c> identity of a struct element that has EXACTLY ONE FormLink property and no
    /// Name/EditorID/Title identity — e.g. PerkPlacement → <c>Perk=03AF81:Skyrim.esm</c>. Null when the struct has no
    /// FormLink or MORE THAN ONE (ambiguous — don't guess which is the identity, #198). A present-but-null link still
    /// counts: it names the field and shows the null FormKey, the exact signal a reader chasing a dangling ref wants.
    /// Display-only, best-effort — any reflection fault yields null (falls back to the bare <c>[Type]</c>).</summary>
    /// <param name="val">The structural value whose link fields are inspected.</param>
    /// <param name="t">Its runtime type.</param>
    /// <returns>A single unambiguous <c>Field=FormKey</c> identity, otherwise null.</returns>
    static string? LoneFormLinkIdentity(object val, Type t)
    {
        PropertyInfo? only = null;
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length != 0) continue;
            if (!typeof(IFormLinkGetter).IsAssignableFrom(p.PropertyType)) continue;
            if (only is not null) return null;   // 2+ FormLink fields — ambiguous, don't guess
            only = p;
        }
        if (only is null) return null;
        try { return only.GetValue(val) is IFormLinkGetter fl ? $"{only.Name}={fl.FormKey}" : null; }
        catch { return null; }
    }

    /// <summary>Public-instance modeled field names off a runtime type (Loqui infra filtered) — the reflection
    /// sibling of <see cref="ModeledFieldNames"/> for substructs (which aren't corpus-keyed by a name we hold
    /// here). Best-effort, display-only.</summary>
    /// <param name="runtimeType">The concrete overlay/mutable substructure type.</param>
    /// <returns>Distinct readable content-property names in reflection order.</returns>
    static IEnumerable<string> ReflectedFieldNames(Type runtimeType)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var primary = WriteEngine.PrimaryGetter(runtimeType);
        var ifaces = new List<Type>();
        if (primary is not null) { ifaces.Add(primary); ifaces.AddRange(primary.GetInterfaces()); }
        else { ifaces.Add(runtimeType); ifaces.AddRange(runtimeType.GetInterfaces()); }
        foreach (var iface in ifaces)
            foreach (var p in iface.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (p.GetIndexParameters().Length == 0
                    && p.DeclaringType?.Namespace?.StartsWith("Loqui", StringComparison.Ordinal) != true
                    && !IsInfrastructure(p)
                    && seen.Add(p.Name))
                    yield return p.Name;
    }

    /// <summary>Drop Loqui/Mutagen plumbing members that aren't real data fields — the translation/registration
    /// properties the corpus omits but raw reflection surfaces (e.g. a substruct's <c>BinaryWriteTranslator</c>).
    /// Keeps reflective substruct expansion as clean as the corpus-driven whole-record dump.</summary>
    /// <param name="p">A reflected public instance property.</param>
    /// <returns>True when the property is framework machinery rather than modeled record data.</returns>
    static bool IsInfrastructure(PropertyInfo p)
    {
        if (p.Name is "BinaryWriteTranslator" or "Registration" or "StaticRegistration"
            or "CommonInstance" or "CommonSetterInstance" or "CommonSetterTranslationInstance") return true;
        var tn = p.PropertyType.Name;
        return tn.Contains("BinaryWriteTranslat", StringComparison.Ordinal)
            || tn.EndsWith("BinaryTranslation", StringComparison.Ordinal);
    }

    /// <summary>True if <paramref name="t"/> is MODELED record content the depth walker may descend into — a
    /// Mutagen or Noggog type. Everything else that reaches the substruct branch is .NET plumbing: in practice a
    /// <see cref="System.Type"/>/RuntimeType (a ConditionData arm's <c>Parameter1Type</c>/<c>Parameter2Type</c>),
    /// or an <see cref="Assembly"/>/<see cref="System.Reflection.Module"/>/<see cref="MemberInfo"/>/<see
    /// cref="Attribute"/> reached through one. Those carry no record data — descending them leaks the assembly's
    /// whole type metadata — so the expander renders only their one-line summary token and stops. The modeled
    /// corpus IS the boundary (cornerstone): a type outside Mutagen's own universe is not ours to expand.
    /// Reflection objects are also blocked explicitly so the intent reads clearly and stays robust if a non-
    /// modeled support namespace ever appears.</summary>
    /// <param name="t">The runtime value type considered for recursive field expansion.</param>
    /// <returns>True only for Mutagen/Noggog model types that belong to the record surface.</returns>
    static bool IsModeledContent(Type t)
    {
        if (typeof(MemberInfo).IsAssignableFrom(t)            // Type : MemberInfo — covers Type/RuntimeType, MethodInfo, …
            || typeof(Assembly).IsAssignableFrom(t)
            || typeof(System.Reflection.Module).IsAssignableFrom(t)
            || typeof(Attribute).IsAssignableFrom(t)) return false;
        var ns = t.Namespace;
        return ns is not null
            && (ns.StartsWith("Mutagen.Bethesda", StringComparison.Ordinal)
                || ns.StartsWith("Noggog", StringComparison.Ordinal));
    }

    // ======================================================================
    //  EMIT — the inverse of WriteEngine.Coerce. The branch order mirrors Coerce's Try* family order
    //  (primitive → enum → formlink → value-type), so the two surfaces cannot drift on which token a
    //  type round-trips through. FLOI is checked first (parent-aware, NOT coercible) exactly as the
    //  write side special-cases it.
    // ======================================================================

    /// <summary>Converts one modeled leaf to the exact token accepted by <see cref="WriteEngine"/>. Unsupported
    /// scalar shapes and containers return a named no-value result rather than an invented token.</summary>
    /// <param name="val">The live property value; null becomes <see cref="AbsentNote"/>.</param>
    /// <param name="declaredType">The property's declared type, used for nullable/enum/FLOI classification.</param>
    /// <param name="parent">The leaf's owning object — needed only for a FormLinkOrIndex condition target, whose
    /// form-vs-index mode is carried by the parent arm's discriminator booleans.</param>
    /// <returns>A round-trip token, absence/failure note, or structural container summary.</returns>
    internal static LeafRead EmitToken(object? val, Type declaredType, object parent)
    {
        if (val is null) return LeafRead.None(AbsentNote);
        var u = Nullable.GetUnderlyingType(declaredType) ?? declaredType;

        // FLOI (wave-4 condition targets) — emit a token ClassifyFloiValue re-accepts (FormKey, or
        // "alias N" / "packdata N"), inferred from the parent arm's UseAliases/UsePackageData.
        if (WriteEngine.IsFormLinkOrIndex(u)) return EmitFloi(val, parent);

        // primitive (inverse of TryPrimitive)
        if (TryEmitPrimitive(val, out var prim)) return LeafRead.Value(prim);
        // enum (inverse of TryEnum) — ToString gives the name(s); Enum.Parse(ignoreCase) re-accepts,
        // including the comma form for a [Flags] combination. For a [Flags] enum we ALSO carry the underlying
        // bit pattern + type (the display token is unchanged) so the query predicate can bit-test (`has`) and
        // compare numerically without tripping over the name-vs-number rendering split (HCBR 2026-06-24).
        if (u.IsEnum || val.GetType().IsEnum)
        {
            var token = val.ToString() ?? "";
            var enumType = u.IsEnum ? u : val.GetType();
            if (enumType.IsDefined(typeof(FlagsAttribute), false) && TryEnumBits(val, enumType, out var bits))
                return LeafRead.FlagsValue(token, new FlagBits(bits, enumType));
            return LeafRead.Value(token);
        }
        // formlink (inverse of TryFormLink) — FormKey.ToString() ↔ FormKey.Factory. A present-but-null
        // link (FormKey.Null) is NOT a round-trippable token (the write surface sets links to a real
        // FormKey, never "Null"), so surface it as no-value — consistent with what Coerce accepts.
        if (val is IFormLinkGetter fl)
            return fl.FormKey.IsNull ? LeafRead.None(NullLinkNote) : LeafRead.Value(fl.FormKey.ToString());
        // TranslatedString (FULL/DESC) — emit the resolved .String (the inverse of Coerce's implicit
        // `record.Name = "x"`). A genuinely-empty "" still round-trips as a value; a NULL .String is an
        // UNRESOLVED localized string (no .STRINGS entry for the target language in the workspace) and is
        // surfaced LOUD as no-value, never a blank token — so the Q3 accounting fires instead of a silent
        // non-match (HCBR-2026-06-24). Checked before TryEmitValueType, which previously folded the null
        // into "" here.
        if (val.GetType().FullName == "Mutagen.Bethesda.Strings.TranslatedString")
        {
            var s = ReflectString(val, "String");
            return s is null ? LeafRead.None(UnresolvedStringNote) : LeafRead.Value(s);
        }
        // value types (inverse of TryValueType)
        if (TryEmitValueType(val, out var vt)) return LeafRead.Value(vt);

        // Not a single-token VALUE leaf: a substruct / collection / arm container. The oracle never
        // drives these AS leaves — their sub-leaves are driven individually (exactly like write-proof).
        // Summarise for the read display, with the same dict-vs-list marker the depth walk renders, and carry the
        // element count STRUCTURALLY (Container) so the presence predicate tells an empty list from a carried one
        // without re-parsing the display note.
        bool isDict = WriteEngine.ClosedInterface(val.GetType(), typeof(IDictionary<,>)) is not null
                   || WriteEngine.ClosedInterface(val.GetType(), typeof(IReadOnlyDictionary<,>)) is not null;
        var summary = SummariseContainer(val, isDict, out var count);
        return LeafRead.Container(summary, count);
    }

    /// <summary>The unsigned bit pattern of a boxed enum value, robust across every underlying integer type
    /// (signed or unsigned) — the bits a <c>has</c> predicate ANDs against, and the write engine's flags
    /// Add/Remove OR/AND-NOT operand. Read through the declared underlying type so a high-bit-set signed enum
    /// yields its two's-complement pattern rather than overflowing.</summary>
    /// <param name="val">A boxed value belonging to <paramref name="enumType"/>.</param>
    /// <param name="enumType">The declared concrete enum type.</param>
    /// <param name="bits">Receives the unsigned bit pattern on success, otherwise zero.</param>
    /// <returns>False when conversion cannot be performed without throwing.</returns>
    internal static bool TryEnumBits(object val, Type enumType, out ulong bits)
    {
        bits = 0;
        try
        {
            var prim = Convert.ChangeType(val, Enum.GetUnderlyingType(enumType), CultureInfo.InvariantCulture);
            bits = prim switch
            {
                ulong ul => ul,
                long l => unchecked((ulong)l),
                uint ui => ui,
                int i => unchecked((ulong)(long)i),
                ushort us => us,
                short s => unchecked((ulong)(long)s),
                byte b => b,
                sbyte sb => unchecked((ulong)(long)sb),
                _ => Convert.ToUInt64(prim, CultureInfo.InvariantCulture),
            };
            return true;
        }
        catch { return false; }
    }

    /// <summary>Resolve a flag NAME (or a comma-combo, case-insensitive) against a <c>[Flags]</c> enum type to its
    /// bit pattern — the name-operand path for the query predicate's <c>has</c>/<c>=</c> (e.g. <c>has Body</c>).
    /// False if the text is not a member (or combo of members) of the enum. A numeric string also parses here, but
    /// the caller resolves numerics first, so this only ever sees names.</summary>
    /// <param name="enumType">The concrete flags-enum type supplying member names.</param>
    /// <param name="name">One member or comma-separated member combination.</param>
    /// <param name="bits">Receives the parsed unsigned bit pattern on success.</param>
    /// <returns>True only when the operand parses and its underlying bits can be represented.</returns>
    internal static bool TryEnumBitsFromName(Type enumType, string name, out ulong bits)
    {
        bits = 0;
        try { return TryEnumBits(Enum.Parse(enumType, name.Trim(), ignoreCase: true), enumType, out bits); }
        catch { return false; }
    }

    /// <summary>The DISPLAY-ONLY biped-slot decode for a <c>BodyTemplate.FirstPersonFlags</c> leaf (enum
    /// <c>BipedObjectFlag</c>): the equipped SLOT NUMBERS ("slots 32, 34, 53") derived from the bit pattern
    /// (slot = 30 + bit index). Armor/slot analysis wants the slot numbers, but <c>[Flags].ToString()</c> gives
    /// enum NAMES when every set bit is named ("Body") and falls back to a bare decimal the moment an unnamed
    /// modder slot is set (e.g. <c>8388980</c> for a slot-53 addon) — neither is the slot list the modder reasons
    /// in (HCBR-2026-07-12). This annotation rides <see cref="FieldValue.Display"/>, so the round-trip
    /// <see cref="LeafRead.Token"/> is untouched (write/read-proof/diff never see it). Gated to BipedObjectFlag by
    /// name — the slot=30+bit mapping is meaningless for any other <c>[Flags]</c> enum. Null for every non-biped
    /// leaf, an unset mask, or a non-flags value.</summary>
    /// <param name="leaf">A previously emitted leaf, optionally carrying flags metadata.</param>
    /// <returns>A slot-number annotation, or null when the mapping does not apply.</returns>
    internal static string? FlagSlotDisplay(LeafRead leaf)
    {
        if (!leaf.HasValue || leaf.Flags is not { } fb || fb.EnumType.Name != "BipedObjectFlag") return null;
        var slots = new List<int>();
        for (int i = 0; i < 32; i++) if ((fb.Bits & (1UL << i)) != 0) slots.Add(30 + i);
        if (slots.Count == 0) return null;
        return (slots.Count == 1 ? "slot " : "slots ") + string.Join(", ", slots);
    }

    /// <summary>The DISPLAY-ONLY annotation for a <c>[Flags]</c> enum leaf — the human-readable decode that rides
    /// <see cref="FieldValue.Display"/> without touching the round-trip <see cref="LeafRead.Token"/>. A biped-slot
    /// flags leaf gets the slot-number decode (<see cref="FlagSlotDisplay"/>); every OTHER flags enum gets the
    /// unknown-bits decode (<see cref="FlagBitsDisplay"/>), which fires only when unnamed bits are present. The two
    /// are mutually exclusive by construction — a biped leaf with any bit set already yields a slot decode, and one
    /// with no bit set has no unknown bits either — so the <c>??</c> never double-annotates. Null when neither
    /// applies (a non-flags leaf, or a flags leaf whose every set bit is already named).</summary>
    /// <param name="leaf">A previously emitted leaf.</param>
    /// <returns>The one applicable flags annotation, otherwise null.</returns>
    internal static string? FlagDisplay(LeafRead leaf) => FlagSlotDisplay(leaf) ?? FlagBitsDisplay(leaf);

    /// <summary>The DISPLAY-ONLY decode for a <c>[Flags]</c> enum leaf carrying bits the catalog does NOT name — the
    /// case where <c>[Flags].ToString()</c> abandons the name list and renders a bare decimal (e.g. an NPC
    /// <c>Configuration.Flags</c> whose value includes an unnamed modder/game-version bit), silently losing even the
    /// KNOWN bits a consumer needs (gender / uniqueness / ghost state — #255). This surfaces the known bits by NAME
    /// plus the unnamed remainder as an explicit hex mask — <c>&lt;known flag names&gt; (+unknown bits 0x…)</c> — so
    /// the common bits stay directly consumable and the presence of unknown bits is STATED, not hidden. Rides
    /// <see cref="FieldValue.Display"/>, so the round-trip <see cref="LeafRead.Token"/> (the bare decimal, which
    /// <c>Enum.Parse</c> re-accepts) is untouched — write / read-proof / diff never see it, exactly like the
    /// biped-slot decode. Null when the leaf is not a flags enum OR every set bit is already named (ToString gave
    /// the full name list — nothing to recover).</summary>
    /// <param name="leaf">A flags leaf whose original token remains untouched.</param>
    /// <returns>A known-name plus unknown-mask annotation, or null when every bit is nameable.</returns>
    internal static string? FlagBitsDisplay(LeafRead leaf)
    {
        if (!leaf.HasValue || leaf.Flags is not { } fb) return null;
        // Peel the NAMEABLE bits exactly the way .NET's [Flags].ToString() does: greedily apply each named member that
        // is FULLY contained (largest value first, so a multi-bit COMBO member wins over its constituent bits), and
        // whatever bits no member can cover are the unknown remainder. Do NOT just OR every member's bits into one
        // "known" mask: a bit that exists ONLY inside a multi-bit combo member (e.g. Package.Flag.WearSleepOutfit)
        // would count as "known" yet ToString can't name it on its own, so the "known names" slot would itself render
        // a bare decimal — the very thing this decode exists to avoid (PR #261 review).
        var members = new List<ulong>();
        foreach (var member in Enum.GetValues(fb.EnumType))
            if (TryEnumBits(member, fb.EnumType, out var mb) && mb != 0) members.Add(mb);
        members.Sort((a, b) => b.CompareTo(a));   // descending (unsigned) — a combo before its constituent bits
        ulong remainder = fb.Bits;
        foreach (var mb in members) if ((remainder & mb) == mb) remainder &= ~mb;
        if (remainder == 0) return null;   // every set bit is nameable — ToString already gave the full name list
        // The nameable bits are exactly a union of whole members, so ToString renders them as clean names (never a
        // decimal); state the remainder as an explicit hex mask so nothing is silently dropped.
        ulong nameable = fb.Bits & ~remainder;
        var names = nameable == 0 ? null : Enum.ToObject(fb.EnumType, nameable).ToString();
        return string.IsNullOrEmpty(names) || names == "0"
            ? $"unknown bits 0x{remainder:X}"
            : $"{names} (+unknown bits 0x{remainder:X})";
    }

    /// <summary>Emits invariant, round-trip text for the primitive family accepted by
    /// <c>WriteEngine.TryPrimitive</c>.</summary>
    /// <param name="val">The boxed candidate value.</param>
    /// <param name="token">Receives the token, or an empty string when the type is not primitive.</param>
    /// <returns>True when the value belongs to the supported primitive family.</returns>
    static bool TryEmitPrimitive(object val, out string token)
    {
        switch (val)
        {
            case string s: token = s; return true;
            case bool b: token = b ? "True" : "False"; return true;
            // round-trippable "R" so float.Parse/double.Parse reproduce the exact IEEE bits.
            case float fl: token = fl.ToString("R", CultureInfo.InvariantCulture); return true;
            case double d: token = d.ToString("R", CultureInfo.InvariantCulture); return true;
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                token = Convert.ToString(val, CultureInfo.InvariantCulture)!; return true;
        }
        token = "";
        return false;
    }

    /// <summary>Emits the structured value-type tokens accepted by <c>WriteEngine.TryValueType</c>, including
    /// colors, time values, model identifiers, Noggog points/percentages, byte slices, and asset links.</summary>
    /// <param name="val">The boxed candidate value.</param>
    /// <param name="token">Receives the token, or an empty string when no supported shape matches.</param>
    /// <returns>True when a faithful inverse token was produced.</returns>
    static bool TryEmitValueType(object val, out string token)
    {
        token = "";
        switch (val)
        {
            case System.Drawing.Color c: token = $"{c.R},{c.G},{c.B},{c.A}"; return true;       // "R,G,B,A"
            case DateTime dt: token = dt.ToString("O", CultureInfo.InvariantCulture); return true;
            case TimeOnly t: token = t.ToString("O", CultureInfo.InvariantCulture); return true;
            case char ch: token = ch.ToString(); return true;
            case string[] arr: token = string.Join(",", arr); return true;
            case FormKey fk: token = fk.ToString(); return true;
            case ModKey mk: token = mk.ToString(); return true;
            case RecordType rt: token = rt.ToString(); return true;
        }

        var rt2 = val.GetType();
        var fn = rt2.FullName;

        // (TranslatedString is handled earlier in EmitToken — a null .String surfaces as a loud no-value note,
        //  not the blank token this branch used to fold it into; see UnresolvedStringNote.)

        // Noggog.Percent — emit the [0..1] fraction its single-arg ctor takes. Find the underlying
        // numeric member BY TYPE, not a guessed name: Percent stores exactly one double (its ToString
        // is the "33%" display form, which Coerce can't parse — the round-trip oracle caught that).
        if (fn == "Noggog.Percent")
        { token = NumericComponentInvariant(val) ?? val.ToString() ?? ""; return true; }

        // Noggog point structs P2*/P3* — components in constructor order ("x,y,z").
        if (rt2.Namespace == "Noggog" && (rt2.Name.StartsWith("P2") || rt2.Name.StartsWith("P3")))
        { token = PointComponents(val); return true; }

        // (ReadOnly)MemorySlice<byte> — raw blob as a hex string.
        if (IsByteMemorySlice(rt2)) { token = Convert.ToHexString(MemorySliceBytes(val)); return true; }

        // AssetLink<T> family — the stored path string. Recognised by generic-definition NAME (the mutable
        // AssetLink<T> Coerce builds, the getter overlay's AssetLinkGetter<T>, or the IAssetLink(Getter)<T>
        // interfaces) so the value READ off a getter is handled, not only the mutable type — the same
        // by-name recognition the engine uses for the FLOI family. Shares ONE predicate with the write
        // coercion (WriteEngine.IsAssetLinkFamily) so read and write can't drift on what an asset link is.
        if (WriteEngine.IsAssetLinkFamily(rt2))
        { token = ReflectString(val, "GivenPath", "RawPath", "DataRelativePath") ?? val.ToString() ?? ""; return true; }

        return false;
    }

    // -- FLOI (mirror SetFloi / ClassifyFloiValue) -----------------------------
    /// <summary>Emit a condition-target FormLinkOrIndex as the token that re-creates it: a FormKey in
    /// form mode, else "alias N" / "packdata N" per the owning arm's discriminator. The index payload
    /// accessor is read defensively; if the mode or index can't be read cleanly the leaf is surfaced as
    /// a note (never a guessed four bytes — Q3). The precise index round-trip is exercised by the
    /// oracle's FLOI phase.</summary>
    /// <param name="val">The FormLinkOrIndex value object.</param>
    /// <param name="parent">The condition-data arm carrying mode discriminator booleans.</param>
    /// <returns>A FormKey/index token or a named no-value result when the representation is unreadable.</returns>
    static LeafRead EmitFloi(object val, object parent)
    {
        bool? useAliases = ReflectBool(parent, "UseAliases");
        bool? usePackData = ReflectBool(parent, "UsePackageData");
        if (useAliases is null || usePackData is null)
            return LeafRead.None($"(floi: parent {parent.GetType().Name} has no UseAliases/UsePackageData discriminator)");

        if (useAliases == false && usePackData == false)
        {
            // Form mode. The binary overlay's FLOI is NOT itself a link — it carries the link in its
            // .Link property, the same accessor the write side reads (ReadFloiFormKey, oracle-proven).
            // A present-but-null link stays a note, matching plain FormLink leaves (HCBR-2026-06-09-02).
            if (val is IFormLinkGetter fl) return LeafRead.Value(fl.FormKey.ToString());
            if (WriteEngine.ReadFloiFormKey(val) is { } fk) return LeafRead.Value(fk.ToString());
            return LeafRead.None($"(floi: form mode, null or unreadable FormKey on {val.GetType().Name})");
        }

        // index mode — read the numeric index defensively (FormLinkOrIndex carries it alongside the link).
        var idx = ReflectUInt(val, "Index", "RawIndex", "FormKeyOrIndex");
        if (idx is null) return LeafRead.None("(floi: index mode, index accessor unresolved — refined in oracle)");
        return LeafRead.Value(useAliases == true ? $"alias {idx}" : $"packdata {idx}");
    }

    // ======================================================================
    //  Reflection helpers — read an accessor by candidate names, defensively.
    //  The round-trip oracle validates which accessor is the faithful inverse;
    //  a wrong guess fails the proof loud + precise rather than silently.
    // ======================================================================

    /// <summary>Reads the first named public string property available on an implementation-variant object.</summary>
    /// <param name="obj">The value whose accessors are inspected.</param>
    /// <param name="names">Candidate property names in preference order.</param>
    /// <returns>The first string value found, otherwise null.</returns>
    static string? ReflectString(object obj, params string[] names)
    {
        foreach (var n in names)
        {
            var p = obj.GetType().GetProperty(n, BindingFlags.Public | BindingFlags.Instance);
            if (p?.GetValue(obj) is string s) return s;
        }
        return null;
    }

    /// <summary>Reads an explicitly named boolean through Mutagen-aware property resolution.</summary>
    /// <param name="obj">The owner object.</param>
    /// <param name="name">The modeled property name.</param>
    /// <returns>The boolean value, or null when the accessor/value is unavailable.</returns>
    static bool? ReflectBool(object obj, string name)
        => WriteEngine.ResolveProperty(obj.GetType(), name)?.GetValue(obj) as bool?;

    /// <summary>Reads the first candidate non-negative integer accessor and normalizes it to unsigned form.</summary>
    /// <param name="obj">The value whose accessors are inspected.</param>
    /// <param name="names">Candidate property names in preference order.</param>
    /// <returns>The normalized value, or null when no supported accessor exists.</returns>
    static uint? ReflectUInt(object obj, params string[] names)
    {
        foreach (var n in names)
        {
            var v = obj.GetType().GetProperty(n, BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj);
            if (v is uint u) return u;
            if (v is int i && i >= 0) return (uint)i;
        }
        return null;
    }

    /// <summary>Emit a single-component numeric value object's underlying number, found BY TYPE (the
    /// first public double/float property, else field) — robust to the member's name. Used for
    /// Noggog.Percent, whose ToString is a display form Coerce can't re-parse.</summary>
    /// <param name="val">A single-component numeric wrapper.</param>
    /// <returns>Invariant round-trip text, or null when no numeric component can be found.</returns>
    static string? NumericComponentInvariant(object val)
    {
        var t = val.GetType();
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (p.GetIndexParameters().Length == 0 && (p.PropertyType == typeof(double) || p.PropertyType == typeof(float)))
                return Convert.ToDouble(p.GetValue(val), CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
        foreach (var fld in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (fld.FieldType == typeof(double) || fld.FieldType == typeof(float))
                return Convert.ToDouble(fld.GetValue(val), CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
        return null;
    }

    /// <summary>Emit a Noggog P2*/P3* point's components in CONSTRUCTOR-PARAMETER order ("x,y,z"),
    /// reading each via the matching public property — so the token splits back into the same ctor
    /// args TryValueType's ConstructByCtor consumes.</summary>
    /// <param name="val">The Noggog point value.</param>
    /// <returns>Comma-separated invariant components in constructor order.</returns>
    static string PointComponents(object val)
    {
        var t = val.GetType();
        var ctor = t.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        var parms = ctor?.GetParameters();
        if (parms is null || parms.Length == 0) return val.ToString() ?? "";
        var parts = new List<string>(parms.Length);
        foreach (var pp in parms)
        {
            var prop = t.GetProperty(pp.Name!, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            var comp = prop?.GetValue(val);
            parts.Add(comp switch
            {
                float f => f.ToString("R", CultureInfo.InvariantCulture),
                double d => d.ToString("R", CultureInfo.InvariantCulture),
                null => "0",
                _ => Convert.ToString(comp, CultureInfo.InvariantCulture) ?? "0",
            });
        }
        return string.Join(",", parts);
    }

    /// <summary>Recognizes Noggog mutable/read-only memory slices whose element type is exactly byte.</summary>
    /// <param name="t">The candidate runtime type.</param>
    /// <returns>True only for supported generic byte-slice definitions.</returns>
    static bool IsByteMemorySlice(Type t)
        => t.IsGenericType
           && (t.GetGenericTypeDefinition() == typeof(Noggog.MemorySlice<>) || t.GetGenericTypeDefinition() == typeof(Noggog.ReadOnlyMemorySlice<>))
           && t.GetGenericArguments()[0] == typeof(byte);

    // AssetLink-family recognition lives in WriteEngine.IsAssetLinkFamily (ONE predicate, shared with write
    // coercion — read emits the path, write builds the link from it; they must agree on the family).

    /// <summary>True if <paramref name="t"/> is a VMAD script-property arm (ScriptObjectProperty, the scalar
    /// ScriptInt/Float/Bool/StringProperty arms, and the *ListProperty arms) — recognised by the shared getter
    /// interface, so every arm matches by construction with no per-arm list, on the overlay getter or the
    /// mutable type alike. The depth walker opens such a property's direct value members one bounded level past
    /// the depth floor (1.3.1 item 2 — read parity with the write surface). One of TWO type-targeted exceptions
    /// to the depth gate (the other is <see cref="IsConditionData"/>); every other substruct stops at the floor.</summary>
    /// <param name="t">The candidate runtime/declared getter type.</param>
    /// <returns>True for every VMAD script-property arm.</returns>
    static bool IsScriptProperty(Type t) => typeof(IScriptPropertyGetter).IsAssignableFrom(t);

    /// <summary>True if <paramref name="t"/> is a polymorphic CONDITION-DATA arm (GetActorValueConditionData,
    /// GetFactionRankConditionData — every <c>ConditionData</c> subtype) — recognised by the shared
    /// <c>IConditionDataGetter</c> interface, so every arm matches by construction with no per-arm list, on the
    /// overlay getter or the mutable type alike. Like <see cref="IsScriptProperty"/> the depth walker opens such an
    /// arm's parameter fields one bounded level past the depth floor so a <c>Conditions</c>-list dump reaches the
    /// arm's params (Faction/Global/Reference/RunOnType…) without an extra depth level or a per-row <c>Data</c> path
    /// (#258 — the params surfaced ONLY via a direct arm path before). An arm's direct members are leaves/links, so
    /// this opens exactly one level and never unbounded-descends; every non-arm substruct still stops at the floor.</summary>
    /// <param name="t">The candidate runtime/declared getter type.</param>
    /// <returns>True for every polymorphic condition-data arm.</returns>
    static bool IsConditionData(Type t) => typeof(IConditionDataGetter).IsAssignableFrom(t);

    /// <summary>Copies a Noggog byte slice to an owned array using its direct API or a reflected length/indexer
    /// fallback. Throws when neither representation is available because returning partial bytes would corrupt the
    /// round-trip token.</summary>
    /// <param name="slice">A value already accepted by <see cref="IsByteMemorySlice"/>.</param>
    /// <returns>An owned byte array containing the complete slice.</returns>
    static byte[] MemorySliceBytes(object slice)
    {
        // Noggog slices expose ToArray() (and a Length + indexer fallback). Reflection-robust either way.
        var toArray = slice.GetType().GetMethod("ToArray", Type.EmptyTypes);
        if (toArray?.Invoke(slice, null) is byte[] arr) return arr;
        var lenProp = slice.GetType().GetProperty("Length", BindingFlags.Public | BindingFlags.Instance);
        var idxer = slice.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(p => p.GetIndexParameters().Length == 1 && p.PropertyType == typeof(byte));
        if (lenProp?.GetValue(slice) is int len && idxer is not null)
        {
            var bytes = new byte[len];
            for (int i = 0; i < len; i++) bytes[i] = (byte)idxer.GetValue(slice, new object[] { i })!;
            return bytes;
        }
        throw new InvalidOperationException($"Cannot extract bytes from MemorySlice {slice.GetType().Name}.");
    }

    /// <summary>A short, non-round-trippable description of a container leaf (substruct / list / dict /
    /// arm) for the read display. Its sub-leaves are the round-trippable surface. A collection renders as a
    /// clean <c>[list: N item(s)]</c> / <c>[dict: N pair(s)]</c> — the Mutagen overlay class name
    /// (<c>BinaryOverlayListByStartIndex`1</c>) is container plumbing, NOT the element type, so it is pure noise
    /// to the reader and is dropped (HCBR-2026-07-12). The <c>item(s)</c>/<c>pair(s)</c> marker is LOAD-BEARING —
    /// <c>FieldsDiff</c> splits numeric-keyed dicts (Package.Data) out of positional-list comparison on the
    /// exact <c>" pair(s)]"</c> substring (PR #28 review) — so it is kept verbatim. A substruct keeps its
    /// <c>[TypeName]</c> (e.g. <c>[BodyTemplate]</c>): there the type name IS informative.</summary>
    /// <param name="val">A non-null container or substructure.</param>
    /// <param name="isDict">Whether an enumerable is logically key/value rather than positional.</param>
    /// <returns>The load-bearing display summary.</returns>
    static string SummariseContainer(object val, bool isDict = false) => SummariseContainer(val, isDict, out _);

    /// <summary>Overload that also yields the element <paramref name="count"/>: a number for a list/dict (0 = empty),
    /// null for a substruct (no element count — present by being non-null). The presence predicate reads this to
    /// tell a carried list from an empty one; every display caller keeps the count-free overload above. One
    /// enumeration, one format source (the <c>item(s)</c>/<c>pair(s)</c> marker stays load-bearing for FieldsDiff).</summary>
    /// <param name="val">A non-null container or substructure.</param>
    /// <param name="isDict">Whether an enumerable is logically key/value rather than positional.</param>
    /// <param name="count">Receives collection size, or null for a non-enumerable substructure.</param>
    /// <returns>The same load-bearing summary as the count-free overload.</returns>
    static string SummariseContainer(object val, bool isDict, out int? count)
    {
        count = null;
        if (val is System.Collections.IEnumerable en and not string)
        {
            int n = 0;
            foreach (var _ in en) n++;
            count = n;
            return $"[{(isDict ? "dict" : "list")}: {n} {(isDict ? "pair(s)" : "item(s)")}]";
        }
        return $"[{RecordNaming.StripGetterInterface(val.GetType().Name)}]";
    }

    /// <summary>The modeled field names for the whole-record dump. Prefer the CORPUS — the authoritative
    /// by-construction modeled-field set (exactly what the read-proof drives, infra-free) — and fall back
    /// to the record's getter interfaces (Loqui-namespace filtered) only when the corpus isn't built.</summary>
    /// <param name="typeName">Normalized corpus record-type key.</param>
    /// <param name="recordRuntimeType">Concrete overlay/mutable type used by the reflection fallback.</param>
    /// <returns>Modeled top-level field names in corpus or stable reflected order.</returns>
    static IEnumerable<string> ModeledFieldNames(string typeName, Type recordRuntimeType)
    {
        Corpus? corpus = null;
        try { corpus = CorpusRulebook.LoadCorpus(); } catch { /* corpus not built / unparseable → reflection fallback */ }
        if (corpus is not null && corpus.Types.TryGetValue(typeName, out var schema))
        {
            foreach (var f in schema.Fields) yield return f.Name;
            yield break;
        }

        // Fallback (no corpus): the record's getter interfaces, with the Loqui infrastructure filter
        // (drops plumbing like Registration / BinaryWriteTranslator / Type declared on Loqui base types).
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var primary = WriteEngine.PrimaryGetter(recordRuntimeType);
        if (primary is null) yield break;
        var ifaces = new List<Type> { primary };
        ifaces.AddRange(primary.GetInterfaces());
        foreach (var iface in ifaces)
            foreach (var p in iface.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (p.GetIndexParameters().Length == 0
                    && p.DeclaringType?.Namespace?.StartsWith("Loqui", StringComparison.Ordinal) != true
                    && seen.Add(p.Name))
                    yield return p.Name;
    }
}
