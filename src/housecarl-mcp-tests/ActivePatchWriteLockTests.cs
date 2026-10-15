using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// Writing into a patch that is ACTIVE in the resolver's order. These tests check the saved record contents
/// after removal or Apply, without requiring Windows file-sharing behavior. Each test writes its own patch
/// in its own folder.
/// </summary>
[Trait("tier", "integration")]
public sealed class ActivePatchWriteLockTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-writelock-" + Guid.NewGuid().ToString("N"));

    public ActivePatchWriteLockTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp cleanup best-effort */ } }

    // Probe arm REMOVE (FIX): RemoveRecords writes into the active patch, leaving one record.
    [Fact]
    public void RemoveWritesIntoTheActivePatch()
    {
        var target = TwoKeywordPatch(new ModKey("HcWriteLockGuard", ModType.Plugin), out var removeFk);
        using var r = LoadOrderResolver.Build(new[] { target });

        var o = WritePatchBuilder.RemoveRecords(r, new[] { removeFk }, target);

        Assert.True(o.Success, o.Error);
        Assert.Equal(1, o.RemainingRecords);
        using var back = SkyrimMod.CreateFromBinaryOverlay(target, SkyrimRelease.SkyrimSE);
        Assert.Single(back.EnumerateMajorRecords());
    }

    // Probe arm APPLY: Apply re-edits a record the active patch itself overrides (the winner is the target), and the
    // edited value lands.
    [Fact]
    public void ApplyReEditsTheActivePatchsOwnOverride()
    {
        var mKey = new ModKey("HcWriteLockGuardMaster", ModType.Master);
        var qKey = new ModKey("HcWriteLockGuardPatch", ModType.Plugin);
        var mPath = Path.Combine(_dir, mKey.FileName.String);
        var qPath = Path.Combine(_dir, qKey.FileName.String);
        var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
        var w = m.Weapons.AddNew(); w.EditorID = "HcGuardWeap"; w.BasicStats = new WeaponBasicStats { Damage = 10 };
        m.BeginWrite.ToPath(mPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        using (var mOv = SkyrimMod.CreateFromBinaryOverlay(mPath, SkyrimRelease.SkyrimSE))
        {
            var q = new SkyrimMod(qKey, SkyrimRelease.SkyrimSE);
            q.Weapons.GetOrAddAsOverride(mOv.Weapons.First(x => x.FormKey == w.FormKey)).BasicStats!.Damage = 20;
            q.BeginWrite.ToPath(qPath).WithLoadOrder(new ISkyrimModGetter[] { mOv }).Write();
        }

        using (var r = LoadOrderResolver.Build(new[] { mPath, qPath }))
        {
            var edit = new WritePatchBuilder.PatchEdit { Target = w.FormKey, Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = "777" };
            var o = WritePatchBuilder.Apply(r, TestCorpus.Rulebook, new[] { edit }, qPath, extend: true);
            Assert.True(o.Success, o.Error);
        }

        using var back = SkyrimMod.CreateFromBinaryOverlay(qPath, SkyrimRelease.SkyrimSE);
        Assert.Equal(777, (int)back.Weapons.First(x => x.FormKey == w.FormKey).BasicStats!.Damage);
    }

    string TwoKeywordPatch(ModKey key, out FormKey second)
    {
        var path = Path.Combine(_dir, key.FileName.String);
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        mod.Keywords.AddNew().EditorID = "HcWriteLockGuard_A";
        var b = mod.Keywords.AddNew(); b.EditorID = "HcWriteLockGuard_B";
        second = b.FormKey;
        mod.BeginWrite.ToPath(path).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        return path;
    }
}
