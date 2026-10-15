using System.Text.Json;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

[Trait("tier", "unit")]
public sealed class AmethystConnectionTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-connection-" + Guid.NewGuid().ToString("N"));
    readonly string _profileRoot;
    readonly string _configDir;
    readonly string _paths;
    readonly string _deploy;
    readonly string _manifest;

    public AmethystConnectionTests()
    {
        _profileRoot = Path.Combine(_root, "staging");
        _configDir = Path.Combine(_root, "config");
        _paths = Path.Combine(_configDir, "paths.json");
        _deploy = Path.Combine(_configDir, "deploy_state.json");
        _manifest = Path.Combine(_root, "connection.json");
        Directory.CreateDirectory(_profileRoot);
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(_paths, JsonSerializer.Serialize(new { staging_path = _profileRoot }));
        File.WriteAllText(_deploy, "{}");
        WriteManifest(1, _paths);
    }

    public void Dispose() { Directory.Delete(_root, recursive: true); }

    void WriteManifest(int version, string paths) => File.WriteAllText(_manifest, JsonSerializer.Serialize(new
    {
        schemaVersion = version, manager = "amethyst", gameId = "skyrim_se",
        profileRoot = _profileRoot, gameConfigDir = _configDir,
        pathsFile = paths, deployStateFile = _deploy,
    }));

    [Fact]
    public void SchemaV1ConnectionLoadsWithoutReadingManagerWinners()
    {
        var connection = AmethystConnection.Open(_manifest);

        Assert.Equal(_profileRoot, connection.ProfileRoot);
        Assert.Equal(_paths, connection.PathsFile);
    }

    [Fact]
    public void UnknownSchemaIsRefused()
    {
        WriteManifest(2, _paths);

        Assert.Contains("schemaVersion 2", Assert.Throws<AmethystConfigurationException>(() => AmethystConnection.Open(_manifest)).Message);
    }

    [Fact]
    public void ChangedStagingRootIsRefused()
    {
        var different = Path.Combine(_root, "other-staging");
        Directory.CreateDirectory(different);
        File.WriteAllText(_paths, JsonSerializer.Serialize(new { staging_path = different }));

        Assert.Contains("disagrees", Assert.Throws<AmethystConfigurationException>(() => AmethystConnection.Open(_manifest)).Message);
    }

    [Fact]
    public void PathsFileOutsideGameConfigIsRefused()
    {
        var outside = Path.Combine(_root, "paths.json");
        File.WriteAllText(outside, "{}");
        WriteManifest(1, outside);

        Assert.Contains("inside gameConfigDir", Assert.Throws<AmethystConfigurationException>(() => AmethystConnection.Open(_manifest)).Message);
    }
}
