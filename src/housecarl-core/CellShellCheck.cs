using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace HousecarlCore;

/// <summary>Describes world content that remains to be authored for one newly created cell.</summary>
/// <param name="Cell">Cell FormKey.</param>
/// <param name="EditorId">Cell EditorID.</param>
/// <param name="Interior">Whether this is an interior cell.</param>
/// <param name="MustProvide">Lighting, terrain, water, or navigation work still required.</param>
public sealed record CellShell(FormKey Cell, string EditorId, bool Interior, IReadOnlyList<string> MustProvide);

/// <summary>Collects structural-shell notes for cells created by one write call.</summary>
/// <param name="Cells">Created cell notes.</param>
public sealed record CellShellReport(IReadOnlyList<CellShell> Cells)
{
    /// <summary>Gets a post-write inspection error, or null when the check ran.</summary>
    public string? CheckError { get; init; }

    /// <summary>Gets whether the report has neither cell notes nor an inspection error.</summary>
    public bool IsEmpty => Cells.Count == 0 && CheckError is null;

    /// <summary>Reusable result for a write that created no cells.</summary>
    public static readonly CellShellReport Empty = new(Array.Empty<CellShell>());
}

/// <summary>Reports the world-building work that remains after a cell record is created.</summary>
/// <remarks>
/// This is a post-write diagnostic and cannot turn a successful cell write into a failed transaction.
/// </remarks>
public static class CellShellCheck
{
    /// <summary>Record catalog name used to identify created cells.</summary>
    public const string CellCatalogName = "Cell";

    /// <summary>Inspects cells created by one completed write call.</summary>
    /// <param name="patchPath">Native path to the just-written patch.</param>
    /// <param name="created">Records created by that call.</param>
    /// <returns>Cell notes, or a report carrying a fault-isolated inspection error.</returns>
    public static CellShellReport Run(string patchPath, IReadOnlyList<WritePatchBuilder.CreatedRecord> created)
    {
        var cellEdids = new Dictionary<FormKey, string>();
        foreach (var c in created)
            if (string.Equals(c.RecordType, CellCatalogName, StringComparison.Ordinal))
                cellEdids[c.FormKey] = c.EditorId;
        if (cellEdids.Count == 0) return CellShellReport.Empty;

        ISkyrimModGetter? patch = null;
        try
        {
            patch = SkyrimMod.CreateFromBinaryOverlay(patchPath, SkyrimRelease.SkyrimSE);
            var shells = new List<CellShell>();
            // EnumerateMajorRecords<ICellGetter> finds cells in BOTH the interior Cells group AND worldspace blocks.
            foreach (var cell in patch.EnumerateMajorRecords<ICellGetter>())
            {
                if (!cellEdids.TryGetValue(cell.FormKey, out var edid)) continue;
                bool interior = cell.Flags.HasFlag(Cell.Flag.IsInteriorCell);
                shells.Add(new CellShell(cell.FormKey, edid, interior, MustProvide(interior)));
            }
            return shells.Count == 0 ? CellShellReport.Empty : new CellShellReport(shells);
        }
        catch (Exception ex)
        {
            return CellShellReport.Empty with { CheckError = $"{ex.GetType().Name}: {ex.Message}" };
        }
        finally { (patch as IDisposable)?.Dispose(); }
    }

    /// <summary>Gets the standing world-content requirements for an interior or exterior cell.</summary>
    /// <param name="interior">Whether the cell is interior.</param>
    /// <returns>Requirements that cannot be inferred from individual record fields.</returns>
    static IReadOnlyList<string> MustProvide(bool interior) => interior
        ? new[]
        {
            "lighting — a Lighting Template and/or lighting settings (else the cell renders pitch black)",
            "navmesh (else NPCs cannot path or spawn)",
        }
        : new[]
        {
            "terrain — a LAND record (else the cell is an empty void)",
            "water height + a region/location",
            "navmesh (else NPCs cannot path)",
        };
}
