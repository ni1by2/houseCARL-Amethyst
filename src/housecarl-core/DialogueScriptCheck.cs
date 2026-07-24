using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>Classifies the result-script binding on one dialogue response.</summary>
public enum ScriptBindingStatus
{
    /// <summary>A fragment or attached script is bound and every class has a compiled PEX.</summary>
    BoundAndCompiled,
    /// <summary>A VMAD is present but binds no usable fragment/script (no real Begin/End fragment, no named attached
    /// script) — byte-valid, runs NOTHING.</summary>
    BindingIncomplete,
    /// <summary>A script class is bound but its compiled `Scripts\&lt;class&gt;.pex` is absent on disk — runs NOTHING
    /// until compiled.</summary>
    ScriptNotCompiled,
    /// <summary>The created INFO could not be located in the written patch.</summary>
    Undetermined,
}

/// <summary>Reports result-script binding and compiled-file coverage for one INFO.</summary>
/// <param name="Info">INFO FormKey.</param>
/// <param name="TopicEditorId">Structural parent topic EditorID.</param>
/// <param name="Status">Binding classification.</param>
/// <param name="Scripts">Distinct bound Papyrus class names.</param>
/// <param name="MissingPex">Expected compiled paths absent from the active asset index.</param>
/// <param name="ReadIncomplete">Whether unreadable archives make missing results inconclusive.</param>
/// <param name="Detail">Human-readable verdict and remedy.</param>
public sealed record ScriptBindingFinding(
    FormKey Info,
    string TopicEditorId,
    ScriptBindingStatus Status,
    IReadOnlyList<string> Scripts,
    IReadOnlyList<string> MissingPex,
    bool ReadIncomplete,
    string Detail)
{
    /// <summary>
    /// Gets whether a real Begin/End result fragment exists, excluding attached-script-only bindings.
    /// </summary>
    public bool HasFragment { get; init; }
}

/// <summary>Collects per-INFO result-script findings for one create call.</summary>
/// <param name="Findings">Findings for created INFOs that carry a VMAD, plus structural lookup failures.</param>
public sealed record ScriptBindingReport(IReadOnlyList<ScriptBindingFinding> Findings)
{
    /// <summary>Gets a whole-check post-write error, or null when the check ran.</summary>
    public string? CheckError { get; init; }

    /// <summary>Gets whether the call produced neither findings nor a check error.</summary>
    public bool IsEmpty => Findings.Count == 0 && CheckError is null;

    /// <summary>Reusable clean result for calls that created no INFO records.</summary>
    public static readonly ScriptBindingReport Empty = new(Array.Empty<ScriptBindingFinding>());
}

/// <summary>Validates Papyrus result-script bindings on newly written dialogue lines.</summary>
/// <remarks>
/// VMAD structure is read entirely from the written INFO. Each bound class is then checked against one pinned asset
/// snapshot for <c>Scripts\class.pex</c>. This is a post-write diagnostic and never changes record creation.
/// </remarks>
public static class DialogueScriptCheck
{
    /// <summary>Runs result-script checks for INFOs created by one write call.</summary>
    /// <param name="patchPath">Native path to the just-written patch.</param>
    /// <param name="created">Records created by that call.</param>
    /// <param name="assets">Active asset resolver used to locate compiled PEX files.</param>
    /// <returns>A complete report; whole-check failures are captured instead of thrown.</returns>
    public static ScriptBindingReport Run(string patchPath, IReadOnlyList<WritePatchBuilder.CreatedRecord> created,
                                          AssetResolver assets)
    {
        // Which created records are dialogue lines (INFOs) — only these get a script-binding check.
        var infoKeys = new HashSet<FormKey>();
        foreach (var c in created)
            if (string.Equals(c.RecordType, VoiceCheck.InfoCatalogName, StringComparison.Ordinal))
                infoKeys.Add(c.FormKey);
        if (infoKeys.Count == 0) return ScriptBindingReport.Empty;

        ISkyrimModGetter? patch = null;
        try
        {
            patch = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            return RunOver(patch, infoKeys, assets);
        }
        catch (Exception ex)
        {
            return ScriptBindingReport.Empty with { CheckError = $"{ex.GetType().Name}: {ex.Message}" };
        }
        finally { (patch as IDisposable)?.Dispose(); }
    }

    /// <summary>Walks created INFOs in an already-open written patch.</summary>
    /// <param name="writtenPatch">Read-only patch overlay owned by the caller.</param>
    /// <param name="infoKeys">Created INFO keys to inspect.</param>
    /// <param name="assets">Active asset resolver.</param>
    /// <returns>Per-INFO binding findings.</returns>
    static ScriptBindingReport RunOver(ISkyrimModGetter writtenPatch, HashSet<FormKey> infoKeys, AssetResolver assets)
    {
        var findings = new List<ScriptBindingFinding>();
        var av = assets.Capture();                           // ONE asset build, so presence + ReadIncomplete agree

        var found = new HashSet<FormKey>();
        foreach (var topic in writtenPatch.DialogTopics)
        {
            foreach (var info in topic.Responses)
            {
                if (!infoKeys.Contains(info.FormKey)) continue;   // a pre-existing INFO the patch carried, or not ours
                found.Add(info.FormKey);
                CheckInfo(info, topic.EditorID ?? "", av, findings);
            }
        }

        // A created INFO outside every topic is a structural inconsistency and must remain visible.
        foreach (var fk in infoKeys)
            if (!found.Contains(fk))
                findings.Add(new ScriptBindingFinding(fk, "", ScriptBindingStatus.Undetermined,
                    Array.Empty<string>(), Array.Empty<string>(), false,
                    "created but not found under any topic in the written patch — cannot check its result-script " +
                    "binding; inspect the patch in xEdit."));

        return new ScriptBindingReport(findings);
    }

    /// <summary>Appends the script-binding verdict for one INFO when it carries a VMAD.</summary>
    /// <param name="info">Dialogue response record.</param>
    /// <param name="topicEdid">Structural parent topic EditorID.</param>
    /// <param name="av">Pinned asset view used for PEX checks.</param>
    /// <param name="findings">Result collector.</param>
    /// <remarks>
    /// A line without a VMAD intends no result script and produces no finding. Real fragment filenames and named
    /// attached scripts each require a compiled PEX. <see cref="DialogueValidate"/> reuses this method.
    /// </remarks>
    internal static void CheckInfo(
        IDialogResponsesGetter info,
        string topicEdid,
        AssetResolver.AssetView av,
        List<ScriptBindingFinding> findings)
    {
        var vmad = info.VirtualMachineAdapter;
        if (vmad is null) return;   // no result script intended — nothing to check

        // A real result fragment can surface in Papyrus.log; attached scripts alone do not set this flag.
        // the single fragment-presence home so the per-finding HasFragment and the validator's per-topic tally
        // (DialogueValidate.FragmentInfoCount) can never drift (item 8).
        bool hasFragment = HasResultFragment(info);

        // The bound script CLASS names that must each have a compiled .pex to actually fire.
        var names = new List<string>();
        var frag = vmad.ScriptFragments;
        if (hasFragment) names.Add(frag!.FileName!.Trim());
        foreach (var entry in vmad.Scripts)
            if (!string.IsNullOrWhiteSpace(entry.Name)) names.Add(entry.Name.Trim());

        // A VMAD that binds nothing usable (no real fragment, no named attached script) is byte-valid but inert — a
        // A FileName without a Begin/End fragment is a hollow declaration, not a binding.
        if (names.Count == 0)
        {
            findings.Add(new ScriptBindingFinding(info.FormKey, topicEdid, ScriptBindingStatus.BindingIncomplete,
                Array.Empty<string>(), Array.Empty<string>(), false,
                "a script adapter (VMAD) is present but binds no usable result-script fragment or attached script — " +
                "byte-valid but it runs NOTHING. Wire a FileName and a Begin/End fragment " +
                "or remove the empty adapter."));
            return;
        }

        // Each bound class needs Scripts\<class>.pex on disk to fire. Distinct (case-insensitive) so two contributors
        // naming the same class don't double-report.
        var distinct = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var missing = new List<string>();
        foreach (var name in distinct)
        {
            // Papyrus namespaces map ':' to subdirectories. Generated TIF/QF fragment classes remain flat.
            var relPex = $@"Scripts\{name.Replace(':', '\\')}.pex";
            if (!av.Resolve(relPex).Exists) missing.Add(relPex);
        }

        if (missing.Count == 0)
            findings.Add(new ScriptBindingFinding(info.FormKey, topicEdid, ScriptBindingStatus.BoundAndCompiled,
                distinct, Array.Empty<string>(), av.ReadIncomplete,
                $"result script bound + compiled ({string.Join(", ", distinct)}).") { HasFragment = hasFragment });
        else
            findings.Add(new ScriptBindingFinding(info.FormKey, topicEdid, ScriptBindingStatus.ScriptNotCompiled,
                distinct, missing, av.ReadIncomplete,
                $"result script bound ({string.Join(", ", distinct)}) but the compiled .pex is missing on disk — " +
                "it runs NOTHING until compiled (housecarl_compile_script).") { HasFragment = hasFragment });
    }

    /// <summary>Tests whether an INFO carries a named real Begin or End result fragment.</summary>
    /// <param name="info">Dialogue response to inspect.</param>
    /// <returns>True only when a fragment filename and real Begin/End member are both present.</returns>
    internal static bool HasResultFragment(IDialogResponsesGetter info)
    {
        var frag = info.VirtualMachineAdapter?.ScriptFragments;
        return frag is not null && !string.IsNullOrWhiteSpace(frag.FileName) && HasRealFragment(frag);
    }

    /// <summary>Tests whether a fragment container has a real Begin or End entry.</summary>
    /// <param name="frag">Fragment container.</param>
    /// <returns>True when either entry names code.</returns>
    static bool HasRealFragment(IScriptFragmentsGetter frag)
        => IsReal(frag.OnBegin) || IsReal(frag.OnEnd);

    /// <summary>Tests whether one optional fragment entry names a script or generated fragment.</summary>
    /// <param name="f">Optional fragment entry.</param>
    /// <returns>True when either name is non-empty.</returns>
    static bool IsReal(IScriptFragmentGetter? f)
        => f is not null && (!string.IsNullOrWhiteSpace(f.ScriptName) || !string.IsNullOrWhiteSpace(f.FragmentName));
}
