using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>Offline, read-only view of the selected manager's cached Nexus update metadata.</summary>
[McpServerToolType]
public static class UpdateStatusTools
{
    [McpServerTool(Name = ToolNames.UpdateStatus, ReadOnly = true, Title = "Local mod-update cache (no network)"),
     Description(
         "Read the selected manager's locally cached Nexus update state; this is offline and makes no network request. " +
         "MO2 uses its local meta.ini cache. Amethyst uses the selected saved profile's captured Nexus metadata, " +
         "including its cached update and ignore flags. Results identify their manager and evidence source; cached " +
         "state may be stale, incomplete, or use a different version scheme, and missing state does not mean up to date. " +
         "To verify live, pass a row's exact verify token when available to " + ToolNames.NexusCheckUpdates +
         ", then use " + ToolNames.NexusMod + " changelog=true to inspect changes. This tool changes nothing.")]
    public static string UpdateStatus(
        LoadOrderService svc,
        [Description("Optional. Max characters before the mod lists are cut with an explicit notice. 0 = the server default (~40k).")]
            int max_chars = 0) => Guard.Tool(ToolNames.UpdateStatus, () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        var data = svc.UpdateCache();
        return UpdateStatusWire.Render(data, Wire.Cap(max_chars));
    });
}

/// <summary>Renders the manager-specific cached Nexus update state.</summary>
static class UpdateStatusWire
{
    public static string Render(UpdateCacheData d, int cap)
    {
        if (string.Equals(d.Manager, "Amethyst", StringComparison.OrdinalIgnoreCase))
            return RenderAmethyst(d, cap);
        var sb = new StringBuilder();
        sb.Append("MO2 update cache (LOCAL, no network) — instance: ")
          .Append(d.InstanceDir ?? "explicit-paths mode").Append('\n');

        var flagged = new List<ModUpdateEntry>();
        var ignored = new List<ModUpdateEntry>();
        int current = 0, neverChecked = 0;
        foreach (var e in d.Entries)
        {
            if (string.IsNullOrWhiteSpace(e.Newest)) { neverChecked++; continue; }   // MO2 never learned a newer version
            bool differs = !string.Equals(e.Newest.Trim(), (e.Installed ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
            if (!differs) { current++; continue; }
            if (e.Ignored is not null && string.Equals(e.Newest.Trim(), e.Ignored.Trim(), StringComparison.OrdinalIgnoreCase))
                ignored.Add(e);
            else flagged.Add(e);
        }

        sb.Append(d.Entries.Count).Append(" Nexus-linked mod(s): ")
          .Append(flagged.Count).Append(" differ from MO2's cached newest · ")
          .Append(ignored.Count).Append(" ignored · ")
          .Append(current).Append(" match · ")
          .Append(neverChecked).Append(" never checked");
        if (d.UntrackedCount > 0) sb.Append("  (+").Append(d.UntrackedCount).Append(" non-Nexus mods/separators skipped)");
        sb.Append('\n');
        foreach (var p in d.Problems) sb.Append("[!] ").Append(p).Append('\n');

        AppendEntries(sb, "installed ≠ MO2's cached newest — CANDIDATES to verify (direction not assured)", flagged, cap);
        AppendEntries(sb, "ignored — you told MO2 to skip this version", ignored, cap);

        sb.Append("\nnote: this is MO2's OWN cached view, not a live check. A 'differ' is only a CANDIDATE — MO2's cached ")
          .Append("'newest' can be stale or a different version scheme (some installed versions here are actually NEWER than ")
          .Append("the cached value), so the direction is not assured and 'never checked' is NOT 'up to date'. Many Nexus ")
          .Append("pages also host several independently-versioned files, so a cached 'differ' is often a false alarm. To ")
          .Append("confirm, feed these mods to " + ToolNames.NexusCheckUpdates + " using the 'id#fileid' form shown per row (the ")
          .Append("'verify:' token) — it checks each installed FILE's live status directly, clearing the multi-file-page ")
          .Append("false positive; then " + ToolNames.NexusMod + " changelog=true to see what changed. (Rows with no fileid can't ")
          .Append("be checked at file level — verify those by hand.)");
        return sb.ToString().TrimEnd('\n');
    }

    static string RenderAmethyst(UpdateCacheData d, int cap)
    {
        var sb = new StringBuilder();
        sb.Append("Amethyst update cache (LOCAL, no network) — mods library: ").Append(d.ModsDir).Append('\n');
        var flagged = d.Entries.Where(e => e.CachedHasUpdate == true && e.CachedIgnoreUpdate != true).ToList();
        var ignored = d.Entries.Where(e => e.CachedHasUpdate == true && e.CachedIgnoreUpdate == true).ToList();
        int current = d.Entries.Count(e => e.CachedHasUpdate == false);
        int noFlag = d.Entries.Count(e => e.CachedHasUpdate is null);
        sb.Append(d.Entries.Count).Append(" Nexus-linked mod(s): ")
          .Append(flagged.Count).Append(" update(s) flagged · ").Append(ignored.Count).Append(" ignored · ")
          .Append(current).Append(" no update flagged · ").Append(noFlag).Append(" without update state");
        if (d.UntrackedCount > 0) sb.Append("  (+").Append(d.UntrackedCount).Append(" non-Nexus mods/separators skipped)");
        sb.Append('\n');
        if (d.SnapshotWarning is not null) sb.Append("[!] ").Append(d.SnapshotWarning).Append('\n');
        foreach (var p in d.Problems) sb.Append("[!] ").Append(p).Append('\n');
        AppendAmethystEntries(sb, "Amethyst marks an update available", flagged, cap);
        AppendAmethystEntries(sb, "Amethyst marks the available update ignored", ignored, cap);
        sb.Append("\nnote: these are Amethyst's cached [General] hasUpdate/ignoreUpdate values from meta.ini; they are not a live Nexus check. ")
          .Append("Amethyst's singular fileid is the installed file ID used by the verify: token. Missing update state means ")
          .Append("the cache did not report a state, not that Nexus was checked recently.");
        return sb.ToString().TrimEnd('\n');
    }

    static void AppendAmethystEntries(StringBuilder sb, string label, IReadOnlyList<ModUpdateEntry> rows, int cap)
    {
        sb.Append('\n').Append(label).Append(" (").Append(rows.Count).Append("):");
        if (rows.Count == 0) { sb.Append(" none\n"); return; }
        sb.Append('\n');
        int shown = 0;
        foreach (var e in rows)
        {
            if (sb.Length >= cap) { sb.Append("  ... [").Append(rows.Count - shown).Append(" more omitted at max_chars=").Append(cap).Append("]\n"); break; }
            sb.Append("  - ").Append(e.Folder);
            if (e.Enabled == false) sb.Append("  [disabled]");
            sb.Append("  [id ").Append(e.ModId).Append("]  installed v").Append(e.Installed ?? "?")
              .Append(" · Amethyst cached v").Append(e.Newest ?? "?");
            if (e.InstalledFileIds.Count > 0)
                sb.Append("  · verify: ").Append(e.ModId).Append('#').Append(e.InstalledFileIds[0]);
            else sb.Append("  · no installed fileid");
            sb.Append('\n'); shown++;
        }
    }

    static void AppendEntries(StringBuilder sb, string label, IReadOnlyList<ModUpdateEntry> rows, int cap)
    {
        sb.Append('\n').Append(label).Append(" (").Append(rows.Count).Append("):");
        if (rows.Count == 0) { sb.Append(" none\n"); return; }
        sb.Append('\n');
        int shown = 0;
        foreach (var e in rows)
        {
            if (sb.Length >= cap) { sb.Append("  ... [").Append(rows.Count - shown).Append(" more omitted at max_chars=").Append(cap).Append("]\n"); break; }
            sb.Append("  - ").Append(e.Folder);
            if (e.Enabled == false) sb.Append("  [disabled]");
            sb.Append("  [id ").Append(e.ModId).Append("]  installed v").Append(e.Installed ?? "?")
              .Append(" · MO2 cached v").Append(e.Newest ?? "?");
            // The installed file ids are the join key for a file-level live check, which is what clears the multi-file-page false positive; a FOMOD or manual mod has none, and that is stated.
            if (e.InstalledFileIds.Count > 0)
                sb.Append("  · verify: ").Append(e.ModId).Append('#')
                  .Append(string.Join("#", e.InstalledFileIds));   // '#' joins fileids — ',' separates ENTRIES in the check grammar
            else
                sb.Append("  · no fileid (FOMOD/manual — file-level check n/a)");
            var checkedOn = StaleDay(e.LastUpdate);
            if (checkedOn is not null) sb.Append("  (checked ").Append(checkedOn).Append(')');
            sb.Append('\n'); shown++;
        }
    }

    /// <summary>Unix-seconds string → yyyy-MM-dd, or null if it isn't a usable timestamp.</summary>
    static string? StaleDay(string? unix)
    {
        if (long.TryParse(unix, out var s) && s > 0)
            try { return DateTimeOffset.FromUnixTimeSeconds(s).ToString("yyyy-MM-dd"); } catch { }
        return null;
    }
}
