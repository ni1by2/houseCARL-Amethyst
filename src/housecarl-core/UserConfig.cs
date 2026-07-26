using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HousecarlCore;

/// <summary>
/// The on-disk user config shape (houseCARL.user.json) — the values houseCARL persists for ITSELF at runtime, separate
/// from the shipped appsettings.json. Independent concerns share this one file: the Amethyst connection, diagnostic-log
/// paths, in-place consent, and pending-redeployment evidence. They MUST coexist — a write of one must never clobber
/// the others — which is why the only writer is <see cref="UserConfigStore.Update"/> (read-modify-write under a lock),
/// never a whole-object overwrite.
/// </summary>
public sealed class UserConfig
{
    /// <summary>The schema-v1 Amethyst connection manifest selected at runtime.</summary>
    public string? AmethystConnectionManifest { get; set; }

    /// <summary>Diagnostic-log directories saved by <c>housecarl_set_tool_path</c>, keyed by
    /// <c>papyrus_logs</c> or <c>crash_logs</c>. Legacy executable keys are ignored.</summary>
    public Dictionary<string, string>? ToolPaths { get; set; }

    /// <summary>Resolved on-disk plugin paths (normalized, lower-cased full paths) the user has acknowledged for
    /// IN-PLACE editing — the PERSISTENT, cross-session first-touch handshake of the in-place write lane. A path present
    /// here means the user accepted that houseCARL writes that ORIGINAL file in place (it will no longer be untouched);
    /// it waives the CONSENT axis ONLY, never the touched-record verify (a tool-capability fact no acknowledgement can
    /// override). Null/absent until the first in-place acknowledgement. The third independent concern in this file —
    /// like the other two it is read-modify-written ONLY through <see cref="UserConfigStore.Update"/> so it can never
    /// clobber (or be clobbered by) the connection or tool paths.</summary>
    public List<string>? InPlaceAcknowledged { get; set; }

    /// <summary>Staged writes that are not game-visible until Amethyst rebuilds and deploys. Null/absent means no
    /// pending verification; entries are replaced by profile plus canonical Data-relative path.</summary>
    public List<PendingAmethystWrite>? PendingAmethystWrites { get; set; }
}

/// <summary>
/// The single OWNER of houseCARL.user.json — every read and write of that file goes through here, so independent
/// concerns can never clobber each other's fields. Hardened per the
/// 2026-06-12 adversarial hunt (F3, hunter-PROVEN silent clobbers):
///   • ATOMIC — <see cref="Update"/> serializes to a sibling temp file and renames it over the target (same volume),
///     so a reader never sees a half-written file and a crash mid-write never corrupts the saved config.
///   • CROSS-PROCESS — the read-modify-write runs under a NAMED mutex derived from the file path, so two server
///     processes sharing the file (CLI plugin + desktop app) serialize instead of clobbering each other's field
///     (the old gate was process-local only).
///   • CORRUPT = LOUD — an unparseable file is BACKED UP beside itself (.corrupt.bak) and REPORTED via the returned
///     note, never silently treated as blank (the old path silently wiped every saved setting on the next Update).
/// Best-effort + HONEST: a write failure (e.g. a read-only data dir) is RETURNED, not thrown or swallowed, so the
/// calling tool can tell the user the choice won't survive a restart. One instance is registered as a singleton and
/// shared by the MCP load-order service and tool bridge.
/// </summary>
public sealed class UserConfigStore
{
    /// <summary>Absolute or caller-selected path to the one configuration file this store owns.</summary>
    readonly string _path;

    /// <summary>Fast in-process serialization gate used before the cross-process mutex.</summary>
    readonly object _gate = new();      // process-local fast path; the named mutex below adds the cross-process half

    /// <summary>Per-config named mutex shared by concurrent Codex/Claude server processes.</summary>
    readonly Mutex _mutex;              // named per-file: CLI + desktop server processes serialize on the same config

    /// <summary>Stable human-readable serialization settings for persisted user state.</summary>
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Creates the sole read/write owner for one user configuration file.</summary>
    /// <param name="path">Native path to houseCARL.user.json; the file may not exist yet.</param>
    public UserConfigStore(string path)
    {
        _path = path;
        _mutex = new Mutex(initiallyOwned: false, MutexName(path));
    }

    /// <summary>The file this store owns (for the tool confirmation / diagnostics).</summary>
    public string FilePath => _path;

    /// <summary>A stable, legal mutex name for the config file: same file (case-insensitively) ⇒ same mutex in any
    /// process of this session. The stable prefix keeps Codex and Claude server instances on the same lock.</summary>
    /// <param name="path">Configuration path whose normalized identity scopes the lock.</param>
    /// <returns>A short deterministic name that does not expose the user's full filesystem path.</returns>
    static string MutexName(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant()));
        return "Local\\houseCARL-user-config-" + Convert.ToHexString(hash, 0, 12);
    }

    /// <summary>Run <paramref name="body"/> holding BOTH locks. An abandoned mutex (the other process died holding it)
    /// counts as acquired — the file itself stays consistent because writes are atomic renames. A timeout proceeds
    /// WITHOUT the cross-process half rather than deadlocking a tool call forever; the process-local gate still holds,
    /// and the atomic rename bounds the damage to last-write-wins (never a torn file).</summary>
    /// <typeparam name="T">Value produced while both gates are held.</typeparam>
    /// <param name="body">Read or read-modify-write operation to serialize.</param>
    /// <returns>The operation's value.</returns>
    T WithLocks<T>(Func<T> body)
    {
        lock (_gate)
        {
            bool taken = false;
            try { taken = _mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { taken = true; }
            try { return body(); }
            finally { if (taken) _mutex.ReleaseMutex(); }
        }
    }

    /// <summary>Read the current config. A missing file yields a fresh blank <see cref="UserConfig"/>; a CORRUPT file is
    /// backed up beside itself and reported via <paramref name="note"/> (Q3 — never silently "nothing saved yet"), then
    /// also yields blank so a tool call still proceeds.</summary>
    /// <param name="note">Recovery/read warning for the caller to surface, or null after a normal read.</param>
    /// <returns>The parsed configuration or an explicitly reported blank fallback.</returns>
    public UserConfig Load(out string? note)
    {
        var (cfg, n) = WithLocks(() => { var c = ReadOrRecover(out var rn); return (c, rn); });
        note = n;
        return cfg;
    }

    /// <summary>Read the current config, discarding any recovery note — for callers that only need the values and a
    /// later <see cref="Update"/> (which re-reports) or the boot path's noted Load owns the loudness.</summary>
    /// <returns>The parsed configuration or blank fallback.</returns>
    public UserConfig Load() => Load(out _);

    /// <summary>Apply <paramref name="mutate"/> to the CURRENT on-disk config and write it back ATOMICALLY (temp +
    /// rename) — the ONLY way the file is written, so independent concerns merge instead of overwriting. Returns (ok, error,
    /// note): a write failure is reported in <c>error</c>, not thrown (Q3 — "works this session, won't persist"); a
    /// corrupt prior file is backed up and named in <c>note</c> even when the write itself succeeds, so a recovery is
    /// never silent. The whole read-modify-write runs under the cross-process lock.</summary>
    /// <param name="mutate">In-memory change applied to the latest state while both locks are held.</param>
    /// <returns>Persistence success, write error, and any corrupt-file recovery note.</returns>
    public (bool ok, string? error, string? note) Update(Action<UserConfig> mutate)
    {
        return WithLocks<(bool, string?, string?)>(() =>
        {
            string? note = null;
            try
            {
                var cfg = ReadOrRecover(out note);
                mutate(cfg);
                var dir = Path.GetDirectoryName(_path);   // the data dir (${CLAUDE_PLUGIN_DATA}) may not exist on the first save
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, Json));
                AtomicFile.Commit(tmp, _path);   // crash-atomic swap (File.Replace / rename) — a reader never sees a torn or vanished file
                return (true, null, note);
            }
            catch (Exception ex) { return (false, ex.Message, note); }
        });
    }

    /// <summary>True iff <paramref name="pluginPath"/> already carries a PERSISTED in-place acknowledgement — the
    /// cross-session first-touch handshake (in-place write lane). Normalized full-path compare, so a file is identified
    /// the same way it was recorded regardless of the caller's path spelling. FAIL-SAFE (Q3): a missing / unreadable /
    /// corrupt config reads as NOT acknowledged, so the handshake re-prompts rather than silently proceeding to write a
    /// user's original. Waives the CONSENT axis only — the touched-record verify still runs.</summary>
    /// <param name="pluginPath">Physical staging plugin path proposed for in-place editing.</param>
    /// <returns>True only when an equivalent path is present in persisted consent state.</returns>
    public bool IsInPlaceAcknowledged(string pluginPath)
    {
        var key = NormalizePath(pluginPath);
        var ack = Load().InPlaceAcknowledged;
        return ack is not null && ack.Any(p => string.Equals(NormalizePath(p), key, StringComparison.Ordinal));
    }

    /// <summary>PERSIST an in-place acknowledgement for <paramref name="pluginPath"/> (idempotent — never duplicated),
    /// through the same atomic read-modify-write as every other field so it can never clobber the connection or tool
    /// paths sharing this file. Returns (ok, error): a write failure is RETURNED, not thrown (Q3 — the caller can tell
    /// the user the edit proceeded but the acknowledgement won't survive a restart, so the next session re-prompts).</summary>
    /// <param name="pluginPath">Physical staging plugin whose risk the user acknowledged.</param>
    /// <returns>Persistence success and an actionable write error.</returns>
    public (bool ok, string? error) RecordInPlaceAcknowledged(string pluginPath)
    {
        var key = NormalizePath(pluginPath);
        var (ok, error, _) = Update(cfg =>
        {
            cfg.InPlaceAcknowledged ??= new List<string>();
            if (!cfg.InPlaceAcknowledged.Any(p => string.Equals(NormalizePath(p), key, StringComparison.Ordinal)))
                cfg.InPlaceAcknowledged.Add(key);
        });
        return (ok, error);
    }

    /// <summary>Adds or replaces one pending write for the same profile and Data path.</summary>
    /// <param name="pending">Completed staging write awaiting manager-visible verification.</param>
    /// <returns>Persistence success and an actionable error when saving failed.</returns>
    public (bool ok, string? error) RecordPendingAmethystWrite(PendingAmethystWrite pending)
    {
        var (ok, error, _) = Update(cfg =>
        {
            cfg.PendingAmethystWrites ??= new List<PendingAmethystWrite>();
            cfg.PendingAmethystWrites.RemoveAll(x =>
                x.ProfileName == pending.ProfileName
                && x.DataRelativePath.Equals(pending.DataRelativePath, StringComparison.OrdinalIgnoreCase));
            cfg.PendingAmethystWrites.Add(pending);
        });
        return (ok, error);
    }

    /// <summary>Clears only writes proven visible after a later Amethyst deployment.</summary>
    /// <param name="snapshot">Fresh complete manager state used by the fail-closed verification gate.</param>
    /// <returns>Counts cleared/remaining plus an error when updated state could not be persisted.</returns>
    public (int cleared, int remaining, string? error) VerifyPendingAmethystWrites(ManagerSnapshot snapshot)
    {
        var current = Load().PendingAmethystWrites;
        if (current is null || current.Count == 0) return (0, 0, null);
        var cleared = 0;
        var (ok, error, _) = Update(cfg =>
        {
            cfg.PendingAmethystWrites ??= new List<PendingAmethystWrite>();
            cleared = cfg.PendingAmethystWrites.RemoveAll(x => AmethystRedeploy.IsVerified(x, snapshot));
        });
        var remaining = Load().PendingAmethystWrites?.Count ?? 0;
        return (cleared, remaining, ok ? null : error);
    }

    /// <summary>Returns a detached view for status rendering; callers cannot mutate the stored list.</summary>
    /// <returns>Snapshot array of pending writes, or an empty array when none are stored.</returns>
    public IReadOnlyList<PendingAmethystWrite> PendingAmethystWrites() =>
        Load().PendingAmethystWrites?.ToArray() ?? Array.Empty<PendingAmethystWrite>();

    /// <summary>Canonical identity for an in-place acknowledgement: the full, lower-cased path, so the same on-disk file
    /// matches whatever path spelling reaches the check. Best-effort — an un-rootable string falls back to a trimmed
    /// lower-case compare rather than throwing (the worst case is a redundant re-prompt, never a wrong waiver).</summary>
    /// <param name="p">Plugin path from a call or persisted acknowledgement.</param>
    /// <returns>Best-effort stable comparison key.</returns>
    static string NormalizePath(string p)
    {
        try { return Path.GetFullPath(p).ToLowerInvariant(); }
        catch { return p.Trim().ToLowerInvariant(); }
    }

    /// <summary>The tolerant-but-LOUD read (hunt F3): missing ⇒ blank, parseable ⇒ as saved, CORRUPT ⇒ back the file up
    /// beside itself (.corrupt.bak — kept until the user deletes it; re-copied while the corrupt file persists) and
    /// return blank with a note naming the backup and what was lost. The corrupt original is COPIED, not moved, so a
    /// read never destroys evidence; the next successful <see cref="Update"/> replaces it with a clean file.</summary>
    /// <param name="note">Recovery or read-failure explanation; null for a normal read.</param>
    /// <returns>Parsed configuration, or a blank configuration after an explicitly reported failure.</returns>
    UserConfig ReadOrRecover(out string? note)
    {
        note = null;
        if (!File.Exists(_path)) return new UserConfig();
        string text;
        try { text = File.ReadAllText(_path); }
        catch (Exception ex)
        {
            note = $"could not read '{_path}' ({ex.Message}) — proceeding as if nothing were saved; the file was left untouched.";
            return new UserConfig();
        }
        try { return JsonSerializer.Deserialize<UserConfig>(text) ?? new UserConfig(); }
        catch (Exception ex)
        {
            var backup = _path + ".corrupt.bak";
            try
            {
                File.Copy(_path, backup, overwrite: true);
                note = $"houseCARL.user.json was unreadable (corrupt JSON: {ex.Message}). The corrupt file was backed up to " +
                       $"'{backup}'; previously saved settings (Amethyst connection / tool paths) are NOT loaded and need re-saving.";
            }
            catch (Exception bex)
            {
                note = $"houseCARL.user.json is unreadable (corrupt JSON: {ex.Message}) AND backing it up failed ({bex.Message}). " +
                       $"The corrupt file remains at '{_path}'; previously saved settings are NOT loaded.";
            }
            return new UserConfig();
        }
    }
}
