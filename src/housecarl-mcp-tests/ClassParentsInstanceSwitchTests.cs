using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The decompiler's class-parent cache across an instance switch: a scan that snapshotted the old mods folder
/// must not publish the old tree as the complete map once the switch has invalidated it.</summary>
[Trait("tier", "unit")]
public sealed class ClassParentsInstanceSwitchTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-class-parents-switch-" + Guid.NewGuid().ToString("N"));
    readonly string _oldInstance;
    readonly string _newInstance;
    readonly LoadOrderService _svc;

    public ClassParentsInstanceSwitchTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "game", "Data"));
        _oldInstance = StageInstance("old", "HcOldTreeChild", "HcOldTreeHost");
        _newInstance = StageInstance("new", "HcNewTreeChild", "HcNewTreeHost");
        _svc = LoadOrderService.WithInstance(_oldInstance, 0, new UserConfigStore(Path.Combine(_root, "houseCARL.user.json")));
    }

    /// <summary>One MO2 instance whose mods tree holds one loose .psc naming <paramref name="child"/> extends <paramref name="parent"/>.</summary>
    string StageInstance(string name, string child, string parent)
    {
        var instance = Path.Combine(_root, name);
        var profile = Path.Combine(instance, "profiles", "Default");
        var source = Path.Combine(instance, "mods", "ScriptMod", "Scripts", "Source");
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");
        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "# header\r\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), "");
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), "# header\r\n+ScriptMod\r\n");
        File.WriteAllText(Path.Combine(source, child + ".psc"), $"ScriptName {child} extends {parent}\r\n");
        return instance;
    }

    public void Dispose()
    {
        _svc.BeforeClassParentsPublishForGuard = null;
        _svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }

    [Fact]
    public void AnInstanceSwitchMidScanDoesNotCacheTheOldTree()
    {
        var old = new AmethystFixtureConnection(_oldInstance);
        var next = new AmethystFixtureConnection(_newInstance);
        _svc.SetAmethystSnapshot(old.Manifest);
        var captured = _svc.CaptureSavedSnapshot();
        using (AmethystSnapshotContext.Use(captured))
        {
            var before = _svc.ClassParentsForDecompile();
            Assert.Equal("HcOldTreeHost", before.Edges["HcOldTreeChild"]);
            _svc.SetAmethystSnapshot(next.Manifest);
            var during = _svc.ClassParentsForDecompile();
            Assert.Equal("HcOldTreeHost", during.Edges["HcOldTreeChild"]);
            Assert.False(during.Edges.ContainsKey("HcNewTreeChild"));
        }
        using var newScope = AmethystSnapshotContext.Use(_svc.CaptureSavedSnapshot());
        var after = _svc.ClassParentsForDecompile();
        Assert.Equal("HcNewTreeHost", after.Edges["HcNewTreeChild"]);
        Assert.False(after.Edges.ContainsKey("HcOldTreeChild"));
        Assert.Null(after.TopUpMissing);
    }
}
