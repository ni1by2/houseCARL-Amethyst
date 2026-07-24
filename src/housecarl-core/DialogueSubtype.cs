using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>Describes how a dialogue topic's four-character SNAM marker was handled.</summary>
public enum MarkerFill
{
    /// <summary>An explicit non-blank marker was preserved.</summary>
    AlreadySet,
    /// <summary>A missing or stale marker was replaced with the marker for the numeric subtype.</summary>
    Filled,
    /// <summary>No marker is known for the numeric subtype, so no safe change was possible.</summary>
    Unmodeled,
}

/// <summary>Maps numeric DIAL subtypes to the four-character SNAM markers used by Skyrim.</summary>
/// <remarks>
/// Mutagen stores the numeric subtype and marker independently. New topics need both. The table comes from xEdit's
/// TES5 record definitions because the marker cannot be derived reliably from the enum name or vanilla DATA values.
/// </remarks>
public static class DialogueSubtype
{
    /// <summary>
    /// Maps contiguous xEdit subtype indexes to Mutagen enum names and SNAM markers.
    /// </summary>
    /// <remarks>
    /// Index 3 has no Mutagen enum member, so its name is empty. Tests pin every named row to its enum value.
    /// </remarks>
    static readonly (string Name, string Marker)[] Table =
    {
        ("Custom", "CUST"),                          //   0
        ("ForceGreet", "PFGT"),                      //   1
        ("Rumors", "RUMO"),                          //   2
        ("", "FVDL"),                                //   3  (xEdit 'Custom?'; not a Mutagen enum member)
        ("Intimidate", "INTI"),                      //   4
        ("Flatter", "FLAT"),                         //   5
        ("Bribe", "BRIB"),                           //   6
        ("AskGift", "ASKG"),                         //   7
        ("Gift", "GIFF"),                            //   8
        ("AskFavor", "ASKF"),                        //   9
        ("Favor", "FAVO"),                           //  10
        ("ShowRelationships", "SHRE"),               //  11
        ("Follow", "FOLL"),                          //  12
        ("Reject", "FRJT"),                          //  13
        ("Scene", "SCEN"),                           //  14
        ("Show", "SHOW"),                            //  15
        ("Agree", "AGRE"),                           //  16
        ("Refuse", "REFU"),                          //  17
        ("ExitFavorState", "FEXT"),                  //  18
        ("MoralRefusal", "MREF"),                    //  19
        ("FlyingMountLand", "FMLX"),                 //  20
        ("FlyingMountCancelLand", "FMXL"),           //  21
        ("FlyingMountAcceptTarget", "FMAT"),         //  22
        ("FlyingMountRejectTarget", "FMRT"),         //  23
        ("FlyingMountNoTarget", "FMNT"),             //  24
        ("FlyingMountDestinationReached", "FMDR"),   //  25
        ("Attack", "ATCK"),                          //  26
        ("PowerAttack", "POAT"),                     //  27
        ("Bash", "BASH"),                            //  28
        ("Hit", "HIT_"),                             //  29
        ("Flee", "FLEE"),                            //  30
        ("Bleedout", "BLED"),                        //  31
        ("AvoidThreat", "AVTH"),                     //  32
        ("Death", "DETH"),                           //  33
        ("GroupStrategy", "GRST"),                   //  34
        ("Block", "BLOC"),                           //  35
        ("Taunt", "TAUT"),                           //  36
        ("AllyKilled", "ALKL"),                      //  37
        ("Steal", "STEA"),                           //  38
        ("Yield", "YIEL"),                           //  39
        ("AcceptYield", "ACYI"),                     //  40
        ("PickpocketCombat", "PICC"),                //  41
        ("Assault", "ASSA"),                         //  42
        ("Murder", "MURD"),                          //  43
        ("AssaultNC", "ASNC"),                       //  44
        ("MurderNC", "MUNC"),                        //  45
        ("PickpocketNC", "PICN"),                    //  46
        ("StealFromNC", "STFN"),                     //  47
        ("TrespassAgainstNC", "TRAN"),               //  48
        ("Trespass", "TRES"),                        //  49
        ("WerewolfTransformCrime", "WTCR"),          //  50
        ("VoicePowerStartShort", "VPSS"),            //  51
        ("VoicePowerStartLong", "VPSL"),             //  52
        ("VoicePowerEndShort", "VPES"),              //  53
        ("VoicePowerEndLong", "VPEL"),               //  54
        ("AlertIdle", "ALIL"),                       //  55
        ("LostIdle", "LOIL"),                        //  56
        ("NormalToAlert", "NOTA"),                   //  57
        ("AlertToCombat", "ALTC"),                   //  58
        ("NormalToCombat", "NOTC"),                  //  59
        ("AlertToNormal", "ALTN"),                   //  60
        ("CombatToNormal", "COTN"),                  //  61
        ("CombatToLost", "COLO"),                    //  62
        ("LostToNormal", "LOTN"),                    //  63
        ("LostToCombat", "LOTC"),                    //  64
        ("DetectFriendDie", "DFDA"),                 //  65
        ("ServiceRefusal", "SERU"),                  //  66
        ("Repair", "REPA"),                          //  67
        ("Travel", "TRAV"),                          //  68
        ("Training", "TRAI"),                        //  69
        ("BarterExit", "BAEX"),                      //  70
        ("RepairExit", "REEX"),                      //  71
        ("Recharge", "RECH"),                        //  72
        ("RechargeExit", "RCEX"),                    //  73
        ("TrainingExit", "TREX"),                    //  74
        ("ObserveCombat", "OBCO"),                   //  75
        ("NoticeCorpse", "NOTI"),                    //  76
        ("TimeToGo", "TITG"),                        //  77
        ("Goodbye", "GBYE"),                         //  78
        ("Hello", "HELO"),                           //  79
        ("SwingMeleeWeapon", "SWMW"),                //  80
        ("ShootBow", "FIWE"),                        //  81
        ("ZKeyObject", "ZKEY"),                      //  82
        ("Jump", "JUMP"),                            //  83
        ("KnockOverObject", "KNOO"),                 //  84
        ("DestroyObject", "DEOB"),                   //  85
        ("StandOnFurniture", "STOF"),                //  86
        ("LockedObject", "LOOB"),                    //  87
        ("PickpocketTopic", "PICT"),                 //  88
        ("PursueIdleTopic", "PURS"),                 //  89
        ("SharedInfo", "IDAT"),                      //  90
        ("PlayerCastProjectileSpell", "PCPS"),       //  91
        ("PlayerCastSelfSpell", "PCSS"),             //  92
        ("PlayerShout", "PCSH"),                     //  93
        ("Idle", "IDLE"),                            //  94
        ("EnterSprintBreath", "BREA"),               //  95
        ("EnterBowZoomBreath", "ENBZ"),              //  96
        ("ExitBowZoomBreath", "EXBZ"),               //  97
        ("ActorCollideWithActor", "ACAC"),           //  98
        ("PlayerInIronSights", "PIRN"),              //  99
        ("OutOfBreath", "OUTB"),                     // 100
        ("CombatGrunt", "GRNT"),                     // 101
        ("LeaveWaterBreath", "LWBS"),                // 102
    };

    /// <summary>Gets the number of modeled subtype indexes.</summary>
    public static int Count => Table.Length;

    /// <summary>Gets the Mutagen enum name for a modeled index.</summary>
    /// <param name="index">Numeric subtype index.</param>
    /// <returns>The enum name, or an empty string for an invalid or unnamed index.</returns>
    public static string NameAt(int index) => index >= 0 && index < Table.Length ? Table[index].Name : "";

    /// <summary>Gets the SNAM marker for a numeric subtype.</summary>
    /// <param name="subtypeIndex">Numeric subtype index.</param>
    /// <returns>The four-character marker, or null when the index is not modeled.</returns>
    public static string? MarkerFor(int subtypeIndex) =>
        subtypeIndex >= 0 && subtypeIndex < Table.Length ? Table[subtypeIndex].Marker : null;

    /// <summary>Gets the SNAM marker for a Mutagen subtype.</summary>
    /// <param name="subtype">Mutagen subtype value.</param>
    /// <returns>The four-character marker, or null when the value is not modeled.</returns>
    public static string? MarkerFor(DialogTopic.SubtypeEnum subtype) => MarkerFor((int)subtype);

    /// <summary>Tests whether a SNAM marker contains no usable characters.</summary>
    /// <param name="marker">Marker to inspect.</param>
    /// <returns>True for empty, whitespace-only, or NUL-only markers.</returns>
    public static bool IsBlankMarker(RecordType marker)
    {
        var s = marker.Type;
        return string.IsNullOrEmpty(s) || string.IsNullOrWhiteSpace(s) || s.All(c => c == '\0');
    }

    /// <summary>Fills a blank SNAM marker from the topic's numeric subtype.</summary>
    /// <param name="topic">Writable topic.</param>
    /// <param name="marker">Receives the marker when a fill occurs; otherwise null.</param>
    /// <returns>A value describing whether the marker was preserved, filled, or unmodeled.</returns>
    public static MarkerFill NormalizeMarker(IDialogTopic topic, out string? marker)
    {
        marker = null;
        if (!IsBlankMarker(topic.SubtypeName)) return MarkerFill.AlreadySet;   // explicit marker wins — never override
        if (MarkerFor((int)topic.Subtype) is not { } tag)
            return MarkerFill.Unmodeled;
        topic.SubtypeName = new RecordType(tag);
        marker = tag;
        return MarkerFill.Filled;
    }

    /// <summary>Synchronizes SNAM to the topic's current numeric subtype after an explicit subtype edit.</summary>
    /// <param name="topic">Writable topic whose subtype was changed by the caller.</param>
    /// <param name="marker">Receives the marker when synchronization changes it; otherwise null.</param>
    /// <returns>A value describing whether the marker already matched, changed, or was unmodeled.</returns>
    /// <remarks>
    /// The caller must invoke this only when the same edit changed Subtype without explicitly setting SNAM.
    /// </remarks>
    public static MarkerFill SyncMarkerToSubtype(IDialogTopic topic, out string? marker)
    {
        marker = null;
        if (MarkerFor((int)topic.Subtype) is not { } tag) return MarkerFill.Unmodeled;
        if (string.Equals(topic.SubtypeName.Type, tag, StringComparison.Ordinal)) return MarkerFill.AlreadySet;
        topic.SubtypeName = new RecordType(tag);
        marker = tag;
        return MarkerFill.Filled;
    }
}
