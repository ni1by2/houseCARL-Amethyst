using Mutagen.Bethesda.Plugins;

namespace HousecarlCore;

/// <summary>Identifies one of the two generated files that together define an NPC's face.</summary>
public enum FaceGenSlot
{
    /// <summary>The generated head geometry stored as a NIF beneath the <c>facegeom</c> directory.</summary>
    Mesh,

    /// <summary>The generated face-tint texture stored as a DDS beneath the <c>facetint</c> directory.</summary>
    Tint
}

/// <summary>
/// Derives canonical Data-relative FaceGen paths from an NPC's defining <see cref="FormKey"/>.
/// This pure transform performs no load-order lookup or filesystem access.
/// </summary>
/// <remarks>
/// Skyrim names the directory after the plugin that defines the NPC, not the plugin whose override wins.
/// It names the file with a zero high byte followed by the six-digit local FormID. Keeping this rule in one
/// helper prevents read, copy, and compaction features from resolving different NPC assets.
/// </remarks>
public static class FaceGenPath
{
    /// <summary>Builds the canonical Bethesda path for one generated face file.</summary>
    /// <param name="fk">
    /// NPC identity. Its ModKey supplies the defining-plugin directory and its local ID supplies the filename.
    /// </param>
    /// <param name="slot">Whether to return the head mesh or face-tint texture path.</param>
    /// <returns>
    /// A backslash-separated Data-relative path with the local FormID rendered as eight uppercase hexadecimal digits.
    /// </returns>
    /// <remarks>An enum value other than <see cref="FaceGenSlot.Mesh"/> follows the tint branch.</remarks>
    public static string For(FormKey fk, FaceGenSlot slot)
    {
        // The defining plugin and local FormID remain stable when another plugin wins the record.
        var master = fk.ModKey.FileName.ToString();
        var name = "00" + fk.ID.ToString("X6");
        return slot == FaceGenSlot.Mesh
            ? $@"meshes\actors\character\facegendata\facegeom\{master}\{name}.nif"
            : $@"textures\actors\character\facegendata\facetint\{master}\{name}.dds";
    }

    /// <summary>Builds both generated face paths in mesh-then-tint order.</summary>
    /// <param name="fk">NPC identity used by <see cref="For(FormKey, FaceGenSlot)"/>.</param>
    /// <returns>
    /// A two-entry read-only list whose tuples identify the slot and its canonical Data-relative path.
    /// </returns>
    public static IReadOnlyList<(FaceGenSlot Slot, string RelPath)> Both(FormKey fk)
        => new[] { (FaceGenSlot.Mesh, For(fk, FaceGenSlot.Mesh)), (FaceGenSlot.Tint, For(fk, FaceGenSlot.Tint)) };
}
