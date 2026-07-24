using System.Reflection;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>Classifies a dialogue validation finding.</summary>
public enum DialogueIssueSeverity
{
    /// <summary>A broken relationship that the game cannot honor.</summary>
    Problem,
    /// <summary>A suspicious or tool-incompatible shape that needs author review.</summary>
    Warning,
}

/// <summary>Reports one actionable dialogue validation finding.</summary>
/// <param name="Severity">Finding severity.</param>
/// <param name="Message">Specific problem description and remediation guidance.</param>
public sealed record DialogueIssue(DialogueIssueSeverity Severity, string Message);

/// <summary>Collects structural, voice, and script findings for one winning topic.</summary>
/// <param name="Topic">Topic FormKey.</param>
/// <param name="TopicEditorId">Topic EditorID.</param>
/// <param name="WinnerPlugin">Plugin providing the winning topic.</param>
/// <param name="InfoCount">Live INFO record count, not spoken-row count.</param>
/// <param name="ConditionedInfoCount">Live INFO count with at least one condition.</param>
/// <param name="DeletedInfoCount">Deleted INFO records intentionally skipped.</param>
/// <param name="FragmentInfoCount">Live INFO records with a Papyrus result fragment.</param>
/// <param name="Category">Resolved topic category.</param>
/// <param name="Subtype">Resolved numeric subtype name.</param>
/// <param name="SubtypeName">Resolved four-character SNAM marker.</param>
/// <param name="Issues">Structural and parity findings.</param>
/// <param name="VoiceLines">Determinate voice-asset findings.</param>
/// <param name="VoiceUndetermined">Voice checks that could not be completed.</param>
/// <param name="ScriptFindings">Result-script binding findings.</param>
public sealed record TopicValidation(
    FormKey Topic, string TopicEditorId, string WinnerPlugin,
    int InfoCount, int ConditionedInfoCount, int DeletedInfoCount, int FragmentInfoCount,
    string Category, string Subtype, string SubtypeName,
    IReadOnlyList<DialogueIssue> Issues,
    IReadOnlyList<VoiceLine> VoiceLines,
    IReadOnlyList<VoiceUndetermined> VoiceUndetermined,
    IReadOnlyList<ScriptBindingFinding> ScriptFindings);

/// <summary>Reports SEQ coverage and freshness for a start-game-enabled quest.</summary>
/// <param name="QuestIsSge">Whether the quest requires SEQ coverage.</param>
/// <param name="DefiningPlugin">Plugin that owns the quest FormKey.</param>
/// <param name="WinnerPlugin">Plugin providing the active quest record.</param>
/// <param name="OnDiskFormId">Master-relative FormID stored in SEQ, or zero when unavailable.</param>
/// <param name="SeqExists">Whether a winning SEQ asset exists.</param>
/// <param name="SeqContainsQuest">Whether SEQ lists the quest, or null when unreadable.</param>
/// <param name="SeqNewerThanPlugin">Whether loose SEQ is at least as new as the plugin, or null when unknown.</param>
/// <param name="Note">Reason a result could not be determined.</param>
public sealed record SeqLintFinding(
    bool QuestIsSge, string DefiningPlugin, string WinnerPlugin, uint OnDiskFormId,
    bool SeqExists, bool? SeqContainsQuest, bool? SeqNewerThanPlugin, string? Note);

/// <summary>Collects the result of validating one DIAL, QUST, DLVW, or DLBR input.</summary>
/// <param name="Input">Requested FormKey.</param>
/// <param name="InputKind">Resolved record kind, or <c>error</c>.</param>
/// <param name="InputEditorId">Resolved input EditorID.</param>
/// <param name="InputWinnerPlugin">Plugin providing the active input record.</param>
/// <param name="Topics">Topic validations; empty for view, branch, and error inputs.</param>
public sealed record DialogueValidationReport(
    FormKey Input, string InputKind, string? InputEditorId, string? InputWinnerPlugin,
    IReadOnlyList<TopicValidation> Topics)
{
    /// <summary>Gets an expected input or resolution error.</summary>
    public string? Error { get; init; }

    /// <summary>Gets an unexpected validation execution error.</summary>
    public string? CheckError { get; init; }

    /// <summary>Gets whether unreadable archives make negative asset findings inconclusive.</summary>
    public bool ReadIncomplete { get; init; }

    /// <summary>Gets parity findings that belong to the input record rather than an individual topic.</summary>
    public IReadOnlyList<DialogueIssue> InputIssues { get; init; } = Array.Empty<DialogueIssue>();

    /// <summary>Gets the SEQ result for a start-game-enabled quest, or null when SEQ is not required.</summary>
    public SeqLintFinding? SeqLint { get; init; }

    /// <summary>Creates a report for an expected input or resolution error.</summary>
    /// <param name="fk">Requested FormKey.</param>
    /// <param name="error">Actionable error text.</param>
    /// <returns>An error report with no topic results.</returns>
    public static DialogueValidationReport ForError(FormKey fk, string error) =>
        new(fk, "error", null, null, Array.Empty<TopicValidation>()) { Error = error };

    /// <summary>Creates a report for an unexpected validation failure.</summary>
    /// <param name="fk">Requested FormKey.</param>
    /// <param name="err">Exception description.</param>
    /// <returns>A check-error report with no topic results.</returns>
    public static DialogueValidationReport ForCheckError(FormKey fk, string err) =>
        new(fk, "error", null, null, Array.Empty<TopicValidation>()) { CheckError = err };
}

/// <summary>Validates the active dialogue graph and its supporting assets.</summary>
/// <remarks>
/// Validation uses one load-order session and one asset snapshot. It checks only winning topic contents, which are
/// the INFO records visible to the game. It reports structural defects but cannot evaluate condition truth, spoken
/// audio quality, or lip synchronization.
/// </remarks>
public static class DialogueValidate
{
    /// <summary>Type filter used when a quest validation scans winning DIAL records.</summary>
    static readonly Type[] DialTypes = { typeof(IDialogTopicGetter) };

    /// <summary>Converts a Creation Kit parity gap into a validator warning.</summary>
    /// <param name="noun">Human-readable record type.</param>
    /// <param name="fk">Affected record FormKey.</param>
    /// <param name="gap">Missing subrecord description.</param>
    /// <returns>A consistently formatted warning.</returns>
    static DialogueIssue GapIssue(string noun, FormKey fk, CkParityGap gap) =>
        new(DialogueIssueSeverity.Warning, $"{noun} {fk} is missing the {gap.Subrecord} subrecord — {gap.Detail}");

    /// <summary>Resolves an active dialogue record and validates its applicable scope.</summary>
    /// <param name="resolver">Active plugin and record resolver.</param>
    /// <param name="assets">Active loose and archive asset resolver.</param>
    /// <param name="fk">DIAL, QUST, DLVW, or DLBR FormKey.</param>
    /// <returns>A report that captures both expected errors and unexpected check failures.</returns>
    public static DialogueValidationReport Run(LoadOrderResolver resolver, AssetResolver assets, FormKey fk)
    {
        try
        {
            var view = resolver.Capture();                       // pin ONE index build for the whole validation
            using var session = resolver.OpenSession();          // one set of overlays, disposed at run end (Option B)
            var av = assets.Capture();                           // Pin asset presence and incomplete-read state.

            // Load-order winner resolver for each INFO's Speaker → NPC → VoiceType and the topic's Quest. Cached for
            // the run so a topic full of lines sharing a speaker doesn't re-enumerate a master per line.
            var loCache = new Dictionary<FormKey, IMajorRecordGetter?>();
            IMajorRecordGetter? Resolve(FormKey k)
            {
                if (k.IsNull) return null;
                if (loCache.TryGetValue(k, out var c)) return c;
                IMajorRecordGetter? g = view.ResolveWinner(k) is { } w
                    ? view.GetRecord(session, w.WinnerPlugin, k)
                    : null;
                loCache[k] = g;
                return g;
            }

            // Cheap O(1) existence check (the index dict, no body fetch): a dangling/missing reference — the common
            // breakage — is caught here, so only a PRESENT link pays Resolve's body fetch (to name a wrong type). See
            // ValidateTopic.BadRef.
            bool InOrder(FormKey k) => !k.IsNull && view.ResolveWinner(k) is not null;

            var win = view.ResolveWinner(fk);
            if (win is null)
                return DialogueValidationReport.ForError(fk,
                    $"{fk} is not in the active load order — nothing to validate. Pass a dialogue topic (DIAL) " +
                    "FormID to validate one topic, a quest (QUST) FormID to validate all of its topics, or a " +
                    "dialogue view (DLVW) / branch (DLBR) FormID for a record-level CK-parity check.");

            var body = view.GetRecord(session, win.Value.WinnerPlugin, fk);
            if (body is null)
                return DialogueValidationReport.ForError(fk,
                    $"{fk} resolves to a winner in {win.Value.WinnerPlugin} but its body could not be fetched. " +
                    "The plugin may have changed since the index was built; re-run to rebuild and try again.");

            if (body is IDialogTopicGetter topic)
            {
                var tv = ValidateTopic(topic, win.Value.WinnerPlugin, InOrder, Resolve, av);
                return new DialogueValidationReport(
                    fk,
                    "topic",
                    topic.EditorID ?? "",
                    win.Value.WinnerPlugin,
                    new[] { tv })
                    { ReadIncomplete = av.ReadIncomplete };
            }

            if (body is IQuestGetter quest)
            {
                // Fan out: a topic points UP at its quest, so scan every winning DialogTopic in the order and keep
                // the ones whose Quest is this quest. A whole-order DIAL winner scan (accuracy over perf — an
                // on-demand validate, not a hot path). Each scanned body is FULLY walked by ValidateTopic before the
                // scan iterator advances (and disposes that overlay) — the WinnerRecordsOfType consume-before-advance
                // contract.
                // SEQ staleness/coverage lint (item 7): keyed on the QUEST input, independent of its topics — a
                // start-game-enabled quest needs a .seq that lists it, or it (and all its dialogue) stays dormant.
                var seqLint = CheckSeq(view, av, fk, quest, win.Value.WinnerPlugin);

                var topics = new List<TopicValidation>();
                foreach (var (tfk, _, tbody) in view.WinnerRecordsOfType(DialTypes))
                {
                    if (tbody is not IDialogTopicGetter dt) continue;
                    if (NonNull(dt.Quest.FormKeyNullable) is not { } qk || qk != fk) continue;
                    var wp = view.ResolveWinner(tfk)?.WinnerPlugin ?? win.Value.WinnerPlugin;
                    topics.Add(ValidateTopic(dt, wp, InOrder, Resolve, av));
                }

                // Quest parity gaps belong to the input and are reported once, not repeated for every owned topic.
                // Presence predicates are shared with the fill path. Existing ANAM values are not judged.
                var questGaps = DialogueCkParity.MissingQuestDefaults(quest)
                    .Select(g => GapIssue("Quest", fk, g)).ToList();

                return new DialogueValidationReport(fk, "quest", quest.EditorID ?? "", win.Value.WinnerPlugin, topics)
                    { ReadIncomplete = av.ReadIncomplete, SeqLint = seqLint, InputIssues = questGaps };
            }

            // DLVW and DLBR carry no INFO list. Validate their own parity fields and leave Topics empty.
            if (body is IDialogViewGetter dlvw)
            {
                var gaps = DialogueCkParity.MissingViewDefaults(dlvw)
                    .Select(g => GapIssue("DialogView", fk, g)).ToList();
                return new DialogueValidationReport(fk, "view", dlvw.EditorID ?? "", win.Value.WinnerPlugin,
                    Array.Empty<TopicValidation>()) { InputIssues = gaps };
            }

            if (body is IDialogBranchGetter dlbr)
            {
                var gaps = DialogueCkParity.MissingBranchDefaults(dlbr)
                    .Select(g => GapIssue("DialogBranch", fk, g)).ToList();
                return new DialogueValidationReport(fk, "branch", dlbr.EditorID ?? "", win.Value.WinnerPlugin,
                    Array.Empty<TopicValidation>()) { InputIssues = gaps };
            }

            return DialogueValidationReport.ForError(fk,
                $"{fk} resolves to a {RecordNaming.StripOverlay(body.GetType().Name)} in " +
                $"{win.Value.WinnerPlugin}, not a dialogue topic (DIAL), quest (QUST), dialogue view (DLVW), " +
                "or dialogue branch (DLBR). Pass one of those record types.");
        }
        catch (Exception ex)
        {
            return DialogueValidationReport.ForCheckError(fk, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Validates one already-resolved winning topic and all of its live INFO records.</summary>
    /// <param name="topic">Winning topic body.</param>
    /// <param name="winnerPlugin">Plugin that supplies the body.</param>
    /// <param name="inOrder">Cheap active FormKey existence test.</param>
    /// <param name="resolve">Active winner body resolver.</param>
    /// <param name="assetView">Pinned asset snapshot.</param>
    /// <returns>Structural, voice, and script results for the topic.</returns>
    /// <remarks>
    /// Deleted INFO records are counted but skipped. Empty PNAM is normal; only a set but invalid previous link is
    /// reported. LinkTo is the topic-to-topic conversation handoff and is checked independently.
    /// </remarks>
    internal static TopicValidation ValidateTopic(IDialogTopicGetter topic, string winnerPlugin,
        Func<FormKey, bool> inOrder, Func<FormKey, IMajorRecordGetter?> resolve, AssetResolver.AssetView assetView)
    {
        var edid = topic.EditorID ?? "";
        var issues = new List<DialogueIssue>();
        var voiceLines = new List<VoiceLine>();
        var voiceUndet = new List<VoiceUndetermined>();
        var scriptFindings = new List<ScriptBindingFinding>();

        // Text-encoding lint (item 1): flag non-ASCII in the player-facing strings — the topic Name (once), each
        // INFO Prompt, and each spoken response Text — because the CK/Papyrus user-facing text surface is
        // effectively Windows-1252/ASCII, so an em-dash, ellipsis, or smart quote renders as in-game mojibake.
        // WARN only (a heuristic — HTML/Ultralight UIs render Unicode fine) and REPORT-ONLY (this validator never
        // mutates; it suggests the ASCII substitute, never performs it).
        CheckEncoding(topic.Name?.String, $"DialogTopic.Name ({edid})", issues);

        // Check indexed existence before fetching the body so missing links stay cheap and wrong types stay specific.
        string? BadRef(FormKey target, string expects, Func<IMajorRecordGetter, bool> isExpected)
        {
            if (!inOrder(target)) return $"is not in the active load order ({expects} missing or disabled)";
            var body = resolve(target);
            if (body is not null && isExpected(body)) return null;
            var actual = body is null
                ? "an unreadable record"
                : "a " + RecordNaming.StripOverlay(body.GetType().Name);
            return $"resolves to {actual}, not {expects}";
        }

        // --- Quest wiring: most functional topics are owned by a quest; an unowned one may never present its lines.
        var questFk = NonNull(topic.Quest.FormKeyNullable);
        if (questFk is null)
            issues.Add(new(DialogueIssueSeverity.Warning,
                "DialogTopic.Quest is unset — this topic is not owned by a quest. Most dialogue topics are; " +
                "an unowned topic may never present its lines in game. Verify this is intentional."));
        else if (BadRef(questFk.Value, "a quest (QUST)", b => b is IQuestGetter) is { } qwhy)
            issues.Add(new(DialogueIssueSeverity.Problem,
                $"DialogTopic.Quest points at {questFk.Value}, which {qwhy} — the owning quest is unresolved."));

        // --- Branch wiring: optional (many topics have none), but if set it must resolve to a real DLBR.
        var branchFk = NonNull(topic.Branch.FormKeyNullable);
        if (branchFk is not null &&
            BadRef(branchFk.Value, "a dialogue branch (DLBR)", b => b is IDialogBranchGetter) is { } bwhy)
            issues.Add(new(DialogueIssueSeverity.Problem,
                $"DialogTopic.Branch points at {branchFk.Value}, which {bwhy} — the branch wiring is broken."));

        // A Custom topic without BNAM plays in game but can crash the CK flowchart editor. The owning branch cannot
        // be inferred safely, so validation warns instead of filling it.
        if (topic.Subtype == DialogTopic.SubtypeEnum.Custom && branchFk is null)
            issues.Add(new(DialogueIssueSeverity.Warning,
                "DialogTopic.Branch (BNAM) is unset on this Custom topic — it plays fine in game, but the Creation "
                + "Kit's Dialogue Views editor auto-wraps a branch-less topic in a container branch and then crashes "
                + "rendering the flowchart (FlowchartX64). Set Branch to the owning DialogBranch (DLBR) before opening "
                + "this topic in the CK."));

        // Blank SNAM is a load risk on a newly defined topic. An override may inherit a usable base marker, so it is
        // malformed but receives the lower warning severity.
        if (DialogueSubtype.IsBlankMarker(topic.SubtypeName))
        {
            var expected = DialogueSubtype.MarkerFor((int)topic.Subtype);
            bool isOverride = !string.Equals(
                topic.FormKey.ModKey.FileName.String,
                winnerPlugin,
                StringComparison.OrdinalIgnoreCase);
            var fix = expected is not null
                ? $"Set it to {expected} (the marker for Subtype={topic.Subtype}); houseCARL's create tools " +
                  $"auto-fill it, or use housecarl_set_field SubtypeName={expected}."
                : $"Set the correct 4-char marker for Subtype={topic.Subtype} with housecarl_set_field.";
            issues.Add(isOverride
                ? new(DialogueIssueSeverity.Warning,
                    "DialogTopic.SubtypeName (SNAM) is empty (0000) on this override of " +
                    $"{topic.FormKey.ModKey.FileName}. The base marker may still apply, but the override is " +
                    $"malformed. {fix}")
                : new(DialogueIssueSeverity.Problem,
                    "DialogTopic.SubtypeName (SNAM) is empty (0000). This plugin defines the topic, so the " +
                    $"blank marker can crash the game while loading; the record is malformed. {fix}"));
        }

        // Alias indexes are quest-relative. Skip only those checks when the winning owner quest is unavailable.
        HashSet<uint>? ownerAliasIds = null;
        // Global tags are checked only after the owning quest resolves, so a missing quest never creates false gaps.
        HashSet<string>? ownerTextGlobals = null;
        string ownerQuestLabel = "the owning quest";
        if (questFk is { } ownerFk && resolve(ownerFk) is IQuestGetter ownerQuest)
        {
            ownerAliasIds = ownerQuest.Aliases.Select(a => a.ID).ToHashSet();
            // Text tag names are case-insensitive. Null or unresolved entries cannot cover a tag.
            ownerTextGlobals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in ownerQuest.TextDisplayGlobals)
                if (!g.FormKey.IsNull &&
                    resolve(g.FormKey) is IGlobalGetter glob &&
                    glob.EditorID is { Length: > 0 } gid)
                    ownerTextGlobals.Add(gid);
            ownerQuestLabel = $"the owning quest {ownerQuest.EditorID ?? ownerFk.ToString()}";
        }

        // Deleted INFO records are removed lines: count them separately and run no checks against them.
        int infoCount = 0, conditioned = 0, deleted = 0, fragmentInfos = 0;
        foreach (var info in topic.Responses)
        {
            if (info.IsDeleted) { deleted++; continue; }
            infoCount++;

            // CK-parity subrecords (INFO CNAM/ENAM): a winning INFO missing the FavorLevel (CNAM) or response-Flags
            // (ENAM) subrecord that Mutagen omits when unset is the shape that crashes the Creation Kit when this
            // topic is opened in the dialogue editor (the game itself tolerates it → WARN, matching the BNAM lint).
            // The absence probe is DialogueCkParity's — the SAME one the create path FILLS through — so a create that
            // populates the subrecord and this check that flags its absence can never drift. Real CK/xEdit-authored
            // INFOs always carry both, so this fires only on a bare-authored record; houseCARL's own create tools
            // auto-fill it. (The rest of the CK-parity family lives elsewhere: DLVW DNAM/ENAM and DLBR TNAM are their
            // own input kinds, QUST ANAM/objective FNAM rides the quest input's InputIssues, and DIAL PNAM is the one
            // permanent boundary — a non-nullable float with no "unset" signal. See the header's CANNOT-CHECK note.)
            foreach (var gap in DialogueCkParity.MissingInfoDefaults(info))
                issues.Add(GapIssue("INFO", info.FormKey, gap));

            // Fragment-presence tally (item 8): does this line carry a result-script FRAGMENT (a code path that can
            // surface in Papyrus.log)? Via the single fragment-presence home, so this never drifts from the per-INFO
            // HasFragment the script check sets.
            if (DialogueScriptCheck.HasResultFragment(info)) fragmentInfos++;

            // Text-encoding lint (item 1) — this line's player-facing strings: its menu Prompt and each spoken row.
            CheckEncoding(info.Prompt?.String, $"INFO {info.FormKey} Prompt", issues);
            int rnum = 0;
            foreach (var resp in info.Responses)
                CheckEncoding(resp.Text?.String, $"INFO {info.FormKey} response {++rnum} text", issues);

            // A global tag renders as [...] unless its EditorID is listed by the owning quest.
            if (ownerTextGlobals is not null)
                CheckGlobalTags(info, ownerTextGlobals, ownerQuestLabel, issues);

            // PNAM (PreviousDialog): vanilla leaves it empty and orders intra-topic by Conditions, so ABSENCE is the
            // norm and is NEVER flagged. Only a SET previous-link that doesn't resolve to an INFO is a real defect.
            var pnam = NonNull(info.PreviousDialog.FormKeyNullable);
            if (pnam is not null &&
                BadRef(pnam.Value, "a dialogue line (INFO)", b => b is IDialogResponsesGetter) is { } pwhy)
                issues.Add(new(DialogueIssueSeverity.Problem,
                    $"INFO {info.FormKey} has a previous-link (PNAM -> {pnam.Value}) that {pwhy}."));

            // LinkTo: the REAL conversation chain — this line hands off to the next topic(s). A set link to a missing
            // DialogTopic is a broken chain; an empty LinkTo is a normal terminal line (never flagged).
            foreach (var link in info.LinkTo)
            {
                var lk = link.FormKey;
                if (!lk.IsNull && BadRef(lk, "a dialogue topic (DIAL)", b => b is IDialogTopicGetter) is { } lwhy)
                    issues.Add(new(DialogueIssueSeverity.Problem,
                        $"INFO {info.FormKey} links (LinkTo) to {lk}, which {lwhy} — the conversation " +
                        "chain is broken."));
            }

            if (info.Conditions.Count > 0)
            {
                conditioned++;
                // Static condition (CTDA) well-formedness lints (item 4) — the data-layer-decidable, true-positive
                // subset. They catch MALFORMED conditions; they do NOT (and cannot) evaluate whether a well-formed
                // one passes — that stays the running game's job (the standing CTDA limit, restated in the render).
                CheckConditions(info, ownerAliasIds, ownerQuestLabel, inOrder, resolve, issues);
            }

            // Reuse the per-INFO voice and result-script checks over every live INFO. These are the
            // exact methods the per-create teeth run, so the create path and the validator can never drift.
            VoiceCheck.CheckInfo(info, topic, resolve, assetView, voiceLines, voiceUndet);
            DialogueScriptCheck.CheckInfo(info, edid, assetView, scriptFindings);
        }

        return new TopicValidation(
            topic.FormKey, edid, winnerPlugin, infoCount, conditioned, deleted, fragmentInfos,
            topic.Category.ToString(), topic.Subtype.ToString(), DescribeSubtypeName(topic.SubtypeName),
            issues, voiceLines, voiceUndet, scriptFindings);
    }

    /// <summary>Checks SEQ existence, membership, and freshness for a start-game-enabled quest.</summary>
    /// <param name="view">Pinned plugin index.</param>
    /// <param name="av">Pinned asset snapshot.</param>
    /// <param name="fk">Quest FormKey.</param>
    /// <param name="quest">Winning quest body.</param>
    /// <param name="winnerPlugin">Plugin supplying the winning body.</param>
    /// <returns>Null when SEQ is unnecessary; otherwise a fault-isolated SEQ result.</returns>
    /// <remarks>
    /// Archive-contained SEQ assets have no native file timestamp, so membership and freshness remain unknown.
    /// Override winners are retained in the result so rendering can avoid blaming the defining plugin too strongly.
    /// </remarks>
    internal static SeqLintFinding? CheckSeq(LoadOrderResolver.IndexView view, AssetResolver.AssetView av,
        FormKey fk, IQuestGetter quest, string winnerPlugin)
    {
        if (!quest.Flags.HasFlag(Quest.Flag.StartGameEnabled)) return null;   // not SGE → no .seq needed, no lint
        var defining = fk.ModKey.FileName;
        try
        {
            var pluginPath = view.PluginPath(defining);
            if (pluginPath is null)
                return new SeqLintFinding(true, defining, winnerPlugin, 0, false, null, null,
                    $"could not locate the defining plugin '{defining}' on disk to check its .seq.");

            uint onDisk = SeqFile.OnDiskFormIdFromPlugin(pluginPath, fk);
            var seqRel = $@"SEQ\{Path.GetFileNameWithoutExtension(defining)}.seq";
            var seqSource = av.ResolveForPlacement(seqRel).Sources.FirstOrDefault();

            if (seqSource is null)                                           // No SEQ in the asset index.
                return new SeqLintFinding(true, defining, winnerPlugin, onDisk, false, null, null, null);

            if (seqSource.LooseFilePath is null)                            // the winning .seq is inside a BSA
                return new SeqLintFinding(true, defining, winnerPlugin, onDisk, true, null, null,
                    "the winning .seq is inside a BSA, so its contents and modification time can't be checked here.");

            var seqBytes = File.ReadAllBytes(seqSource.LooseFilePath);
            bool contains = SeqFile.SeqContains(seqBytes, onDisk);
            bool newer = File.GetLastWriteTimeUtc(seqSource.LooseFilePath) >= File.GetLastWriteTimeUtc(pluginPath);
            return new SeqLintFinding(true, defining, winnerPlugin, onDisk, true, contains, newer, null);
        }
        catch (Exception ex)
        {
            return new SeqLintFinding(
                true,
                defining,
                winnerPlugin,
                0,
                false,
                null,
                null,
                $"the .seq check could not run: {ex.Message}");
        }
    }

    /// <summary>Formats a SNAM marker, making a blank marker visible.</summary>
    /// <param name="rt">Marker value.</param>
    /// <returns>The marker text, or <c>&lt;none&gt;</c>.</returns>
    static string DescribeSubtypeName(RecordType rt) => DialogueSubtype.IsBlankMarker(rt) ? "<none>" : rt.Type;

    /// <summary>Normalizes an absent or explicit null FormKey to null.</summary>
    /// <param name="fk">Nullable FormKey.</param>
    /// <returns>A non-null target, or null.</returns>
    static FormKey? NonNull(FormKey? fk) => fk is { } v && !v.IsNull ? v : null;

    /// <summary>Known readable ASCII replacements for common punctuation.</summary>
    static readonly IReadOnlyDictionary<char, string> AsciiSubstitute = new Dictionary<char, string>
    {
        ['—'] = "-",    // em dash
        ['–'] = "-",    // en dash
        ['…'] = "...",  // ellipsis
        ['‘'] = "'",    // left single quote
        ['’'] = "'",    // right single quote / apostrophe
        ['“'] = "\"",   // left double quote
        ['”'] = "\"",   // right double quote
        ['•'] = "*",    // bullet
    };

    /// <summary>Adds one warning when player-facing text contains non-ASCII characters.</summary>
    /// <param name="s">Text to inspect.</param>
    /// <param name="locus">Field label used in the warning.</param>
    /// <param name="issues">Finding collector.</param>
    static void CheckEncoding(string? s, string locus, List<DialogueIssue> issues)
    {
        if (string.IsNullOrEmpty(s)) return;
        var offenders = new List<char>();
        foreach (var ch in s) if ((int)ch > 0x7F && !offenders.Contains(ch)) offenders.Add(ch);
        if (offenders.Count == 0) return;

        var desc = string.Join(", ", offenders.Select(c => $"U+{(int)c:X4} '{c}'"));
        var subs = offenders
            .Where(AsciiSubstitute.ContainsKey)
            .Select(c => $"'{c}'->\"{AsciiSubstitute[c]}\"")
            .ToList();
        var sug = subs.Count > 0 ? $" Suggested ASCII: {string.Join(", ", subs)}." : "";
        issues.Add(new(DialogueIssueSeverity.Warning,
            $"{locus} contains non-ASCII char(s) {desc}. The CK/Papyrus text surface is effectively " +
            $"Windows-1252/ASCII, so these usually render as in-game mojibake.{sug}"));
    }

    /// <summary>Matches global text-replacement tags, including formatted variants.</summary>
    static readonly Regex GlobalTagRx = new(
        @"<Global(?:\.\w+)?=([^<>]+)>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Warns when an INFO references a global absent from its quest's display-global list.</summary>
    /// <param name="info">INFO whose prompt and response text are scanned.</param>
    /// <param name="ownerTextGlobals">Case-insensitive set of allowed global EditorIDs.</param>
    /// <param name="ownerQuestLabel">Quest label used in messages.</param>
    /// <param name="issues">Finding collector.</param>
    static void CheckGlobalTags(
        IDialogResponsesGetter info,
        HashSet<string> ownerTextGlobals,
        string ownerQuestLabel,
        List<DialogueIssue> issues)
    {
        // Report each missing global once per INFO.
        var flagged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Scan(string? text, string locus)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (Match m in GlobalTagRx.Matches(text))
            {
                var name = m.Groups[1].Value.Trim();
                if (name.Length == 0 || ownerTextGlobals.Contains(name) || !flagged.Add(name)) continue;
                issues.Add(new(DialogueIssueSeverity.Warning,
                    $"INFO {info.FormKey} {locus} uses the text-replacement tag {m.Value}, but {name} is not a "
                    + $"global in {ownerQuestLabel}'s TextDisplayGlobals. In game the tag renders as [...] and the "
                    + $"global is not substituted. Add {name} to the quest's Text Display Globals."));
            }
        }
        Scan(info.Prompt?.String, "Prompt");
        int rnum = 0;
        foreach (var resp in info.Responses) Scan(resp.Text?.String, $"response {++rnum} text");
    }

    /// <summary>Checks one INFO's CTDA rows for structural defects that require no game evaluation.</summary>
    /// <param name="info">INFO whose conditions are inspected.</param>
    /// <param name="ownerAliasIds">Owning quest alias IDs, or null when the quest could not be resolved.</param>
    /// <param name="ownerQuestLabel">Quest label used in messages.</param>
    /// <param name="inOrder">Active FormKey existence test.</param>
    /// <param name="resolve">Active winner body resolver.</param>
    /// <param name="issues">Finding collector.</param>
    /// <remarks>
    /// Checks missing run-on references, dead alias indexes, dangling form parameters and globals, and GetIsID calls
    /// aimed at placed references. It does not guess whether a well-formed condition will evaluate true in game.
    /// </remarks>
    internal static void CheckConditions(
        IDialogResponsesGetter info,
        HashSet<uint>? ownerAliasIds,
        string ownerQuestLabel,
        Func<FormKey, bool> inOrder,
        Func<FormKey, IMajorRecordGetter?> resolve,
        List<DialogueIssue> issues)
    {
        int n = 0;
        foreach (var cond in info.Conditions)
        {
            n++;
            var data = cond.Data;
            var fn = data.Function.ToString();
            // The run-on reference has a dedicated check and is excluded from the generic parameter scan.
            var refKey = data.Reference.FormKey;

            // FormLinkOrIndex contains a FormKey only in form mode. Alias and package-data modes contain an index;
            // treating their overlay Link as a form would create false dangling-reference warnings.
            bool floiIsForm = !data.UseAliases && !data.UsePackageData;

            // 1. Run On a specific Reference but none / a missing one is set — the function runs against nothing.
            if (data.RunOnType == Condition.RunOnType.Reference)
            {
                if (data.Reference.IsNull)
                    issues.Add(new(DialogueIssueSeverity.Warning,
                        $"INFO {info.FormKey} condition #{n} ({fn}) is set to Run On a specific reference, but no " +
                        "reference is set. It evaluates against nothing."));
                else if (!inOrder(refKey) && !EngineImplicit.IsImplicit(refKey))
                    issues.Add(new(DialogueIssueSeverity.Warning,
                        $"INFO {info.FormKey} condition #{n} ({fn}) Run On reference {refKey} is not in the " +
                        "active load order. The gate evaluates against nothing."));
            }

            // 2. Dead alias index. Skip when the owner quest was unavailable.
            if (ownerAliasIds is not null)
            {
                if (data.RunOnType == Condition.RunOnType.QuestAlias && BadAlias(data.RunOnTypeIndex, ownerAliasIds))
                    issues.Add(AliasIssue(
                        info.FormKey,
                        n,
                        fn,
                        data.RunOnTypeIndex,
                        "is set to Run On",
                        "reference-alias",
                        ownerQuestLabel));
                if (data is IGetIsAliasRefConditionDataGetter gar && BadAlias(gar.ReferenceAliasIndex, ownerAliasIds))
                    issues.Add(AliasIssue(
                        info.FormKey,
                        n,
                        fn,
                        gar.ReferenceAliasIndex,
                        "references",
                        "reference-alias",
                        ownerQuestLabel));
                if (data is IGetInCurrentLocAliasConditionDataGetter gla &&
                    BadAlias(gla.LocationAliasIndex, ownerAliasIds))
                    issues.Add(AliasIssue(
                        info.FormKey,
                        n,
                        fn,
                        gla.LocationAliasIndex,
                        "references",
                        "location-alias",
                        ownerQuestLabel));
            }

            // 3. Dangling form-link PARAMETER — generic, BY CONSTRUCTION: reflect over the Data arm's properties and
            //    take every form TARGET it carries — a plain FormLink (Faction, Spell, Keyword, FormList, …) AND a
            //    FORM-MODE FormLinkOrIndex (the condition union — Quest, GetIsID's Object, …; read via the write-side
            //    ReadFloiFormKey). An alias/package-data-mode FLOI is GATED OUT by floiIsForm (its index is not a form,
            //    and its overlay .Link is a bogus low key — see the mode-gate note above); the alias-INDEX paths it
            //    leaves are lint 2's int-property domain, and an alias-mode FLOI *parameter* is deliberately out of
            //    scope (a future extension, not a dangling-form case). The Run On Reference slot is skipped by name
            //    (lint 1 owns it). A param pointing at a form not in the active order is a dead reference. Mirrors the
            //    write engine's FLOI handling, so it covers every function Mutagen models.
            foreach (var p in data.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.Name == "Reference" || p.GetIndexParameters().Length != 0) continue;   // run-on ref → lint 1
                object? v; try { v = p.GetValue(data); } catch { continue; }
                // Branch 1 (plain FormLink) is defensive: on a ConditionData arm the ONLY plain FormLink is the
                // (skipped) run-on Reference — every function TARGET is the FormLinkOrIndex union (branch 2). A FLOI
                // is read via its .Link, not as an IFormLinkGetter, so it never falls into branch 1 and bypasses the
                // mode gate (proven by COND-ALIAS-FLOI: green with the gate on). Branch 1 has no index mode anyway,
                // so it needs no gate; it correctly handles a plain-FormLink param should any arm ever carry one.
                FormKey? paramFk =
                    v is IFormLinkGetter fl && !fl.IsNull ? fl.FormKey
                    : floiIsForm && WriteEngine.IsFormLinkOrIndex(p.PropertyType)
                        ? WriteEngine.ReadFloiFormKey(v)
                        : null;
                if (paramFk is { } pk && !inOrder(pk) && !EngineImplicit.IsImplicit(pk))
                    issues.Add(new(DialogueIssueSeverity.Warning,
                        $"INFO {info.FormKey} condition #{n} ({fn}) references {pk}, which is not in the " +
                        "active load order. The form may be deleted, disabled, or mistyped."));
            }

            // 4. Dangling global comparison value — a ConditionGlobal compared against a GLOB not in the order. The
            //    comparison value lives on the Condition, not the Data arm, so it's outside the param sweep above.
            if (cond is IConditionGlobalGetter cg && !cg.ComparisonValue.IsNull && !inOrder(cg.ComparisonValue.FormKey))
                issues.Add(new(DialogueIssueSeverity.Warning,
                    $"INFO {info.FormKey} condition #{n} ({fn}) compares against global " +
                    $"{cg.ComparisonValue.FormKey}, which is not in the active load order."));

            // 5. GetIsID pointed at a PLACED reference — the wrong KIND of form (a dangling one is already caught by
            //    lint 3). GetIsID compares the run-on actor's BASE form, so a placed-instance FormID can never match.
            //    Gated on floiIsForm too: an alias-mode GetIsID.Object is an index, not a form to resolve (without the
            //    gate its overlay link could trip resolve()); a form-mode Object is the base-vs-placed case.
            if (data is IGetIsIDConditionDataGetter gid &&
                floiIsForm &&
                WriteEngine.ReadFloiFormKey(gid.Object) is { } objFk &&
                inOrder(objFk) &&
                resolve(objFk) is IPlacedGetter)
                issues.Add(new(DialogueIssueSeverity.Warning,
                    $"INFO {info.FormKey} condition #{n} (GetIsID) points at placed reference {objFk}, but " +
                    "GetIsID compares the run-on actor's base form. Pass the base NPC_/object."));
        }
    }

    /// <summary>Tests whether a signed condition alias index is absent from the owning quest.</summary>
    /// <param name="idx">Condition alias index.</param>
    /// <param name="ownerAliasIds">Valid unsigned alias IDs.</param>
    /// <returns>True for negative or unknown indexes.</returns>
    static bool BadAlias(int idx, HashSet<uint> ownerAliasIds) => idx < 0 || !ownerAliasIds.Contains((uint)idx);

    /// <summary>Builds a warning for a condition that names a missing quest alias.</summary>
    /// <param name="infoFk">Owning INFO FormKey.</param>
    /// <param name="n">One-based condition number.</param>
    /// <param name="fn">Condition function name.</param>
    /// <param name="idx">Missing alias ID.</param>
    /// <param name="verb">Message verb describing the reference.</param>
    /// <param name="aliasKind">Reference or location alias label.</param>
    /// <param name="ownerQuestLabel">Owning quest label.</param>
    /// <returns>A consistently formatted warning.</returns>
    static DialogueIssue AliasIssue(
        FormKey infoFk,
        int n,
        string fn,
        int idx,
        string verb,
        string aliasKind,
        string ownerQuestLabel) =>
        new(DialogueIssueSeverity.Warning,
            $"INFO {infoFk} condition #{n} ({fn}) {verb} {ownerQuestLabel}'s {aliasKind} #{idx}, but that " +
            "quest defines no alias with that ID. The gate evaluates against nothing.");
}
