using System.Text.Json;
using HousecarlCore;

namespace HousecarlGenerator;

/// <summary>Synthetic Linux fixtures for the setup-manifest layout contract; no game data required.</summary>
public static class AmethystLayoutProbe
{
    public static int RunGuard(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "hc-amethyst-layout-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            SharedAndProfileSwitch(root);
            UnknownSchema(root);
            DataSafety(root);
            Console.WriteLine("PASS: Amethyst manifest, profile switching, staging, precedence, and Data safety");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            return 1;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    static void SharedAndProfileSwitch(string root)
    {
        var fixture = Fixture.Create(Path.Combine(root, "shared"), deployActive: true, dataCore: true);
        var layout = new AmethystLayout(fixture.Manifest);
        var first = layout.Capture();
        Equal("default", first.ActiveProfileName, "default profile");
        Equal(Path.Combine(fixture.ProfileRoot, "mods"), first.ModsDir, "shared mods");
        Equal("HARDLINK", first.LastDeploymentMode ?? "", "deployment mode");
        True(!layout.RefreshIfStale(), "unchanged layout reported stale");

        var alternate = Path.Combine(fixture.ProfileRoot, "profiles", "Unicode Ω");
        var overrideGame = Path.Combine(fixture.Root, "Skyrim Override");
        Directory.CreateDirectory(alternate);
        Directory.CreateDirectory(Path.Combine(overrideGame, "Data_Core"));
        Write(Path.Combine(alternate, "profile_state.json"), new
        {
            profile_settings = new { profile_specific_mods = true, game_path = overrideGame }
        });
        Write(fixture.Deploy, new
        {
            last_active_profile = "Unicode Ω", deploy_active = true, last_deploy_mode = "HARDLINK"
        });

        True(layout.RefreshIfStale(), "active-profile switch was not detected");
        var second = layout.Capture();
        Equal("Unicode Ω", second.ActiveProfileName, "switched profile");
        Equal(Path.Combine(alternate, "mods"), second.ModsDir, "profile-specific mods");
        Equal(overrideGame, second.GamePath, "profile game_path precedence");
    }

    static void UnknownSchema(string root)
    {
        var fixture = Fixture.Create(Path.Combine(root, "schema"), deployActive: false, dataCore: false);
        var manifest = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(fixture.Manifest))!;
        manifest["schemaVersion"] = 2;
        Write(fixture.Manifest, manifest);
        Throws(() => new AmethystLayout(fixture.Manifest), "schemaVersion 2 is unsupported");
    }

    static void DataSafety(string root)
    {
        var inactive = Fixture.Create(Path.Combine(root, "inactive"), deployActive: false, dataCore: false);
        Equal(Path.Combine(inactive.Game, "Data"), new AmethystLayout(inactive.Manifest).Capture().VanillaDataDir,
            "inactive Data fallback");

        var active = Fixture.Create(Path.Combine(root, "active"), deployActive: true, dataCore: false);
        Throws(() => new AmethystLayout(active.Manifest), "deployment is active but Data_Core is missing");
    }

    static void Write(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value));
    }

    static void Equal(string expected, string actual, string arm)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidOperationException($"{arm}: expected '{expected}', got '{actual}'");
    }

    static void True(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    static void Throws(Action action, string text)
    {
        try { action(); }
        catch (AmethystConfigurationException ex) when (ex.Message.Contains(text, StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException($"expected AmethystConfigurationException containing '{text}'");
    }

    sealed record Fixture(string Root, string ProfileRoot, string Game, string Manifest, string Deploy)
    {
        public static Fixture Create(string root, bool deployActive, bool dataCore)
        {
            var config = Path.Combine(root, "config", "games", "Skyrim Special Edition");
            var profileRoot = Path.Combine(root, "staging with spaces", "Моды");
            var game = Path.Combine(root, "Skyrim Special Edition");
            var profile = Path.Combine(profileRoot, "profiles", "default");
            Directory.CreateDirectory(config);
            Directory.CreateDirectory(profile);
            Directory.CreateDirectory(Path.Combine(game, dataCore ? "Data_Core" : "Data"));
            var paths = Path.Combine(config, "paths.json");
            var deploy = Path.Combine(config, "deploy_state.json");
            Write(paths, new { staging_path = profileRoot, game_path = game, deploy_mode = "hardlink" });
            Write(deploy, new
            {
                last_active_profile = "default", deploy_active = deployActive, last_deploy_mode = "HARDLINK"
            });
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
                createdBy = "housecarl-amethyst-setup",
                setupVersion = "1.0.0"
            });
            return new Fixture(root, profileRoot, game, manifest, deploy);
        }
    }
}
