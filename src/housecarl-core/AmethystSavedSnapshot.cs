using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using MessagePack;
using Microsoft.Data.Sqlite;
using Org.BouncyCastle.Crypto.Digests;

namespace HousecarlCore;

/// <summary>A complete saved schema-9 filegraph. Amethyst's own native reader restores its winners from saved inputs.</summary>
public sealed class AmethystSavedSnapshot
{
    /// <summary>The currently tested producer contract, Amethyst Testing 97499673 (native/Python filegraph trees unchanged from 2f54fdd).</summary>
    public const ulong RulesRevision = 13;
    /// <summary>The selected profile and catalog generation, with an unverified-freshness warning.</summary>
    public AmethystSnapshotProvenance Provenance { get; private set; } = null!;
    /// <summary>Canonical profile folder selected by deploy_state.json.</summary>
    public string ProfileDir { get; private set; } = "";
    /// <summary>Effective staging mods root; shared or profile-specific.</summary>
    public string ModsDir { get; private set; } = "";
    /// <summary>Effective overwrite provider root.</summary>
    public string OverwriteDir { get; private set; } = "";
    /// <summary>Effective root-folder provider root.</summary>
    public string RootFolderDir { get; private set; } = "";
    /// <summary>Effective game installation root, including a non-default profile's override.</summary>
    public string GameDir { get; private set; } = "";
    /// <summary>The separately preserved vanilla Data_Core, or undeployed Data; never merged deployed Data.</summary>
    public string VanillaDataDir { get; private set; } = "";
    /// <summary>The profile's saved filegraph catalog.</summary>
    public string DatabasePath { get; private set; } = "";
    /// <summary>Profile text composition, used only for plugin order/activation and user diagnostics.</summary>
    public Mo2Composition Composition { get; private set; } = null!;
    /// <summary>Exact staged or vanilla paths of active plugins, in the profile's load order.</summary>
    public IReadOnlyList<string> OrderedPluginPaths { get; private set; } = [];
    /// <summary>Captured input contents and identity stamps, including absent optional files.</summary>
    readonly Dictionary<string, (byte[]? Bytes, LinuxFileIdentity? Identity)> _inputs = new(StringComparer.Ordinal);
    /// <summary>Source identities observed during this call; checked again before publishing its results.</summary>
    readonly Dictionary<string, LinuxFileIdentity> _readSources = new(StringComparer.Ordinal);
    /// <summary>New result artifacts created during this call, removable if final validation refuses.</summary>
    readonly Dictionary<string, byte[]?> _artifacts = new(StringComparer.Ordinal);
    /// <summary>Canonical source roots, used to refuse escaping links and root changes during a call.</summary>
    readonly Dictionary<string, string> _roots = new(StringComparer.Ordinal);
    /// <summary>All candidate rows of the profile's selected variants, including disabled-mod source lookup.</summary>
    readonly Dictionary<long, Candidate> _candidates = [];
    /// <summary>Producer-restored winners, indexed by their saved namespace and destination key.</summary>
    readonly Dictionary<(int Namespace, string Destination), long> _winners = [];
    /// <summary>Active providers per destination; only the saved winner determines which provider wins.</summary>
    readonly Dictionary<string, List<Candidate>> _providers = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Saved rank inputs order diagnostic copies only; winner IDs always come from the producer's native snapshot.</summary>
    readonly Dictionary<string, long> _modRanks = [], _archiveRanks = [], _pluginRanks = [];
    /// <summary>The database path identity at capture, independent of WAL or main-file content stamps.</summary>
    LinuxFileIdentity _databaseIdentity;
    /// <summary>Saved intent used only during capture, then disposed; it is never substituted for live freshness evidence.</summary>
    JsonDocument _intent = null!;

    /// <summary>One persisted provider's destination, exact source, saved variant, and catalog content stamp.</summary>
    /// <param name="Id">Candidate identifier used by saved winner rows.</param>
    /// <param name="Kind">Producer kind: loose 0, root 1, overwrite 2, archive member 3.</param>
    /// <param name="Mod">Exact source mod folder or special provider name.</param>
    /// <param name="ModKey">Producer's normalized mod identity.</param>
    /// <param name="Target">Producer destination domain; Skyrim's game domain is supported here.</param>
    /// <param name="Destination">Safe game-relative key; casing does not choose the winner.</param>
    /// <param name="Source">Exact native source-relative path, preserved from UTF-8 bytes.</param>
    /// <param name="LegacyRel">Producer's archive-entry or post-strip path.</param>
    /// <param name="Size">Raw source file size, including the archive size for an archive member.</param>
    /// <param name="MtimeNs">Raw source last-write time in nanoseconds.</param>
    /// <param name="Enabled">Whether the saved profile enables this provider.</param>
    /// <param name="Deployable">Whether the producer permits deployment of a loose candidate.</param>
    /// <param name="Identities">Producer's saved exclusive identity groups, unsupported for this Skyrim contract.</param>
    /// <param name="PluginKey">Producer's plugin binding for an archive; never inferred from its staged folder.</param>
    /// <param name="ArchiveKey">Producer's saved composite archive identity.</param>
    /// <param name="Ordinal">Producer's inventory ordering of this raw file.</param>
    sealed record Candidate(long Id, int Kind, string Mod, string ModKey, string Target,
        string Destination, string Source, string LegacyRel, long Size, long MtimeNs,
        bool Enabled, bool Deployable, List<string> Identities, string? PluginKey, string? ArchiveKey, uint Ordinal)
    {
        /// <summary>The namespace dictated by the producer's stored provider kind.</summary>
        public int Namespace => Kind == 1 ? 1 : Kind == 3 ? 2 : 0;
    }

    /// <summary>Capture one saved generation without invoking any manager mutation or migration.</summary>
    /// <param name="manifestPath">Explicitly selected schema-v1 connection manifest.</param>
    /// <returns>A complete read-only capture; unknown versions and inconsistent state throw actionable errors.</returns>
    public static AmethystSavedSnapshot Capture(string manifestPath)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("Amethyst saved-snapshot mode requires Linux");
        var snapshot = new AmethystSavedSnapshot();
        try { snapshot.Read(manifestPath); return snapshot; }
        catch (Exception ex) when (ex is SqliteException or JsonException or MessagePackSerializationException or DecoderFallbackException)
        { throw new InvalidOperationException($"cannot read Amethyst's saved filegraph: {ex.Message}; refresh the selected profile in Amethyst and retry", ex); }
        finally { snapshot._intent?.Dispose(); }
    }

    /// <summary>Read connection roots, one SQLite transaction, and the selected profile's plugin paths.</summary>
    void Read(string manifestPath)
    {
        Input(manifestPath, required: true);
        var connection = AmethystConnection.Open(manifestPath);
        using var paths = JsonDocument.Parse(Input(connection.PathsFile, true)!);
        using var deploy = JsonDocument.Parse(Input(connection.DeployStateFile, true)!);
        var profileName = deploy.RootElement.TryGetProperty("last_active_profile", out var active)
            ? active.GetString() ?? throw new InvalidOperationException("Amethyst last_active_profile is empty") : "default";
        SafePart(profileName, "selected profile");
        ProfileDir = Root(Path.Combine(connection.ProfileRoot, "profiles", profileName));
        using var state = JsonDocument.Parse(Input(Path.Combine(ProfileDir, "profile_state.json"), false) ?? "{}"u8.ToArray());
        JsonElement settings = default;
        if (state.RootElement.TryGetProperty("profile_settings", out var currentSettings)) settings = currentSettings;
        else if (Input(Path.Combine(ProfileDir, "profile_settings.json"), false) is { } legacy)
        {
            using var legacySettings = JsonDocument.Parse(legacy);
            settings = legacySettings.RootElement.Clone();
        }
        var specific = settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("profile_specific_mods", out var specificValue)
            && specificValue.ValueKind == JsonValueKind.True;
        var library = Root(specific ? ProfileDir : connection.ProfileRoot);
        ModsDir = Root(Path.Combine(library, "mods"));
        OverwriteDir = Path.Combine(library, "overwrite");
        RootFolderDir = Path.Combine(library, "Root_Folder");
        var game = paths.RootElement.GetProperty("game_path").GetString()
            ?? throw new InvalidOperationException("Amethyst paths.json has no game_path; configure the game in Amethyst");
        if (profileName != "default" && settings.ValueKind == JsonValueKind.Object &&
            settings.TryGetProperty("game_path", out var overridePath) && overridePath.GetString() is { Length: > 0 } selectedGame)
            game = selectedGame;
        GameDir = Root(game);
        var core = Path.Combine(GameDir, "Data_Core");
        var deployed = deploy.RootElement.TryGetProperty("deploy_active", out var flag) && flag.ValueKind == JsonValueKind.True;
        if (!Directory.Exists(core) && deployed)
            throw new InvalidOperationException("Amethyst is deployed but its vanilla Data_Core is missing; restore or repair the vanilla backup in Amethyst before reading");
        VanillaDataDir = Root(Directory.Exists(core) ? core : Path.Combine(GameDir, "Data"));
        DatabasePath = Path.Combine(library, "filegraph.sqlite3");
        _databaseIdentity = LinuxFileIdentity.Of(DatabasePath);
        using (var database = OpenDatabase(DatabasePath))
        {
            Execute(database, "BEGIN");
            if (Convert.ToInt32(Scalar(database, "PRAGMA user_version")) != 9 ||
                MetaNumber(database, "engine_revision") != 1 || MetaNumber(database, "rules_revision") != RulesRevision)
                throw new InvalidOperationException("unsupported Amethyst filegraph schema or engine/rules revision; this build supports schema 9, engine 1, rules 13");
            if (!Meta(database, "ready").AsSpan().SequenceEqual(new byte[] { 1 }))
                throw new InvalidOperationException("Amethyst filegraph catalog is not ready; refresh it in Amethyst");
            var inventory = MetaNumber(database, "inventory_generation");
            if (inventory == 0) throw new InvalidOperationException("Amethyst filegraph inventory is empty; refresh it in Amethyst");
            if (Convert.ToInt64(Scalar(database, "SELECT COUNT(*) FROM operations WHERE state NOT IN ('committed','failed')")) != 0)
                throw new InvalidOperationException("Amethyst has an unfinished filegraph operation; finish Refresh or recover Deploy/Restore in Amethyst");
            using (var foreignKeys = Command(database, "PRAGMA foreign_key_check"))
            using (var rows = foreignKeys.ExecuteReader())
                if (rows.Read()) throw new InvalidOperationException("Amethyst filegraph has broken joins; repair it with Amethyst Refresh");
            ReadProfile(database, inventory);
            ReadCandidates(database);
            ReadWinners(database);
            Execute(database, "ROLLBACK");
        }
        Composition = ReadComposition();
        OrderedPluginPaths = ResolvePlugins();
        CheckUnchanged();
    }

    /// <summary>Check the saved profile's intent and generation; serialized payload contents must agree with their columns.</summary>
    void ReadProfile(SqliteConnection database, ulong inventory)
    {
        using var query = Command(database, "SELECT generation,inventory_generation,ready,intent_hash,rules_hash,intent_payload FROM profiles WHERE profile_id=$profile");
        using var row = query.ExecuteReader();
        if (!row.Read()) throw new InvalidOperationException($"selected Amethyst profile is absent from filegraph: '{ProfileDir}'; open and refresh that profile in Amethyst");
        var generation = row.GetInt64(0);
        if (generation <= 0 || row.GetInt64(2) != 1 || row.GetInt64(1) != checked((long)inventory))
            throw new InvalidOperationException("Amethyst profile graph is incomplete or behind its inventory; refresh the selected profile");
        var intentHash = row.GetFieldValue<byte[]>(3);
        var rulesHash = row.GetFieldValue<byte[]>(4);
        if (intentHash.Length != 24 || rulesHash.Length != 20)
            throw new InvalidOperationException("Amethyst profile fingerprints are malformed; refresh the selected profile");
        _intent = AmethystIntentPayload.Read(row.GetFieldValue<byte[]>(5));
        var intent = _intent.RootElement;
        if (intent.GetProperty("profile_id").GetString() != ProfileDir ||
            !PayloadBytes(intent.GetProperty("intent_hash")).AsSpan().SequenceEqual(intentHash) ||
            !PayloadBytes(intent.GetProperty("rules_hash")).AsSpan().SequenceEqual(rulesHash))
            throw new InvalidOperationException("Amethyst profile intent payload disagrees with its saved identity/fingerprints");
        if (!intent.GetProperty("loose_beats_archive").GetBoolean())
            throw new InvalidOperationException("unsupported Amethyst asset precedence for Skyrim; select a Skyrim SE profile");
        var digest = new Blake2bDigest(192);
        foreach (var name in new[] { "modlist.txt", "profile_state.json", "plugins.txt", "loadorder.txt" })
        {
            var label = Encoding.UTF8.GetBytes(name);
            digest.BlockUpdate(label, 0, label.Length);
            var bytes = Input(Path.Combine(ProfileDir, name), name is "modlist.txt" or "loadorder.txt") ?? new byte[] { 0 };
            digest.BlockUpdate(bytes, 0, bytes.Length);
        }
        var current = new byte[24]; digest.DoFinal(current, 0);
        if (!current.AsSpan().SequenceEqual(intentHash))
            throw new InvalidOperationException("Amethyst profile intent changed since its last saved filegraph; refresh the selected profile before reading");
        var vanillaArchivesOmitted = Directory.EnumerateFiles(VanillaDataDir).Any(p => Path.GetExtension(p).Equals(".bsa", StringComparison.OrdinalIgnoreCase));
        Provenance = new(ProfileDir, generation, inventory, vanillaArchivesOmitted);
    }

    /// <summary>Read only this profile's selected persisted variants; dynamic routing has already been performed by Amethyst.</summary>
    void ReadCandidates(SqliteConnection database)
    {
        var modEntries = _intent.RootElement.GetProperty("mods").EnumerateArray().ToList();
        for (var i = 0; i < modEntries.Count; i++) _modRanks[modEntries[i].GetProperty("key").GetString()!] = modEntries.Count - i;
        foreach (var name in new[] { "archive_order", "plugin_order" })
        {
            var ranks = name == "archive_order" ? _archiveRanks : _pluginRanks;
            long index = 1;
            foreach (var value in _intent.RootElement.GetProperty(name).EnumerateArray()) ranks[value.GetString()!.ToLowerInvariant()] = index++;
        }
        var mods = _intent.RootElement.GetProperty("mods").EnumerateArray().ToDictionary(
            m => m.GetProperty("key").GetString()!, m => (Variant: m.GetProperty("variant_key").GetString()!, Enabled: m.GetProperty("enabled").GetBoolean()), StringComparer.Ordinal);
        var specials = _intent.RootElement.GetProperty("special_variants").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
        using (var query = Command(database, "SELECT m.name_key,pm.enabled,rv.variant_key FROM profile_mods pm JOIN mods m USING(mod_id) LEFT JOIN route_variants rv USING(variant_id) WHERE pm.profile_id=$profile"))
        using (var rows = query.ExecuteReader())
        {
            var found = new HashSet<string>(StringComparer.Ordinal);
            while (rows.Read())
            {
                var key = rows.GetString(0);
                if (!mods.TryGetValue(key, out var mod) || rows.IsDBNull(2) || mod.Variant != rows.GetString(2) || mod.Enabled != (rows.GetInt64(1) != 0))
                    throw new InvalidOperationException("Amethyst selected profile variants disagree with saved intent; refresh the profile");
                found.Add(key);
            }
            if (!found.SetEquals(mods.Keys)) throw new InvalidOperationException("Amethyst profile is missing required mod manifests; refresh the profile");
        }
        using (var query = Command(database, """
            SELECT c.candidate_id,c.provider_kind,m.name_display,m.name_key,t.target_key,d.path_key,
                   rf.source_rel,c.legacy_rel,rf.size,rf.mtime_ns,c.deployable,rv.variant_key,rf.mod_id,rv.mod_id,c.plugin_key,c.archive_key,rf.ordinal
            FROM candidates c JOIN route_variants rv USING(variant_id) JOIN mods m ON m.mod_id=rv.mod_id
            JOIN raw_files rf USING(file_id) JOIN destinations d USING(destination_id) JOIN targets t USING(target_id)
            """))
        using (var rows = query.ExecuteReader())
        {
            while (rows.Read())
            {
                var modKey = rows.GetString(3);
                var variant = rows.GetString(11);
                var selected = mods.TryGetValue(modKey, out var mod) ? mod.Variant : specials.GetValueOrDefault(modKey);
                if (selected != variant) continue;
                var kind = checked((int)rows.GetInt64(1));
                if (kind is < 0 or > 3 || rows.GetInt64(12) != rows.GetInt64(13))
                    throw new InvalidOperationException("Amethyst candidate kind or source-mod join is invalid");
                var name = rows.GetString(2);
                if (!specials.ContainsKey(modKey)) SafePart(name, "mod folder");
                var candidate = new Candidate(rows.GetInt64(0), kind, name, modKey, rows.GetString(4),
                    Relative(rows.GetFieldValue<byte[]>(5)), Relative(rows.GetFieldValue<byte[]>(6)), rows.GetString(7),
                    rows.GetInt64(8), rows.GetInt64(9), specials.ContainsKey(modKey) || mod.Enabled, rows.GetInt64(10) != 0, [], rows.IsDBNull(14) ? null : rows.GetString(14),
                    rows.IsDBNull(15) ? null : rows.GetString(15), checked((uint)rows.GetInt64(16)));
                AssetResolver.ValidateRelPath(candidate.LegacyRel);
                _candidates.Add(candidate.Id, candidate);
            }
        }
        using var identities = Command(database, "SELECT candidate_id,identity_key FROM candidate_identities");
        using var identitiesReader = identities.ExecuteReader();
        while (identitiesReader.Read())
            if (_candidates.TryGetValue(identitiesReader.GetInt64(0), out var candidate))
                candidate.Identities.Add(Convert.ToHexString(identitiesReader.GetFieldValue<byte[]>(1)));
    }

    /// <summary>Use the same native snapshot restoration as Amethyst; its SQL winner projection can omit valid entries.</summary>
    void ReadWinners(SqliteConnection database)
    {
        // Testing 2f54fdd derives exclusive identities only for BG3 .pak files, never Skyrim.
        // Refuse an unexpected identity contract rather than emulate another game's suppression rules.
        if (_candidates.Values.Any(c => c.Identities.Count > 0))
            throw new InvalidOperationException("unsupported exclusive-identity candidates in a Skyrim saved filegraph");
        AmethystNativeSnapshot.Read(database, ProfileDir, Provenance.Generation, Provenance.InventoryGeneration, row =>
        {
            var ns = row.GetProperty("namespace").GetString() switch
            { "normal" => 0, "root" => 1, "archive" => 2, _ => throw new InvalidOperationException("unsupported Amethyst winner namespace") };
            var id = row.GetProperty("candidate_id").GetInt64();
            var destination = Relative(PayloadBytes(row.GetProperty("destination_key")));
            if (!_candidates.TryGetValue(id, out var candidate) || candidate.Namespace != ns || candidate.Destination != destination ||
                candidate.Target != row.GetProperty("target").GetString() || candidate.ModKey != row.GetProperty("mod_key").GetString() ||
                candidate.Source != Relative(PayloadBytes(row.GetProperty("source_rel"))) || !candidate.Enabled)
                throw new InvalidOperationException("Amethyst native snapshot winner joins are incomplete or inconsistent");
            if (candidate.Target != "game") throw new InvalidOperationException($"unsupported Skyrim filegraph target '{candidate.Target}'");
            if (!_winners.TryAdd((ns, destination), id)) throw new InvalidOperationException("Amethyst native snapshot contains duplicate winners");
        });
        foreach (var candidate in _candidates.Values)
        {
            if (!candidate.Enabled || candidate.Target != "game" || (candidate.Kind != 3 && !candidate.Deployable)) continue;
            if (candidate.Kind != 3 && _intent.RootElement.GetProperty("disabled_plugin_paths").TryGetProperty(candidate.ModKey, out var disabled) &&
                !candidate.LegacyRel.Contains('/') && !candidate.LegacyRel.Contains('\\') &&
                disabled.EnumerateArray().Any(path => Encoding.UTF8.GetString(PayloadBytes(path)) == candidate.LegacyRel.ToLowerInvariant())) continue;
            if (!_providers.TryGetValue(candidate.Destination, out var providers)) _providers[candidate.Destination] = providers = [];
            providers.Add(candidate);
        }
        foreach (var providers in _providers.Values) providers.Sort((left, right) => SavedCopyRank(right).CompareTo(SavedCopyRank(left)));
        // A recorded winner must survive all saved selection checks; inconsistent data is never a guessed winner.
        foreach (var pair in _winners)
            if (!_providers.GetValueOrDefault(pair.Key.Destination, []).Any(c => c.Id == pair.Value))
                throw new InvalidOperationException("Amethyst saved winner is excluded by its own profile intent");
    }

    /// <summary>Order copies by the pinned producer's saved rank inputs; this never selects or repairs a winner.</summary>
    (long Primary, long Secondary, uint Ordinal, long Id) SavedCopyRank(Candidate candidate)
    {
        var normal = candidate.Kind == 2 ? long.MaxValue / 4 : _modRanks.GetValueOrDefault(candidate.ModKey);
        if (candidate.Kind != 3) return (normal, normal, candidate.Ordinal, candidate.Id);
        var archive = candidate.ArchiveKey is { } key ? _archiveRanks.GetValueOrDefault(key.ToLowerInvariant(), normal) : normal;
        return candidate.PluginKey is { } plugin
            ? (long.MaxValue / 8 + _pluginRanks.GetValueOrDefault(plugin.ToLowerInvariant(), normal), archive, candidate.Ordinal, candidate.Id)
            : (archive, normal, candidate.Ordinal, candidate.Id);
    }

    /// <summary>Read plugin order and activation from the already-hashed profile inputs; file ownership comes only from the graph.</summary>
    Mo2Composition ReadComposition()
    {
        var mods = _intent.RootElement.GetProperty("mods").EnumerateArray().ToList();
        var enabled = mods.Where(m => m.GetProperty("enabled").GetBoolean()).Select(m => m.GetProperty("name").GetString()!).ToList();
        var disabled = mods.Where(m => !m.GetProperty("enabled").GetBoolean()).Select(m => m.GetProperty("name").GetString()!).ToList();
        var ordered = Lines("loadorder.txt").Select(p => p.TrimStart('*')).ToList();
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inactive = new List<string>();
        foreach (var name in Lines("plugins.txt"))
            if (name.StartsWith('*')) active.Add(name[1..]); else inactive.Add(name);
        var implicitNames = ordered.Where(p => !active.Contains(p) && !inactive.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
        return new(enabled, disabled, ordered, active, inactive, implicitNames);
    }

    /// <summary>Resolve active plugin filenames through saved Data winners and the distinct vanilla backup.</summary>
    IReadOnlyList<string> ResolvePlugins()
    {
        var paths = new List<string>();
        foreach (var plugin in Composition.OrderedPluginNames)
        {
            SafePart(plugin, "plugin filename");
            if (Composition.InactivePluginNames.Contains(plugin, StringComparer.OrdinalIgnoreCase)) continue;
            var sources = ResolveAssets(plugin);
            if (sources.Count == 0 || sources[0].Kind != AssetKind.Loose)
                throw new InvalidOperationException($"active plugin '{plugin}' has no saved filegraph or vanilla source; refresh Amethyst");
            paths.Add(sources[0].LooseFilePath!);
        }
        return paths;
    }

    /// <summary>Read exact sources for one Data-relative asset. Saved root winners overlay normal winners; loose wins over archive.</summary>
    /// <param name="relative">Validated Bethesda Data-relative path.</param>
    /// <returns>Recorded winners first within each namespace; additional saved copies never choose the winner.</returns>
    public IReadOnlyList<PlacementSource> ResolveAssets(string relative)
    {
        var rel = AssetResolver.ValidateRelPath(relative).Replace('\\', '/');
        var destination = "Data/" + rel;
        var result = new List<PlacementSource>();
        if (_providers.TryGetValue(destination, out var candidates))
        {
            foreach (var ns in new[] { 1, 0, 2 })
            {
                if (!_winners.TryGetValue((ns, destination.ToLowerInvariant()), out var winner))
                    winner = _winners.FirstOrDefault(p => p.Key.Namespace == ns && p.Key.Destination.Equals(destination, StringComparison.OrdinalIgnoreCase)).Value;
                if (winner == 0) continue;
                foreach (var candidate in candidates.Where(c => c.Namespace == ns).OrderByDescending(c => c.Id == winner))
                {
                    var path = SourcePath(candidate);
                    result.Add(candidate.Kind == 3
                        ? new(Path.GetFileName(path), AssetKind.Bsa, null, path, candidate.LegacyRel.Replace('/', '\\'), OwningMod: candidate.Mod)
                        : new(candidate.ModKey == "[overwrite]" ? "overwrite" : candidate.Mod, AssetKind.Loose, path, null, rel.Replace('/', '\\')));
                }
            }
        }
        var vanilla = VanillaFile(rel);
        if (vanilla is not null)
        {
            var source = new PlacementSource("Data", AssetKind.Loose, vanilla, null, rel.Replace('/', '\\'));
            var firstArchive = result.FindIndex(s => s.Kind == AssetKind.Bsa);
            result.Insert(firstArchive < 0 ? result.Count : firstArchive, source);
        }
        return result;
    }

    /// <summary>Saved Data-relative asset keys; this does not discover out-of-band staging additions.</summary>
    public IEnumerable<string> AssetPaths => _winners.Keys.Select(k => k.Destination).Where(d => d.StartsWith("data/", StringComparison.OrdinalIgnoreCase))
        .Select(d => d[5..].Replace('/', '\\')).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Top-level Data files supplied by an enabled saved loose provider; never discovers new staging files.</summary>
    public IReadOnlyCollection<string> LooseRootFiles(string provider) => _providers.Values.SelectMany(p => p)
        .Where(c => c.Kind != 3 && (c.ModKey == "[overwrite]" ? "overwrite" : c.Mod).Equals(provider, StringComparison.OrdinalIgnoreCase)
            && c.Destination.StartsWith("data/", StringComparison.OrdinalIgnoreCase) && !c.Destination[5..].Contains('/'))
        .Select(c => c.Destination[5..]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Archive provenance from selected saved members, for consumers that need the owning mod/plugin.</summary>
    public IReadOnlyList<ActiveArchive> SavedArchives() => _providers.Values.SelectMany(p => p).Where(c => c.Kind == 3)
        .GroupBy(c => (c.Mod, c.Source)).Select(g =>
        {
            var candidate = g.First();
            var plugin = Composition.OrderedPluginNames.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).Equals(candidate.PluginKey, StringComparison.OrdinalIgnoreCase));
            var rank = plugin is null ? -1 : Composition.OrderedPluginNames.ToList().IndexOf(plugin);
            return new ActiveArchive(SourcePath(candidate), plugin ?? ArchiveDiscovery.IniArchiveOwner, rank, candidate.Mod);
        }).ToList();

    /// <summary>Recorded plugin copies, including disabled providers, for source/header diagnostics; no directory rescan.</summary>
    public IReadOnlyList<PluginFileHit> PluginCopies(string filename)
    {
        SafePart(filename, "plugin filename");
        var copies = _candidates.Values.Where(c => c.Kind != 3 && Path.GetFileName(c.Destination).Equals(filename, StringComparison.OrdinalIgnoreCase))
            .GroupBy(c => (c.Mod, c.Source)).Select(g => g.First())
            .Select(c => new PluginFileHit(SourcePath(c), $"saved mod '{c.Mod}'", c.Enabled)).ToList();
        var vanilla = VanillaFile(filename);
        if (vanilla is not null) copies.Add(new(vanilla, "vanilla Data", true));
        return copies;
    }

    /// <summary>Locate an inactive source from the saved inventory, without rescanning staging priorities.</summary>
    public string? LocatePlugin(string filename, string? mod = null)
    {
        SafePart(filename, "plugin filename");
        var candidates = _candidates.Values.Where(c => c.Kind != 3 && Path.GetFileName(c.Destination).Equals(filename, StringComparison.OrdinalIgnoreCase)
            && (mod is null || c.Mod.Equals(mod, StringComparison.OrdinalIgnoreCase))).GroupBy(c => (c.Mod, c.Source)).Select(g => g.First()).ToList();
        if (candidates.Count > 1) throw new InvalidOperationException($"'{filename}' has several saved sources; specify source.mod to choose one");
        return candidates.Count == 1 ? SourcePath(candidates[0]) : mod is null ? VanillaFile(filename) : null;
    }

    /// <summary>Validate a candidate's exact source and catalog stamps, then retain its identity for final revalidation.</summary>
    string SourcePath(Candidate candidate)
    {
        var root = candidate.ModKey switch { "[overwrite]" => OverwriteDir, "[root_folder]" => RootFolderDir, _ => Path.Combine(ModsDir, candidate.Mod) };
        var path = Path.Combine(root, candidate.Source);
        var canonicalRoot = Root(root);
        var actual = LinuxFileIdentity.Canonical(path);
        if (!actual.StartsWith(canonicalRoot + "/", StringComparison.Ordinal)) throw new InvalidOperationException($"saved source escapes its provider root: '{path}'");
        var stamp = LinuxFileIdentity.Of(path);
        if ((long)stamp.Size != candidate.Size || stamp.MtimeNs != candidate.MtimeNs)
            throw new InvalidOperationException($"Amethyst saved source changed: '{path}'; refresh the selected profile before reading");
        RememberSource(path, stamp);
        return path;
    }

    /// <summary>Find a case-preserving vanilla file only inside the distinct vanilla root; ambiguity refuses.</summary>
    string? VanillaFile(string relative)
    {
        var current = VanillaDataDir;
        foreach (var part in relative.Replace('\\', '/').Split('/'))
        {
            var entries = Directory.EnumerateFileSystemEntries(current).Where(p => Path.GetFileName(p).Equals(part, StringComparison.OrdinalIgnoreCase)).ToList();
            if (entries.Count == 0) return null;
            if (entries.Count > 1) throw new InvalidOperationException($"vanilla paths differ only by case under '{current}'");
            current = entries[0];
        }
        if (!File.Exists(current)) return null;
        var canonical = LinuxFileIdentity.Canonical(current);
        if (!canonical.StartsWith(VanillaDataDir + "/", StringComparison.Ordinal)) throw new InvalidOperationException("vanilla file escapes its source root");
        RememberSource(current, LinuxFileIdentity.Of(current));
        return current;
    }

    /// <summary>Detect a source that changed after an earlier read in this call.</summary>
    void RememberSource(string path, LinuxFileIdentity stamp)
    {
        if (_readSources.TryGetValue(path, out var earlier) && earlier != stamp) throw new InvalidOperationException($"source changed during the call: '{path}'; retry");
        _readSources[path] = stamp;
    }

    /// <summary>Revalidate profile/configuration, canonical roots, database identity/generation and all observed sources.</summary>
    public void CheckUnchanged()
    {
        foreach (var (path, expected) in _inputs)
        {
            var bytes = ReadInput(path, false);
            var identity = bytes is null ? (LinuxFileIdentity?)null : LinuxFileIdentity.Of(path);
            if (expected.Identity != identity || !(expected.Bytes is null ? bytes is null : bytes is not null && expected.Bytes.AsSpan().SequenceEqual(bytes)))
                throw new InvalidOperationException($"Amethyst configuration or profile changed during the call: '{path}'; retry after Refresh");
        }
        foreach (var (path, canonical) in _roots)
            if (LinuxFileIdentity.Canonical(path) != canonical) throw new InvalidOperationException($"Amethyst source root changed during the call: '{path}'; retry");
        var identityNow = LinuxFileIdentity.Of(DatabasePath);
        if ((identityNow.DeviceMajor, identityNow.DeviceMinor, identityNow.Inode) != (_databaseIdentity.DeviceMajor, _databaseIdentity.DeviceMinor, _databaseIdentity.Inode))
            throw new InvalidOperationException("Amethyst filegraph database was replaced during the call; retry");
        using var database = OpenDatabase(DatabasePath);
        Execute(database, "BEGIN");
        if (MetaNumber(database, "inventory_generation") != Provenance.InventoryGeneration ||
            Convert.ToInt32(Scalar(database, "PRAGMA user_version")) != 9 ||
            !Meta(database, "ready").AsSpan().SequenceEqual(new byte[] { 1 }) ||
            MetaNumber(database, "rules_revision") != RulesRevision || MetaNumber(database, "engine_revision") != 1 ||
            Convert.ToInt64(Scalar(database, "SELECT COUNT(*) FROM operations WHERE state NOT IN ('committed','failed')")) != 0)
            throw new InvalidOperationException("Amethyst filegraph inventory changed during the call; retry after Refresh");
        using var query = Command(database, "SELECT generation,inventory_generation,ready FROM profiles WHERE profile_id=$profile");
        using var row = query.ExecuteReader();
        if (!row.Read() || row.GetInt64(0) != Provenance.Generation || row.GetInt64(1) != checked((long)Provenance.InventoryGeneration) || row.GetInt64(2) != 1)
            throw new InvalidOperationException("Amethyst filegraph generation changed during the call; retry");
        foreach (var (path, stamp) in _readSources)
            if (LinuxFileIdentity.Of(path) != stamp) throw new InvalidOperationException($"source changed during the call: '{path}'; retry");
    }

    /// <summary>Register an artifact written by this call so a raced refusal cannot leave a seemingly valid result file.</summary>
    public void RegisterArtifact(string path, byte[]? previous) => _artifacts.TryAdd(path, previous);
    /// <summary>Remove new artifacts and restore replaced artifacts from this failed call; all cleanup failures are reported.</summary>
    public void RemoveFailedArtifacts()
    {
        var failures = new List<Exception>();
        foreach (var (path, previous) in _artifacts)
            try { if (previous is null) File.Delete(path); else AtomicFile.WriteAllBytes(path, previous); }
            catch (Exception ex) { failures.Add(ex); }
        if (failures.Count > 0) throw new AggregateException("Could not roll back all artifacts from the failed read", failures);
        _artifacts.Clear();
    }

    /// <summary>Capture exact input bytes and identity, including an absent optional file.</summary>
    byte[]? Input(string path, bool required)
    {
        if (_inputs.TryGetValue(path, out var captured)) return captured.Bytes;
        var before = File.Exists(path) ? (LinuxFileIdentity?)LinuxFileIdentity.Of(path) : null;
        var bytes = ReadInput(path, required);
        var after = bytes is null ? (LinuxFileIdentity?)null : LinuxFileIdentity.Of(path);
        if (before != after) throw new InvalidOperationException($"Amethyst input changed while opening: '{path}'; retry");
        _inputs[path] = (bytes, after);
        return bytes;
    }

    /// <summary>Read a file without equating inaccessible data with an absent optional input.</summary>
    static byte[]? ReadInput(string path, bool required)
    {
        try { return File.ReadAllBytes(path); }
        catch (FileNotFoundException) when (!required) { return null; }
        catch (DirectoryNotFoundException) when (!required) { return null; }
    }

    /// <summary>Remember a canonical source root so symlink changes are detected at publication.</summary>
    string Root(string path) { var canonical = LinuxFileIdentity.Canonical(path); _roots[path] = canonical; return canonical; }
    /// <summary>Profile lines from the captured bytes; comments and empty lines have no composition meaning.</summary>
    IEnumerable<string> Lines(string name) => Encoding.UTF8.GetString(Input(Path.Combine(ProfileDir, name), false) ?? []).Split('\n')
        .Select(l => l.Trim().TrimStart('\uFEFF')).Where(l => l.Length > 0 && !l.StartsWith('#'));
    /// <summary>Refuse traversal and path-shaped names before selecting a profile or source mod.</summary>
    static void SafePart(string value, string label)
    { if (value is "" or "." or ".." || value.Contains('/') || value.Contains('\\') || value.Contains('\0')) throw new InvalidOperationException($"unsafe {label}: '{value}'"); }
    /// <summary>Decode exact UTF-8 path bytes and refuse absolute/traversing paths rather than guessing their spelling.</summary>
    static string Relative(byte[] bytes)
    {
        var text = new UTF8Encoding(false, true).GetString(bytes);
        if (text.Length == 0 || text.StartsWith('/') || text.Contains('\0') || text.Contains('\\') || text.Split('/').Any(p => p is "" or "." or ".."))
            throw new InvalidOperationException("Amethyst filegraph contains an unsafe or unsupported source/destination path");
        return text;
    }
    /// <summary>Decode the producer's serde byte arrays, with binary encoding also accepted for producer-built fixtures.</summary>
    static byte[] PayloadBytes(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetBytesFromBase64()
        : value.EnumerateArray().Select(v => v.GetByte()).ToArray();
    /// <summary>Open an existing catalog read-only, with ordinary WAL support and a bounded busy timeout.</summary>
    static SqliteConnection OpenDatabase(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly,
            Pooling = false, Cache = SqliteCacheMode.Default, DefaultTimeout = 2 }.ToString());
        try { connection.Open(); Execute(connection, "PRAGMA query_only=ON"); Execute(connection, "PRAGMA busy_timeout=2000"); return connection; }
        catch { connection.Dispose(); throw; }
    }
    /// <summary>Create a constant SQL query, binding the canonical profile identity when requested.</summary>
    SqliteCommand Command(SqliteConnection connection, string sql)
    { var command = connection.CreateCommand(); command.CommandText = sql; if (sql.Contains("$profile")) command.Parameters.AddWithValue("$profile", ProfileDir); return command; }
    /// <summary>Execute a scalar constant query; missing values are invalid saved data.</summary>
    object Scalar(SqliteConnection connection, string sql) { using var command = Command(connection, sql); return command.ExecuteScalar() ?? throw new InvalidOperationException("Amethyst filegraph metadata is missing"); }
    /// <summary>Execute read-transaction/connection pragmas only; no schema or data writes are issued.</summary>
    static void Execute(SqliteConnection connection, string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    /// <summary>Read one required metadata blob without changing the producer's catalog.</summary>
    static byte[] Meta(SqliteConnection connection, string key)
    { using var command = connection.CreateCommand(); command.CommandText = "SELECT value FROM meta WHERE key=$key"; command.Parameters.AddWithValue("$key", key); return command.ExecuteScalar() as byte[] ?? throw new InvalidOperationException($"Amethyst metadata '{key}' is missing"); }
    /// <summary>Decode one producer little-endian 64-bit counter; malformed counters refuse.</summary>
    static ulong MetaNumber(SqliteConnection connection, string key)
    { var bytes = Meta(connection, key); if (bytes.Length != 8) throw new InvalidOperationException($"Amethyst metadata '{key}' is malformed"); return BinaryPrimitives.ReadUInt64LittleEndian(bytes); }
}
