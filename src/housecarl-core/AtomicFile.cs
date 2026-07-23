namespace HousecarlCore;

/// <summary>
/// Crash-atomic file commit — the one primitive every houseCARL write funnels its FINAL swap through.
///
/// A complete file is first STAGED into a temp on the SAME volume as its final path (the caller's job), then handed
/// here. <see cref="Commit"/> swaps it into place with NO unlink-then-rename window:
///
///  • target EXISTS  → <c>File.Replace</c>: an atomic content swap. A crash mid-commit leaves either the OLD
///    complete file or the NEW complete file — never a missing or half-written one. Windows may preserve destination
///    metadata and may refuse a sharing-locked target. Linux installs the staged inode; existing handles and Amethyst
///    hardlinks continue to reference the old inode until they are reopened or redeployed.
///  • target ABSENT  → <c>File.Move</c> (an atomic rename onto a free name): <c>File.Replace</c> cannot create — it
///    requires an existing target — so the fresh-file case it throws on is served by a rename, itself atomic.
///
/// This replaces the former product-wide <c>File.Move(overwrite: true)</c>. Same-volume staging is the caller's
/// invariant — <c>File.Replace</c> THROWS across volumes (a loud, correct refusal) rather than silently degrading to a
/// non-atomic copy. Platform-specific permission or metadata failures also surface instead of silently degrading.
///
/// Holds no handle at rest. The atomic-commit guard verifies Windows replacement metadata separately from Linux
/// open-inode semantics, while both platforms prove byte-exact output, consumed staging, and loud pre-swap failure.
/// </summary>
// PUBLIC (facegen-diagnostics Phase 3): place_asset writes from the MCP layer (not a core friend), so the primitive is
// public. <see cref="Commit"/> is the low-level swap (caller stages same-volume); <see cref="WriteAllBytes"/> is the
// high-level convenience that does the same-volume staging for you (it materializes the bytes into a sibling temp, so a
// cross-volume SOURCE never reaches Commit). The .esp/BSA/config writers keep calling Commit directly with their own staging.
public static class AtomicFile
{
    /// <summary>Crash-atomically write <paramref name="bytes"/> to <paramref name="finalPath"/>: stage them into a SIBLING
    /// temp (same volume as the target, so the swap is never cross-volume — the place tool may read its source from another
    /// volume or a BSA, but the bytes are in hand by here) and <see cref="Commit"/> it into place. The caller must have
    /// created the destination directory. THROWS (Q3, never a silent partial write) on any failure, deleting the temp first
    /// so no scratch is left; on a throw the prior <paramref name="finalPath"/>, if any, is byte-intact (Commit's guarantee).</summary>
    public static void WriteAllBytes(string finalPath, byte[] bytes)
    {
        var staged = finalPath + ".houseCARL-tmp";                 // sibling of the target ⇒ same volume ⇒ Commit's invariant holds
        try { if (File.Exists(staged)) File.Delete(staged); } catch { /* a stuck temp surfaces on the write below */ }
        try
        {
            File.WriteAllBytes(staged, bytes);
            Commit(staged, finalPath);
        }
        catch
        {
            try { if (File.Exists(staged)) File.Delete(staged); } catch { /* best-effort: never mask the real failure */ }
            throw;
        }
    }

    /// <summary>Commit a fully-written <paramref name="stagedPath"/> onto <paramref name="finalPath"/> crash-atomically.
    /// Both MUST be on the same volume. THROWS (never a silent no-op) if the staged file is missing or the swap fails —
    /// the caller reports it (Q3); on any throw the prior <paramref name="finalPath"/>, if it existed, is byte-intact.</summary>
    public static void Commit(string stagedPath, string finalPath)
    {
        try
        {
            File.Replace(stagedPath, finalPath, destinationBackupFileName: null);
        }
        // Catch FileNotFoundException ONLY, and deliberately: a cross-volume swap surfaces here as IOException
        // (ERROR_UNABLE_TO_MOVE_REPLACEMENT_2) and, given the same-volume invariant, that's a caller bug we WANT loud
        // with the original retained — widening this catch to IOException would silently degrade it into a non-atomic
        // cross-volume copy (Q3). An EFS/special-ACL merge error likewise surfaces loud here, original byte-intact.
        catch (FileNotFoundException)
        {
            // File.Replace requires an existing destination; the fresh-file case (no prior output) it throws on is
            // served by a rename instead. overwrite:true keeps that branch idempotent against the (mutex-guarded,
            // houseCARL-owned-path) sub-millisecond TOCTOU where the target appears between the probe and here — there
            // is no original to lose on a path just judged fresh. A MISSING SOURCE still throws FileNotFoundException
            // here, so File.Move re-throws it loud rather than masking it as a silent no-op.
            File.Move(stagedPath, finalPath, overwrite: true);
        }
    }
}
