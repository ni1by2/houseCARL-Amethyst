namespace HousecarlCore;

/// <summary>
/// The houseCARL ownership marker written into a generated mod folder's <c>meta.ini</c> — the ONE structural signal
/// that houseCARL authored a mod folder (and may therefore modify it in place; a missing marker FAIL-SAFES to
/// not-owned, so houseCARL refuses to touch a folder it can't prove it made — Q3). The marker is manager metadata at
/// the mod root rather than a Data-relative payload, so Amethyst never deploys it into the game Data directory.
///
/// Single-sourced here so the writer (<c>LoadOrderService.WriteOwnerMeta</c>), the owner-detection reader
/// (<c>LoadOrderService.IsHouseCarlOwned</c>), the in-place edit audit stamp, and the CI probe fixtures that seed an
/// owned folder all reference ONE literal and can't drift — the same "one shared home" discipline
/// <see cref="FormIdRange"/> applies to numeric ranges. (The unrelated <c>[houseCARL]</c> stderr LOG prefixes in the
/// MCP layer are a different literal for a different job and are deliberately NOT folded in here.)
/// </summary>
public static class HousecarlOwnerMeta
{
    /// <summary>The custom <c>meta.ini</c> section header that flags a houseCARL-generated Amethyst staging folder.
    /// Matched case-insensitively against a trimmed line; paired with <c>generated=true</c> under it, which is what
    /// owner-detection actually keys on.</summary>
    public const string Section = "[houseCARL]";
}
