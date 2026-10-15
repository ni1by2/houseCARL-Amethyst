using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

[Trait("tier", "unit")]
public sealed class PluginPathsTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-plugin-paths-" + Guid.NewGuid().ToString("N"));
    readonly string _mods;
    readonly string _data;
    readonly string _overwrite;

    public PluginPathsTests()
    {
        _mods = Path.Combine(_root, "mods");
        _data = Path.Combine(_root, "game", "Data");
        _overwrite = Path.Combine(_root, "overwrite");
        Directory.CreateDirectory(_mods);
        Directory.CreateDirectory(_data);
        Directory.CreateDirectory(_overwrite);
    }

    Mo2Roots Roots(string? mods = null) => new(Path.Combine(_root, "profile"), _data, mods ?? _mods, _overwrite);

    [Fact]
    public void ClassifiesNativePathsAndPreservesLayerPrecedence()
    {
        var modFile = Path.Combine(_mods, "Data", "nested", "Fixture.esp");
        var overwriteFile = Path.Combine(_overwrite, "Fixture.esp");
        var dataFile = Path.Combine(_data, "Fixture.esp");
        foreach (var path in new[] { modFile, overwriteFile, dataFile })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fixture");
        }

        Assert.Equal(new SourceLayer(SourceLayerKind.ModFolder, "Data"), PluginPaths.InstallLayerOfPath(modFile, Roots(_mods + Path.DirectorySeparatorChar)));
        Assert.Equal(new SourceLayer(SourceLayerKind.Overwrite, AssetResolver.OverwriteLayerName), PluginPaths.InstallLayerOfPath(overwriteFile, Roots()));
        Assert.Equal(new SourceLayer(SourceLayerKind.GameData, AssetResolver.DataLayerName), PluginPaths.InstallLayerOfPath(dataFile, Roots()));
    }

    [Fact]
    public void NormalizesDotSegmentsButRejectsRootsSiblingsAndRootLevelFiles()
    {
        var nested = Path.Combine(_mods, "One", "Fixture.esp");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        File.WriteAllText(nested, "fixture");
        var throughParent = Path.Combine(_mods, "..", "mods", "One", "Fixture.esp");
        var sibling = Path.Combine(_root, "mods-backup", "One", "Fixture.esp");
        var rootLevel = Path.Combine(_mods, "Fixture.esp");
        Directory.CreateDirectory(Path.GetDirectoryName(sibling)!);
        Directory.CreateDirectory(_mods);
        File.WriteAllText(sibling, "fixture");
        File.WriteAllText(rootLevel, "fixture");

        Assert.Equal("One", PluginPaths.InstallLayerOfPath(throughParent, Roots())?.Name);
        Assert.Null(PluginPaths.InstallLayerOfPath(sibling, Roots()));
        Assert.Null(PluginPaths.InstallLayerOfPath(rootLevel, Roots()));
        Assert.Null(PluginPaths.InstallLayerOfPath(_mods, Roots()));
    }

    [Fact]
    public void LinuxContainmentIsCaseSensitive()
    {
        if (OperatingSystem.IsWindows()) return;
        var caseDistinct = Path.Combine(_mods, "case", "One", "Fixture.esp");
        Directory.CreateDirectory(Path.GetDirectoryName(caseDistinct)!);
        File.WriteAllText(caseDistinct, "fixture");

        Assert.Null(PluginPaths.InstallLayerOfPath(caseDistinct, Roots(Path.Combine(_mods, "Case"))));
        Assert.Equal("case", PluginPaths.InstallLayerOfPath(caseDistinct, Roots())?.Name);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
