using System.Text.Json;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

[Trait("tier", "unit")]
public sealed class SteamProtonResolverTests
{
    [Fact]
    public void ResolvesOnlyTheCurrentSkyrimSeMappingAndSelectedPrefix()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var world = new AmethystSnapshotWorld();
        var fixture = CreateSteam(world);
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);

        var selected = SteamProtonResolver.Resolve(snapshot, fixture.Home, []);

        Assert.Equal("489830", selected.AppId);
        Assert.Equal(fixture.ToolName, selected.ToolName);
        Assert.Equal(fixture.Prefix, selected.PrefixPath);
        Assert.Equal(fixture.SteamRoot, selected.SteamRoot);
        Assert.Equal(fixture.Proton, selected.ProtonExecutable);
        var start = new System.Diagnostics.ProcessStartInfo { FileName = "/prefix/PapyrusCompiler.exe" };
        start.ArgumentList.Add("SomeScript");
        selected.Configure(start);
        Assert.Equal(fixture.Proton, start.FileName);
        Assert.Equal(new[] { "run", "/prefix/PapyrusCompiler.exe", "SomeScript" }, start.ArgumentList.ToArray());
        Assert.Equal(fixture.CompatData, start.Environment["STEAM_COMPAT_DATA_PATH"]);
        Assert.Equal(fixture.Prefix, start.Environment["WINEPREFIX"]);
        Assert.Equal("489830", start.Environment["STEAM_COMPAT_APP_ID"]);
        var unicodePath = Path.Combine(world.Root, "mods", "Élite Armor");
        Directory.CreateDirectory(unicodePath);
        Assert.Equal("Z:" + unicodePath.Replace("/", "\\", StringComparison.Ordinal), selected.TranslatePath(unicodePath));
        var missingOutput = Path.Combine(unicodePath, "Papyrus Résultats.pex");
        Assert.Equal("Z:" + missingOutput.Replace("/", "\\", StringComparison.Ordinal), selected.TranslatePath(missingOutput));
    }

    [Fact]
    public void MissingMappingDoesNotFallBackToStalePrefixConfigInfo()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var world = new AmethystSnapshotWorld();
        var fixture = CreateSteam(world, includeMapping: false);
        File.WriteAllText(Path.Combine(fixture.CompatData, "config_info"), "proton_test\n");
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);

        var error = Assert.Throws<InvalidOperationException>(() => SteamProtonResolver.Resolve(snapshot, fixture.Home, []));
        Assert.Contains("CompatToolMapping", error.Message);
        Assert.Contains("config_info", error.Message);
    }

    [Fact]
    public void RefusesMultipleInstalledCopiesOfTheSelectedTool()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var world = new AmethystSnapshotWorld();
        var fixture = CreateSteam(world);
        var second = Path.Combine(fixture.SteamRoot, "compatibilitytools.d", fixture.ToolName);
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(second, "compatibilitytool.vdf"), $"\"compatibilitytools\" {{ \"compat_tools\" {{ \"{fixture.ToolName}\" {{ \"install_path\" \".\" }} }} }}");
        File.WriteAllText(Path.Combine(second, "proton"), "#!/bin/sh\nexit 0\n");
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);

        Assert.Contains("multiple installed runners", Assert.Throws<InvalidOperationException>(
            () => SteamProtonResolver.Resolve(snapshot, fixture.Home, [])).Message);
    }

    [Fact]
    public void RefusesPrefixesOutsideTheSelectedSkyrimCompatdataLayout()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var world = new AmethystSnapshotWorld();
        var fixture = CreateSteam(world);
        SetPrefixPath(world, Path.Combine(world.Root, "some-other-prefix"));
        Directory.CreateDirectory(Path.Combine(world.Root, "some-other-prefix", "drive_c", "users", "steamuser", "Documents", "My Games", "Skyrim Special Edition"));
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);

        Assert.Contains("unsupported prefix layout", Assert.Throws<InvalidOperationException>(
            () => SteamProtonResolver.Resolve(snapshot, fixture.Home, [])).Message);
    }

    [Fact]
    public void RefusesAnArgumentNotReachableThroughPrefixDriveMappings()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var world = new AmethystSnapshotWorld();
        var fixture = CreateSteam(world, createRootDrive: false);
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);
        var selected = SteamProtonResolver.Resolve(snapshot, fixture.Home, []);

        var error = Assert.Throws<InvalidOperationException>(() => selected.TranslatePath("/etc/passwd"));
        Assert.Contains("not reachable through a drive mapping", error.Message);
    }

    [Fact]
    public void RefusesDuplicateWineDriveLetters()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var world = new AmethystSnapshotWorld();
        var fixture = CreateSteam(world);
        var dosDevices = Path.Combine(fixture.Prefix, "dosdevices");
        Directory.CreateSymbolicLink(Path.Combine(dosDevices, "Z:"), "/tmp");
        var selected = SteamProtonResolver.Resolve(AmethystSavedSnapshot.Capture(world.Manifest), fixture.Home, []);

        Assert.Contains("multiple host mappings for drive Z", Assert.Throws<InvalidOperationException>(
            () => selected.TranslatePath("/tmp")).Message);
    }

    [Fact]
    public void RefusesEffectiveGamePathOutsideTheSelectedSteamLibrary()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var world = new AmethystSnapshotWorld();
        var fixture = CreateSteam(world);
        SetGamePath(world, Path.Combine(world.Root, "game"));
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);

        Assert.Contains("effective Skyrim SE game path", Assert.Throws<InvalidOperationException>(
            () => SteamProtonResolver.Resolve(snapshot, fixture.Home, [])).Message);
    }

    [Fact]
    public void VdfReaderRejectsUnmatchedNonVdfBytes()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var world = new AmethystSnapshotWorld();
        var fixture = CreateSteam(world);
        var config = Path.Combine(fixture.SteamRoot, "config", "config.vdf");
        File.AppendAllText(config, " unquoted-garbage");
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);

        Assert.Contains("unsupported or malformed bytes", Assert.Throws<InvalidOperationException>(
            () => SteamProtonResolver.Resolve(snapshot, fixture.Home, [])).Message);
    }

    [Theory]
    [InlineData("proton_11", "Proton 11.0")]
    [InlineData("proton_hotfix", "Proton Hotfix")]
    public void ResolvesCanonicalDescriptorlessValveTools(string toolName, string directory)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var world = new AmethystSnapshotWorld();
        var fixture = CreateSteam(world, toolName: toolName, includeDescriptor: false, toolDirectoryName: directory);
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);

        var selected = SteamProtonResolver.Resolve(snapshot, fixture.Home, []);

        Assert.Equal(toolName, selected.ToolName);
        Assert.EndsWith(directory, selected.ToolDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsCustomToolInstallPathEscapingThroughSymlinkedParent()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var world = new AmethystSnapshotWorld();
        var fixture = CreateSteam(world);
        var outside = Path.Combine(world.Root, "outside-tool");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "proton"), "#!/bin/sh\nexit 0\n");
        Directory.CreateSymbolicLink(Path.Combine(fixture.ToolDirectory, "linked"), outside);
        File.WriteAllText(Path.Combine(fixture.ToolDirectory, "compatibilitytool.vdf"),
            $"\"compatibilitytools\" {{ \"compat_tools\" {{ \"{fixture.ToolName}\" {{ \"install_path\" \"linked\" }} }} }}");
        var snapshot = AmethystSavedSnapshot.Capture(world.Manifest);

        Assert.Contains("no matching installed Proton", Assert.Throws<InvalidOperationException>(
            () => SteamProtonResolver.Resolve(snapshot, fixture.Home, [])).Message);
    }

    [Fact]
    public void ResolvesSelectedToolFromSystemCompatibilityDirectory()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var world = new AmethystSnapshotWorld();
        var fixture = CreateSteam(world, toolName: "proton-cachyos-slr");
        var systemRoot = Path.Combine(world.Root, "usr", "share", "steam", "compatibilitytools.d");
        var installed = Path.Combine(systemRoot, fixture.ToolName);
        Directory.CreateDirectory(systemRoot);
        Directory.Move(fixture.ToolDirectory, installed);

        var selected = SteamProtonResolver.Resolve(AmethystSavedSnapshot.Capture(world.Manifest), fixture.Home, [systemRoot]);

        Assert.Equal(fixture.ToolName, selected.ToolName);
        Assert.Equal(Path.Combine(installed, "proton"), selected.ProtonExecutable);
        Assert.Equal(fixture.Prefix, selected.PrefixPath);
        Assert.Equal(fixture.CompatData, selected.CompatDataPath);
    }

    [Fact]
    public void RefusesDistinctLibraryAndSystemCopiesOfTheSelectedTool()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var world = new AmethystSnapshotWorld();
        var fixture = CreateSteam(world);
        var systemRoot = Path.Combine(world.Root, "usr", "local", "share", "steam", "compatibilitytools.d");
        var installed = Path.Combine(systemRoot, fixture.ToolName);
        Directory.CreateDirectory(installed);
        File.Copy(Path.Combine(fixture.ToolDirectory, "compatibilitytool.vdf"), Path.Combine(installed, "compatibilitytool.vdf"));
        File.Copy(fixture.Proton, Path.Combine(installed, "proton"));

        var error = Assert.Throws<InvalidOperationException>(() =>
            SteamProtonResolver.Resolve(AmethystSavedSnapshot.Capture(world.Manifest), fixture.Home, [systemRoot]));

        Assert.Contains("multiple installed runners", error.Message);
    }

    [Fact]
    public void SystemToolDoesNotSupplyAMissingSkyrimMapping()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var world = new AmethystSnapshotWorld();
        var fixture = CreateSteam(world, includeMapping: false);
        var systemRoot = Path.Combine(world.Root, "system-tools");
        Directory.CreateDirectory(systemRoot);
        Directory.Move(fixture.ToolDirectory, Path.Combine(systemRoot, fixture.ToolName));

        var error = Assert.Throws<InvalidOperationException>(() =>
            SteamProtonResolver.Resolve(AmethystSavedSnapshot.Capture(world.Manifest), fixture.Home, [systemRoot]));

        Assert.Contains("no current CompatToolMapping", error.Message);
    }

    [Fact]
    public void RejectsSystemToolInstallPathEscapingThroughSymlinkedParent()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var world = new AmethystSnapshotWorld();
        var fixture = CreateSteam(world);
        var systemRoot = Path.Combine(world.Root, "system-tools");
        var installed = Path.Combine(systemRoot, fixture.ToolName);
        Directory.CreateDirectory(systemRoot);
        Directory.Move(fixture.ToolDirectory, installed);
        var outside = Path.Combine(world.Root, "outside-tool");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "proton"), "#!/bin/sh\nexit 0\n");
        Directory.CreateSymbolicLink(Path.Combine(installed, "linked"), outside);
        File.WriteAllText(Path.Combine(installed, "compatibilitytool.vdf"),
            $"\"compatibilitytools\" {{ \"compat_tools\" {{ \"{fixture.ToolName}\" {{ \"install_path\" \"linked\" }} }} }}");

        var error = Assert.Throws<InvalidOperationException>(() =>
            SteamProtonResolver.Resolve(AmethystSavedSnapshot.Capture(world.Manifest), fixture.Home, [systemRoot]));

        Assert.Contains("no matching installed Proton", error.Message);
    }

    sealed record SteamFixture(string Home, string SteamRoot, string Library, string ToolName,
        string ToolDirectory, string Proton, string Prefix, string CompatData, string GameDir);

    static SteamFixture CreateSteam(AmethystSnapshotWorld world, bool includeMapping = true, bool createRootDrive = true,
        string toolName = "proton_test", bool includeDescriptor = true, string toolDirectoryName = "Proton Test")
    {
        var home = Path.Combine(world.Root, "home");
        var steam = Path.Combine(home, ".local", "share", "Steam");
        var library = Path.Combine(steam, "steamapps");
        var compat = Path.Combine(library, "compatdata", "489830");
        var prefix = Path.Combine(compat, "pfx");
        var name = toolName;
        var toolDir = Path.Combine(library, "common", toolDirectoryName);
        var proton = Path.Combine(toolDir, "proton");
        var gameDir = Path.Combine(library, "common", "Skyrim Special Edition");
        Directory.CreateDirectory(Path.Combine(gameDir, "Data"));
        File.WriteAllBytes(Path.Combine(gameDir, "SkyrimSE.exe"), "MZ"u8.ToArray());
        Directory.CreateDirectory(Path.Combine(prefix, "drive_c", "users", "steamuser", "Documents", "My Games", "Skyrim Special Edition"));
        Directory.CreateDirectory(Path.Combine(prefix, "drive_c"));
        var dosDevices = Path.Combine(prefix, "dosdevices");
        Directory.CreateDirectory(dosDevices);
        Directory.CreateSymbolicLink(Path.Combine(dosDevices, "c:"), "../drive_c");
        if (createRootDrive) Directory.CreateSymbolicLink(Path.Combine(dosDevices, "z:"), Path.DirectorySeparatorChar.ToString());
        Directory.CreateDirectory(toolDir);
        File.WriteAllText(proton, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(proton, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (includeDescriptor)
            File.WriteAllText(Path.Combine(toolDir, "compatibilitytool.vdf"), $"\"compatibilitytools\" {{ \"compat_tools\" {{ \"{name}\" {{ \"install_path\" \".\" }} }} }}");
        Directory.CreateDirectory(Path.Combine(steam, "config"));
        var mapping = includeMapping ? $"\"489830\" {{ \"name\" \"{name}\" \"config\" \"\" \"priority\" \"250\" }}" : "";
        File.WriteAllText(Path.Combine(steam, "config", "config.vdf"),
            $"\"InstallConfigStore\" {{ \"Software\" {{ \"Valve\" {{ \"Steam\" {{ \"CompatToolMapping\" {{ {mapping} }} }} }} }} }}");
        File.WriteAllText(Path.Combine(library, "libraryfolders.vdf"), $"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{Escape(steam)}\" }} }}");
        SetPrefixPath(world, prefix);
        SetGamePath(world, gameDir);
        return new SteamFixture(home, steam, steam, name, toolDir, proton, prefix, compat, gameDir);
    }

    static void SetPrefixPath(AmethystSnapshotWorld world, string prefix)
    {
        var pathFile = Path.Combine(world.Root, "manager-config", "paths.json");
        using var document = JsonDocument.Parse(File.ReadAllText(pathFile));
        var paths = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        paths["prefix_path"] = JsonDocument.Parse(JsonSerializer.Serialize(prefix)).RootElement.Clone();
        File.WriteAllText(pathFile, JsonSerializer.Serialize(paths));
    }

    static void SetGamePath(AmethystSnapshotWorld world, string game)
    {
        var pathFile = Path.Combine(world.Root, "manager-config", "paths.json");
        using var document = JsonDocument.Parse(File.ReadAllText(pathFile));
        var paths = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        paths["game_path"] = JsonDocument.Parse(JsonSerializer.Serialize(game)).RootElement.Clone();
        File.WriteAllText(pathFile, JsonSerializer.Serialize(paths));
    }

    static string Escape(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
