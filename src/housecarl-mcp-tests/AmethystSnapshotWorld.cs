using System.Text.Json;
using System.Text.Json.Nodes;
using MessagePack;
using Microsoft.Data.Sqlite;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Org.BouncyCastle.Crypto.Digests;

namespace HousecarlMcpTests;

/// <summary>A relocated, producer-generated schema-9 catalog with real Mutagen records; mutations stay in this temporary world.</summary>
sealed class AmethystSnapshotWorld : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "hc-snapshot-test-" + Guid.NewGuid().ToString("N"));
    readonly string _profileName;
    readonly bool _profileSpecific;
    public string Profile => Path.Combine(Root, "profiles", _profileName);
    public string Library => _profileSpecific ? Profile : Root;
    public string Database => Path.Combine(Library, "filegraph.sqlite3");
    public string Manifest => Path.Combine(Root, "connection.json");
    public UserConfigStore Store { get; }
    public JsonElement ExpectedWinners { get; }

    /// <summary>Restore producer data without rebuilding its routing or winners; only fixture locations, plugin bytes and stamps change.</summary>
    public AmethystSnapshotWorld(bool profileSpecific = false, string profileName = "default", bool gameOverride = false)
    {
        _profileName = profileName; _profileSpecific = profileSpecific;
        _ = TestCorpus.Path;
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(HarnessPaths.RepoRoot,
            "src", "housecarl-mcp-tests", "fixtures", "amethyst", "schema9-snapshot.json")));
        var data = fixture.RootElement;
        ExpectedWinners = data.GetProperty("winners").Clone();
        foreach (var file in data.GetProperty("files").EnumerateObject())
        {
            var path = Path.Combine(Library, file.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, file.Value.GetBytesFromBase64());
            if (path.EndsWith(".esp", StringComparison.Ordinal))
            {
                var plugin = new SkyrimMod(ModKey.FromFileName(Path.GetFileName(path)), SkyrimRelease.SkyrimSE);
                for (var i = 0; i < 10; i++) plugin.Weapons.AddNew().EditorID = "Snapshot" + Path.GetFileName(Path.GetDirectoryName(path)) + i;
                plugin.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
            }
        }
        Directory.CreateDirectory(Profile);
        foreach (var file in data.GetProperty("profile_files").EnumerateObject()) File.WriteAllText(Path.Combine(Profile, file.Name), file.Value.GetString());
        Directory.CreateDirectory(Path.Combine(Root, "game", "Data"));
        if (profileSpecific || gameOverride)
        {
            Directory.CreateDirectory(Path.Combine(Root, "alternate-game", "Data"));
            File.WriteAllText(Path.Combine(Profile, "profile_state.json"), JsonSerializer.Serialize(new
            {
                profile_settings = new { profile_specific_mods = profileSpecific, game_path = gameOverride ? Path.Combine(Root, "alternate-game") : "" }
            }));
        }
        Directory.CreateDirectory(Path.Combine(Root, "manager-config"));
        File.WriteAllText(Path.Combine(Root, "manager-config", "paths.json"), JsonSerializer.Serialize(new { staging_path = Root, game_path = Path.Combine(Root, "game") }));
        File.WriteAllText(Path.Combine(Root, "manager-config", "deploy_state.json"), JsonSerializer.Serialize(new { last_active_profile = profileName, deploy_active = false }));
        File.WriteAllText(Manifest, JsonSerializer.Serialize(new { schemaVersion = 1, manager = "amethyst", gameId = "skyrim_se", profileRoot = Root,
            gameConfigDir = Path.Combine(Root, "manager-config"), pathsFile = Path.Combine(Root, "manager-config", "paths.json"), deployStateFile = Path.Combine(Root, "manager-config", "deploy_state.json") }));
        Execute(data.GetProperty("catalog_sql").GetString()!.Replace(data.GetProperty("root").GetString()!, Root, StringComparison.Ordinal)
            .Replace(Path.Combine(Root, "profiles", "default"), Profile, StringComparison.Ordinal));
        using (var connection = Open())
        {
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT rf.file_id,m.name_key,m.name_display,rf.source_rel FROM raw_files rf JOIN mods m USING(mod_id)";
            var files = new List<(long Id, string Path)>();
            using (var rows = query.ExecuteReader())
                while (rows.Read())
                {
                    var key = rows.GetString(1);
                    var providerRoot = key == "[overwrite]" ? Path.Combine(Library, "overwrite")
                        : key == "[root_folder]" ? Path.Combine(Library, "Root_Folder") : Path.Combine(Library, "mods", rows.GetString(2));
                    files.Add((rows.GetInt64(0), Path.Combine(providerRoot, System.Text.Encoding.UTF8.GetString(rows.GetFieldValue<byte[]>(3)))));
                }
            foreach (var (id, path) in files)
            {
                var stamp = LinuxFileIdentity.Of(path);
                query.CommandText = "UPDATE raw_files SET size=$size,mtime_ns=$time WHERE file_id=$id";
                query.Parameters.Clear(); query.Parameters.AddWithValue("$size", checked((long)stamp.Size));
                query.Parameters.AddWithValue("$time", stamp.MtimeNs); query.Parameters.AddWithValue("$id", id); query.ExecuteNonQuery();
            }
            query.Parameters.Clear();
            var intent = JsonNode.Parse(data.GetProperty("intent").GetRawText())!;
            intent["profile_id"] = Profile;
            var digest = new Blake2bDigest(192);
            foreach (var name in new[] { "modlist.txt", "profile_state.json", "plugins.txt", "loadorder.txt" })
            {
                var label = System.Text.Encoding.UTF8.GetBytes(name); digest.BlockUpdate(label, 0, label.Length);
                var path = Path.Combine(Profile, name); var bytes = File.Exists(path) ? File.ReadAllBytes(path) : new byte[] { 0 };
                digest.BlockUpdate(bytes, 0, bytes.Length);
            }
            var hash = new byte[24]; digest.DoFinal(hash, 0);
            intent["intent_hash"] = new JsonArray(hash.Select(b => (JsonNode?)JsonValue.Create(b)).ToArray());
            query.CommandText = "UPDATE profiles SET intent_hash=$hash,intent_payload=$payload";
            query.Parameters.AddWithValue("$hash", hash); query.Parameters.AddWithValue("$payload", MessagePackSerializer.ConvertFromJson(intent.ToJsonString())); query.ExecuteNonQuery();
        }
        Store = new UserConfigStore(Path.Combine(Root, "user-state", "houseCARL.user.json"));
    }

    /// <summary>Open this test's writable catalog for a controlled structural mutation.</summary>
    public SqliteConnection Open() { var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false, ForeignKeys = false }.ToString()); connection.Open(); return connection; }
    /// <summary>Run a test mutation; product code uses a separate read-only connection.</summary>
    public void Execute(string sql) { using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    /// <summary>Remove only this fixture's temporary tree.</summary>
    public void Dispose() => Directory.Delete(Root, recursive: true);
}
