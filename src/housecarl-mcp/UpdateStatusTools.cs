using System.Text;

namespace HousecarlMcp;

/// <summary>
/// houseCARL's LOCAL mod-update reader (no network). Reads MO2's OWN Nexus update cache — the modid / version /
/// newestVersion fields MO2 writes into each mod's meta.ini — and reports which installed mods MO2 already believes have
/// a newer version. This is the cheap FIRST pass of update triage: it narrows a big modlist to the handful worth
/// verifying online (housecarl_nexus_check_updates), reading only files MO2 has already populated. Works fully offline;
/// it does NOT touch Nexus and does NOT modify anything. Sits in the MO2-static-read lane beside housecarl_load_order_status.
/// </summary>
// Retained temporarily for upstream comparison; not registered because its cache format is MO2-specific.
static class UpdateStatusTools
{
    internal static string UpdateStatus(
        LoadOrderService svc,
        int max_chars = 0) => Guard.Tool("housecarl_update_status", () =>
    {
        if (svc.ConfigPromptOrNull() is { } prompt) return prompt;
        var data = svc.UpdateCache();
        return UpdateStatusWire.Render(data, max_chars > 0 ? max_chars : 40_000);
    });
}

/// <summary>Renders <see cref="UpdateCacheData"/>: a summary line, then the mods MO2 has a newer version cached for
/// (the actionable set), then the ignored set. Current / never-checked mods are counted, not listed (the point is the
/// FILTER). Every render ends with the Q3 honesty note — this is MO2's cached view, not a live Nexus check.</summary>
static class UpdateStatusWire
{
    public static string Render(UpdateCacheData d, int cap)
    {
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
          .Append("confirm, feed these mods to housecarl_nexus_check_updates using the 'id#fileid' form shown per row (the ")
          .Append("'verify:' token) — it checks each installed FILE's live status directly, clearing the multi-file-page ")
          .Append("false positive; then housecarl_nexus_mod changelog=true to see what changed. (Rows with no fileid can't ")
          .Append("be checked at file level — verify those by hand.)");
        return sb.ToString().TrimEnd('\n');
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
            // Surface the exact installed file id(s): the join key for a FILE-level live check (housecarl_nexus_check_updates
            // 'id#fileid' form) — the way to clear the multi-file-page false positive this cached diff can't tell from a real
            // update. A FOMOD/manual mod has none, and that's stated (Q3 — the file-level check can't run there, don't imply it can).
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
