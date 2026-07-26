using System.Text.Json;

namespace HousecarlCore;

/// <summary>Describes one validated write operation against a record field path.</summary>
public sealed class WriteRequest
{
    /// <summary>Gets the schema catalog name of the target record.</summary>
    public required string RecordType { get; init; }

    /// <summary>Gets field-name hops from the record root to the target leaf.</summary>
    public required string[] Path { get; init; }

    /// <summary>Gets the requested write verb.</summary>
    public required string Verb { get; init; }

    /// <summary>Gets an optional dictionary key or list index.</summary>
    public string? Key { get; init; }

    /// <summary>Gets an optional scalar value.</summary>
    public string? Value { get; init; }

    /// <summary>Gets optional replacement list values.</summary>
    public string[]? Values { get; init; }

    /// <summary>Gets optional replacement or merge dictionary entries.</summary>
    public Dictionary<string, string>? Entries { get; init; }

    /// <summary>Gets an optional structure built from fields and nested writes.</summary>
    public StructSpec? Struct { get; init; }

    /// <summary>Gets optional structures for batch add or replacement.</summary>
    public IReadOnlyList<StructSpec>? Structs { get; init; }
}

/// <summary>Describes a modeled structure assembled from flat values and recursive write operations.</summary>
public sealed class StructSpec
{
    /// <summary>Gets the concrete catalog type to instantiate.</summary>
    public required string Type { get; init; }

    /// <summary>Gets optional flat field values applied as Set operations.</summary>
    public Dictionary<string, string>? Fields { get; init; }

    /// <summary>Gets optional positional constructor arguments.</summary>
    public string[]? CtorArgs { get; init; }

    /// <summary>Gets optional recursive writes rooted at the new structure.</summary>
    public List<WriteRequest>? Sets { get; init; }
}

/// <summary>Validates write requests against the reflection-generated corpus before Mutagen mutation.</summary>
/// <remarks>Every rejection names the invalid shape and the accepted alternative.</remarks>
public sealed class CorpusRulebook
{
    readonly Corpus _corpus;
    CorpusRulebook(Corpus corpus) => _corpus = corpus;

    /// <summary>Accepted text forms for a condition FormLink-or-index target.</summary>
    const string FloiTargetForms =
        "a FormID (XXXXXX:Plugin.esp → form mode), a bare index, or 'alias N' / 'packdata N' (→ index mode)";

    /// <summary>Gets the number of reflected schema types.</summary>
    public int TypeCount => _corpus.TotalTypes;

    /// <summary>Gets a schema type by catalog name, or null when absent.</summary>
    public TypeSchema? Type(string name) => _corpus.Types.GetValueOrDefault(name);

    /// <summary>Gets or sets the single corpus path used by default loaders.</summary>
    /// <remarks>Production startup must set an absolute installed path because the host working directory is arbitrary.</remarks>
    public static string CorpusPath { get; set; } = Path.Combine("generated", "corpus.json");

    /// <summary>Load the validator rulebook from the configured <see cref="CorpusPath"/>.</summary>
    public static CorpusRulebook Load() => new(LoadCorpus());

    /// <summary>Load the validator rulebook from an explicit path (the harness; tests).</summary>
    public static CorpusRulebook Load(string corpusJsonPath) => new(LoadCorpus(corpusJsonPath));

    /// <summary>The raw deserialised <see cref="Corpus"/> from the configured <see cref="CorpusPath"/> — for
    /// consumers that want the catalog model directly (the read engine's field-name lookup; the harness'
    /// coerce-audit / census / write-proof) rather than the validator wrapper.</summary>
    public static Corpus LoadCorpus() => LoadCorpus(CorpusPath);

    /// <summary>Loads raw corpus data from an explicit path and rejects missing or null content.</summary>
    public static Corpus LoadCorpus(string corpusJsonPath)
    {
        if (!File.Exists(corpusJsonPath))
            throw new FileNotFoundException(
                $"corpus.json not found at {Path.GetFullPath(corpusJsonPath)}. Generate it first: " +
                "dotnet run --project src/housecarl-generator");
        return JsonSerializer.Deserialize<Corpus>(File.ReadAllText(corpusJsonPath))
            ?? throw new InvalidOperationException("corpus.json deserialised to null.");
    }

    /// <summary>Validates one write request and returns null only when its complete shape is legal.</summary>
    /// <remarks>
    /// <paramref name="siblingEditorIds"/> permits self or earlier <c>@editorid</c> references during record creation.
    /// Other write lanes pass null because they have no same-call allocation context.
    /// </remarks>
    public string? Validate(WriteRequest req, IReadOnlyCollection<string>? siblingEditorIds = null)
    {
        // Root validation at the declared record schema; structures reuse the same path and leaf rules.
        var recType = Type(req.RecordType);
        if (recType is null)
            return $"Unknown record type '{req.RecordType}': absent from the Mutagen corpus ({TypeCount} types). " +
                   "If Mutagen models it, that's a real coverage gap to surface — never a value to guess.";
        return ValidateFromType(recType, req, siblingEditorIds);
    }

    /// <summary>Validates a write rooted at either a record or a newly composed structure.</summary>
    string? ValidateFromType(TypeSchema root, WriteRequest req, IReadOnlyCollection<string>? siblingEditorIds = null)
    {
        if (req.Path.Length == 0)
            return "Empty path: a write must target at least one field.";

        // Validate every intermediate path hop before examining the target leaf.
        var current = root;
        for (int i = 0; i < req.Path.Length - 1; i++)
        {
            if (!TrySeg(req.Path[i], out var segName, out var segKey, out var segErr)) return segErr;
            var field = FindField(current, segName, out _, out var polyErr);
            if (polyErr is not null) return polyErr;
            if (field is null) return FieldNotFound(current, segName);

            if (segKey is null)
            {
                // Plain hops descend substructures or a polymorphic base whose runtime arm is checked during apply.
                if (field.Cardinality == "substruct" && field.TypeRef is { } tr)
                {
                    var next = Type(tr);
                    if (next is null)
                        return $"Path hop '{segName}' on '{current.Name}' points to type '{tr}', absent from the corpus.";
                    current = next;
                }
                else if (field.Cardinality == "polymorphic" && field.TypeRef is { } ptr)
                {
                    var next = Type(ptr);
                    if (next is null)
                        return $"Path hop '{segName}' on '{current.Name}' points to polymorphic-base '{ptr}', absent from the corpus.";
                    current = next;
                }
                else
                    return $"Cannot descend through '{segName}' on '{current.Name}': it is a {field.Cardinality}, not a substruct. " +
                           $"(To step INTO a collection element, index it: '{segName}[<index/key>]'.)";
            }
            else
            {
                // GenderedItem<T> accepts [0] and [1] as aliases for its male and female arms.
                if (field.Cardinality == "substruct" && field.TypeRef is { } gtr
                    && gtr.StartsWith("GenderedItem<", StringComparison.Ordinal))
                {
                    if (segKey is not ("0" or "1"))
                        return $"Gendered field '{segName}' on '{current.Name}' is indexed by [0] (male) or [1] (female); got '{segKey}'. " +
                               $"(Its halves are also reachable by name: '{segName}.Male' / '{segName}.Female'.)";
                    var armRef = GenderedArmRef(gtr);
                    var armType = armRef is null ? null : Type(armRef);
                    if (armType is null)
                        return $"'{segName}[{segKey}]' on '{current.Name}' steps into a gendered scalar/value arm ('{armRef}'), which has " +
                               $"no sub-fields to navigate — set its halves by name ('{segName}.Male' / '{segName}.Female').";
                    current = armType;
                    continue;
                }

                // Bracketed hops may descend only into navigable list or dictionary structure elements.
                if (field.Cardinality is not ("list" or "dict"))
                    return $"'{segName}[{segKey}]' on '{current.Name}' indexes a {field.Cardinality}, which is not a collection.";
                if (field.ElementTypeRef is not { } er)
                    return $"'{segName}' on '{current.Name}' is a collection of scalar values, not navigable structs — " +
                           "edit its element at the leaf with the verb + Key, don't step into it.";
                var elem = Type(er);
                if (elem is null)
                    return $"'{segName}' element type '{er}' on '{current.Name}' is absent from the corpus.";
                if (elem.Kind == "record")
                    return $"'{segName}' on '{current.Name}' holds records ({er}); a record is resolved on its own, " +
                           "not reached by stepping into a parent (nested-group wave).";
                // Use the apply engine's non-negative index recognizer so preflight and mutation accept the same shape.
                if (field.Cardinality == "list" && !WriteEngine.IsValidListIndexValue(segKey))
                    return $"List '{segName}' on '{current.Name}' must be indexed by a non-negative integer; got '{segKey}'.";
                // Validate dictionary keys against the CLR key type used by the apply engine.
                if (field.Cardinality == "dict"
                    && CheckValue(field.KeyType, segKey, $"dict key for '{segName}'", DictKeyType(field)?.AssemblyQualifiedName) is { } ke)
                    return ke;
                current = elem;
            }
        }

        // A bracketed leaf is invalid because element edits express their selector through Key.
        if (!TrySeg(req.Path[^1], out var leafName, out var leafKey, out var leafErr)) return leafErr;
        if (leafKey is not null)
        {
            // Point bracketed gendered leaves to their named halves instead of suggesting collection verbs.
            if (FindField(current, leafName, out _, out _) is { Cardinality: "substruct", TypeRef: { } ltr }
                && ltr.StartsWith("GenderedItem<", StringComparison.Ordinal))
                return $"Gendered field '{leafName}' on '{current.Name}' renders as [0]/[1] but is not a list — set its " +
                       $"halves by name: '{leafName}.Male' (=[0]) / '{leafName}.Female' (=[1]).";
            return $"Path '{req.Path[^1]}' brackets a collection element at the LEAF; brackets navigate mid-path only. " +
                   "To edit a list/dict element, target the collection field and use the verb + Key (SetAtIndex/Set/Remove).";
        }
        var leaf = FindField(current, leafName, out var leafOwner, out var leafPolyErr);
        if (leafPolyErr is not null) return leafPolyErr;
        if (leaf is null) return FieldNotFound(current, leafName);

        // Batch composition has its own legality rules and is validated before singular value shapes.
        if (req.Structs is not null)
            return ComposesLegality(leaf, leafOwner, req, siblingEditorIds);

        // CopyFrom validates field transplantability here; the write pipeline resolves its named source plugin.
        if (string.Equals(req.Verb, "CopyFrom", StringComparison.Ordinal))
            return CopyFromLegality(leaf, leafOwner);

        // Validate the verb against the leaf cardinality before coercing values.
        if (VerbLegality(leaf, req) is { } verbErr) return verbErr;

        // Record identity is never editable content.
        if (leaf.IsIdentity)
            return $"'{leaf.Name}' on '{leafOwner.Name}' is record identity (FormKey/ModKey), not an editable content field.";

        // Explain read-only fields and discriminator alternatives before value validation.
        if (!leaf.Writable) return WritabilityRejection(leafOwner, leaf);

        // Validate keys, scalar values, collections, enums, FormLinks, and composed structures.
        return ValueLegality(leaf, req, siblingEditorIds);
    }

    /// <summary>Extract the arm type T from a gendered field's <c>GenderedItem&lt;T&gt;</c> TypeRef — e.g.
    /// "GenderedItem&lt;ArmorModel&gt;" → "ArmorModel". Returns the inner ref verbatim: a nested generic like
    /// "FormLinkNullable&lt;TextureSet&gt;" (a scalar/value arm) comes back whole and simply won't resolve as a
    /// corpus type, which the caller correctly surfaces as a non-navigable arm. Null if the string isn't the
    /// expected GenderedItem&lt;…&gt; shape.</summary>
    static string? GenderedArmRef(string typeRef)
    {
        const string head = "GenderedItem<";
        if (!typeRef.StartsWith(head, StringComparison.Ordinal) || !typeRef.EndsWith(">", StringComparison.Ordinal))
            return null;
        var inner = typeRef[head.Length..^1].Trim();
        return inner.Length == 0 ? null : inner;
    }

    /// <summary>Checks the exact CLR enum type for <see cref="FlagsAttribute"/>.</summary>
    static bool IsFlagsEnumLeaf(FieldSchema leaf)
    {
        if (leaf.Cardinality != "enum") return false;
        var aq = leaf.MutableTypeAssemblyQualified ?? leaf.GetterTypeAssemblyQualified;
        if (WriteEngine.ResolveType(aq) is not { } rt) return false;
        var u = Nullable.GetUnderlyingType(rt) ?? rt;
        return u.IsEnum && u.IsDefined(typeof(FlagsAttribute), false);
    }

    /// <summary>Validates whether a verb and optional key fit a field's cardinality.</summary>
    static string? VerbLegality(FieldSchema leaf, WriteRequest req)
    {
        var c = leaf.Cardinality;
        var hasKey = req.Key is not null;
        switch (req.Verb)
        {
            case "Set":
                if (c == "dict") return hasKey ? null : $"Set on dict field '{leaf.Name}' requires a key.";
                if (c == "list") return $"Set is not valid on list '{leaf.Name}' — use SetAtIndex (with an index) or ReplaceAll.";
                return hasKey ? $"Set on {c} field '{leaf.Name}' does not take a key." : null;
            case "Add":
                // Dictionary Add requires a key; list Add appends and takes no key.
                if (c == "dict") return hasKey ? null : $"Add on dict field '{leaf.Name}' requires a key.";
                if (c == "list") return null;
                // Add on a flags enum sets one bit and preserves all others.
                if (IsFlagsEnumLeaf(leaf))
                    return hasKey ? $"Add on flags field '{leaf.Name}' takes no key — the value IS the flag to set." : null;
                return $"Add is only valid on a list/dict or a [Flags] enum; '{leaf.Name}' is {c}.";
            case "Remove":
                // Dictionary Remove requires a key; list Remove may select by index or value.
                if (c == "dict") return hasKey ? null : $"Remove on dict field '{leaf.Name}' requires a key.";
                if (c == "list") return null;
                // Remove on a flags enum clears one bit and preserves all others.
                if (IsFlagsEnumLeaf(leaf))
                    return hasKey ? $"Remove on flags field '{leaf.Name}' takes no key — the value IS the flag to clear." : null;
                return leaf.Nullable ? null : $"Remove on non-nullable {c} field '{leaf.Name}' is not valid.";
            case "ReplaceAll":
                return c is "list" or "dict" ? null : $"ReplaceAll is only valid on list/dict; '{leaf.Name}' is {c}.";
            case "SetAtIndex":
                // Require the index here; ValueLegality validates its numeric shape.
                if (c != "list") return $"SetAtIndex is only valid on list; '{leaf.Name}' is {c}.";
                return hasKey ? null : $"SetAtIndex on list '{leaf.Name}' requires an index.";
            case "Merge":
                return c == "dict" ? null : $"Merge is only valid on dict; '{leaf.Name}' is {c}.";
            default:
                return $"Unknown verb '{req.Verb}'. Legal: Set, Add, Remove, ReplaceAll, SetAtIndex, Merge, CopyFrom.";
        }
    }

    /// <summary>Validates a complete batch of structures for list Add or ReplaceAll.</summary>
    string? ComposesLegality(FieldSchema leaf, TypeSchema owner, WriteRequest req,
        IReadOnlyCollection<string>? siblingEditorIds)
    {
        if (req.Verb is not ("Add" or "ReplaceAll"))
            return $"composes= appends/replaces a LIST of modeled elements — use it with Add (append each) or " +
                   $"ReplaceAll (clear, then append each), not {req.Verb}. (For one element use compose=; to overwrite " +
                   "one index use SetAtIndex with compose=.)";
        if (leaf.Cardinality != "list")
            return $"composes= builds a LIST of modeled elements, but '{leaf.Name}' on '{owner.Name}' is a " +
                   $"{leaf.Cardinality}. (A dict takes keyed entries, not a positional list; a substruct/scalar takes " +
                   "compose= / value=.)";
        if (!IsComposableElement(leaf))
            return $"'{leaf.Name}' on '{owner.Name}' holds " +
                   (leaf.FormLinkTarget is not null ? "formlink" : "coercible") +
                   $" values ({leaf.ElementTypeRef ?? leaf.ElementType}), not modeled structs — use values= " +
                   "(ReplaceAll) / value= (Add), not composes=.";
        if (req.Structs!.Count == 0)
            return req.Verb is "ReplaceAll"
                ? null   // ReplaceAll composes=[] = CLEAR the modeled list (the modeled twin of ReplaceAll values=[]); apply Clears + appends nothing
                : $"composes= for '{leaf.Name}' is empty — supply one or more element specs (only ReplaceAll composes=[] is meaningful, to clear the list).";
        for (int i = 0; i < req.Structs.Count; i++)
            if (StructElementLegality(leaf, req.Structs[i], siblingEditorIds) is { } elemErr)
                return $"composes[{i}]: {elemErr}";
        return null;
    }

    /// <summary>Validates that a field can be transplanted by CopyFrom.</summary>
    string? CopyFromLegality(FieldSchema leaf, TypeSchema owner)
    {
        if (leaf.IsIdentity)
            return $"'{leaf.Name}' on '{owner.Name}' is record identity (FormKey/ModKey), not a copyable content field.";
        if (!leaf.Writable) return WritabilityRejection(owner, leaf);
        if (leaf.Cardinality is "list" or "dict" && SchemaClassifier.ClassifyElement(leaf, _corpus) == ElementKind.Record)
            return $"'{leaf.Name}' on '{owner.Name}' holds owned child records ({leaf.ElementTypeRef}); CopyFrom copies a " +
                   "FIELD's value, not owned child records. To carry the WHOLE record from another plugin use " +
                   "housecarl_forward_record; a child record is authored on its own (housecarl_create_record with parent=).";
        if (leaf.Cardinality == "dict")
            return $"'{leaf.Name}' on '{owner.Name}' is a dict field; CopyFrom transplants scalar / formlink / list / " +
                   "sub-struct fields — a dict isn't transplanted yet. Set its entries individually, or forward the whole record.";
        return null;
    }

    /// <summary>Explains why a field is identity, discriminator-controlled, or otherwise read-only.</summary>
    static string WritabilityRejection(TypeSchema owner, FieldSchema leaf)
    {
        if (leaf.IsIdentity)
            return $"'{leaf.Name}' on '{owner.Name}' is record identity (FormKey/ModKey), not an editable content field.";
        if (leaf.Cardinality == "polymorphic" && leaf.Arms is { Count: > 0 } arms)
            return $"'{leaf.Name}' on '{owner.Name}' is fixed by which arm is selected. To change it, Set '{leaf.Name}' " +
                   $"to one of its arms: {string.Join(", ", arms)}.";
        if (owner.Kind is "arm" or "polymorphic-base")
            return $"'{leaf.Name}' is a discriminator on '{owner.Name}' — its value is fixed by which arm is selected. " +
                   "To change it, Set the parent polymorphic field to a different arm (P-DISC).";
        return $"'{leaf.Name}' on '{owner.Name}' is not writable — Mutagen exposes no setter (computed / discriminator / " +
               "no-mutable-interface). houseCARL faithfully reports Mutagen's writability; this is not a houseCARL gap.";
    }

    /// <summary>Validates selectors and values after path, verb, identity, and writability checks.</summary>
    string? ValueLegality(FieldSchema leaf, WriteRequest req, IReadOnlyCollection<string>? siblingEditorIds = null)
    {
        // Same-call @editorid references are valid only for FormLinks during record creation.
        if (WriteEngine.IsSameCallSiblingRef(req.Value, out var sibEdid))
        {
            if (siblingEditorIds is null)
                return $"'{req.Value}' for '{leaf.Name}': a '@editorid' reference names a record being created in the " +
                       "SAME housecarl_create_record / housecarl_bulk_create call — when editing an existing record " +
                       "there are no same-call creations to point at. Use the target's FormID (a record already " +
                       "written into a houseCARL patch is addressable by FormID with into= that patch).";
            // A singular token must target either one FormLink or one element of a FormLink list.
            var onFormLink = leaf.Cardinality == "formlink"
                          || (leaf.Cardinality == "list" && leaf.FormLinkTarget is not null);
            if (!onFormLink)
                return $"Same-call reference '{req.Value}' for '{leaf.Name}' is only valid on a FormLink field, but " +
                       $"'{leaf.Name}' is a {leaf.Cardinality}.";
            // Set a singular link or Add one list element.
            var verbFits = (leaf.Cardinality == "formlink" && req.Verb == "Set")
                        || (leaf.Cardinality == "list" && req.Verb == "Add");
            if (!verbFits)
                return $"Same-call reference '{req.Value}' for '{leaf.Name}' is only valid as a Set value on a singular " +
                       $"FormLink field or an Add value on a FormLink list (the verb was '{req.Verb}', '{leaf.Name}' " +
                       $"is a {leaf.Cardinality}).";
            // A sibling token is the entire link value and cannot be combined with a compose specification.
            if (req.Struct is not null)
                return $"Same-call reference '{req.Value}' for '{leaf.Name}' takes no compose spec — the '@editorid' " +
                       "value IS the whole FormLink target; remove struct=.";
            return siblingEditorIds.Contains(sibEdid) ? null
                : $"Same-call reference '{req.Value}' for '{leaf.Name}': no record with editorid '{sibEdid}' is created " +
                  "EARLIER in this call (a record may also reference ITSELF by its own editorid) — declare it before " +
                  "the record that references it (in spec order).";
        }
        // ReplaceAll may mix @editorid tokens and literal FormIDs in a FormLink list.
        if (req.Values is { } vals && vals.Any(v => WriteEngine.IsSameCallSiblingRef(v, out _)))
        {
            if (siblingEditorIds is null)
                return $"a '@editorid' reference for '{leaf.Name}' names a record being created in the SAME " +
                       "housecarl_create_record / housecarl_bulk_create call — when editing an existing record there " +
                       "are no same-call creations to point at. Use the target's FormID (a record already written " +
                       "into a houseCARL patch is addressable by FormID with into= that patch).";
            if (!(req.Verb == "ReplaceAll" && leaf.Cardinality == "list" && leaf.FormLinkTarget is not null))
                return $"a '@editorid' same-call reference for '{leaf.Name}' is only supported as an Add value or a " +
                       $"ReplaceAll value on a FormLink list (the verb was '{req.Verb}', '{leaf.Name}' is a {leaf.Cardinality}).";
            // Values already describe the complete list and cannot be combined with a compose specification.
            if (req.Struct is not null)
                return $"a '@editorid' same-call reference for '{leaf.Name}' takes no compose spec — the '@editorid' " +
                       "entries ARE the FormLink elements; remove struct=.";
            foreach (var v in vals)
            {
                if (WriteEngine.IsSameCallSiblingRef(v, out var vEd))
                {
                    if (!siblingEditorIds.Contains(vEd))
                        return $"Same-call reference '@{vEd}' for '{leaf.Name}': no record with editorid '{vEd}' is " +
                               "created EARLIER in this call (a record may also reference ITSELF by its own editorid) — " +
                               "declare it before the record that references it (in spec order).";
                }
                else if (!WriteEngine.IsValidFormLinkValue(v)) return FormLinkElementReject(v, leaf);
            }
            return null;
        }
        // The current corpus has no FormLink-valued dictionary, so sibling tokens cannot appear in Entries.
        if (req.Entries is { } ents && ents.Values.Any(v => WriteEngine.IsSameCallSiblingRef(v, out _)))
            return $"a '@editorid' same-call reference for '{leaf.Name}' is only supported on a FormLink list, not " +
                   "inside a dict value — no formlink-valued dict is modeled.";
        // Validate dictionary key and list index shapes with the same CLR types and recognizers used during apply.
        if (leaf.Cardinality == "dict")
        {
            var keyAq = DictKeyType(leaf)?.AssemblyQualifiedName;
            string? KeyShape(string? k) => CheckValue(leaf.KeyType, k, $"dict key for '{leaf.Name}'", keyAq);
            if (req.Verb is "Set" or "Add" or "Remove" && req.Key is { } dKey && KeyShape(dKey) is { } dKeyErr)
                return dKeyErr;
            if (req.Verb is "Merge" or "ReplaceAll" && req.Entries is { } keyEnts)
                foreach (var k in keyEnts.Keys)
                    if (KeyShape(k) is { } entKeyErr) return entKeyErr;
        }
        if (leaf.Cardinality == "list" && req.Verb is "SetAtIndex" or "Remove" && req.Key is { } lIdx
            && !WriteEngine.IsValidListIndexValue(lIdx))
            return $"Illegal list index '{lIdx}' for '{leaf.Name}': expected a non-negative integer. " +
                   "(Whether the index is in range is checked at apply, against the live list.)";
        if (req.Verb is "Set" && leaf.Cardinality == "dict")
        {
            // Composable dictionary values use the same structure validation as list elements.
            if (IsComposableElement(leaf)) return StructElementLegality(leaf, req.Struct, siblingEditorIds);
            if (req.Value is null) return $"Set on dict '{leaf.Name}' requires a value.";
            return CheckValue(leaf.ElementType, req.Value, $"dict value for '{leaf.Name}'", leaf.ElementTypeAssemblyQualified);
        }
        if (req.Verb is "Set" && leaf.Cardinality == "polymorphic")
            return ArmLegality(leaf, req.Struct, siblingEditorIds);
        // A modeled composable substructure may be Set from a StructSpec; coercible value types stay on the value path.
        if (req.Verb is "Set" && SchemaClassifier.IsComposableSubstructLeaf(leaf, _corpus))
            return StructLeafLegality(leaf, req.Struct, siblingEditorIds);
        if (req.Verb is "Set")
        {
            // A compose specification on this branch targets a plain value and must be rejected explicitly.
            if (req.Value is null)
                return req.Struct is not null
                    ? $"'{leaf.Name}' is set from a plain value (value=…), not a compose spec."
                    : $"Set on '{leaf.Name}' requires a value.";
            // FormLink-or-index condition targets use their shared classifier; ordinary FormLinks use FormKey syntax.
            if (leaf.Cardinality is "formlink" or "substruct")
            {
                var faq = leaf.MutableTypeAssemblyQualified ?? leaf.GetterTypeAssemblyQualified;
                if (WriteEngine.ResolveType(faq) is { } frt && WriteEngine.IsFormLinkOrIndex(frt))
                    return WriteEngine.TryClassifyFloiValue(req.Value) ? null
                        : $"Illegal condition target '{req.Value}' for '{leaf.Name}': expected {FloiTargetForms}.";
                // A normal FormLink accepts a parseable FormKey or one of the supported null-clear forms.
                if (leaf.Cardinality == "formlink")
                    return WriteEngine.IsValidFormLinkValue(req.Value) ? null
                        : $"Illegal FormLink target '{req.Value}' for '{leaf.Name}': expected a FormID " +
                          "(XXXXXX:Plugin.esp) or a null-clear ('0', '00000000', 'Null', '000000:Null').";
                return CoercibilityReject(leaf);
            }
            return CheckValue(leaf.Type, req.Value, $"value for '{leaf.Name}'",
                leaf.MutableTypeAssemblyQualified ?? leaf.GetterTypeAssemblyQualified);
        }
        // Validate flags Add and Remove values against the exact enum type before mutation.
        if (req.Verb is "Add" or "Remove" && IsFlagsEnumLeaf(leaf))
        {
            if (req.Value is null)
            {
                // Valueless Remove clears a nullable field; non-nullable fields must name one flag or Set zero.
                if (req.Verb == "Add")
                    return $"Add on flags field '{leaf.Name}' requires a flag value (the bit to set).";
                return leaf.Nullable ? null
                    : $"Remove on flags field '{leaf.Name}' needs the flag to clear (value=<flag>) — a non-nullable flags " +
                      "field can't be whole-cleared; to turn ALL bits off, Set it to '0'.";
            }
            return CheckValue(leaf.Type, req.Value, $"flag value for '{leaf.Name}'",
                leaf.MutableTypeAssemblyQualified ?? leaf.GetterTypeAssemblyQualified);
        }
        // Add and SetAtIndex require one plain value when the element is value-coercible.
        if (leaf.Cardinality is "list" or "dict" && req.Verb is "Add" or "SetAtIndex"
            && req.Value is null && IsValueCoercibleElement(leaf))
            return $"{req.Verb} on '{leaf.Name}' requires an element value.";
        // Validate every supplied FormLink element with the same recognizer used by mutation.
        if (leaf.Cardinality is "list" or "dict" && leaf.FormLinkTarget is not null)
        {
            if (req.Value is { } ev && !WriteEngine.IsValidFormLinkValue(ev)) return FormLinkElementReject(ev, leaf);
            foreach (var v in req.Values ?? Array.Empty<string>())
                if (!WriteEngine.IsValidFormLinkValue(v)) return FormLinkElementReject(v, leaf);
            foreach (var kv in req.Entries ?? new())
                if (!WriteEngine.IsValidFormLinkValue(kv.Value)) return FormLinkElementReject(kv.Value, leaf);
        }
        // Validate non-FormLink element values only in the request slots consumed by the selected verb.
        if (leaf.Cardinality is "list" or "dict" && IsValueCoercibleElement(leaf) && leaf.FormLinkTarget is null)
        {
            string? ElemShape(string? v) =>
                CheckValue(leaf.ElementType, v, $"element value for '{leaf.Name}'", leaf.ElementTypeAssemblyQualified);
            if (req.Value is { } ev
                && (req.Verb is "Add" or "SetAtIndex" || (req.Verb is "Remove" && req.Key is null))
                && ElemShape(ev) is { } evErr)
                return evErr;
            // Lists consume Values; dictionaries consume Entries values.
            if (leaf.Cardinality == "list" && req.Verb is "ReplaceAll")
                foreach (var v in req.Values ?? Array.Empty<string>())
                    if (ElemShape(v) is { } valsErr) return valsErr;
            if (leaf.Cardinality == "dict" && req.Verb is "Merge" or "ReplaceAll")
                foreach (var kv in req.Entries ?? new())
                    if (ElemShape(kv.Value) is { } entErr) return entErr;
        }
        // Owned child records must be created through the record axis rather than collection value verbs.
        if (leaf.Cardinality is "list" or "dict" && req.Verb is "Add" or "SetAtIndex" or "ReplaceAll"
            && SchemaClassifier.ClassifyElement(leaf, _corpus) == ElementKind.Record)
            return $"'{leaf.Name}' holds owned child records ({leaf.ElementTypeRef}); a child record is created on its " +
                   "own (the record axis), not added into a parent's collection by a write verb. Use housecarl_create_record " +
                   "/ housecarl_bulk_create with parent= the parent's FormID (and collection= when the parent holds more " +
                   "than one fitting list) — surfaced here, never accepted and thrown at apply.";
        // Modeled structure and arm elements are built from StructSpec rather than a plain value.
        if (leaf.Cardinality is "list" or "dict" && IsComposableElement(leaf))
        {
            // Add and SetAtIndex share one recursive structure validator.
            if (req.Verb is "Add" or "SetAtIndex")
                return StructElementLegality(leaf, req.Struct, siblingEditorIds);
            // Singular input shapes cannot express ReplaceAll or Merge of modeled elements.
            if (req.Verb is "ReplaceAll" or "Merge")
                return $"'{leaf.Name}' holds modeled elements ({leaf.ElementTypeRef}); compose them one at a time " +
                       $"(Add to append, SetAtIndex to overwrite an index; Set for a dict key) — {req.Verb} of modeled " +
                       $"elements is a later surface.";
        }
        // Non-coercible modeled or record elements can be removed only by list index.
        if (req.Verb == "Remove" && leaf.Cardinality == "list" && req.Key is null
            && leaf.FormLinkTarget is null && !IsValueCoercibleElement(leaf))
            return $"'{leaf.Name}' holds modeled/record elements ({leaf.ElementTypeRef ?? leaf.ElementType}); remove one " +
                   "BY INDEX (Remove with a Key = its position), not by value — a modeled or record element has no " +
                   "plain-value form to match. (Value-based removal of such an element is a later surface.)";
        return null;
    }

    /// <summary>Checks whether a collection element must be built from a structure specification.</summary>
    bool IsComposableElement(FieldSchema leaf)
        => SchemaClassifier.ClassifyElement(leaf, _corpus) is ElementKind.Struct or ElementKind.Arm;

    /// <summary>Checks whether a collection element is written by coercing one plain value.</summary>
    bool IsValueCoercibleElement(FieldSchema leaf)
        => SchemaClassifier.ClassifyElement(leaf, _corpus) is ElementKind.ScalarCoercible or ElementKind.WholeCoercible;

    /// <summary>Validates one composed collection element against its concrete schema or legal polymorphic arm.</summary>
    string? StructElementLegality(FieldSchema leaf, StructSpec? spec, IReadOnlyCollection<string>? siblingEditorIds = null)
    {
        if (spec is null)
            return $"'{leaf.Name}' takes a build-from-parts element (a modeled {leaf.ElementTypeRef}); supply a compose spec, not a plain value.";
        var er = leaf.ElementTypeRef!;
        var elemSchema = Type(er);
        if (elemSchema is null) return $"Element type '{er}' for '{leaf.Name}' absent from corpus.";

        // A polymorphic base must be composed through one of its concrete arms, never by the base name itself.
        bool isPolyBase = elemSchema is { Kind: "polymorphic-base" };
        var legalArms = (elemSchema.Arms ?? new()).Where(a => a != er).ToList();

        TypeSchema specSchema;
        if (spec.Type == er)
        {
            if (isPolyBase)
                return $"'{spec.Type}' is the polymorphic base of '{leaf.Name}' — the base itself cannot be composed; " +
                       $"choose a concrete arm. Legal element types: {string.Join(", ", legalArms)}.";
            specSchema = elemSchema;
        }
        else if (isPolyBase && legalArms.Contains(spec.Type))
            specSchema = Type(spec.Type)
                ?? throw new InvalidOperationException($"Arm '{spec.Type}' of '{er}' is listed but absent from the corpus — regenerate corpus.json.");
        else
        {
            var legal = isPolyBase && legalArms.Count > 0
                ? $" Legal element types: {string.Join(", ", legalArms)}." : "";
            return $"Element spec type '{spec.Type}' does not match '{leaf.Name}' element type '{er}'.{legal}";
        }
        return StructSpecContents(spec, specSchema, siblingEditorIds);
    }

    /// <summary>Validates a complete structure assigned to a composable substructure leaf.</summary>
    string? StructLeafLegality(FieldSchema leaf, StructSpec? spec, IReadOnlyCollection<string>? siblingEditorIds = null)
    {
        var tr = leaf.TypeRef!;
        var schema = Type(tr);
        if (schema is null) return $"Struct type '{tr}' for '{leaf.Name}' absent from corpus.";
        if (spec is null)
            return $"'{leaf.Name}' is a {tr} struct — set it by composing from parts (a compose spec, e.g. " +
                   $"{{\"type\":\"{tr}\", \"fields\":{{…}}}}), or navigate into it and Set a sub-field; a plain value can't express a struct.";
        if (spec.Type != tr)
            return $"Compose type '{spec.Type}' does not match '{leaf.Name}' struct type '{tr}'.";
        return StructSpecContents(spec, schema, siblingEditorIds);
    }

    /// <summary>Validates constructor arguments, flat fields, and recursive writes within a structure specification.</summary>
    string? StructSpecContents(StructSpec spec, TypeSchema structSchema, IReadOnlyCollection<string>? siblingEditorIds = null)
    {
        // Validate constructor arity and values with the apply engine's constructor recognizer.
        if (spec.CtorArgs is { } ctorArgs && WriteEngine.TryRecognizeCtorArgs(spec.Type, ctorArgs) is { } ctorErr)
            return ctorErr;
        foreach (var f in spec.Fields ?? new())
        {
            var af = structSchema.Fields.FirstOrDefault(x => x.Name == f.Key);
            if (af is null) return FieldNotFound(structSchema, f.Key);
            // Structure fields use the same creation-only FormLink sibling-reference rules as top-level values.
            if (WriteEngine.IsSameCallSiblingRef(f.Value, out var fEd))
            {
                if (siblingEditorIds is null)
                    return $"a '@editorid' reference for '{f.Key}' on '{spec.Type}' names a record being created in the " +
                           "SAME housecarl_create_record / housecarl_bulk_create call — when editing an existing record " +
                           "there are no same-call creations to point at. Use the target's FormID.";
                if (af.Cardinality != "formlink")
                    return $"Same-call reference '{f.Value}' for '{f.Key}' on '{spec.Type}' is only valid on a FormLink " +
                           $"field, but '{f.Key}' is a {af.Cardinality}.";
                if (!siblingEditorIds.Contains(fEd))
                    return $"Same-call reference '{f.Value}' for '{f.Key}' on '{spec.Type}': no record with editorid " +
                           $"'{fEd}' is created EARLIER in this call (a record may also reference ITSELF by its own " +
                           "editorid) — declare it before the record that references it (in spec order).";
                continue;
            }
            if (CheckValue(af.Type, f.Value, $"'{f.Key}' on '{spec.Type}'",
                    af.MutableTypeAssemblyQualified ?? af.GetterTypeAssemblyQualified) is { } e) return e;
        }
        foreach (var s in spec.Sets ?? new())
            // Recursive writes inherit the same sibling-reference context.
            if (ValidateFromType(structSchema, s, siblingEditorIds) is { } e) return e;
        return null;
    }

    /// <summary>Rejects a whole-value Set when the engine cannot coerce the leaf type.</summary>
    static string? CoercibilityReject(FieldSchema leaf)
    {
        var aq = leaf.MutableTypeAssemblyQualified ?? leaf.GetterTypeAssemblyQualified;
        if (WriteEngine.ResolveType(aq) is not { } rt) return null;
        if (WriteEngine.CanCoerce(rt)) return null;
        if (leaf.Cardinality == "substruct")
            return $"'{leaf.Name}' is a {leaf.TypeRef ?? leaf.Type} substruct — a direct Set isn't supported; " +
                   "navigate into it and Set a sub-field.";
        return $"'{leaf.Name}' ({leaf.Type}) needs a typed-value spec, not a plain value (e.g. a condition " +
               "FormLinkOrIndex target). Known deferred surface — surfaced, never silently accepted.";
    }

    /// <summary>Formats a collection-specific malformed FormLink rejection.</summary>
    static string FormLinkElementReject(string value, FieldSchema leaf) =>
        $"Illegal FormLink element '{value}' for '{leaf.Name}': expected a FormID (XXXXXX:Plugin.esp) " +
        "or a null-clear ('0', '00000000', 'Null', '000000:Null').";

    /// <summary>Validates a polymorphic Set against a concrete legal arm and that arm's schema.</summary>
    string? ArmLegality(FieldSchema leaf, StructSpec? arm, IReadOnlyCollection<string>? siblingEditorIds = null)
    {
        if (arm is null) return $"Set on polymorphic field '{leaf.Name}' requires an arm (which arm + its data).";
        // Exclude the base itself from legal arms; concrete bases may instead be edited through dotted subfields.
        var baseName = leaf.TypeRef;
        var legal = (leaf.Arms ?? (baseName is { } tr ? Type(tr)?.Arms : null) ?? new()).Where(a => a != baseName).ToList();
        if (baseName is not null && arm.Type == baseName)
        {
            var dottedLane = leaf.MutableTypeAssemblyQualified is { } aq
                             && WriteEngine.ResolveType(aq) is { IsAbstract: false, IsInterface: false }
                ? $" If no listed arm fits this record (the field's live type is the base itself), skip the compose " +
                  $"and Set the base's subfields by dotted path instead ('{leaf.Name}.<subfield>' — the field " +
                  $"auto-instantiates on first Set)."
                : "";
            return $"'{arm.Type}' is the polymorphic base of '{leaf.Name}' — the base itself cannot be composed; " +
                   $"choose a concrete arm. Legal arms: {string.Join(", ", legal)}.{dottedLane}";
        }
        if (!legal.Contains(arm.Type))
            return $"Illegal arm '{arm.Type}' for '{leaf.Name}'. Legal arms: {string.Join(", ", legal)}.";
        var armSchema = Type(arm.Type);
        if (armSchema is null) return $"Arm '{arm.Type}' absent from corpus.";
        return StructSpecContents(arm, armSchema, siblingEditorIds);
    }

    /// <summary>Resolves a dictionary key CLR type from the field's own generic type metadata.</summary>
    static System.Type? DictKeyType(FieldSchema leaf)
    {
        var aq = leaf.MutableTypeAssemblyQualified ?? leaf.GetterTypeAssemblyQualified;
        if (aq is null || WriteEngine.ResolveType(aq) is not { IsGenericType: true } dt) return null;
        var args = dt.GetGenericArguments();
        return args.Length == 2 ? args[0] : null;
    }

    /// <summary>Validates one textual value against its exact CLR type, with a catalog fallback when unresolved.</summary>
    string? CheckValue(string? typeName, string? value, string what, string? aq = null)
    {
        if (value is null) return $"Missing {what}.";

        if (aq is not null && WriteEngine.ResolveType(aq) is { } rt)
        {
            var u = Nullable.GetUnderlyingType(rt) ?? rt;
            if (u.IsEnum)
            {
                if (Enum.GetNames(u).Any(n => string.Equals(n, value, StringComparison.OrdinalIgnoreCase))) return null;
                if (WriteEngine.TryCoerce(value, rt, out _)) return null;
                return $"Illegal {what}: '{value}' is not a legal {u.Name} value. Legal: {string.Join(", ", Enum.GetNames(u))}.";
            }
            // Condition FormLink-or-index targets use the parent-aware classifier rather than ordinary coercion.
            if (WriteEngine.IsFormLinkOrIndex(rt))
                return WriteEngine.TryClassifyFloiValue(value) ? null
                    : $"Illegal {what}: '{value}' is not a legal condition target — expected {FloiTargetForms}.";
            if (!WriteEngine.TryCoerce(value, rt, out _))
                return $"Illegal {what}: '{value}' does not coerce to {typeName ?? u.Name}.";
            return null;
        }

        // Fall back to catalog enum values only when exact CLR metadata cannot be resolved.
        if (typeName is null) return null;
        if (Type(typeName) is { Kind: "enum", EnumValues: { } legal }
            && !legal.Any(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase)))
            return $"Illegal {what}: '{value}' is not a legal {typeName} value. Legal: {string.Join(", ", legal)}.";
        return null;
    }

    /// <summary>Parses a path segment with the engine parser and converts syntax exceptions into validation text.</summary>
    static bool TrySeg(string segment, out string name, out string? key, out string? error)
    {
        try { (name, key) = WriteEngine.ParseSegment(segment); error = null; return true; }
        catch (Exception ex) { name = segment; key = null; error = ex.Message; return false; }
    }

    /// <summary>Formats an unknown-field rejection with a bounded sample of available fields.</summary>
    string FieldNotFound(TypeSchema owner, string name)
    {
        var sample = owner.Fields.Select(f => f.Name).Take(12).ToList();
        var more = owner.Fields.Count > sample.Count ? $", … (+{owner.Fields.Count - sample.Count} more)" : "";
        var arms = owner is { Kind: "polymorphic-base", Arms.Count: > 0 }
            ? $" Also searched its arms ({string.Join(", ", owner.Arms!.Where(a => a != owner.Name))})."
            : "";
        return $"No field '{name}' on '{owner.Name}'. Fields: {string.Join(", ", sample)}{more}.{arms}";
    }

    /// <summary>Finds a field on a type or shape-compatible polymorphic arms.</summary>
    /// <remarks>Arm fields must agree in writable shape because the runtime arm is unknown during validation.</remarks>
    FieldSchema? FindField(TypeSchema owner, string name, out TypeSchema effectiveOwner, out string? error)
    {
        effectiveOwner = owner; error = null;
        if (owner.Fields.FirstOrDefault(f => f.Name == name) is { } direct) return direct;
        if (owner is not { Kind: "polymorphic-base", Arms.Count: > 0 }) return null;

        var hits = new List<(TypeSchema arm, FieldSchema field)>();
        foreach (var armName in owner.Arms!)
        {
            if (armName == owner.Name) continue;                       // the base lists itself as an arm; already checked
            if (Type(armName) is not { } arm)
            {
                // A listed arm absent from the same generated catalog means the corpus is incomplete or stale.
                error = $"Arm '{armName}' of polymorphic-base '{owner.Name}' is listed but ABSENT from the corpus — " +
                        "corpus.json is stale or incompletely generated; regenerate it (dotnet run --project src/housecarl-generator).";
                return null;
            }
            if (arm.Fields.FirstOrDefault(f => f.Name == name) is { } af) hits.Add((arm, af));
        }
        if (hits.Count == 0) return null;

        var (firstArm, firstField) = hits[0];
        foreach (var (arm, f) in hits.Skip(1))
            if (!SameShape(firstField, f))
            {
                error = $"Field '{name}' exists on several arms of '{owner.Name}' with CONFLICTING shapes " +
                        $"('{firstArm.Name}': {firstField.Cardinality} {firstField.Type} vs '{arm.Name}': {f.Cardinality} {f.Type}) — " +
                        "the validator cannot pick one statically. Read the element first to learn its concrete arm, " +
                        "then target a field whose shape is unambiguous.";
                return null;
            }
        effectiveOwner = firstArm;
        return firstField;
    }

    /// <summary>Checks whether two arm fields have equivalent navigation and write-validation shapes.</summary>
    internal static bool SameShape(FieldSchema a, FieldSchema b) =>
        a.Cardinality == b.Cardinality && a.Type == b.Type && a.TypeRef == b.TypeRef
        && a.ElementType == b.ElementType && a.ElementTypeRef == b.ElementTypeRef
        && a.Writable == b.Writable && a.IsIdentity == b.IsIdentity
        && SameWriteLegalType(a.GetterTypeAssemblyQualified, b.GetterTypeAssemblyQualified)
        && SameWriteLegalType(a.MutableTypeAssemblyQualified, b.MutableTypeAssemblyQualified)
        && SameWriteLegalType(a.ElementTypeAssemblyQualified, b.ElementTypeAssemblyQualified);

    /// <summary>Compares CLR types after unwrapping nullable value types, with raw-name fallback if unresolved.</summary>
    static bool SameWriteLegalType(string? a, string? b)
    {
        if (a == b) return true;
        if (a is null || b is null) return false;
        var ta = WriteEngine.ResolveType(a);
        var tb = WriteEngine.ResolveType(b);
        if (ta is null || tb is null) return a == b;
        return (Nullable.GetUnderlyingType(ta) ?? ta) == (Nullable.GetUnderlyingType(tb) ?? tb);
    }
}
