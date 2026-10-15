using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Compacting a voiced plugin carries each dialogue line's FormID-keyed voice files (.fuz audio, .lip sync) to
/// the line's new INFO FormID, so the compacted mod does not go mute: to a fresh mod folder, in place, per line across
/// two lines, and with nothing to carry. Driven through <c>LoadOrderService.CompactPlugin</c>.</summary>
[Trait("tier", "integration")]
public sealed class CompactVoiceCarryTests
{
    const string VoiceType = "FemaleEventoned";
    const int RespNum = 1;
    static readonly byte[] Fuz = { 0x46, 0x55, 0x5A, 0x01, 0x02, 0x03 };
    static readonly byte[] Lip = { 0x4C, 0x49, 0x50, 0x10, 0x11 };

    static string Voice(FormKey info, string topic, VoiceFile kind) => VoicePath.For(info, VoiceType, "", topic, RespNum, kind);

    static void AddTopic(SkyrimMod m, uint topicId, uint infoId, string topicEdid)
    {
        var topic = new DialogTopic(new FormKey(m.ModKey, topicId), SkyrimRelease.SkyrimSE) { EditorID = topicEdid };
        topic.Responses.Add(new DialogResponses(new FormKey(m.ModKey, infoId), SkyrimRelease.SkyrimSE));
        m.DialogTopics.Add(topic);
    }

    static FormKey InfoKey(string pluginPath, string topicEdid)
    {
        using var pp = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE);
        return pp.DialogTopics.Single(t => t.EditorID == topicEdid).Responses.Single().FormKey;
    }

    // NEW-FILE: .fuz + .lip carried byte-exact to the NEW INFO FormID, in the light window, under the fresh mod folder;
    // the report says 2 scanned / 2 files / 1 line, no failures
    [Fact]
    public void ANewFileCompactCarriesFuzAndLipToTheNewInfoId()
    {
        using var inst = new CompactCarryInstance("VoiceNf", m => AddTopic(m, 0x810, 0x811, "HcVoiceTopic"));
        var old = new FormKey(inst.Key, 0x811);
        inst.Loose(Voice(old, "HcVoiceTopic", VoiceFile.Fuz), Fuz);
        inst.Loose(Voice(old, "HcVoiceTopic", VoiceFile.Lip), Lip);
        inst.Loose(VoicePath.For(old, VoiceType, "", "HcVoiceTopic", 2, VoiceFile.Fuz), new byte[] { 0x46, 0x55, 0x5A, 0x02 });

        var o = inst.Compact();
        Assert.True(o.Success, o.Error);
        var ik = InfoKey(o.OutputPath, "HcVoiceTopic");
        Assert.InRange(ik.ID, RemapEngine.EslFloor, RemapEngine.EslCeiling);
        Assert.NotEqual(old.ID, ik.ID);
        var root = Path.GetDirectoryName(o.OutputPath)!;
        Assert.NotEqual(inst.ModDir, root);
        Assert.True(CompactCarryInstance.Holds(BethesdaPath.Under(root, Voice(ik, "HcVoiceTopic", VoiceFile.Fuz)), Fuz));
        Assert.True(CompactCarryInstance.Holds(BethesdaPath.Under(root, Voice(ik, "HcVoiceTopic", VoiceFile.Lip)), Lip));
        Assert.True(CompactCarryInstance.Holds(BethesdaPath.Under(root,
            VoicePath.For(ik, VoiceType, "", "HcVoiceTopic", 2, VoiceFile.Fuz)), new byte[] { 0x46, 0x55, 0x5A, 0x02 }));
        Assert.Equal(3, o.VoiceRename?.FilesScanned);
        Assert.Equal(3, o.VoiceRename?.FilesCarried);
        Assert.Equal(1, o.VoiceRename?.LinesCarried);
        Assert.Empty(o.VoiceRename!.Failures);
    }

    // NEW-FILE: the OLD-FormID voice is left untouched (non-destructive)
    [Fact]
    public void ANewFileCompactLeavesTheOldVoiceUntouched()
    {
        using var inst = new CompactCarryInstance("VoiceNf", m => AddTopic(m, 0x810, 0x811, "HcVoiceTopic"));
        var oldFuz = Voice(new FormKey(inst.Key, 0x811), "HcVoiceTopic", VoiceFile.Fuz);
        inst.Loose(oldFuz, Fuz);
        Assert.True(inst.Compact().Success);
        Assert.True(CompactCarryInstance.Holds(BethesdaPath.Under(inst.ModDir, oldFuz), Fuz));
    }

    // IN-PLACE: the voice is carried into the target's OWN folder at the new FormID; the old file stays as a harmless orphan
    [Fact]
    public void AnInPlaceCompactCarriesTheVoiceIntoTheTargetsFolderAndLeavesTheOldOrphan()
    {
        using var inst = new CompactCarryInstance("VoiceIp", m => AddTopic(m, 0x820, 0x821, "HcVoiceTopic"));
        var oldFuz = Voice(new FormKey(inst.Key, 0x821), "HcVoiceTopic", VoiceFile.Fuz);
        inst.Loose(oldFuz, Fuz);

        var o = inst.Compact(inPlace: true);
        Assert.True(o.Success, o.Error);
        Assert.True(o.InPlace);
        var ik = InfoKey(o.OutputPath, "HcVoiceTopic");
        Assert.NotEqual(0x821u, ik.ID);
        Assert.True(CompactCarryInstance.Holds(BethesdaPath.Under(inst.ModDir, Voice(ik, "HcVoiceTopic", VoiceFile.Fuz)), Fuz));
        Assert.Equal(1, o.VoiceRename?.FilesCarried);
        Assert.Equal(1, o.VoiceRename?.LinesCarried);
        Assert.True(File.Exists(BethesdaPath.Under(inst.ModDir, oldFuz)));
    }

    // MULTI-LINE: two INFOs with DISTINCT audio each keep their OWN .fuz across the renumber
    [Fact]
    public void EachDialogueLineKeepsItsOwnAudio()
    {
        byte[] fuzA = { 0xA0, 0x01, 0x02 }, fuzB = { 0xB0, 0x01, 0x02 };
        using var inst = new CompactCarryInstance("VoiceMl", m =>
        {
            AddTopic(m, 0x830, 0x831, "HcTopicA");
            AddTopic(m, 0x832, 0x833, "HcTopicB");
        });
        inst.Loose(Voice(new FormKey(inst.Key, 0x831), "HcTopicA", VoiceFile.Fuz), fuzA);
        inst.Loose(Voice(new FormKey(inst.Key, 0x833), "HcTopicB", VoiceFile.Fuz), fuzB);

        var o = inst.Compact(inPlace: true);
        Assert.True(o.Success, o.Error);
        Assert.True(CompactCarryInstance.Holds(
            BethesdaPath.Under(inst.ModDir, Voice(InfoKey(o.OutputPath, "HcTopicA"), "HcTopicA", VoiceFile.Fuz)), fuzA));
        Assert.True(CompactCarryInstance.Holds(
            BethesdaPath.Under(inst.ModDir, Voice(InfoKey(o.OutputPath, "HcTopicB"), "HcTopicB", VoiceFile.Fuz)), fuzB));
        Assert.Equal(2, o.VoiceRename?.FilesCarried);
        Assert.Equal(2, o.VoiceRename?.LinesCarried);
    }

    // NO-VOICE: a plugin with no voice files carries nothing and is NOT a failure
    [Fact]
    public void APluginWithNoVoiceFilesCarriesNothingAndIsNotAFailure()
    {
        using var inst = new CompactCarryInstance("VoiceNone", m => AddTopic(m, 0x840, 0x841, "HcVoiceTopic"));
        var o = inst.Compact();
        Assert.True(o.Success, o.Error);
        Assert.Equal(0, o.VoiceRename?.FilesScanned);
        Assert.Equal(0, o.VoiceRename?.FilesCarried);
        Assert.Equal(0, o.VoiceRename?.LinesCarried);
        Assert.Empty(o.VoiceRename!.Failures);
    }

    [Fact]
    public void VoiceDestinationRewritesOnlyThePluginDirectoryAndRejectsEscapes()
    {
        Assert.True(AssetRenameService.TryRewriteVoicePath(
            @"sound/voice/Donor.esp/MaleEvenToned/Sub/HcQ_HcT_00000A11_1.fuz",
            @"Sound\Voice\donor.ESP", @"Sound\Voice\Merged.esp", "HcQ_HcT_00000B22_1.fuz",
            out var rewritten, out var error), error);
        Assert.Equal(@"Sound\Voice\Merged.esp\MaleEvenToned\Sub\HcQ_HcT_00000B22_1.fuz", rewritten);

        Assert.False(AssetRenameService.TryRewriteVoicePath(
            @"Sound\Voice\Donor.esp\..\Other\voice.fuz", @"Sound\Voice\Donor.esp",
            @"Sound\Voice\Merged.esp", "voice.fuz", out var escaped, out _));
        Assert.Empty(escaped);
        Assert.False(AssetRenameService.TryRewriteVoicePath(
            @"Sound\Voice\Other.esp\MaleEvenToned\voice.fuz", @"Sound\Voice\Donor.esp",
            @"Sound\Voice\Merged.esp", "voice.fuz", out escaped, out _));
    }
}
