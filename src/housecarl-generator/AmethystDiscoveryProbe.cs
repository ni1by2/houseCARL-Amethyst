using System.Text.Json;
using HousecarlCore;
using HousecarlSetup;

namespace HousecarlGenerator;

/// <summary>Proves native, default-staging, Flatpak, ambiguity, and fail-before-write discovery behavior.</summary>
internal static class AmethystDiscoveryProbe
{
    /// <summary>Runs all standalone connection-setup scenarios in temporary native paths.</summary>
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("==============================================================");
        Console.WriteLine(" Amethyst standalone discovery guard");
        Console.WriteLine("==============================================================");
        int failures = 0;
        void Check(bool condition, string label)
        {
            Console.WriteLine((condition ? "  PASS  " : "  FAIL  ") + label);
            if (!condition) failures++;
        }

        string root = Path.Combine(Path.GetTempPath(), "amethyst-discovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            string home = Path.Combine(root, "home Ω");
            string xdg = Path.Combine(home, ".config");
            Directory.CreateDirectory(home);

            Fixture custom = Fixture.Create(
                Path.Combine(xdg, "AmethystModManager"),
                Path.Combine(root, "custom staging"),
                Path.Combine(root, "game custom"),
                stagingValue: "custom");
            Directory.CreateDirectory(Path.GetDirectoryName(custom.Manifest)!);
            File.WriteAllText(custom.Manifest, "old incomplete manifest");
            var customResult = AmethystDiscovery.CreateConnection(new(home, xdg));
            Check(customResult.ProfileRoot == custom.ProfileRoot, "native/AppImage/AUR config resolves custom staging");
            Check(new AmethystLayout(customResult.ManifestPath).Capture().ActiveProfileName == "default",
                "written manifest opens through the production layout");
            Check(!Directory.EnumerateFiles(Path.GetDirectoryName(custom.Manifest)!, "*.tmp-*").Any(),
                "atomic replacement leaves no temporary manifest");

            Directory.Delete(Path.Combine(xdg, "AmethystModManager"), recursive: true);
            string profilesBase = Path.Combine(root, "profiles base");
            Fixture blank = Fixture.Create(
                Path.Combine(root, "blank config"),
                Path.Combine(profilesBase, "Skyrim Special Edition"),
                Path.Combine(root, "game blank"),
                stagingValue: "");
            var blankResult = AmethystDiscovery.CreateConnection(
                new(home, xdg, blank.ConfigRoot, profilesBase));
            Check(blankResult.ProfileRoot == blank.ProfileRoot,
                "blank staging_path follows Amethyst's default profiles convention");
            Check(new AmethystLayout(blankResult.ManifestPath).Capture().ProfileDir ==
                  Path.Combine(blank.ProfileRoot, "profiles", "default"),
                "production layout accepts Amethyst's blank default-staging value");

            string flatpakRoot = Path.Combine(
                home, ".var", "app", "io.github.Amethyst.ModManager", "config", "AmethystModManager");
            Fixture flatpak = Fixture.Create(
                flatpakRoot,
                Path.Combine(root, "flatpak staging"),
                Path.Combine(root, "game flatpak"),
                stagingValue: "custom");
            var flatpakResult = AmethystDiscovery.CreateConnection(new(home, xdg));
            Check(flatpakResult.GameConfigDir == flatpak.GameConfig,
                "Flatpak config is discovered when native config is absent");

            Fixture nativeAgain = Fixture.Create(
                Path.Combine(xdg, "AmethystModManager"),
                Path.Combine(root, "native staging two"),
                Path.Combine(root, "game native two"),
                stagingValue: "custom");
            bool ambiguous = Throws(
                () => AmethystDiscovery.CreateConnection(new(home, xdg)),
                "multiple usable Amethyst Skyrim configurations");
            Check(ambiguous, "native plus Flatpak ambiguity fails with explicit-path guidance");
            Check(!File.Exists(nativeAgain.Manifest) && File.Exists(flatpak.Manifest),
                "ambiguity writes no new manifest");

            string brokenRoot = Path.Combine(root, "broken config");
            Fixture broken = Fixture.Create(
                brokenRoot,
                Path.Combine(root, "broken staging"),
                Path.Combine(root, "game broken"),
                stagingValue: "custom");
            File.Delete(Path.Combine(broken.ProfileRoot, "profiles", "default", "plugins.txt"));
            bool unusable = Throws(
                () => AmethystDiscovery.CreateConnection(new(home, xdg, broken.ConfigRoot)),
                "active profile plugins.txt");
            Check(unusable && !File.Exists(broken.Manifest),
                "unusable profile fails before connection.json is written");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        Console.WriteLine(failures == 0
            ? "================ ALL PASS ================"
            : $"================ {failures} CHECK(S) FAILED ================");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Returns true only when an operation throws the expected actionable discovery error.</summary>
    private static bool Throws(Action action, string expected)
    {
        try { action(); }
        catch (InvalidOperationException ex) when (ex.Message.Contains(expected, StringComparison.Ordinal)) { return true; }
        return false;
    }

    /// <summary>Complete synthetic Amethyst configuration and its expected manifest destination.</summary>
    /// <param name="ConfigRoot">Amethyst configuration root containing games.</param>
    /// <param name="GameConfig">Skyrim SE game-config directory.</param>
    /// <param name="ProfileRoot">Effective custom or default staging root.</param>
    /// <param name="Manifest">Expected schema-v1 connection path.</param>
    private sealed record Fixture(
        string ConfigRoot,
        string GameConfig,
        string ProfileRoot,
        string Manifest)
    {
        /// <summary>Creates one usable inactive-deployment profile with custom or blank staging_path.</summary>
        public static Fixture Create(
            string configRoot,
            string profileRoot,
            string game,
            string stagingValue)
        {
            string gameConfig = Path.Combine(configRoot, "games", "Skyrim Special Edition");
            string profile = Path.Combine(profileRoot, "profiles", "default");
            Directory.CreateDirectory(gameConfig);
            Directory.CreateDirectory(profile);
            Directory.CreateDirectory(Path.Combine(profileRoot, "mods"));
            Directory.CreateDirectory(Path.Combine(game, "Data"));
            File.WriteAllText(Path.Combine(profile, "modlist.txt"), "");
            File.WriteAllText(Path.Combine(profile, "plugins.txt"), "");
            File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "");
            Write(Path.Combine(gameConfig, "paths.json"), new
            {
                staging_path = stagingValue == "custom" ? profileRoot : "",
                game_path = game,
                deploy_mode = "hardlink",
            });
            Write(Path.Combine(gameConfig, "deploy_state.json"), new
            {
                last_active_profile = "default",
                deploy_active = false,
                last_deploy_mode = "HARDLINK",
            });
            return new(
                configRoot,
                gameConfig,
                profileRoot,
                Path.Combine(profileRoot, ".housecarl-amethyst", "connection.json"));
        }

        /// <summary>Serializes one manager-owned fixture object after creating its parent.</summary>
        private static void Write(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(value));
        }
    }
}
