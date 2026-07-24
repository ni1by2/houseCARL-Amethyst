using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>Describes one Creation Kit parity value added during record creation.</summary>
/// <param name="Label">Short description used in the operation result.</param>
/// <param name="Reason">Plain-English reason for materializing the field.</param>
public readonly record struct CkParityFill(string Label, string Reason);

/// <summary>Describes one Creation Kit parity subrecord missing from an existing record.</summary>
/// <param name="Subrecord">Subrecord signature and field name.</param>
/// <param name="Detail">Why the omission matters and how to repair it.</param>
public readonly record struct CkParityGap(string Subrecord, string Detail);

/// <summary>Materializes optional dialogue subrecords that the Creation Kit writes unconditionally.</summary>
/// <remarks>
/// Mutagen omits nullable fields when they are unset. The Creation Kit expects several INFO, DLVW, DLBR, DIAL, and
/// QUST fields to exist even at their zero values. Each apply method preserves explicit author values and reports
/// every fill. Its matching missing method uses the same presence predicates.
/// </remarks>
public static class DialogueCkParity
{
    /// <summary>Creation Kit DLVW DNAM default as hexadecimal bytes.</summary>
    public const string ViewDnamHex = "00";

    /// <summary>Creation Kit DLVW ENAM default as hexadecimal bytes.</summary>
    public const string ViewEnamHex = "00000000";

    /// <summary>Materializes missing INFO CNAM and ENAM defaults.</summary>
    /// <param name="info">Writable INFO record.</param>
    /// <returns>Descriptions of values filled; empty when both fields were explicit.</returns>
    public static IReadOnlyList<CkParityFill> ApplyInfoDefaults(IDialogResponses info)
    {
        var fills = new List<CkParityFill>(2);

        // CNAM uses None as its present-but-empty value.
        if (!HasFavorLevel(info))
        {
            info.FavorLevel = FavorLevel.None;
            fills.Add(new CkParityFill(
                "FavorLevel (CNAM subrecord) auto-set to None",
                "None — every CK-authored INFO carries the CNAM (FavorLevel) subrecord; an INFO created without it "
                + "crashes the Creation Kit when its owning topic is opened in the dialogue editor (the game tolerates "
                + "it). CK-parity default-populate, in-model (#131 pattern)."));
        }

        // A new response-flags struct materializes ENAM with zero flags and zero reset hours.
        if (!HasResponseFlags(info))
        {
            info.Flags = new DialogResponseFlags();
            fills.Add(new CkParityFill(
                "Flags (ENAM subrecord) auto-set to empty response flags (Flags=0, ResetHours=0)",
                "empty DialogResponseFlags — every CK-authored INFO carries the ENAM (response flags + reset-hours) "
                + "subrecord; an INFO created without it crashes the Creation Kit when its owning topic is opened. "
                + "This materialises the struct only; the Goodbye conversation-ender flag still needs setting "
                + "explicitly (authoring choice, not a default)."));
        }

        return fills;
    }

    /// <summary>Tests whether INFO CNAM is materialized.</summary>
    /// <param name="info">INFO to inspect.</param>
    /// <returns>True when FavorLevel is present, including an explicit None.</returns>
    static bool HasFavorLevel(IDialogResponsesGetter info) => info.FavorLevel is not null;   // CNAM

    /// <summary>Tests whether INFO ENAM is materialized.</summary>
    /// <param name="info">INFO to inspect.</param>
    /// <returns>True when the response-flags structure is present.</returns>
    static bool HasResponseFlags(IDialogResponsesGetter info) => info.Flags is not null;      // ENAM

    /// <summary>Finds missing INFO CNAM and ENAM subrecords without changing the record.</summary>
    /// <param name="info">INFO to inspect.</param>
    /// <returns>One gap per absent parity field.</returns>
    public static IReadOnlyList<CkParityGap> MissingInfoDefaults(IDialogResponsesGetter info)
    {
        var gaps = new List<CkParityGap>(2);

        if (!HasFavorLevel(info))
            gaps.Add(new CkParityGap("CNAM (FavorLevel)",
                "every CK-authored INFO carries the CNAM (FavorLevel) subrecord; an INFO missing it crashes the "
                + "Creation Kit when its owning topic is opened in the dialogue editor (the game itself tolerates "
                + "it). houseCARL's create tools auto-fill it — set FavorLevel (e.g. None) to populate it."));

        if (!HasResponseFlags(info))
            gaps.Add(new CkParityGap("ENAM (response Flags)",
                "every CK-authored INFO carries the ENAM (response flags + reset-hours) subrecord; an INFO missing it "
                + "crashes the Creation Kit when its owning topic is opened. houseCARL's create tools auto-fill it — "
                + "set the response Flags to populate it."));

        return gaps;
    }

    /// <summary>Materializes missing DLVW DNAM and ENAM byte fields.</summary>
    /// <param name="view">Writable dialogue view.</param>
    /// <returns>Descriptions of values filled.</returns>
    public static IReadOnlyList<CkParityFill> ApplyViewDefaults(IDialogView view)
    {
        var fills = new List<CkParityFill>(2);

        if (!HasDnam(view))
        {
            view.DNAM = Convert.FromHexString(ViewDnamHex);
            fills.Add(new CkParityFill(
                $"DNAM subrecord auto-set to {ViewDnamHex}",
                $"0x{ViewDnamHex} — every CK-authored DialogView carries the DNAM byte subrecord; a bare DLVW (with "
                + "BNAM-less topics) crashes the Creation Kit's Dialogue Views editor. CK-parity default-populate."));
        }

        if (!HasEnam(view))
        {
            view.ENAM = Convert.FromHexString(ViewEnamHex);
            fills.Add(new CkParityFill(
                $"ENAM subrecord auto-set to {ViewEnamHex}",
                $"0x{ViewEnamHex} — every CK-authored DialogView carries the ENAM byte subrecord; pairs with DNAM for "
                + "Creation Kit Dialogue Views parity. CK-parity default-populate."));
        }

        return fills;
    }

    /// <summary>Tests whether a dialogue view carries DNAM.</summary>
    /// <param name="view">Dialogue view to inspect.</param>
    /// <returns>True when DNAM is present.</returns>
    static bool HasDnam(IDialogViewGetter view) => view.DNAM is not null;   // DNAM

    /// <summary>Tests whether a dialogue view carries ENAM.</summary>
    /// <param name="view">Dialogue view to inspect.</param>
    /// <returns>True when ENAM is present.</returns>
    static bool HasEnam(IDialogViewGetter view) => view.ENAM is not null;   // ENAM

    /// <summary>Finds missing DLVW DNAM and ENAM subrecords without changing the record.</summary>
    /// <param name="view">Dialogue view to inspect.</param>
    /// <returns>One gap per absent parity field.</returns>
    public static IReadOnlyList<CkParityGap> MissingViewDefaults(IDialogViewGetter view)
    {
        var gaps = new List<CkParityGap>(2);

        if (!HasDnam(view))
            gaps.Add(new CkParityGap("DNAM",
                $"every CK-authored DialogView carries the DNAM byte subrecord (0x{ViewDnamHex}); a bare DLVW (with "
                + "BNAM-less topics) crashes the Creation Kit's Dialogue Views editor (FlowchartX64 null-deref — the "
                + "game itself tolerates it). houseCARL's create tools auto-fill it — housecarl_set_field DNAM="
                + ViewDnamHex + " to populate it."));

        if (!HasEnam(view))
            gaps.Add(new CkParityGap("ENAM",
                $"every CK-authored DialogView carries the ENAM byte subrecord (0x{ViewEnamHex}), pairing with DNAM "
                + "for Creation Kit Dialogue Views parity; a bare DLVW crashes the CK's Dialogue Views editor (the "
                + "game itself tolerates it). houseCARL's create tools auto-fill it — housecarl_set_field ENAM="
                + ViewEnamHex + " to populate it."));

        return gaps;
    }

    /// <summary>Creation Kit seed priority for a new DIAL.</summary>
    public const float TopicPrioritySeed = 50f;

    /// <summary>Creation Kit category for a new DLBR without an explicit category.</summary>
    public const DialogBranch.CategoryType BranchCategoryDefault = DialogBranch.CategoryType.Player;

    /// <summary>Creation Kit flags value for a new DLBR without explicit flag choices.</summary>
    public const DialogBranch.Flag BranchFlagsDefault = (DialogBranch.Flag)0;

    /// <summary>Materializes missing DLBR category and flags fields.</summary>
    /// <param name="branch">Writable dialogue branch.</param>
    /// <returns>Descriptions of values filled.</returns>
    /// <remarks>
    /// Absent flags are filled with zero because Skyrim otherwise interprets the branch as top-level.
    /// </remarks>
    public static IReadOnlyList<CkParityFill> ApplyBranchDefaults(IDialogBranch branch)
    {
        var fills = new List<CkParityFill>(2);

        if (!HasCategory(branch))
        {
            branch.Category = BranchCategoryDefault;
            fills.Add(new CkParityFill(
                $"Category (TNAM subrecord) auto-set to {BranchCategoryDefault}",
                $"{BranchCategoryDefault} — every CK-authored DialogBranch carries the TNAM (Category) subrecord; "
                + "Player is both the enum's zero-value (a fresh CK branch's default) and the value ~all vanilla "
                + "branches carry across every Flags combination. A Command speech-challenge branch "
                + "is a deliberate authored case that sets Category=Command explicitly; non-override leaves that "
                + "untouched. CK-parity default-populate, in-model (#131 pattern)."));
        }

        if (!HasFlags(branch))
        {
            branch.Flags = BranchFlagsDefault;
            fills.Add(new CkParityFill(
                "Flags (DNAM subrecord) auto-set to 0 (no flags — not top-level, not blocking, not exclusive)",
                "0 — every CK-authored DialogBranch carries the DNAM (Flags) subrecord, and a branch whose flag "
                + "checkboxes the author never ticked carries it as 0 (203 of Skyrim.esm's 3061 branches do exactly "
                + "that). A branch written WITHOUT DNAM is read by the engine as TopLevel, so its topics are published "
                + "to the player's dialogue menu — a nameless Say()-only topic shows up as a selectable \"...\". This "
                + "materialises the subrecord only; TopLevel/Blocking/Exclusive stay an explicit authoring choice a "
                + "0-fill does NOT set, and an explicit Flags always wins. CK-parity default-populate, in-model."));
        }

        return fills;
    }

    /// <summary>Tests whether a branch carries TNAM.</summary>
    /// <param name="branch">Branch to inspect.</param>
    /// <returns>True when Category is present.</returns>
    static bool HasCategory(IDialogBranchGetter branch) => branch.Category is not null;   // TNAM

    /// <summary>Tests whether a branch carries DNAM.</summary>
    /// <param name="branch">Branch to inspect.</param>
    /// <returns>True when Flags is present, including an explicit zero.</returns>
    static bool HasFlags(IDialogBranchGetter branch) => branch.Flags is not null;         // DNAM

    /// <summary>Finds missing DLBR TNAM and DNAM subrecords without changing the record.</summary>
    /// <param name="branch">Dialogue branch to inspect.</param>
    /// <returns>One gap per absent parity field.</returns>
    public static IReadOnlyList<CkParityGap> MissingBranchDefaults(IDialogBranchGetter branch)
    {
        var gaps = new List<CkParityGap>(2);

        if (!HasCategory(branch))
            gaps.Add(new CkParityGap("TNAM (Category)",
                "every CK-authored DialogBranch carries the TNAM (Category) subrecord (~all vanilla branches carry "
                + "Player); a branch missing it differs structurally from a CK-authored one (byte-parity only — no "
                + "confirmed crash). houseCARL's create tools auto-fill it; set Category to populate it."));

        if (!HasFlags(branch))
            gaps.Add(new CkParityGap("DNAM (Flags)",
                "every CK-authored DialogBranch carries the DNAM (Flags) subrecord — as 0 when no flag is ticked (203 "
                + "of Skyrim.esm's 3061 branches). Skyrim reads a missing value as TopLevel, so its topics "
                + "are published to the player's dialogue menu — a nameless Say()-only topic renders as a selectable "
                + "\"...\". This is an in-game defect, not a byte-parity nit: the record is byte-valid and only "
                + "misbehaves once loaded. houseCARL's create tools auto-fill it — set Flags (0 for a hidden branch, "
                + "TopLevel for a player-facing one) to populate it."));

        return gaps;
    }

    /// <summary>Seeds a new DIAL priority when the author did not provide one.</summary>
    /// <param name="topic">Writable dialogue topic.</param>
    /// <param name="authorSetPriority">Whether the create operation explicitly touched Priority.</param>
    /// <returns>The applied fill, or null when the author supplied the value.</returns>
    /// <remarks>
    /// Priority is non-nullable, so caller intent is the only way to distinguish unset from explicit zero.
    /// </remarks>
    public static CkParityFill? ApplyTopicPriorityDefault(IDialogTopic topic, bool authorSetPriority)
    {
        if (authorSetPriority) return null;                 // author set Priority (even to 0) — non-override, no fill
        topic.Priority = TopicPrioritySeed;                 // was 0 (the non-nullable default); seed to the CK's 50
        return new CkParityFill(
            $"Priority (PNAM subrecord) auto-set to {TopicPrioritySeed:0} (CK seed default)",
            $"{TopicPrioritySeed:0} — a CK-authored DialogTopic always writes PNAM (Priority), and 50 is the CK's seed "
            + "for an untouched topic (the dominant value on vanilla Custom topics; authors raise/lower it to order "
            + "competing lines). Priority is a non-nullable float, so this seeds 50 only when the author set no "
            + "Priority at all — an explicit value, including 0, always wins. CK-parity seed.");
    }

    /// <summary>Materializes CK parity fields on a newly created quest and its owned structures.</summary>
    /// <param name="quest">Writable new quest.</param>
    /// <returns>Descriptions of every ANAM, objective FNAM, alias FNAM, and reference-alias VTCK fill.</returns>
    /// <remarks>
    /// ANAM is seeded as max alias ID plus one, or zero without aliases. That derivation is valid for a new quest,
    /// not for an edited quest whose Creation Kit high-water mark may retain deleted IDs.
    /// </remarks>
    public static IReadOnlyList<CkParityFill> ApplyQuestDefaults(IQuest quest)
    {
        var fills = new List<CkParityFill>();

        if (!HasNextAliasID(quest))
        {
            bool hasAliases = quest.Aliases.Count > 0;
            uint next = hasAliases ? quest.Aliases.Max(a => a.ID) + 1u : 0u;
            quest.NextAliasID = next;
            fills.Add(new CkParityFill(
                $"NextAliasID (ANAM subrecord) auto-set to {next}",
                $"{next} — every CK-authored Quest carries ANAM, seeded to the next alias ID the CK would hand out "
                + $"({(hasAliases ? $"max of {quest.Aliases.Count} alias ID(s) + 1" : "0 without aliases")}). "
                + "Non-override: fills only when the author set no NextAliasID. CK-parity default-populate."));
        }

        int idx = 0;
        foreach (var objective in quest.Objectives)
        {
            if (!HasObjectiveFlags(objective))
            {
                objective.Flags = default(QuestObjective.Flag);   // (QuestObjective.Flag)0 — no flags set
                fills.Add(new CkParityFill(
                    $"Objectives[{idx}] (Index {objective.Index}) Flags (FNAM subrecord) auto-set to 0 (no flags)",
                    "0 — every CK-authored quest objective carries the FNAM (flags) subrecord, and vanilla objectives "
                    + "carry 0. This materialises the subrecord only; the OrWithPrevious flag stays an explicit "
                    + "authoring choice. CK-parity default-populate."));
            }
            idx++;
        }

        // All aliases carry FNAM. Only reference aliases carry the actor-oriented VTCK field.
        int aidx = 0;
        foreach (var alias in quest.Aliases)
        {
            if (!HasAliasFlags(alias))
            {
                alias.Flags = default(QuestAlias.Flag);           // (QuestAlias.Flag)0 — no flags set
                fills.Add(new CkParityFill(
                    $"Aliases[{aidx}] (ID {alias.ID}) Flags (FNAM subrecord) auto-set to 0 (no flags)",
                    "0 — every CK-authored quest alias carries the FNAM (flags) subrecord, value 0 when no alias flags "
                    + "are set. This materialises the subrecord only; alias flags (Optional, Essential, …) stay an "
                    + "explicit authoring choice. CK-parity default-populate."));
            }
            if (IsReferenceAlias(alias) && !HasAliasVoiceTypes(alias))
            {
                // SetTo creates a present VTCK containing the null FormID, which differs from an omitted VTCK.
                alias.VoiceTypes.SetTo(FormKey.Null);
                fills.Add(new CkParityFill(
                    $"Aliases[{aidx}] (ID {alias.ID}) VoiceTypes (VTCK) auto-set to null (0x00000000)",
                    "null link — every CK-authored quest REFERENCE alias carries the VTCK (voice-types) subrecord; an "
                    + "alias with no voice type carries it as a null link (0x00000000), not omitted. This materialises "
                    + "the empty subrecord only; a real voice-type list stays an explicit authoring choice. CK-parity "
                    + "default-populate."));
            }
            aidx++;
        }

        return fills;
    }

    /// <summary>Tests whether a quest carries ANAM.</summary>
    /// <param name="quest">Quest to inspect.</param>
    /// <returns>True when NextAliasID is present.</returns>
    static bool HasNextAliasID(IQuestGetter quest) => quest.NextAliasID is not null;                 // ANAM

    /// <summary>Tests whether an objective carries FNAM.</summary>
    /// <param name="objective">Objective to inspect.</param>
    /// <returns>True when Flags is present.</returns>
    static bool HasObjectiveFlags(IQuestObjectiveGetter objective) => objective.Flags is not null;   // FNAM

    /// <summary>Tests whether an alias carries FNAM.</summary>
    /// <param name="alias">Alias to inspect.</param>
    /// <returns>True when Flags is present.</returns>
    static bool HasAliasFlags(IQuestAliasGetter alias) => alias.Flags is not null;                    // FNAM (alias)

    /// <summary>Tests whether an alias carries VTCK, including a present null link.</summary>
    /// <param name="alias">Alias to inspect.</param>
    /// <returns>True when the nullable link container is materialized.</returns>
    static bool HasAliasVoiceTypes(IQuestAliasGetter alias)
        => alias.VoiceTypes.FormKeyNullable is not null;  // VTCK

    /// <summary>Tests whether an alias is actor/reference based and therefore owns VTCK.</summary>
    /// <param name="alias">Alias to inspect.</param>
    /// <returns>True for reference aliases.</returns>
    static bool IsReferenceAlias(IQuestAliasGetter alias) => alias.Type == QuestAlias.TypeEnum.Reference;

    /// <summary>Finds missing quest parity subrecords without judging existing values.</summary>
    /// <param name="quest">Quest to inspect.</param>
    /// <returns>Gaps for ANAM, objective FNAM, alias FNAM, and reference-alias VTCK.</returns>
    public static IReadOnlyList<CkParityGap> MissingQuestDefaults(IQuestGetter quest)
    {
        var gaps = new List<CkParityGap>();

        if (!HasNextAliasID(quest))
            gaps.Add(new CkParityGap("ANAM (NextAliasID)",
                "every CK-authored Quest carries the ANAM (next-alias-ID counter) subrecord; a quest missing it "
                + "differs structurally from a CK-authored one (byte-parity only — no confirmed crash). houseCARL's "
                + "create tools auto-fill it; set NextAliasID to populate it."));

        int idx = 0;
        foreach (var objective in quest.Objectives)
        {
            if (!HasObjectiveFlags(objective))
                gaps.Add(new CkParityGap($"Objectives[{idx}] (Index {objective.Index}) FNAM (Flags)",
                    "every CK-authored quest objective carries FNAM (vanilla objectives use 0); "
                    + "an objective missing it differs structurally from a CK-authored one (byte-parity only — no "
                    + "confirmed crash). houseCARL's create tools auto-fill it; set Flags to populate it."));
            idx++;
        }

        int aidx = 0;
        foreach (var alias in quest.Aliases)
        {
            if (!HasAliasFlags(alias))
                gaps.Add(new CkParityGap($"Aliases[{aidx}] (ID {alias.ID}) FNAM (Flags)",
                    "every CK-authored quest alias carries the FNAM (flags) subrecord (value 0 when no alias flags are "
                    + "set); an alias missing it differs structurally from a CK-authored one (byte-parity only — no "
                    + "confirmed crash). houseCARL's create tools auto-fill it; set Flags to populate it."));
            if (IsReferenceAlias(alias) && !HasAliasVoiceTypes(alias))
                gaps.Add(new CkParityGap($"Aliases[{aidx}] (ID {alias.ID}) VTCK (VoiceTypes)",
                    "every CK-authored quest REFERENCE alias carries the VTCK (voice-types) subrecord (a null link, "
                    + "0x00000000, when no voice type is set); a missing field differs structurally from a CK-authored "
                    + "record (byte-parity only — no confirmed crash). houseCARL's create tools auto-fill it; "
                    + "set the alias's VoiceTypes to populate it."));
            aidx++;
        }

        return gaps;
    }
}
