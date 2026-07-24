using NiflySharp;
using NiflySharp.Blocks;

namespace HousecarlCore;

/// <summary>Inspects and safely edits Skyrim NIF bytes without filesystem or mod-manager access.</summary>
/// <remarks>
/// Inspection reports header identity, block census, shape properties, node hierarchy, and header strings. Writes
/// are limited to <see cref="NifSetOpKind"/> and return bytes only after block-content and semantic read-back gates
/// succeed. Unknown blocks remain preserved and visible. Parse or verification failures return named outcomes rather
/// than partial models. The service layer separately chooses the winning Amethyst asset and commits output bytes.
/// </remarks>
public static class NifService
{
    /// <summary>Skyrim SE NIF user-version marker.</summary>
    const uint SkyrimSeUserVersion = 12;

    /// <summary>Skyrim SE NIF stream-version marker.</summary>
    const uint SkyrimSeStreamVersion = 100;

    /// <summary>
    /// Gets effective Skyrim SE NiAVObject flag defaults by concrete or ancestor block type.
    /// </summary>
    /// <remarks>
    /// Values come from the same nif.xml <c>onlyT</c> defaults used to generate NiflySharp. The source does not name
    /// individual bits, so consumers compare raw values with these defaults instead of inventing bit labels.
    /// </remarks>
    internal static readonly IReadOnlyDictionary<string, uint> AvFlagsSseDefaults =
        new Dictionary<string, uint>(StringComparer.Ordinal)
    {
        ["NiNode"] = 0xE, ["NiLight"] = 0xE, ["BSMultiBoundNode"] = 0xE,
        ["BSTriShape"] = 0x8000E, ["BSSubIndexTriShape"] = 0xE, ["BSMeshLODTriShape"] = 0x100E,
        ["BSFadeNode"] = 0x8000E, ["NiParticleSystem"] = 0x8000E, ["BSMasterParticleSystem"] = 0x8000E,
        ["BSStripParticleSystem"] = 0x8000E, ["NiTriShape"] = 0x8000E, ["NiTriStrips"] = 0x8000E,
        ["BSSegmentedTriShape"] = 0xE, ["BSLeafAnimNode"] = 0x808000E, ["BSTreeNode"] = 0x8080E,
        ["BSDebrisNode"] = 0x8000F, ["BSBlastNode"] = 0x8000F, ["BSDamageStage"] = 0x8000F,
        ["BSOrderedNode"] = 0x8200E, ["BSLODTriShape"] = 0x800000E,
    };

    /// <summary>Finds the documented SSE flag default along a NIF block's inheritance chain.</summary>
    /// <param name="t">Runtime NIF block type, or null.</param>
    /// <returns>Default value and defining ancestor name, or two null values when undocumented.</returns>
    static (uint? Value, string? FromType) ResolveAvDefault(Type? t)
    {
        for (var cur = t; cur is not null && cur != typeof(object); cur = cur.BaseType)
            if (AvFlagsSseDefaults.TryGetValue(cur.Name, out var v)) return (v, cur.Name);
        return (null, null);
    }

    /// <summary>Inspects a mesh from raw bytes.</summary>
    /// <param name="bytes">Complete NIF file contents.</param>
    /// <returns>A complete model or a named parse/read error; never a partial model.</returns>
    public static NifInspectOutcome Inspect(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0)
            return new NifInspectOutcome(null, "the mesh is empty (0 bytes) — nothing to inspect.");

        var nif = new NifFile();
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            int rc = nif.Load(ms);
            if (rc != 0)
                return new NifInspectOutcome(null,
                    $"NiflySharp could not parse this mesh (Load returned {rc}) — it may be truncated, not a NIF, " +
                    "or a format the library rejects. If NifSkope opens it, the file is valid but nonstandard and " +
                    "houseCARL will not guess at its structure.");
        }
        catch (Exception ex)
        {
            return new NifInspectOutcome(null, DescribeLoadException(ex));
        }

        try
        {
            return new NifInspectOutcome(Build(nif), null);
        }
        catch (Exception ex)
        {
            var error =
                $"the mesh parsed but reading its structure failed — {ex.GetType().Name}: {ex.Message}";
            return new NifInspectOutcome(null, error);
        }
    }

    /// <summary>Converts a NiflySharp load exception into actionable user text.</summary>
    /// <param name="ex">Parser exception.</param>
    /// <returns>A strict-boolean-specific remedy or a general named parse error.</returns>
    static string DescribeLoadException(Exception ex)
    {
        var m = ex.Message ?? "";
        if (m.Contains("boolean", StringComparison.OrdinalIgnoreCase))
            return "NiflySharp refused this mesh: a boolean field holds a non-0/1 byte, which the library " +
                   "rejects strictly. NifSkope may open this exporter-specific file, but houseCARL will not " +
                   $"hand-patch around the strict read. ({ex.GetType().Name}: {m})";
        return $"NiflySharp threw while parsing this mesh — {ex.GetType().Name}: {m}";
    }

    /// <summary>Builds the complete inspection model from a successfully loaded NIF.</summary>
    /// <param name="nif">Loaded NIF object graph.</param>
    /// <returns>Immutable inspection data.</returns>
    static NifInspect Build(NifFile nif)
    {
        var header = nif.Header;
        var version = header.Version;
        uint user = version.UserVersion;
        uint stream = version.StreamVersion;
        bool isSe = user == SkyrimSeUserVersion && stream == SkyrimSeStreamVersion;

        int blockCount = header.BlockCount;
        var blocks = nif.Blocks;

        // Block census by ON-DISK type name (what xEdit/NifSkope show) — this also names unknown blocks faithfully,
        // where GetType().Name would flatten every one of them to "NiUnknown".
        var typeById = new string[blockCount];
        for (int i = 0; i < blockCount; i++) typeById[i] = header.GetBlockTypeNameById(i) ?? "?";
        var blockTypes = typeById
            .GroupBy(t => t)
            .Select(g => new NifBlockTypeCount(g.Key, g.Count()))
            .OrderByDescending(c => c.Count).ThenBy(c => c.Type, StringComparer.Ordinal)
            .ToList();

        // Unknown blocks reported by their real on-disk type (preserved-but-opaque — PRFAQ N6). GetType() on a
        // NiUnknown is just "NiUnknown"; the informative name lives in the header's block-type table.
        var unknownTypes = new List<string>();
        for (int i = 0; i < blocks.Count && i < blockCount; i++)
            if (blocks[i] is NiUnknown) unknownTypes.Add(typeById[i]);
        var unknownDistinct = unknownTypes
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        var shapes = new List<NifShape>();
        foreach (var shape in nif.GetShapes()) shapes.Add(BuildShape(nif, shape, isSe));

        return new NifInspect(
            version.VersionString ?? "", user, stream, isSe,
            blockCount, blockTypes,
            nif.HasUnknownBlocks, unknownDistinct,
            shapes, BuildNodeTree(nif, isSe), ReadHeaderStrings(header));
    }

    /// <summary>Builds inspectable values for one shape.</summary>
    /// <param name="nif">Owning NIF used to resolve referenced blocks.</param>
    /// <param name="shape">Shape to inspect.</param>
    /// <param name="isSe">Whether SSE defaults are applicable.</param>
    /// <returns>Shape identity, render properties, partitions, textures, and bones.</returns>
    static NifShape BuildShape(NifFile nif, INiShape shape, bool isSe)
    {
        string name = shape.Name?.String ?? "";
        uint flags = 0; float scale = 1f;
        if (shape is NiAVObject av) { flags = av.Flags_ui; scale = av.Scale; }
        var (defVal, defType) = isSe ? ResolveAvDefault(shape.GetType()) : (null, null);

        var partitions = new List<NifPartition>();
        if (shape.SkinInstanceRef is not null && nif.GetBlock(shape.SkinInstanceRef) is BSDismemberSkinInstance dis)
            foreach (var p in dis.Partitions)
                partitions.Add(new NifPartition((int)p.BodyPart, p.BodyPart.ToString(), (int)p.PartFlag));

        NifAlpha? alpha = null;
        if (shape.HasAlphaProperty && nif.GetBlock<NiAlphaProperty>(shape.AlphaPropertyRef) is { } ap)
        {
            var f = ap.Flags;
            alpha = new NifAlpha(
                f.Value,
                f.AlphaBlend,
                f.SourceBlendMode.ToString(),
                f.DestinationBlendMode.ToString(),
                f.AlphaTest,
                f.TestFunc.ToString(),
                ap.Threshold);
        }

        var textures = new List<NifTexture>();
        var shader = nif.GetShader(shape);
        if (shader?.TextureSetRef is not null && nif.GetBlock(shader.TextureSetRef) is BSShaderTextureSet ts)
            for (int i = 0; i < ts.Textures.Count; i++)
            {
                var c = ts.Textures[i]?.Content;
                if (!string.IsNullOrEmpty(c)) textures.Add(new NifTexture(i, c));
            }

        var bones = nif.GetShapeBoneNames(shape) ?? new List<string>();
        return new NifShape(
            name,
            flags,
            scale,
            shape.GetType().Name,
            defVal,
            defType,
            partitions,
            alpha,
            textures,
            bones);
    }

    /// <summary>Builds a cycle-safe pre-order NiNode tree.</summary>
    /// <param name="nif">Loaded NIF.</param>
    /// <param name="isSe">Whether SSE defaults are applicable.</param>
    /// <returns>Depth-annotated nodes; shapes are reported separately.</returns>
    static List<NifNode> BuildNodeTree(NifFile nif, bool isSe)
    {
        var nodes = new List<NifNode>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

        // Adds each node once and follows only node children; shapes have their own report.
        void Walk(NiNode node, int depth)
        {
            if (node is null || !seen.Add(node)) return;
            uint flags = node is NiAVObject av ? av.Flags_ui : 0u;
            var (defVal, defType) = isSe ? ResolveAvDefault(node.GetType()) : (null, null);
            nodes.Add(new NifNode(depth, node.Name?.String ?? "", flags, node.GetType().Name, defVal, defType));
            foreach (var cref in node.Children.References)
                if (nif.GetBlock(cref) is NiNode child) Walk(child, depth + 1);
        }

        foreach (var root in nif.GetRootNodes()) Walk(root, 0);
        return nodes;
    }

    /// <summary>Reads the contiguous non-empty prefix of the NIF header string table.</summary>
    /// <param name="header">Loaded NIF header.</param>
    /// <returns>At most 8,192 shape, node, bone, material, and path strings.</returns>
    static List<string> ReadHeaderStrings(NiHeader header)
    {
        var strings = new List<string>();
        const int cap = 8192;
        for (int i = 0; i < cap; i++)
        {
            var s = header.GetString(i);
            if (string.IsNullOrEmpty(s)) break;
            strings.Add(s);
        }
        return strings;
    }

    /// <summary>Applies verified whitelist edits to raw Skyrim SE mesh bytes.</summary>
    /// <param name="bytes">Complete original NIF bytes.</param>
    /// <param name="ops">Ordered whitelist operations.</param>
    /// <returns>Verified edited bytes and audit report, or a named refusal.</returns>
    /// <remarks>
    /// Non-SSE streams, unresolved or ambiguous targets, and inapplicable operations are refused. Successful output
    /// must pass both a per-block collateral-change gate and semantic read-back. Unknown blocks are permitted only
    /// when their census remains unchanged.
    /// </remarks>
    public static NifSetOutcome Set(byte[] bytes, IReadOnlyList<NifSetOp> ops)
    {
        if (bytes is null || bytes.Length == 0)
            return NifSetOutcome.Fail("the mesh is empty (0 bytes) — nothing to edit.");
        if (ops is null || ops.Count == 0)
            return NifSetOutcome.Fail("no write op was given.");

        // ---- parse (same fail-loud posture as Inspect) ----
        var nif = new NifFile();
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            if (nif.Load(ms) != 0)
                return NifSetOutcome.Fail(
                    "NiflySharp could not parse this mesh — it may be truncated, not a NIF, or a format the " +
                    "library rejects. Nothing was written.");
        }
        catch (Exception ex) { return NifSetOutcome.Fail(DescribeLoadException(ex)); }

        NifInspect pre;
        try
        {
            pre = Build(nif);
        }
        catch (Exception ex)
        {
            return NifSetOutcome.Fail(
                $"the mesh parsed but reading its structure failed — {ex.GetType().Name}: {ex.Message}. " +
                "Nothing was written.");
        }

        // Writes are intentionally limited to the validated Skyrim SE stream.
        if (!pre.IsSkyrimSE)
            return NifSetOutcome.Fail(
                $"this is NOT a Skyrim SE mesh (user {pre.UserVersion} / stream {pre.StreamVersion}; " +
                "SE is user 12 / stream 100). nif_set only writes SE-stream meshes; cross-game writes are " +
                "untested and refused. Nothing was written.");

        // ---- apply each op; record the exact block(s)/header each is allowed to touch ----
        var applied = new List<NifOpResult>(ops.Count);
        var expectedBlocks = new HashSet<int>();
        bool expectHeader = false;
        foreach (var op in ops)
        {
            var r = ApplyOp(nif, op);
            if (r.Error is not null) return NifSetOutcome.Fail(r.Error);
            applied.Add(new NifOpResult(op.Kind.ToString(), r.Target!, r.Before!, r.After!));
            if (r.TouchedBlock is { } b) expectedBlocks.Add(b);
            if (r.TouchedHeader) expectHeader = true;
        }

        // ---- save the edited mesh to memory ----
        byte[] edited;
        try
        {
            using var outMs = new MemoryStream();
            if (nif.Save(outMs) != 0)
                return NifSetOutcome.Fail(
                    "NiflySharp failed to save the edited mesh. Nothing was written.");
            edited = outMs.ToArray();
        }
        catch (Exception ex)
        {
            return NifSetOutcome.Fail(
                $"saving the edited mesh threw — {ex.GetType().Name}: {ex.Message}. Nothing was written.");
        }

        // ---- GATE 1: block-content diff (offset-immune) ----
        var g1 = VerifyBlockContent(bytes, edited, expectedBlocks, expectHeader);
        if (g1 is not null) return NifSetOutcome.Fail(g1);

        // ---- GATE 2: semantic read-back ----
        var g2 = VerifyReadBack(edited, pre, ops, out var warnings);
        if (g2 is not null) return NifSetOutcome.Fail(g2);

        var report = new NifSetReport(applied,
            expectedBlocks.OrderBy(i => i).ToList(), expectHeader,
            edited.Length - bytes.Length, warnings);
        return new NifSetOutcome(edited, report, null);
    }

    /// <summary>Applies one in-memory whitelist operation and records its allowed mutation scope.</summary>
    /// <param name="nif">Loaded mutable NIF.</param>
    /// <param name="op">Operation to apply.</param>
    /// <returns>
    /// Error or target/before/after accounting plus the one allowed block index or header-change marker.
    /// </returns>
    static (
        string? Error,
        string? Target,
        string? Before,
        string? After,
        int? TouchedBlock,
        bool TouchedHeader) ApplyOp(NifFile nif, NifSetOp op)
    {
        switch (op.Kind)
        {
            case NifSetOpKind.RenameShape:
            {
                if (string.IsNullOrEmpty(op.NewName))
                    return ("rename_shape needs a new_name.", null, null, null, null, false);
                var (shape, err) = ResolveShape(nif, op.Target);
                if (err is not null) return (err, null, null, null, null, false);
                bool duplicate = nif.GetShapes().Any(s =>
                    !ReferenceEquals(s, shape) &&
                    (s.Name?.String ?? "") == op.NewName);
                if (duplicate)
                    return (
                        $"a shape is already named '{op.NewName}' — renaming would create an ambiguous duplicate. " +
                        "Refusing, nothing written.",
                        null, null, null, null, false);
                var av = (NiflySharp.Blocks.NiAVObject)shape!;
                string before = av.Name?.String ?? "";
                av.Name = new NiStringRef(op.NewName);
                return (null, op.Target, before, op.NewName, null, true);
            }
            case NifSetOpKind.RenameNode:
            {
                if (string.IsNullOrEmpty(op.NewName))
                    return ("rename_node needs a new_name.", null, null, null, null, false);
                var (node, err) = ResolveNode(nif, op.Target);
                if (err is not null) return (err, null, null, null, null, false);
                bool duplicate = nif.Blocks.OfType<NiNode>().Any(n =>
                    !ReferenceEquals(n, node) &&
                    (n.Name?.String ?? "") == op.NewName);
                if (duplicate)
                    return (
                        $"a node is already named '{op.NewName}' — renaming would create an ambiguous duplicate. " +
                        "Refusing, nothing written.",
                        null, null, null, null, false);
                string before = node!.Name?.String ?? "";
                node.Name = new NiStringRef(op.NewName);
                return (null, op.Target, before, op.NewName, null, true);
            }
            case NifSetOpKind.SetFlags:
            {
                if (op.Flags is not { } flags)
                    return ("set_flags needs a flags value.", null, null, null, null, false);
                var (av, err) = ResolveAvObject(nif, op.Target);
                if (err is not null) return (err, null, null, null, null, false);
                string before = $"0x{av!.Flags_ui:X}";
                av.Flags_ui = flags;
                return (null, op.Target, before, $"0x{flags:X}", BlockIndexOf(nif, av), false);
            }
            case NifSetOpKind.SetScale:
            {
                if (op.Scale is not { } scale)
                    return ("set_scale needs a scale value.", null, null, null, null, false);
                var (av, err) = ResolveAvObject(nif, op.Target);
                if (err is not null) return (err, null, null, null, null, false);
                string before = av!.Scale.ToString(System.Globalization.CultureInfo.InvariantCulture);
                av.Scale = scale;
                var after = scale.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return (null, op.Target, before, after, BlockIndexOf(nif, av), false);
            }
            case NifSetOpKind.SetAlpha:
            {
                if (op.AlphaFlags is null && op.AlphaThreshold is null)
                    return (
                        "set_alpha needs alpha_flags and/or alpha_threshold.",
                        null, null, null, null, false);
                var (shape, err) = ResolveShape(nif, op.Target);
                if (err is not null) return (err, null, null, null, null, false);
                if (!shape!.HasAlphaProperty || nif.GetBlock<NiAlphaProperty>(shape.AlphaPropertyRef) is not { } ap)
                    return (
                        $"shape '{op.Target}' has no alpha property to set. Nothing was written.",
                        null, null, null, null, false);
                string before = $"0x{ap.Flags.Value:X4}/thr{ap.Threshold}";
                if (op.AlphaFlags is { } fw)
                {
                    var fl = ap.Flags;
                    fl.Value = fw;
                    ap.Flags = fl;
                }
                if (op.AlphaThreshold is { } th) ap.Threshold = th;
                var after = $"0x{ap.Flags.Value:X4}/thr{ap.Threshold}";
                return (null, op.Target, before, after, BlockIndexOf(nif, ap), false);
            }
            case NifSetOpKind.SetPartition:
            {
                if (op.BodyPartId is not { } bp)
                    return ("set_partition needs a body_part_id.", null, null, null, null, false);
                var (shape, err) = ResolveShape(nif, op.Target);
                if (err is not null) return (err, null, null, null, null, false);
                if (shape!.SkinInstanceRef is null ||
                    nif.GetBlock(shape.SkinInstanceRef) is not BSDismemberSkinInstance dis)
                    return (
                        $"shape '{op.Target}' has no BSDismember skin instance. Nothing was written.",
                        null, null, null, null, false);
                var list = dis.Partitions;
                if (list is null || list.Count == 0)
                    return (
                        $"shape '{op.Target}' has an empty partition list. Nothing was written.",
                        null, null, null, null, false);
                int idx;
                if (op.PartitionIndex is { } pi)
                {
                    if (pi < 0 || pi >= list.Count)
                        return (
                            $"partition_index {pi} is out of range for shape '{op.Target}' " +
                            $"({list.Count} partition(s)). Nothing was written.",
                            null, null, null, null, false);
                    idx = pi;
                }
                else if (list.Count == 1) idx = 0;
                else
                    return (
                        $"shape '{op.Target}' has {list.Count} partitions — pass partition_index. " +
                        "Nothing was written.",
                        null, null, null, null, false);
                string before = $"[{idx}]={(int)list[idx].BodyPart}";
                var p = list[idx];
                p.BodyPart = (NiflySharp.Enums.BSDismemberBodyPartType)bp;
                list[idx] = p;
                dis.Partitions = list;
                return (null, op.Target, before, $"[{idx}]={bp}", BlockIndexOf(nif, dis), false);
            }
            case NifSetOpKind.SetPath:
            {
                if (op.TextureSlot is not { } slot)
                    return ("set_path needs a texture_slot.", null, null, null, null, false);
                if (op.Path is null) return ("set_path needs a path.", null, null, null, null, false);
                var (shape, err) = ResolveShape(nif, op.Target);
                if (err is not null) return (err, null, null, null, null, false);
                var shader = nif.GetShader(shape!);
                if (shader?.TextureSetRef is null || nif.GetBlock(shader.TextureSetRef) is not BSShaderTextureSet ts)
                    return (
                        $"shape '{op.Target}' has no shader texture set. Nothing was written.",
                        null, null, null, null, false);
                if (slot < 0 || slot >= ts.Textures.Count)
                    return (
                        $"texture_slot {slot} is out of range for shape '{op.Target}' " +
                        $"({ts.Textures.Count} slot(s)). Nothing was written.",
                        null, null, null, null, false);
                var tex = ts.Textures[slot] ?? new NiflySharp.NiString4();
                string before = tex.Content ?? "";
                tex.Content = op.Path;
                ts.Textures[slot] = tex;
                return (
                    null,
                    op.Target,
                    $"tex[{slot}]={before}",
                    $"tex[{slot}]={op.Path}",
                    BlockIndexOf(nif, ts),
                    false);
            }
            default:
                return ($"unsupported op '{op.Kind}'.", null, null, null, null, false);
        }
    }

    /// <summary>Resolves exactly one shape by authored name.</summary>
    /// <param name="nif">Loaded NIF.</param>
    /// <param name="name">Exact case-sensitive shape name.</param>
    /// <returns>The unique shape or a named missing/ambiguous error.</returns>
    static (INiShape? Shape, string? Error) ResolveShape(NifFile nif, string name)
    {
        var matches = nif.GetShapes().Where(s => (s.Name?.String ?? "") == name).ToList();
        if (matches.Count == 0)
            return (
                null,
                $"no shape named '{name}' in this mesh. Shapes: {ShapeNames(nif)}. Nothing was written.");
        if (matches.Count > 1)
            return (
                null,
                $"more than one shape is named '{name}' — ambiguous, refusing to guess. Nothing was written.");
        return (matches[0], null);
    }

    /// <summary>Resolves exactly one node by authored name.</summary>
    /// <param name="nif">Loaded NIF.</param>
    /// <param name="name">Exact case-sensitive node name.</param>
    /// <returns>The unique node or a named missing/ambiguous error.</returns>
    static (NiNode? Node, string? Error) ResolveNode(NifFile nif, string name)
    {
        var matches = nif.Blocks.OfType<NiNode>().Where(n => (n.Name?.String ?? "") == name).ToList();
        if (matches.Count == 0) return (null, $"no node named '{name}' in this mesh. Nothing was written.");
        if (matches.Count > 1)
            return (
                null,
                $"more than one node is named '{name}' — ambiguous, refusing to guess. Nothing was written.");
        return (matches[0], null);
    }

    /// <summary>Resolves exactly one shape or node as an NiAVObject.</summary>
    /// <param name="nif">Loaded NIF.</param>
    /// <param name="name">Exact case-sensitive object name.</param>
    /// <returns>The unique object or a named missing/ambiguous error.</returns>
    static (NiflySharp.Blocks.NiAVObject? Av, string? Error) ResolveAvObject(NifFile nif, string name)
    {
        var matches = nif.Blocks
            .OfType<NiflySharp.Blocks.NiAVObject>()
            .Where(a => (a.Name?.String ?? "") == name)
            .ToList();
        if (matches.Count == 0) return (null, $"no shape or node named '{name}' in this mesh. Nothing was written.");
        if (matches.Count > 1)
            return (
                null,
                $"more than one shape/node is named '{name}' — ambiguous, refusing to guess. Nothing was written.");
        return (matches[0], null);
    }

    /// <summary>Formats authored shape names for resolution errors.</summary>
    /// <param name="nif">Loaded NIF.</param>
    /// <returns>Comma-separated quoted names.</returns>
    static string ShapeNames(NifFile nif) =>
        string.Join(", ", nif.GetShapes().Select(s => "'" + (s.Name?.String ?? "") + "'"));

    /// <summary>Finds a block's index by reference identity.</summary>
    /// <param name="nif">Owning NIF.</param>
    /// <param name="block">Resolved block object.</param>
    /// <returns>Index parallel to the header block tables, or -1 when absent.</returns>
    static int BlockIndexOf(NifFile nif, object block)
    {
        var blocks = nif.Blocks;
        for (int i = 0; i < blocks.Count; i++) if (ReferenceEquals(blocks[i], block)) return i;
        return -1;
    }

    /// <summary>Rejects collateral block, header, footer, or structural changes.</summary>
    /// <param name="original">Original file bytes.</param>
    /// <param name="edited">NiflySharp-written candidate bytes.</param>
    /// <param name="expectedBlocks">Block indexes operations declared mutable.</param>
    /// <param name="expectHeader">Whether an authored string-table change is expected.</param>
    /// <returns>Null on verified scope, otherwise a refusal.</returns>
    /// <remarks>
    /// The original is normalized through NiflySharp before comparison. Each file is sliced using its own block-size
    /// table, so changing a string length cannot shift offsets and create false collateral differences.
    /// </remarks>
    internal static string? VerifyBlockContent(
        byte[] original,
        byte[] edited,
        HashSet<int> expectedBlocks,
        bool expectHeader)
    {
        byte[] normBaseline;
        try
        {
            var b = new NifFile();
            using var ms = new MemoryStream(original, writable: false);
            if (b.Load(ms) != 0)
                return "verification could not re-parse the original mesh to normalize it — refusing to write.";
            using var outMs = new MemoryStream();
            if (b.Save(outMs) != 0) return "verification could not normalize the original mesh — refusing to write.";
            normBaseline = outMs.ToArray();
        }
        catch (Exception ex)
        {
            return $"verification threw while normalizing the original mesh ({ex.GetType().Name}) — " +
                   "refusing to write.";
        }

        var a = SliceBlocks(normBaseline);
        var c = SliceBlocks(edited);
        if (a is null || c is null)
            return "verification could not recover the block layout to compare — refusing to write " +
                   "(nothing changed on disk).";
        if (a.Value.blocks.Length != c.Value.blocks.Length)
            return $"the edit changed the block COUNT ({a.Value.blocks.Length} → {c.Value.blocks.Length}) — " +
                   "no whitelist operation permits this structural change. Refusing, nothing written.";

        var changed = new List<int>();
        for (int i = 0; i < c.Value.blocks.Length; i++)
            if (!a.Value.blocks[i].AsSpan().SequenceEqual(c.Value.blocks[i])) changed.Add(i);

        var unexpected = changed.Where(i => !expectedBlocks.Contains(i)).ToList();
        if (unexpected.Count > 0)
        {
            var actual = string.Join(", ", unexpected.Select(i => i + " " + c.Value.types[i]));
            var expected = string.Join(", ", expectedBlocks.OrderBy(x => x));
            return $"the edit changed block(s) [{actual}] it should not have touched (expected only [{expected}]). " +
                   "Refusing, nothing written.";
        }

        // A rename changes authored header strings. Resizing an allowed block changes the derived size table.
        bool expectedBlockResized = expectedBlocks.Any(i =>
            i >= 0 &&
            i < a.Value.blocks.Length &&
            a.Value.blocks[i].Length != c.Value.blocks[i].Length);
        bool headerChanged = !c.Value.header.AsSpan().SequenceEqual(a.Value.header);
        if (headerChanged && !expectHeader && !expectedBlockResized)
            return "the edit changed an unexpected header string or block-size entry. Refusing, nothing written.";
        if (c.Value.footer.AsSpan().SequenceEqual(a.Value.footer) == false)
            return "the edit changed root references in the file footer. Refusing, nothing written.";
        return null;
    }

    /// <summary>Slices normalized NIF bytes into header, individual blocks, type names, and footer.</summary>
    /// <param name="buf">Normalized complete NIF bytes.</param>
    /// <returns>Recovered sections, or null when block/footer boundaries cannot be proven.</returns>
    static (byte[] header, byte[][] blocks, string[] types, byte[] footer)? SliceBlocks(byte[] buf)
    {
        NifFile nif;
        try
        {
            nif = new NifFile();
            using var ms = new MemoryStream(buf, writable: false);
            if (nif.Load(ms) != 0) return null;
        }
        catch { return null; }

        int bc = nif.Header.BlockCount;
        long sum = 0;
        var sizes = new int[bc];
        var types = new string[bc];
        for (int i = 0; i < bc; i++)
        {
            sizes[i] = nif.Header.GetBlockSize(i);
            types[i] = nif.Header.GetBlockTypeNameById(i) ?? "?";
            sum += sizes[i];
        }

        // The footer stores a root count followed by that many block indexes. Accept the first candidate whose
        // count and every index are valid. Starting at one prevents a block-zero root from mimicking an empty footer.
        long headerEnd = -1;
        long footerLen = 0;
        for (int nRoots = 1; nRoots <= 64; nRoots++)
        {
            long fl = 4 + 4L * nRoots;
            long cand = buf.LongLength - fl - sum;
            if (cand <= 0) break;
            if (cand + sum + fl > buf.LongLength) continue;
            if (BitConverter.ToUInt32(buf, (int)(cand + sum)) != (uint)nRoots) continue;
            bool refsValid = true;
            for (int r = 0; r < nRoots; r++)
            {
                uint rootRef = BitConverter.ToUInt32(
                    buf,
                    (int)(cand + sum + 4 + 4L * r));
                if (rootRef >= (uint)bc)
                {
                    refsValid = false;
                    break;
                }
            }
            if (refsValid)
            {
                headerEnd = cand;
                footerLen = fl;
                break;
            }
        }
        if (headerEnd < 0) return null;

        var header = new byte[headerEnd];
        Array.Copy(buf, 0, header, 0, headerEnd);
        var blocks = new byte[bc][];
        long pos = headerEnd;
        for (int i = 0; i < bc; i++)
        {
            if (pos + sizes[i] > buf.LongLength) return null;
            blocks[i] = new byte[sizes[i]];
            Array.Copy(buf, pos, blocks[i], 0, sizes[i]);
            pos += sizes[i];
        }
        var footer = new byte[footerLen];
        if (footerLen > 0 && pos + footerLen <= buf.LongLength)
            Array.Copy(buf, pos, footer, 0, footerLen);
        return (header, blocks, types, footer);
    }

    /// <summary>Reloads candidate bytes and verifies semantic operation effects and structural invariants.</summary>
    /// <param name="edited">Candidate edited bytes.</param>
    /// <param name="pre">Original inspection model.</param>
    /// <param name="ops">Requested operations.</param>
    /// <param name="warnings">Preserved-unknown-block warnings on success.</param>
    /// <returns>Null when every check passes, otherwise a refusal.</returns>
    internal static string? VerifyReadBack(
        byte[] edited,
        NifInspect pre,
        IReadOnlyList<NifSetOp> ops,
        out IReadOnlyList<string> warnings)
    {
        warnings = Array.Empty<string>();
        var re = new NifFile();
        try
        {
            using var ms = new MemoryStream(edited, writable: false);
            if (re.Load(ms) != 0)
                return "the written mesh failed to reload for verification — refusing " +
                       "(nothing changed on disk).";
        }
        catch (Exception ex)
        {
            return $"the written mesh threw on verification reload ({ex.GetType().Name}) — refusing " +
                   "(nothing changed on disk).";
        }

        NifInspect post;
        try
        {
            post = Build(re);
        }
        catch (Exception ex)
        {
            return $"the written mesh re-inspect failed ({ex.GetType().Name}) — refusing " +
                   "(nothing changed on disk).";
        }

        if (!post.IsSkyrimSE)
            return "the written mesh is no longer an SE stream — refusing (nothing changed on disk).";
        if (!CensusEqual(pre.BlockTypes, post.BlockTypes))
            return "the written mesh block census changed — no whitelist operation permits this structural drift. " +
                   "Refusing (nothing changed on disk).";
        if (pre.UnknownBlockTypes.Count != post.UnknownBlockTypes.Count)
            return "the written mesh unknown-block count changed — refusing (nothing changed on disk).";

        foreach (var op in ops)
        {
            var (ok, actual) = ReadBackMatches(re, op);
            if (!ok)
                return $"read-back shows {op.Kind} on '{op.Target}' did NOT take effect (now {actual}) — " +
                       "refusing (nothing changed on disk).";
        }

        if (post.HasUnknownBlocks)
        {
            warnings = new[]
            {
                $"this mesh carries {post.UnknownBlockTypes.Count} unknown block type(s) " +
                $"({string.Join(", ", post.UnknownBlockTypes)}) — the unchanged census confirms they were preserved."
            };
        }
        return null;
    }

    /// <summary>Compares ordered block-type census entries.</summary>
    /// <param name="a">Original census.</param>
    /// <param name="b">Candidate census.</param>
    /// <returns>True on exact type/count equality.</returns>
    static bool CensusEqual(
        IReadOnlyList<NifBlockTypeCount> a,
        IReadOnlyList<NifBlockTypeCount> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i].Type != b[i].Type || a[i].Count != b[i].Count)
                return false;
        return true;
    }

    /// <summary>Confirms one operation's requested value in a reloaded mesh.</summary>
    /// <param name="nif">Reloaded candidate NIF.</param>
    /// <param name="op">Original operation.</param>
    /// <returns>Match state and human-readable actual value.</returns>
    static (bool ok, string actual) ReadBackMatches(NifFile nif, NifSetOp op)
    {
        switch (op.Kind)
        {
            case NifSetOpKind.RenameShape:
                return (
                    nif.GetShapes().Any(s => (s.Name?.String ?? "") == op.NewName),
                    $"shape names {ShapeNames(nif)}");
            case NifSetOpKind.RenameNode:
                return (
                    nif.Blocks.OfType<NiNode>().Any(n => (n.Name?.String ?? "") == op.NewName),
                    "(node name)");
            case NifSetOpKind.SetFlags:
            {
                var av = nif.Blocks
                    .OfType<NiflySharp.Blocks.NiAVObject>()
                    .FirstOrDefault(a => (a.Name?.String ?? "") == op.Target);
                var actual = av is null ? "(target gone)" : $"0x{av.Flags_ui:X}";
                return (av is not null && op.Flags is { } f && av.Flags_ui == f, actual);
            }
            case NifSetOpKind.SetScale:
            {
                var av = nif.Blocks
                    .OfType<NiflySharp.Blocks.NiAVObject>()
                    .FirstOrDefault(a => (a.Name?.String ?? "") == op.Target);
                var actual = av is null
                    ? "(target gone)"
                    : av.Scale.ToString(System.Globalization.CultureInfo.InvariantCulture);
                bool matches = av is not null &&
                               op.Scale is { } sc &&
                               Math.Abs(av.Scale - sc) < 1e-6f;
                return (matches, actual);
            }
            case NifSetOpKind.SetAlpha:
            {
                var s = nif.GetShapes().FirstOrDefault(x => (x.Name?.String ?? "") == op.Target);
                var ap = s is not null && s.HasAlphaProperty
                    ? nif.GetBlock<NiAlphaProperty>(s.AlphaPropertyRef)
                    : null;
                if (ap is null) return (false, "(no alpha)");
                bool okF = op.AlphaFlags is not { } fw || ap.Flags.Value == fw;
                bool okT = op.AlphaThreshold is not { } th || ap.Threshold == th;
                return (okF && okT, $"0x{ap.Flags.Value:X4}/thr{ap.Threshold}");
            }
            case NifSetOpKind.SetPartition:
            {
                var s = nif.GetShapes().FirstOrDefault(x => (x.Name?.String ?? "") == op.Target);
                var dis = s?.SkinInstanceRef is not null
                    ? nif.GetBlock(s.SkinInstanceRef) as BSDismemberSkinInstance
                    : null;
                if (dis?.Partitions is null || dis.Partitions.Count == 0) return (false, "(no partitions)");
                int idx = op.PartitionIndex ?? 0;
                if (idx < 0 || idx >= dis.Partitions.Count) return (false, "(index gone)");
                var actual = $"[{idx}]={(int)dis.Partitions[idx].BodyPart}";
                return (op.BodyPartId is { } bp && (int)dis.Partitions[idx].BodyPart == bp, actual);
            }
            case NifSetOpKind.SetPath:
            {
                var s = nif.GetShapes().FirstOrDefault(x => (x.Name?.String ?? "") == op.Target);
                var shader = s is not null ? nif.GetShader(s) : null;
                var ts = shader?.TextureSetRef is not null
                    ? nif.GetBlock(shader.TextureSetRef) as BSShaderTextureSet
                    : null;
                if (ts is null ||
                    op.TextureSlot is not { } slot ||
                    slot < 0 ||
                    slot >= ts.Textures.Count)
                    return (false, "(no texset/slot)");
                return (
                    (ts.Textures[slot]?.Content ?? "") == op.Path,
                    $"tex[{slot}]={ts.Textures[slot]?.Content}");
            }
            default: return (false, "(unknown op)");
        }
    }
}

/// <summary>Contains either a complete NIF inspection or a named error.</summary>
/// <param name="Inspect">Complete model on success; otherwise null.</param>
/// <param name="Error">Parse/read error on failure; otherwise null.</param>
public sealed record NifInspectOutcome(NifInspect? Inspect, string? Error);

/// <summary>Contains the complete supported inspection model for one mesh.</summary>
/// <param name="VersionString">NIF version text.</param>
/// <param name="UserVersion">Numeric header user version.</param>
/// <param name="StreamVersion">Numeric header stream version.</param>
/// <param name="IsSkyrimSE">Whether the user/stream pair identifies Skyrim SE.</param>
/// <param name="BlockCount">Header block count.</param>
/// <param name="BlockTypes">On-disk block-type census.</param>
/// <param name="HasUnknownBlocks">Whether NiflySharp encountered opaque block types.</param>
/// <param name="UnknownBlockTypes">Distinct opaque on-disk type names.</param>
/// <param name="Shapes">Inspectable shape models.</param>
/// <param name="Nodes">Depth-annotated pre-order node tree.</param>
/// <param name="HeaderStrings">Contiguous non-empty header-string prefix.</param>
public sealed record NifInspect(
    string VersionString,
    uint UserVersion,
    uint StreamVersion,
    bool IsSkyrimSE,
    int BlockCount,
    IReadOnlyList<NifBlockTypeCount> BlockTypes,
    bool HasUnknownBlocks,
    IReadOnlyList<string> UnknownBlockTypes,
    IReadOnlyList<NifShape> Shapes,
    IReadOnlyList<NifNode> Nodes,
    IReadOnlyList<string> HeaderStrings);

/// <summary>Counts one on-disk NIF block type.</summary>
/// <param name="Type">On-disk type name.</param>
/// <param name="Count">Number of blocks of this type.</param>
public sealed record NifBlockTypeCount(string Type, int Count);

/// <summary>Contains supported render and skin properties for one shape.</summary>
/// <param name="Name">Authored shape name.</param>
/// <param name="Flags">Raw NiAVObject flags.</param>
/// <param name="Scale">Local scale.</param>
/// <param name="BlockType">Runtime NIF block type.</param>
/// <param name="FlagsDefault">Documented SSE default, or null when unavailable.</param>
/// <param name="FlagsDefaultType">Type or ancestor supplying that default.</param>
/// <param name="Partitions">BSDismember partitions; empty when absent.</param>
/// <param name="Alpha">Decoded alpha property; null when absent.</param>
/// <param name="Textures">Non-empty shader texture slots.</param>
/// <param name="Bones">Shape bone names.</param>
public sealed record NifShape(
    string Name,
    uint Flags,
    float Scale,
    string BlockType,
    uint? FlagsDefault,
    string? FlagsDefaultType,
    IReadOnlyList<NifPartition> Partitions,
    NifAlpha? Alpha,
    IReadOnlyList<NifTexture> Textures,
    IReadOnlyList<string> Bones);

/// <summary>Describes one BSDismember partition.</summary>
/// <param name="BodyPartId">Numeric body-part identifier.</param>
/// <param name="BodyPartName">Decoded enum name.</param>
/// <param name="PartFlags">Raw partition flags.</param>
public sealed record NifPartition(int BodyPartId, string BodyPartName, int PartFlags);

/// <summary>Contains raw and decoded NiAlphaProperty values.</summary>
/// <param name="Flags">Raw 16-bit flags.</param>
/// <param name="Blend">Whether alpha blending is enabled.</param>
/// <param name="SourceBlendMode">Source blend function.</param>
/// <param name="DestinationBlendMode">Destination blend function.</param>
/// <param name="Test">Whether alpha testing is enabled.</param>
/// <param name="TestFunction">Alpha comparison function.</param>
/// <param name="Threshold">Alpha-test threshold.</param>
public sealed record NifAlpha(
    ushort Flags,
    bool Blend,
    string SourceBlendMode,
    string DestinationBlendMode,
    bool Test,
    string TestFunction,
    byte Threshold);

/// <summary>Identifies one non-empty shader texture slot.</summary>
/// <param name="Slot">BSShaderTextureSet slot index.</param>
/// <param name="Path">Authored texture path.</param>
public sealed record NifTexture(int Slot, string Path);

/// <summary>Describes one node in the pre-order NiNode tree.</summary>
/// <param name="Depth">Tree depth; roots are zero.</param>
/// <param name="Name">Authored node name.</param>
/// <param name="Flags">Raw NiAVObject flags.</param>
/// <param name="BlockType">Runtime NIF block type.</param>
/// <param name="FlagsDefault">Documented SSE default, or null when unavailable.</param>
/// <param name="FlagsDefaultType">Type or ancestor supplying that default.</param>
public sealed record NifNode(
    int Depth,
    string Name,
    uint Flags,
    string BlockType,
    uint? FlagsDefault,
    string? FlagsDefaultType);

/// <summary>Names supported NIF edit operations.</summary>
public enum NifSetOpKind
{
    /// <summary>Changes a shape name in the header string table.</summary>
    RenameShape,
    /// <summary>Changes a node name in the header string table.</summary>
    RenameNode,
    /// <summary>Replaces raw NiAVObject flags.</summary>
    SetFlags,
    /// <summary>Replaces NiAVObject scale.</summary>
    SetScale,
    /// <summary>Replaces one BSDismember body-part identifier.</summary>
    SetPartition,
    /// <summary>Replaces alpha flags and/or threshold.</summary>
    SetAlpha,
    /// <summary>Replaces one shader texture-set path.</summary>
    SetPath
}

/// <summary>Describes one whitelist NIF edit.</summary>
/// <param name="Kind">Operation kind.</param>
/// <param name="Target">Current exact shape or node name.</param>
/// <param name="NewName">Replacement name for rename operations.</param>
/// <param name="Flags">Replacement flags for <see cref="NifSetOpKind.SetFlags"/>.</param>
/// <param name="Scale">Replacement scale for <see cref="NifSetOpKind.SetScale"/>.</param>
/// <param name="BodyPartId">Replacement body-part ID for <see cref="NifSetOpKind.SetPartition"/>.</param>
/// <param name="PartitionIndex">Partition index, defaulting to zero when omitted.</param>
/// <param name="AlphaFlags">Optional replacement alpha flags.</param>
/// <param name="AlphaThreshold">Optional replacement alpha threshold.</param>
/// <param name="TextureSlot">Texture-set slot for <see cref="NifSetOpKind.SetPath"/>.</param>
/// <param name="Path">Replacement texture path.</param>
public sealed record NifSetOp(
    NifSetOpKind Kind,
    string Target,
    string? NewName = null,
    uint? Flags = null,
    float? Scale = null,
    int? BodyPartId = null,
    int? PartitionIndex = null,
    ushort? AlphaFlags = null,
    byte? AlphaThreshold = null,
    int? TextureSlot = null,
    string? Path = null);

/// <summary>Contains either verified edited NIF bytes and a report, or a named refusal.</summary>
/// <param name="WrittenBytes">Verified candidate bytes on success; otherwise null.</param>
/// <param name="Report">Verification and operation audit on success; otherwise null.</param>
/// <param name="Error">Named refusal on failure; otherwise null.</param>
public sealed record NifSetOutcome(byte[]? WrittenBytes, NifSetReport? Report, string? Error)
{
    /// <summary>Creates a failed edit result.</summary>
    /// <param name="error">Named refusal.</param>
    /// <returns>A result containing no bytes or report.</returns>
    public static NifSetOutcome Fail(string error) => new(null, null, error);
}

/// <summary>Reports a successful verified NIF edit.</summary>
/// <param name="Ops">Per-operation before/after audit.</param>
/// <param name="ChangedBlocks">Block indexes allowed and observed as mutable.</param>
/// <param name="HeaderChanged">Whether a rename changed the authored string table.</param>
/// <param name="SizeDelta">Candidate byte length minus original byte length.</param>
/// <param name="Warnings">Non-fatal preserved-unknown-block notes.</param>
public sealed record NifSetReport(
    IReadOnlyList<NifOpResult> Ops,
    IReadOnlyList<int> ChangedBlocks,
    bool HeaderChanged,
    long SizeDelta,
    IReadOnlyList<string> Warnings);

/// <summary>Reports one applied operation.</summary>
/// <param name="Op">Operation kind.</param>
/// <param name="Target">Resolved target.</param>
/// <param name="Before">Value before mutation.</param>
/// <param name="After">Requested value after mutation.</param>
public sealed record NifOpResult(string Op, string Target, string Before, string After);
