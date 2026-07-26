using System.Reflection;
using System.Text.Json;

namespace HousecarlCore;

/// <summary>
/// Provides the documented SkyPatcher grammar used to interpret parsed
/// <c>key=value</c> segments.
///
/// <para>
/// The catalog is a closed enumeration of filters and operations transcribed
/// from the bundled <c>skypatcher-authoring</c> reference. Unknown keys remain
/// <see cref="SkyPatcherKeyRole.Unknown"/> instead of inheriting guessed semantics.
/// </para>
///
/// <para>
/// The embedded JSON is loaded once. Catalog guards cross-check each record's
/// subfolder, signature, and primary filter against the reference index. This
/// class classifies grammar only; Mutagen field mapping and value resolution
/// belong to the overlay layer.
/// </para>
/// </summary>
public sealed class SkyPatcherCatalog
{
    /// <summary>The connective vocabulary, DERIVED from the loaded catalog (union of every filter's
    /// non-empty connectives) so it can't drift from the JSON — a re-transcribed new connective is
    /// stripped without touching this class. Longest-first so 'Excluded' is tried before 'Exclude'
    /// before 'Or'; ties broken ordinally for determinism.</summary>
    readonly string[] _connectiveSuffixes;

    /// <summary>Every record type's catalog, in load order.</summary>
    public IReadOnlyList<SkyPatcherRecordCatalog> Records { get; }

    readonly Dictionary<string, SkyPatcherRecordCatalog> _bySubfolder;   // subfolder (case-insensitive) → catalog
    readonly Dictionary<string, RecordLookup> _lookup;                    // subfolder (case-insensitive) → name maps

    sealed record RecordLookup(
        Dictionary<string, SkyPatcherFilterDef> Filters,   // base filter name (ordinal) → def
        Dictionary<string, SkyPatcherOpDef> Operations);   // op name (ordinal) → def

    SkyPatcherCatalog(IReadOnlyList<SkyPatcherRecordCatalog> records)
    {
        Records = records;
        _bySubfolder = new(StringComparer.OrdinalIgnoreCase);
        _lookup = new(StringComparer.OrdinalIgnoreCase);
        foreach (var r in records)
        {
            // Deliberate case asymmetry: subfolders match case-insensitively as host paths, but filter/op
            // key names match case-SENSITIVELY as the reference documents them. Whether the real SkyPatcher
            // DLL acceptance of e.g. 'attackdamage' is not established; until verified, a wrong-cased key
            // classifies as Unknown (a loud warn, never a silent guess).
            _bySubfolder[r.Subfolder] = r;
            _lookup[r.Subfolder] = new RecordLookup(
                r.Filters.ToDictionary(f => f.Name, f => f, StringComparer.Ordinal),
                r.Operations.ToDictionary(o => o.Name, o => o, StringComparer.Ordinal));
        }
        _connectiveSuffixes = records.SelectMany(r => r.Filters).SelectMany(f => f.Connectives)
            .Where(c => c.Length > 0).Distinct(StringComparer.Ordinal)
            .OrderByDescending(c => c.Length).ThenBy(c => c, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The record catalog for an INI subfolder (e.g. "weapon", "constructibleObject"), or null if unknown.</summary>
    public SkyPatcherRecordCatalog? ForSubfolder(string subfolder)
        => subfolder is not null && _bySubfolder.TryGetValue(subfolder, out var r) ? r : null;

    /// <summary>
    /// Classify a raw segment key against a record type's catalog. Order: exact operation (ops take no
    /// connective) → bare filter → filter + a documented connective suffix → Unknown. Both filter paths
    /// enforce the documented connective set: the bare form is only valid when the filter documents ""
    /// among its connectives (six filters exist ONLY in suffixed form — e.g. filterByFirstPersonModelOr —
    /// and their bare spelling is an undocumented token that must warn, not pass), and a suffix is only
    /// stripped when the remaining base is a real filter that documents that connective, so an operation
    /// that merely ends in "Or" isn't mis-split.
    /// </summary>
    public SkyPatcherKeyClass Classify(SkyPatcherRecordCatalog record, string key)
    {
        var lk = _lookup[record.Subfolder];

        if (lk.Operations.TryGetValue(key, out var op))
            return new SkyPatcherKeyClass(SkyPatcherKeyRole.Operation, key, null, null, op);

        if (lk.Filters.TryGetValue(key, out var bare) && bare.Connectives.Contains(""))
            return new SkyPatcherKeyClass(SkyPatcherKeyRole.Filter, key, "", bare, null);

        foreach (var c in _connectiveSuffixes)
            if (key.Length > c.Length && key.EndsWith(c, StringComparison.Ordinal))
            {
                var baseKey = key[..^c.Length];
                if (lk.Filters.TryGetValue(baseKey, out var f) && f.Connectives.Contains(c))
                    return new SkyPatcherKeyClass(SkyPatcherKeyRole.Filter, baseKey, c, f, null);
            }

        return new SkyPatcherKeyClass(SkyPatcherKeyRole.Unknown, key, null, null, null);
    }

    // ---- loading -----------------------------------------------------------------------------------

    static SkyPatcherCatalog? _cached;

    /// <summary>Load the embedded catalog (memoized). Throws loudly on a missing/malformed resource — a
    /// catalog that silently loaded empty would make every operation appear unknown.</summary>
    public static SkyPatcherCatalog Load() => _cached ??= LoadFrom(EmbeddedJson.Read("skypatcher-catalog.json", "SkyPatcher catalog"));

    /// <summary>Parse a catalog from JSON text (also the guard's entry point for a fixture).</summary>
    public static SkyPatcherCatalog LoadFrom(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var records = new List<SkyPatcherRecordCatalog>();
        foreach (var el in doc.RootElement.EnumerateArray())
            records.Add(ParseRecord(el));
        return new SkyPatcherCatalog(records);
    }

    /// <summary>Parses one record-type entry and all of its filters and operations.</summary>
    static SkyPatcherRecordCatalog ParseRecord(JsonElement el)
    {
        var recordType = Str(el, "recordType");
        var filters = new List<SkyPatcherFilterDef>();
        var ops = new List<SkyPatcherOpDef>();

        // A PRESENT-but-wrong-kind node throws loudly like every other malformed field — a mistyped
        // Parsing a present filters/operations/connectives node as empty on a type
        // error would silently make every key appear unknown.
        if (el.TryGetProperty("filters", out var fs) && RequireArray(fs, "filters", recordType))
            foreach (var f in fs.EnumerateArray())
                filters.Add(new SkyPatcherFilterDef(
                    Str(f, "name"),
                    ParseFilterKind(Str(f, "kind"), recordType),
                    f.TryGetProperty("connectives", out var cs) && RequireArray(cs, "connectives", recordType)
                        ? cs.EnumerateArray().Select(c => c.GetString() ?? "").ToArray()
                        : new[] { "" },
                    OptStr(f, "selects")));

        if (el.TryGetProperty("operations", out var opsEl) && RequireArray(opsEl, "operations", recordType))
            foreach (var o in opsEl.EnumerateArray())
                ops.Add(new SkyPatcherOpDef(
                    Str(o, "name"),
                    ParseShape(Str(o, "shape"), recordType),
                    ParseTractability(Str(o, "tractability"), recordType),
                    o.TryGetProperty("stateful", out var st) && st.ValueKind == JsonValueKind.True,
                    OptStr(o, "note")));

        return new SkyPatcherRecordCatalog(
            recordType, Str(el, "sig"), Str(el, "subfolder"), Str(el, "primaryFilter"),
            filters, ops, OptStr(el, "note"));
    }

    /// <summary>True when the (present) node is an array; throws loudly on any other kind.</summary>
    static bool RequireArray(JsonElement el, string prop, string ctx)
        => el.ValueKind == JsonValueKind.Array ? true
            : throw new InvalidOperationException($"SkyPatcher catalog [{ctx}]: '{prop}' is present but not an array.");

    /// <summary>Reads a required JSON string property.</summary>
    static string Str(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new InvalidOperationException($"SkyPatcher catalog entry missing required string '{prop}'.");

    /// <summary>Reads an optional JSON string property.</summary>
    static string? OptStr(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Parses a filter-kind token or reports its record context.</summary>
    static SkyPatcherFilterKind ParseFilterKind(string s, string ctx) => s switch
    {
        "primary" => SkyPatcherFilterKind.Primary,
        "crosscutting" => SkyPatcherFilterKind.CrossCutting,
        "recordSpecific" => SkyPatcherFilterKind.RecordSpecific,
        "restrict" => SkyPatcherFilterKind.Restrict,
        "hasPlugins" => SkyPatcherFilterKind.HasPlugins,
        "overrideAware" => SkyPatcherFilterKind.OverrideAware,
        "noFilter" => SkyPatcherFilterKind.NoFilter,
        _ => throw new InvalidOperationException($"SkyPatcher catalog [{ctx}]: unknown filter kind '{s}'."),
    };

    /// <summary>Parses an operation-shape token or reports its record context.</summary>
    static SkyPatcherOpShape ParseShape(string s, string ctx) => s switch
    {
        "set" => SkyPatcherOpShape.Set,
        "mult" => SkyPatcherOpShape.Mult,
        "add_numeric" => SkyPatcherOpShape.AddNumeric,
        "collection" => SkyPatcherOpShape.Collection,
        "mirror" => SkyPatcherOpShape.Mirror,
        "flags" => SkyPatcherOpShape.Flags,
        "rename" => SkyPatcherOpShape.Rename,
        "null_clear" => SkyPatcherOpShape.NullClear,
        "compound" => SkyPatcherOpShape.Compound,
        _ => throw new InvalidOperationException($"SkyPatcher catalog [{ctx}]: unknown op shape '{s}'."),
    };

    /// <summary>Parses a tractability token or reports its record context.</summary>
    static SkyPatcherTractability ParseTractability(string s, string ctx) => s.ToUpperInvariant() switch
    {
        "CLEAN" => SkyPatcherTractability.Clean,
        "COLLECTION" => SkyPatcherTractability.Collection,
        "HARD" => SkyPatcherTractability.Hard,
        _ => throw new InvalidOperationException($"SkyPatcher catalog [{ctx}]: unknown tractability '{s}'."),
    };
}

/// <summary>How a filter combines its values (by suffix): Primary is the record's own filterBy&lt;Type&gt;.</summary>
public enum SkyPatcherFilterKind
{
    /// <summary>Selects records by the current record type's primary identity.</summary>
    Primary,
    /// <summary>Uses a filter shared across several record types.</summary>
    CrossCutting,
    /// <summary>Uses a filter defined only for this record type.</summary>
    RecordSpecific,
    /// <summary>Restricts records already selected by another filter.</summary>
    Restrict,
    /// <summary>Tests whether named plugins are present.</summary>
    HasPlugins,
    /// <summary>Uses winning or originating override context.</summary>
    OverrideAware,
    /// <summary>Applies without selecting records through a filter.</summary>
    NoFilter,
}

/// <summary>The value-grammar shape of an operation (inventory categories a–i).</summary>
public enum SkyPatcherOpShape
{
    /// <summary>Replaces a value.</summary>
    Set,
    /// <summary>Multiplies an existing numeric value.</summary>
    Mult,
    /// <summary>Adds to an existing numeric value.</summary>
    AddNumeric,
    /// <summary>Adds, removes, or replaces collection members.</summary>
    Collection,
    /// <summary>Copies or mirrors a related value.</summary>
    Mirror,
    /// <summary>Mutates individual flag bits.</summary>
    Flags,
    /// <summary>Changes a record's display or editor name.</summary>
    Rename,
    /// <summary>Clears a nullable value.</summary>
    NullClear,
    /// <summary>Encodes several coordinated changes in one operation.</summary>
    Compound,
}

/// <summary>How faithfully the overlay can resolve an operation's post-state.</summary>
public enum SkyPatcherTractability
{
    /// <summary>The resulting scalar state can be resolved directly.</summary>
    Clean,
    /// <summary>The result depends on ordered collection state.</summary>
    Collection,
    /// <summary>The runtime result cannot be reproduced safely by the static overlay.</summary>
    Hard,
}

/// <summary>What a classified segment key is.</summary>
public enum SkyPatcherKeyRole
{
    /// <summary>The key selects or restricts target records.</summary>
    Filter,
    /// <summary>The key changes selected records.</summary>
    Operation,
    /// <summary>The key is absent from the bundled reference catalog.</summary>
    Unknown,
}

/// <summary>One documented filter token (base name; connective variants are in <see cref="Connectives"/>).</summary>
public sealed record SkyPatcherFilterDef(string Name, SkyPatcherFilterKind Kind, IReadOnlyList<string> Connectives, string? Selects);

/// <summary>One documented operation token, with its shape, tractability, and stateful-value flag.</summary>
public sealed record SkyPatcherOpDef(string Name, SkyPatcherOpShape Shape, SkyPatcherTractability Tractability, bool Stateful, string? Note);

/// <summary>One record type's full closed filter+operation catalog. <see cref="Note"/> carries e.g. the OMOD gap.</summary>
public sealed record SkyPatcherRecordCatalog(
    string RecordType,
    string Sig,
    string Subfolder,
    string PrimaryFilter,
    IReadOnlyList<SkyPatcherFilterDef> Filters,
    IReadOnlyList<SkyPatcherOpDef> Operations,
    string? Note);

/// <summary>
/// The classification of one segment key against a record type. For a filter, <see cref="Connective"/>
/// is "" (bare) / "Or" / "Excluded" / "Exclude" and <see cref="BaseKey"/> is the connective-stripped
/// name. For an operation, <see cref="Operation"/> is set. <see cref="SkyPatcherKeyRole.Unknown"/> means
/// the key is in no reference entry for this type — surface it, don't assume.
/// </summary>
public sealed record SkyPatcherKeyClass(
    SkyPatcherKeyRole Role,
    string BaseKey,
    string? Connective,
    SkyPatcherFilterDef? Filter,
    SkyPatcherOpDef? Operation);
