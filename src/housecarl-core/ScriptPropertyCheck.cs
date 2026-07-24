using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Pex;
using Mutagen.Bethesda;

namespace HousecarlCore;

/// <summary>Compares VMAD property bindings with Auto properties declared by compiled scripts.</summary>
/// <remarks>
/// The sweep follows each PEX extends chain and includes quest-alias script attachments. It reports unbound object
/// properties, uninitialized scalar Auto properties, and explicit null object bindings. Custom accessor properties
/// and initialized scalars are excluded. Missing scripts, missing ancestors, record failures, and unreadable archives
/// remain visible instead of becoming clean results.
/// </remarks>
public static class ScriptPropertyCheck
{
    /// <summary>Runs the script-property binding sweep over selected active plugins.</summary>
    /// <param name="resolver">Active plugin and record resolver.</param>
    /// <param name="assets">Active loose and archive asset resolver.</param>
    /// <param name="scope">Plugin filenames, or null/empty for all non-excluded active plugins.</param>
    /// <param name="limit">Maximum unbound and null-object findings retained; totals remain uncapped.</param>
    /// <returns>A complete result or a named scope error with no partial reports.</returns>
    public static ScriptCheckResult Run(
        LoadOrderResolver resolver,
        AssetResolver assets,
        IReadOnlyList<string>? scope,
        int limit)
    {
        var view = resolver.Capture();
        var av = assets.Capture();          // Pin PEX winners and incomplete-read state.

        // An invalid explicit scope fails before any partial scan.
        List<string> targets;
        if (scope is { Count: > 0 })
        {
            targets = new List<string>(scope.Count);
            foreach (var name in scope)
            {
                if (!view.ContainsPlugin(name))
                    return ScriptCheckResult.Fail($"plugin not in the load order: {name}.{view.AbsenceClause(name)}");
                if (view.ExcludedPlugins.TryGetValue(name, out var why))
                    return ScriptCheckResult.Fail(
                        $"plugin '{name}' was excluded because it could not be parsed ({why}) — fix or remove it; " +
                        "it cannot be checked.");
                targets.Add(name);
            }
        }
        else
        {
            targets = new List<string>();
            foreach (var n in resolver.PluginNames)
                if (!view.ExcludedPlugins.ContainsKey(n)) targets.Add(n);
        }

        // Many records share a script class, so resolve each extends chain once per sweep.
        var chainCache = new Dictionary<string, ChainResult>(StringComparer.OrdinalIgnoreCase);

        var reports = new List<RecordScriptFindings>();
        int recordsWithScripts = 0, totalUnbound = 0, totalNull = 0, totalUnverifiable = 0;
        int findingBudget = limit;
        bool capped = false;

        foreach (var plugin in targets)
        {
            string? scanError = null;
            try
            {
                foreach (var (fk, _, body, _) in view.RecordsIn(new[] { plugin }, null))
                {
                    // A bad VMAD becomes a per-record scan error while other records continue.
                    try
                    {
                        if (body is not IHaveVirtualMachineAdapterGetter have) continue;
                        if (have.VirtualMachineAdapter is not { } vmad) continue;
                        var scriptEntries = CollectScriptEntries(vmad);
                        if (scriptEntries.Count == 0) continue;
                        recordsWithScripts++;

                        var unbound = new List<UnboundProperty>();
                        var nulls = new List<NullObjectProperty>();
                        var unver = new List<ScriptUnverifiable>();

                        foreach (var entry in scriptEntries)
                        {
                            var scriptClass = entry.Name?.Trim();
                            if (string.IsNullOrEmpty(scriptClass))
                            {
                                unver.Add(new ScriptUnverifiable("(unnamed)",
                                    "the script attachment carries no class name — cannot resolve its .pex."));
                                continue;
                            }

                            // Alias-bound properties have a null Object by design and are not null-object findings.
                            foreach (var p in entry.Properties)
                                if (p is IScriptObjectPropertyGetter op &&
                                    op.Object.FormKey.IsNull &&
                                    op.Alias < 0 &&
                                    !string.IsNullOrWhiteSpace(p.Name))
                                    nulls.Add(new NullObjectProperty(scriptClass, p.Name!.Trim()));

                            var chain = ResolveChain(av, chainCache, scriptClass);
                            if (chain.OwnLoadError is not null)
                            {
                                unver.Add(new ScriptUnverifiable(scriptClass, chain.OwnLoadError));
                                continue;   // can't read the script's own properties — nothing to compare against
                            }

                            var boundNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var p in entry.Properties)
                                if (!string.IsNullOrWhiteSpace(p.Name)) boundNames.Add(p.Name!.Trim());

                            foreach (var d in chain.Declared)
                            {
                                if (boundNames.Contains(d.Name)) continue;
                                // A scalar with a baked initializer has the author's intended default — leaving it
                                // unbound is correct, so it is not a finding.
                                if (!d.IsObjectType && d.HasInitializer) continue;
                                unbound.Add(new UnboundProperty(
                                    scriptClass,
                                    d.DeclaringScript,
                                    d.Name,
                                    d.TypeName,
                                    d.IsObjectType));
                            }

                            if (chain.ChainNote is not null)
                                unver.Add(new ScriptUnverifiable(scriptClass, chain.ChainNote));
                        }

                        if (unbound.Count == 0 && nulls.Count == 0 && unver.Count == 0) continue;

                        // Apply the shared finding budget (unbound + null are the capped population; unverifiable notes
                        // are few and always kept). The true totals are counted regardless of the cap.
                        totalUnbound += unbound.Count;
                        totalNull += nulls.Count;
                        totalUnverifiable += unver.Count;

                        var keptUnbound = new List<UnboundProperty>();
                        var keptNull = new List<NullObjectProperty>();
                        foreach (var u in unbound)
                        {
                            if (findingBudget > 0) { keptUnbound.Add(u); findingBudget--; }
                            else capped = true;
                        }
                        foreach (var n in nulls)
                        {
                            if (findingBudget > 0) { keptNull.Add(n); findingBudget--; }
                            else capped = true;
                        }

                        reports.Add(new RecordScriptFindings(
                            fk, RecordNaming.StripOverlay(body.GetType().Name), body.EditorID, plugin,
                            keptUnbound, keptNull, unver));
                    }
                    catch (Exception ex)
                    {
                        scanError = (scanError is null ? "" : scanError + "; ")
                                  + $"a record's script adapter could not be read " +
                                    $"({fk} — {ex.GetType().Name}: {ex.Message})";
                    }
                }
            }
            // A plugin enumeration failure is retained as a plugin-specific report.
            catch (Exception ex)
            {
                reports.Add(RecordScriptFindings.PluginScanError(plugin,
                    $"record enumeration aborted partway: {ex.GetType().Name}: {ex.Message}"));
            }

            if (scanError is not null)
                reports.Add(RecordScriptFindings.PluginScanError(plugin, scanError));
        }

        return new ScriptCheckResult(reports, targets.Count, recordsWithScripts, totalUnbound, totalNull,
                                     totalUnverifiable, capped, av.ReadIncomplete, view.ExcludedPlugins, null);
    }

    /// <summary>Collects scripts attached directly to a VMAD and to any quest aliases it owns.</summary>
    /// <param name="vmad">Record virtual-machine adapter.</param>
    /// <returns>Script entries in direct-attachment then alias order.</returns>
    static List<IScriptEntryGetter> CollectScriptEntries(IAVirtualMachineAdapterGetter vmad)
    {
        var entries = new List<IScriptEntryGetter>(vmad.Scripts);
        if (vmad is IQuestAdapterGetter qa)
            foreach (var alias in qa.Aliases)
                entries.AddRange(alias.Scripts);
        return entries;
    }

    /// <summary>Holds declared properties and any own-script or ancestor-chain load failure.</summary>
    /// <param name="Declared">Most-derived-first Auto properties, deduplicated by name.</param>
    /// <param name="ChainNote">Missing or unreadable ancestor note.</param>
    /// <param name="OwnLoadError">Missing or unreadable requested-script error.</param>
    sealed record ChainResult(IReadOnlyList<DeclaredProp> Declared, string? ChainNote, string? OwnLoadError);

    /// <summary>Describes one Auto property declared by a script in the extends chain.</summary>
    /// <param name="Name">Property name.</param>
    /// <param name="TypeName">PEX type name.</param>
    /// <param name="DeclaringScript">Class that declares the property.</param>
    /// <param name="HasInitializer">Whether the backing variable has an initializer.</param>
    /// <param name="IsObjectType">Whether an unbound value becomes None rather than a scalar default.</param>
    sealed record DeclaredProp(
        string Name,
        string TypeName,
        string DeclaringScript,
        bool HasInitializer,
        bool IsObjectType);

    /// <summary>Gets one script's cached extends-chain result.</summary>
    /// <param name="av">Pinned asset view.</param>
    /// <param name="cache">Per-sweep class cache.</param>
    /// <param name="scriptClass">Papyrus class name.</param>
    /// <returns>The cached or newly built chain result.</returns>
    static ChainResult ResolveChain(
        AssetResolver.AssetView av,
        Dictionary<string, ChainResult> cache,
        string scriptClass)
    {
        if (cache.TryGetValue(scriptClass, out var hit)) return hit;
        var result = BuildChain(av, scriptClass);
        cache[scriptClass] = result;
        return result;
    }

    /// <summary>Walks a PEX extends chain and collects its Auto properties.</summary>
    /// <param name="av">Pinned asset view.</param>
    /// <param name="scriptClass">Most-derived Papyrus class.</param>
    /// <returns>A complete or explicitly truncated chain result.</returns>
    static ChainResult BuildChain(AssetResolver.AssetView av, string scriptClass)
    {
        // The first declaration is the most-derived override.
        var byName = new Dictionary<string, DeclaredProp>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? current = scriptClass;
        string? chainNote = null;

        for (int hops = 0; current is { Length: > 0 } && hops < 64; hops++)
        {
            if (!visited.Add(current)) break;   // cycle guard (a malformed hierarchy) — stop, never spin

            if (!TryLoadPex(av, current, out var pex, out var reason))
            {
                if (hops == 0)
                    return new ChainResult(Array.Empty<DeclaredProp>(), null, reason);
                chainNote = $"the extends chain was truncated at '{current}' ({reason}) — properties it and " +
                            "its ancestors declare were not checked.";
                break;
            }

            // A single-script .pex has one object named for the class; take the matching object (else the first).
            var obj = pex!.Objects.FirstOrDefault(
                o => string.Equals(o.Name, current, StringComparison.OrdinalIgnoreCase))
                ?? pex.Objects.FirstOrDefault();
            if (obj is null)
            {
                chainNote ??= $"'{current}.pex' held no script object — its properties were not checked.";
                break;
            }

            foreach (var p in obj.Properties)
            {
                if (!p.Flags.HasFlag(PropertyFlags.AutoVar)) continue;
                var name = p.Name?.Trim();
                if (string.IsNullOrEmpty(name) || byName.ContainsKey(name)) continue;
                var typeName = (p.TypeName ?? "").Trim();
                var backing = obj.Variables.FirstOrDefault(v =>
                    string.Equals(v.Name, p.AutoVarName, StringComparison.OrdinalIgnoreCase));
                bool hasInit = backing is not null && PapyrusDecompiler.InitText(backing.VariableData) is not null;
                byName[name] = new DeclaredProp(name, typeName, current, hasInit, IsObjectType(typeName));
            }

            current = string.IsNullOrWhiteSpace(obj.ParentClassName) ? null : obj.ParentClassName.Trim();
        }

        return new ChainResult(byName.Values.ToList(), chainNote, null);
    }

    /// <summary>Loads a compiled script from its winning loose or archive source.</summary>
    /// <param name="av">Pinned asset view.</param>
    /// <param name="scriptClass">Papyrus class name.</param>
    /// <param name="pex">Receives the parsed PEX on success.</param>
    /// <param name="reason">Receives a specific load failure.</param>
    /// <returns>True only when the complete PEX parsed successfully.</returns>
    static bool TryLoadPex(AssetResolver.AssetView av, string scriptClass, out PexFile? pex, out string? reason)
    {
        pex = null; reason = null;
        // Papyrus namespaces map to host-independent Bethesda path segments.
        var rel = $@"Scripts\{scriptClass.Replace(':', '\\')}.pex";
        var res = av.ResolveForPlacement(rel);
        if (res.Sources.Count == 0)
        {
            var caveat = av.ReadIncomplete
                ? " — and an archive failed to read, so it may merely be unscanned"
                : "";
            reason = $"'{rel}' is not on disk (the script is not compiled, or not in the load order){caveat}.";
            return false;
        }
        var src = res.Sources[0];   // winner first
        try
        {
            if (src.LooseFilePath is not null)
                pex = PexFile.CreateFromFile(src.LooseFilePath, GameCategory.Skyrim);
            else if (src.ArchivePath is not null)
            {
                var bytes = AssetResolver.TryReadArchiveEntry(src.ArchivePath, src.EntryPath);
                if (bytes is null)
                {
                    reason = $"'{rel}' vanished from '{Path.GetFileName(src.ArchivePath)}' between listing and read.";
                    return false;
                }
                using var ms = new MemoryStream(bytes);
                pex = PexFile.CreateFromStream(ms, GameCategory.Skyrim);
            }
            else { reason = $"'{rel}' resolved to no readable source."; return false; }
            return true;
        }
        catch (Exception ex)
        {
            pex = null;
            reason = $"Mutagen cannot read '{rel}' ({ex.GetType().Name}: {ex.Message}).";
            return false;
        }
    }

    /// <summary>Classifies PEX property types by their unbound runtime default.</summary>
    /// <param name="typeName">PEX type name.</param>
    /// <returns>False for scalar primitives; true for objects and arrays that default to None.</returns>
    static bool IsObjectType(string typeName)
        => !(typeName.Equals("Int", StringComparison.OrdinalIgnoreCase)
             || typeName.Equals("Float", StringComparison.OrdinalIgnoreCase)
             || typeName.Equals("Bool", StringComparison.OrdinalIgnoreCase)
             || typeName.Equals("String", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Reports an Auto property declared by a script chain but absent from the record VMAD.</summary>
/// <param name="Script">Attached class.</param>
/// <param name="DeclaringScript">Class in the extends chain that declares the property.</param>
/// <param name="PropertyName">Property name.</param>
/// <param name="PexTypeName">PEX type name.</param>
/// <param name="IsObjectType">Whether the unbound runtime value is None rather than a scalar default.</param>
public sealed record UnboundProperty(
    string Script,
    string DeclaringScript,
    string PropertyName,
    string PexTypeName,
    bool IsObjectType);

/// <summary>Reports a VMAD object-property slot containing neither a FormKey nor a quest alias.</summary>
/// <param name="Script">Attached class.</param>
/// <param name="PropertyName">Property name.</param>
public sealed record NullObjectProperty(string Script, string PropertyName);

/// <summary>Reports a script attachment that could not be checked completely.</summary>
/// <param name="Script">Attached class or placeholder name.</param>
/// <param name="Reason">Missing name, missing PEX, parse failure, or truncated ancestor-chain detail.</param>
public sealed record ScriptUnverifiable(string Script, string Reason);

/// <summary>Collects script-property findings for one record or a plugin-level scan failure.</summary>
/// <param name="Record">Record FormKey, or null FormKey for a plugin scan error.</param>
/// <param name="RecordType">Record type name.</param>
/// <param name="EditorId">Record EditorID.</param>
/// <param name="Plugin">Scanned plugin.</param>
/// <param name="Unbound">Declared Auto properties absent from VMAD.</param>
/// <param name="NullObjects">Object properties explicitly bound to no object or alias.</param>
/// <param name="Unverifiable">Attachments or chains that could not be read completely.</param>
/// <param name="ScanError">Record or plugin enumeration error.</param>
public sealed record RecordScriptFindings(
    FormKey Record, string RecordType, string? EditorId, string Plugin,
    IReadOnlyList<UnboundProperty> Unbound, IReadOnlyList<NullObjectProperty> NullObjects,
    IReadOnlyList<ScriptUnverifiable> Unverifiable, string? ScanError = null)
{
    /// <summary>Creates a plugin-level report when record enumeration cannot complete.</summary>
    /// <param name="plugin">Affected plugin.</param>
    /// <param name="error">Specific enumeration error.</param>
    /// <returns>A report with no record identity or property findings.</returns>
    public static RecordScriptFindings PluginScanError(string plugin, string error) =>
        new(FormKey.Null, "", null, plugin,
            Array.Empty<UnboundProperty>(), Array.Empty<NullObjectProperty>(),
            Array.Empty<ScriptUnverifiable>(), error);
}

/// <summary>Collects reports, uncapped totals, and completeness state for a script-property sweep.</summary>
/// <param name="Reports">Records with findings plus plugin scan errors; clean records are omitted.</param>
/// <param name="PluginsScanned">Plugin count.</param>
/// <param name="RecordsWithScripts">Records carrying at least one script attachment.</param>
/// <param name="TotalUnbound">Uncapped unbound-property count.</param>
/// <param name="TotalNullObject">Uncapped explicit-null object-property count.</param>
/// <param name="TotalUnverifiable">Unverifiable attachment or chain count.</param>
/// <param name="Capped">Whether property detail exceeded the requested limit.</param>
/// <param name="ReadIncomplete">Whether an unreadable archive weakens negative asset findings.</param>
/// <param name="ExcludedPlugins">Plugins omitted by the active index and their reasons.</param>
/// <param name="Error">Scope validation error, or null after a scan.</param>
public sealed record ScriptCheckResult(
    IReadOnlyList<RecordScriptFindings> Reports,
    int PluginsScanned,
    int RecordsWithScripts,
    int TotalUnbound,
    int TotalNullObject,
    int TotalUnverifiable,
    bool Capped,
    bool ReadIncomplete,
    IReadOnlyDictionary<string, string> ExcludedPlugins,
    string? Error)
{
    /// <summary>Gets whether scope validation succeeded.</summary>
    public bool Success => Error is null;

    /// <summary>Creates an empty result for a scope validation error.</summary>
    /// <param name="error">Specific scope error.</param>
    /// <returns>A failed result with zero totals and no reports.</returns>
    public static ScriptCheckResult Fail(string error) =>
        new(Array.Empty<RecordScriptFindings>(), 0, 0, 0, 0, 0, false, false,
            new Dictionary<string, string>(), error);
}
