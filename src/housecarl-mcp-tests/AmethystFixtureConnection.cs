using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessagePack;
using Org.BouncyCastle.Crypto.Digests;

namespace HousecarlMcpTests;

/// <summary>Save an existing synthetic world's files through the actual Amethyst producer for wire tests.</summary>
sealed class AmethystFixtureConnection
{
    public string Manifest { get; }
    public string Profile => _paths.ProfileDir;
    public string Database => Path.Combine(_paths.InstanceDir, "filegraph.sqlite3");
    public Dictionary<string, string> Environment => new()
    {
        ["HouseCarl__ConnectionManifest"] = Manifest,
        ["HouseCarl__FilegraphMode"] = "saved-snapshot",
        ["HouseCarl__CorpusPath"] = TestCorpus.Path,
        ["HOUSECARL_DATA_DIR"] = Path.Combine(_payloads, "server-state"),
        ["XDG_STATE_HOME"] = Path.Combine(_payloads, "state")
    };
    readonly Mo2InstancePaths _paths;
    readonly string _payloads;

    public AmethystFixtureConnection(string instance)
    {
        _paths = Mo2Instance.Resolve(instance);
        _payloads = Path.Combine(instance, ".fixture-inputs");
        Directory.CreateDirectory(_payloads);
        var config = Path.Combine(instance, ".fixture-manager");
        Directory.CreateDirectory(config);
        var iniDir = Path.Combine(_paths.ProfileDir, "ini files");
        Directory.CreateDirectory(iniDir);
        var ini = Path.Combine(_paths.ProfileDir, "Skyrim.ini");
        File.WriteAllText(Path.Combine(iniDir, "Skyrim.ini"), File.Exists(ini) ? File.ReadAllText(ini) : "[Archive]\nsResourceArchiveList=\n");
        File.WriteAllText(Path.Combine(config, "paths.json"), JsonSerializer.Serialize(new
        { staging_path = instance, game_path = _paths.GamePath, profile_ini_files = true }));
        File.WriteAllText(Path.Combine(config, "deploy_state.json"), JsonSerializer.Serialize(new
        { last_active_profile = _paths.ProfileName, deploy_active = false }));
        Manifest = Path.Combine(config, "connection.json");
        File.WriteAllText(Manifest, JsonSerializer.Serialize(new
        { schemaVersion = 1, manager = "amethyst", gameId = "skyrim_se", profileRoot = instance,
          gameConfigDir = config, pathsFile = Path.Combine(config, "paths.json"), deployStateFile = Path.Combine(config, "deploy_state.json") }));
        Refresh();
        File.WriteAllText(Path.Combine(instance, "filegraph.lock"), "");
    }

    public void Refresh()
    {
        foreach (var old in Directory.GetFiles(_payloads, "*.msgpack")) File.Delete(old);
        var composition = Mo2LoadOrder.ReadComposition(_paths.ProfileDir);
        var activation = Path.Combine(_paths.ProfileDir, "plugins.txt");
        var listed = File.ReadAllLines(activation).Select(n => n.Trim().TrimStart('*')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var omitted = composition.OrderedPluginNames.Where(n => !listed.Contains(n)).ToArray();
        if (omitted.Length > 0)
        {
            File.AppendAllLines(activation, omitted);
            composition = Mo2LoadOrder.ReadComposition(_paths.ProfileDir);
        }
        var order = composition.EnabledMods.Concat(composition.DisabledMods).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var mods = new List<object>();
        var index = 0;
        foreach (var name in order)
        {
            var modRoot = Path.Combine(_paths.ModsDir, name);
            var candidates = new List<object>();
            foreach (var path in Directory.EnumerateFiles(modRoot, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(modRoot, path).Replace(Path.DirectorySeparatorChar, '/');
                var stamp = LinuxFileIdentity.Of(path);
                var plugin = PluginFile.Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
                candidates.Add(new
                {
                    source_rel = System.Text.Encoding.UTF8.GetBytes(rel).Select(b => (int)b).ToArray(), source_display = rel,
                    target = "game", destination_key = System.Text.Encoding.UTF8.GetBytes(("data/" + rel).ToLowerInvariant()).Select(b => (int)b).ToArray(),
                    destination_display = "Data/" + rel, size = stamp.Size, mtime_ns = stamp.MtimeNs,
                    flags = plugin ? 4 : 0, plugin_key = plugin ? Path.GetFileName(path).ToLowerInvariant() : null,
                    provider_kind = 0, deployable = true, legacy_root = false, legacy_rel = rel
                });
            }
            var payload = new { mod_name = name, mod_key = name.ToLowerInvariant(), variant_key = "fixture-default", candidates };
            File.WriteAllBytes(Path.Combine(_payloads, $"mod-{index++}.msgpack"), MessagePackSerializer.ConvertFromJson(JsonSerializer.Serialize(payload)));
            mods.Add(new { name, key = name.ToLowerInvariant(), enabled = composition.EnabledMods.Contains(name, StringComparer.OrdinalIgnoreCase), variant_key = "fixture-default" });
        }
        var digest = new Blake2bDigest(192);
        foreach (var name in new[] { "modlist.txt", "profile_state.json", "plugins.txt", "loadorder.txt" })
        {
            var label = System.Text.Encoding.UTF8.GetBytes(name); digest.BlockUpdate(label, 0, label.Length);
            var path = Path.Combine(_paths.ProfileDir, name);
            var bytes = File.Exists(path) ? File.ReadAllBytes(path) : new byte[] { 0 }; digest.BlockUpdate(bytes, 0, bytes.Length);
        }
        var hash = new byte[24]; digest.DoFinal(hash, 0);
        var seed = JsonNode.Parse(File.ReadAllText(Path.Combine(HarnessPaths.RepoRoot, "src", "housecarl-mcp-tests", "fixtures", "amethyst", "schema9-snapshot.json")))!["intent"]!;
        seed["profile_id"] = _paths.ProfileDir;
        seed["intent_hash"] = JsonSerializer.SerializeToNode(hash.Select(b => (int)b));
        seed["mods"] = JsonSerializer.SerializeToNode(mods);
        seed["special_variants"] = new JsonObject(); seed["archive_order"] = new JsonArray();
        seed["plugin_order"] = JsonSerializer.SerializeToNode(composition.OrderedPluginNames.Select(n => Path.GetFileNameWithoutExtension(n).ToLowerInvariant()));
        File.WriteAllBytes(Path.Combine(_payloads, "intent.msgpack"), MessagePackSerializer.ConvertFromJson(seed.ToJsonString()));
        var start = new ProcessStartInfo(System.Environment.GetEnvironmentVariable("AMETHYST_FILEGRAPH_PYTHON") ?? "python3")
        { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in new[] { "-I", Path.Combine(HarnessPaths.RepoRoot, "src", "housecarl-mcp-tests", "fixtures", "amethyst", "build-fixture-catalog.py"),
            _paths.InstanceDir, _paths.ProfileDir, _payloads, System.Environment.GetEnvironmentVariable("AMETHYST_FILEGRAPH_EXTENSION") ?? Path.Combine(AppContext.BaseDirectory, "amethyst_filegraph.abi3.so") })
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000)) { process.Kill(true); throw new TimeoutException("fixture producer did not finish"); }
        if (process.ExitCode != 0) throw new InvalidOperationException(errors.GetAwaiter().GetResult());
    }
}
