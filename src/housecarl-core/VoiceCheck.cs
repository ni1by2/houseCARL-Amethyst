using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>Reports expected voice files for one dialogue response.</summary>
/// <param name="Info">Owning INFO FormKey.</param>
/// <param name="TopicEditorId">Parent topic EditorID used in the filename.</param>
/// <param name="ResponseNumber">Response number embedded in the filename.</param>
/// <param name="FuzPath">Expected spoken-audio path.</param>
/// <param name="FuzPresent">Whether an active provider supplies the FUZ file.</param>
/// <param name="FuzWinner">Winning FUZ provider, or null when absent.</param>
/// <param name="FuzAmbiguous">Whether multiple providers contain the FUZ path.</param>
/// <param name="LipPath">Expected lip-sync path.</param>
/// <param name="LipPresent">Whether an active provider supplies the LIP file.</param>
/// <param name="ReadIncomplete">Whether unreadable archives make absence inconclusive.</param>
public sealed record VoiceLine(
    FormKey Info,
    string TopicEditorId,
    int ResponseNumber,
    string FuzPath,
    bool FuzPresent,
    string? FuzWinner,
    bool FuzAmbiguous,
    string LipPath,
    bool LipPresent,
    bool ReadIncomplete);

/// <summary>Reports one INFO whose voice path cannot be derived statically.</summary>
/// <param name="Info">INFO FormKey.</param>
/// <param name="TopicEditorId">Parent topic EditorID when known.</param>
/// <param name="Reason">Named missing or runtime-assigned graph dependency.</param>
public sealed record VoiceUndetermined(FormKey Info, string TopicEditorId, string Reason);

/// <summary>Collects voice presence and undetermined-path results for one create call.</summary>
/// <param name="Lines">Per-response file checks.</param>
/// <param name="Undetermined">INFOs whose voice path could not be computed.</param>
public sealed record VoiceReport(IReadOnlyList<VoiceLine> Lines, IReadOnlyList<VoiceUndetermined> Undetermined)
{
    /// <summary>Gets the post-write verification error, or null when the check ran.</summary>
    public string? CheckError { get; init; }

    /// <summary>Gets whether the call produced neither findings nor a check error.</summary>
    public bool IsEmpty => Lines.Count == 0 && Undetermined.Count == 0 && CheckError is null;

    /// <summary>Reusable clean result for calls that created no INFO records.</summary>
    public static readonly VoiceReport Empty = new(Array.Empty<VoiceLine>(), Array.Empty<VoiceUndetermined>());
}

/// <summary>Checks whether newly written dialogue lines have active FUZ and LIP files.</summary>
/// <remarks>
/// This post-write diagnostic derives paths from the written topic, quest, speaker, and voice-type graph. Same-call
/// records resolve from the patch before active load-order winners. Asset checks use one pinned snapshot so presence
/// and incomplete-read state are consistent. No write behavior is involved.
/// </remarks>
public static class VoiceCheck
{
    /// <summary>The catalog name (RecordNaming.StripGetterInterface of IDialogResponsesGetter) the create flow
    /// stamps on a created INFO — the filter for "which created records are dialogue lines".</summary>
    public const string InfoCatalogName = "DialogResponses";

    /// <summary>Runs voice presence checks for the INFOs created by one write call.</summary>
    /// <param name="patchPath">Native path to the just-written patch.</param>
    /// <param name="created">Records created by that call.</param>
    /// <param name="resolver">Active record resolver for pre-existing graph dependencies.</param>
    /// <param name="assets">Active asset resolver for loose and BSA presence.</param>
    /// <returns>A complete report; whole-check failures are captured in <see cref="VoiceReport.CheckError"/>.</returns>
    public static VoiceReport Run(string patchPath, IReadOnlyList<WritePatchBuilder.CreatedRecord> created,
                                  LoadOrderResolver resolver, AssetResolver assets)
    {
        // Which created records are dialogue lines (INFOs) — only these get a voice check.
        var infoKeys = new HashSet<FormKey>();
        foreach (var c in created)
            if (string.Equals(c.RecordType, InfoCatalogName, StringComparison.Ordinal))
                infoKeys.Add(c.FormKey);
        if (infoKeys.Count == 0) return VoiceReport.Empty;

        ISkyrimModGetter? patch = null;
        try
        {
            patch = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            return RunOver(patch, infoKeys, resolver, assets);
        }
        catch (Exception ex)
        {
            return VoiceReport.Empty with { CheckError = $"{ex.GetType().Name}: {ex.Message}" };
        }
        finally { (patch as IDisposable)?.Dispose(); }
    }

    /// <summary>Walks created INFOs in an already-open written patch.</summary>
    /// <param name="writtenPatch">Read-only patch overlay owned by the caller.</param>
    /// <param name="infoKeys">Created INFO keys to inspect.</param>
    /// <param name="resolver">Active record resolver.</param>
    /// <param name="assets">Active asset resolver.</param>
    /// <returns>Per-line and undetermined-path findings.</returns>
    static VoiceReport RunOver(ISkyrimModGetter writtenPatch, HashSet<FormKey> infoKeys,
                               LoadOrderResolver resolver, AssetResolver assets)
    {
        var lines = new List<VoiceLine>();
        var undetermined = new List<VoiceUndetermined>();

        // Same-call record lookup off the patch (an NPC / quest / topic created in THIS call lives here, not the
        // load order). One pass; FormKeys are unique within a mod.
        var patchByKey = new Dictionary<FormKey, IMajorRecordGetter>();
        foreach (var rec in writtenPatch.EnumerateMajorRecords())
            patchByKey[rec.FormKey] = rec;

        using var session = resolver.OpenSession();
        var view = resolver.Capture();                       // pin ONE index build for every resolve in this run
        var av = assets.Capture();                           // …and ONE asset build, so presence + ReadIncomplete agree
        var loCache = new Dictionary<FormKey, IMajorRecordGetter?>();

        // Resolve a FormKey to its record getter: the patch first (same-call records), else the load-order winner
        // (existing records). Cached so a bulk_create sharing a speaker doesn't re-enumerate a master per line.
        IMajorRecordGetter? Resolve(FormKey fk)
        {
            if (patchByKey.TryGetValue(fk, out var p)) return p;
            if (loCache.TryGetValue(fk, out var c)) return c;
            IMajorRecordGetter? g = view.ResolveWinner(fk) is { } w
                ? view.GetRecord(session, w.WinnerPlugin, fk)
                : null;
            loCache[fk] = g;
            return g;
        }

        // Walk the patch's topics; each created INFO is in exactly one topic's Responses (its structural parent).
        var foundInfos = new HashSet<FormKey>();
        foreach (var topic in writtenPatch.DialogTopics)
        {
            foreach (var info in topic.Responses)
            {
                if (!infoKeys.Contains(info.FormKey)) continue;   // a pre-existing INFO the patch carried, or not ours
                foundInfos.Add(info.FormKey);
                CheckInfo(info, topic, Resolve, av, lines, undetermined);
            }
        }

        // A created INFO outside every topic is a structural inconsistency and must remain visible.
        foreach (var fk in infoKeys)
            if (!foundInfos.Contains(fk))
                undetermined.Add(new VoiceUndetermined(fk, "",
                    "created but not found under any topic in the written patch — cannot determine its voice path; " +
                    "inspect the patch in xEdit."));

        return new VoiceReport(lines, undetermined);
    }

    /// <summary>Resolves one INFO's graph and appends either per-response checks or one undetermined reason.</summary>
    /// <param name="info">Dialogue response record.</param>
    /// <param name="topic">Structural parent supplying topic and quest context.</param>
    /// <param name="resolve">Patch-first record resolver.</param>
    /// <param name="av">Pinned asset view.</param>
    /// <param name="lines">Per-response result collector.</param>
    /// <param name="undetermined">Collector for paths that cannot be derived statically.</param>
    /// <remarks>
    /// This method is internal because <see cref="DialogueValidate"/> reuses the exact same voice semantics.
    /// A link/branch INFO with no own response and no shared response data produces no finding.
    /// </remarks>
    internal static void CheckInfo(
        IDialogResponsesGetter info,
        IDialogTopicGetter topic,
        Func<FormKey, IMajorRecordGetter?> resolve,
        AssetResolver.AssetView av,
        List<VoiceLine> lines,
        List<VoiceUndetermined> undetermined)
    {
        var topicEdid = topic.EditorID ?? "";

        // No own response lines: either a link/branch node (no spoken audio — skip silently) or one that BORROWS
        // another INFO's audio via ResponseData (it IS voiced, but under the OTHER INFO's path, not computable here).
        // Shared response data is voiced under another INFO's path, so report it instead of claiming no voice.
        if (info.Responses.Count == 0)
        {
            var sharedFk = NonNull(info.ResponseData.FormKeyNullable);
            if (sharedFk is { } sfk)
                undetermined.Add(new VoiceUndetermined(info.FormKey, topicEdid,
                    $"no own response lines — audio comes from shared response data ({sfk}); " +
                    "voice is not checked here, so verify that INFO's .fuz."));
            return;   // ResponseData null ⇒ a genuine link/branch node: no spoken audio to check
        }

        // Null Speaker means the quest alias assigns the voice type at runtime.
        var speakerFk = NonNull(info.Speaker.FormKeyNullable);
        if (speakerFk is null)
        {
            undetermined.Add(new VoiceUndetermined(info.FormKey, topicEdid,
                "no Speaker set — the voice type is assigned at runtime from the quest alias, so the .fuz path " +
                "cannot be computed. " +
                "Set Speaker on this line to make it checkable, or verify the audio yourself."));
            return;
        }
        // Distinguish a missing record from a record of the wrong type:
        // FormKey resolves to nothing, vs it resolves to a record that isn't an NPC (Speaker is typed as a FormLink to
        // an NPC, but real/odd data can point it elsewhere, and the voice type is derived only from an NPC's Voice).
        var speaker = resolve(speakerFk.Value);
        if (speaker is null)
        {
            undetermined.Add(new VoiceUndetermined(info.FormKey, topicEdid,
                $"Speaker {speakerFk.Value} not found in the patch or load order — can't resolve the voice type."));
            return;
        }
        if (speaker is not INpcGetter npc)
        {
            undetermined.Add(new VoiceUndetermined(info.FormKey, topicEdid,
                $"Speaker {speakerFk.Value} resolves to a non-NPC record — the voice type comes from an NPC's " +
                "Voice field, so this path cannot be computed; verify the audio yourself."));
            return;
        }
        var voiceFk = NonNull(npc.Voice.FormKeyNullable);
        if (voiceFk is null)
        {
            undetermined.Add(new VoiceUndetermined(info.FormKey, topicEdid,
                $"Speaker NPC {speakerFk.Value} has no Voice type set — can't compute the voice folder."));
            return;
        }
        var voiceType = (resolve(voiceFk.Value) as IVoiceTypeGetter)?.EditorID;
        if (string.IsNullOrEmpty(voiceType))
        {
            undetermined.Add(new VoiceUndetermined(info.FormKey, topicEdid,
                $"the speaker's Voice type {voiceFk.Value} has no resolvable EditorID — can't name the voice folder."));
            return;
        }

        // Quest EDID — the topic's quest's EditorID (empty when the topic has no quest; that's a real on-disk shape).
        var questFk = NonNull(topic.Quest.FormKeyNullable);
        var questEdid = questFk is { } qfk ? (resolve(qfk) as IQuestGetter)?.EditorID ?? "" : "";

        // Check one FUZ/LIP pair per authored response number.
        foreach (var resp in info.Responses)
        {
            int num = resp.ResponseNumber;
            var fuz = VoicePath.For(info.FormKey, voiceType, questEdid, topicEdid, num, VoiceFile.Fuz);
            var lip = VoicePath.For(info.FormKey, voiceType, questEdid, topicEdid, num, VoiceFile.Lip);
            var fhit = av.Resolve(fuz);
            var lhit = av.Resolve(lip);
            lines.Add(new VoiceLine(
                info.FormKey, topicEdid, num,
                fuz, fhit.Exists, fhit.Winner?.Source, fhit.Ambiguous,
                lip, lhit.Exists,
                av.ReadIncomplete));
        }
    }

    /// <summary>Normalizes an unset or explicit null FormKey to null.</summary>
    /// <param name="fk">Nullable link target.</param>
    /// <returns>A non-null, nonzero FormKey or null.</returns>
    static FormKey? NonNull(FormKey? fk) => fk is { } v && !v.IsNull ? v : null;
}
