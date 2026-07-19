using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>Locks the public connection, persistence, status, and refresh contract.</summary>
public static class AmethystRuntimeProbe
{
    public static int RunGuard(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "hc-amethyst-runtime-" + Guid.NewGuid().ToString("N"));
        try
        {
            var manifest = CreateFixture(root, out var profileRoot, out var deploy);
            var store = new UserConfigStore(Path.Combine(root, "houseCARL.user.json"));
            using var service = LoadOrderService.WithAmethystConnection(null, 0, store);

            Check(service.ConfigPromptOrNull()?.Contains("housecarl_set_amethyst_connection") == true,
                "unconfigured prompt names the Amethyst tool");
            Check(SetupTools.SetAmethystConnection(service, Path.Combine(root, "missing.json")).StartsWith("error:"),
                "invalid manifest fails through the public tool");
            Check(store.Load().AmethystConnectionManifest is null, "invalid manifest is not persisted");

            var confirmation = SetupTools.SetAmethystConnection(service, manifest);
            Check(confirmation.Contains("connected houseCARL-Amethyst"), "public setup tool confirms connection");
            Check(store.Load().AmethystConnectionManifest == manifest, "manifest path persists");
            var status = AmethystTools.Status(service);
            Check(status.Contains("profile: default (shared staging)"), "status reports active shared profile");
            Check(status.Contains("deployment: active (HARDLINK)"), "status reports hardlink deployment");
            var order = service.StatusData();
            Check(order.ResolvedPluginCount == 1, "record resolver uses the Amethyst plugin source");
            Check(order.Composition.LockedMods.SequenceEqual(new[] { "Runtime Mod" }),
                "runtime composition preserves locked mods");

            var alternate = Path.Combine(profileRoot, "profiles", "alternate");
            Directory.CreateDirectory(alternate);
            Write(Path.Combine(alternate, "profile_state.json"),
                new { profile_settings = new { profile_specific_mods = true } });
            Write(deploy, new { last_active_profile = "alternate", deploy_active = true, last_deploy_mode = "HARDLINK" });
            Check(AmethystTools.Refresh(service).StartsWith("refreshed"), "explicit refresh detects profile switch");
            Check(AmethystTools.Status(service).Contains("profile: alternate (profile-specific staging)"),
                "status follows the switched profile");

            Console.WriteLine("PASS: Amethyst runtime connection, persistence, status, and refresh");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            return 1;
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    static string CreateFixture(string root, out string profileRoot, out string deploy)
    {
        var config = Path.Combine(root, "config", "games", "Skyrim Special Edition");
        profileRoot = Path.Combine(root, "staging");
        var game = Path.Combine(root, "game");
        Directory.CreateDirectory(config);
        var profile = Path.Combine(profileRoot, "profiles", "default");
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(Path.Combine(game, "Data_Core"));
        var key = new ModKey("HcAmethystRuntime", ModType.Master);
        var modPath = Path.Combine(profileRoot, "mods", "Runtime Mod", key.FileName.String);
        Directory.CreateDirectory(Path.GetDirectoryName(modPath)!);
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        mod.Weapons.AddNew().EditorID = "HcAmethystRuntimeWeapon";
        mod.BeginWrite.ToPath(modPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), "*Runtime Mod\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*" + key.FileName + "\n");
        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), key.FileName + "\n");
        var paths = Path.Combine(config, "paths.json");
        deploy = Path.Combine(config, "deploy_state.json");
        Write(paths, new { staging_path = profileRoot, game_path = game });
        Write(deploy, new { last_active_profile = "default", deploy_active = true, last_deploy_mode = "HARDLINK" });
        var manifest = Path.Combine(profileRoot, ".housecarl-amethyst", "connection.json");
        Write(manifest, new
        {
            schemaVersion = 1,
            manager = "amethyst",
            gameId = "skyrim_se",
            gameName = "Skyrim Special Edition",
            profileRoot,
            gameConfigDir = config,
            pathsFile = paths,
            deployStateFile = deploy,
            createdBy = "housecarl-amethyst-connector",
            connectorVersion = "1.0.0"
        });
        return manifest;
    }

    static void Write(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value));
    }

    static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
