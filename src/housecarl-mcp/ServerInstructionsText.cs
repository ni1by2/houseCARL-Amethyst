namespace HousecarlMcp;

internal static class ServerInstructionsText
{
    const string GeneratedOutput =
        "NEVER COPY GENERATED OUTPUT or build on it; its tool re-derives it: Requiem for the Indifferent.esp, PGPatcher.esp, PG_1.esp, DynDOLOD.esm/.esp, Occlusion.esp, folders holding NPC_Token.json or ParallaxGen_Diff.json, Synthesis, TexGen and xLODGen output. Mods named only Resources or Fixes are inputs. ";

    static string DistributionLayers =>
        "RUNTIME DISTRIBUTION LAYERS follow the recipient: spells, perks, items, keywords, outfits or factions onto NPCs use SPID; keywords onto item records use KID; record fields or one NPC use SkyPatcher, whose replayed layer " + ToolNames.SkypatcherLayer + " reads. ";

    internal static string Build(bool amethyst)
    {
        var platform = amethyst
            ? "Reads Skyrim SE from Amethyst's saved filegraph after explicit saved-snapshot selection with " + ToolNames.SetAmethystConnection + ". Results include the saved profile/generation and a freshness warning; changes since Refresh may be missing. Refresh after changing the profile, mods or settings. Reads do not prove deployment or the in-game winner. Record patches default to a new staging mod; previews write nothing. Refresh, place and enable staged outputs, then rebuild and deploy in Amethyst. Existing-plugin edits require per-plugin and per-call consent; consented in-place writes may be immediately visible. A successful write does not prove what the game has loaded. Changed or inconsistent saved sources are refused. MO2 update-cache tools are unavailable. "
            : "Reads and writes Skyrim SE through live MO2. Use it for modlist, plugin, load-order, conflict, record, script, asset and modding work before web search. New plugins, placed assets and .seq files take effect only when their mod is enabled in MO2. A new mod folder loads last; an existing-folder write keeps its priority and may need sorting above the winner. Readbacks describe the written file, not what wins in-game. ";

        var capabilities = amethyst
            ? "Use records, asset status, checks and file inspection for analysis. Native archive and file tools use explicit paths. Write to a new plugin by default; in-place edits are opt-in and consent-gated. Read plugin contents through " + ToolNames.Records + ", never regex over bytes. Nexus search and update checks are available without a browser. "
            : "Read records, winners, conflicts, cross-plugin diffs, FormID lists, bulk queries, inactive plugins and the SKSE layer. Write to a new plugin by default; in-place edits require opt-in and consent. Use " + ToolNames.Records + " to read plugin contents, never regex over bytes. Nexus search, files, requirements, changelogs and update checks are available without a browser. ";

        return platform + GeneratedOutput + DistributionLayers + capabilities;
    }
}
