using System.Text.Json;

namespace HousecarlCore;

/// <summary>The saved manager generation a result describes; freshness is explicitly unverified.</summary>
/// <param name="Profile">Canonical manager profile directory.</param>
/// <param name="Generation">Saved profile resolution generation.</param>
/// <param name="InventoryGeneration">Catalog generation consumed by that profile.</param>
public sealed record AmethystSnapshotProvenance(string Profile, long Generation, ulong InventoryGeneration, bool VanillaArchivesOmitted = false)
{
    /// <summary>Explicit coverage limit of this preview; vanilla loose files use the distinct backup root.</summary>
    public const string VanillaArchiveWarning = "This preview does not index vanilla BSA archives. Asset coverage is incomplete when those archives are present; use explicit archive tools for their contents.";
    /// <summary>The warning carried by every result and artifact that depends on this snapshot.</summary>
    public const string Warning = "Using Amethyst's last saved filegraph. Changes made since its last refresh may be missing. " +
        "Refresh the selected profile in Amethyst after changing mods or settings. These results do not confirm what is deployed in-game.";

    /// <summary>Write the same provenance shape into inline JSON, MCP structured content, and artifact manifests.</summary>
    /// <param name="writer">Writer positioned inside the enclosing result object.</param>
    public void WriteTo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("amethyst_snapshot");
        writer.WriteString("profile", Profile);
        writer.WriteNumber("generation", Generation);
        writer.WriteNumber("inventory_generation", InventoryGeneration);
        writer.WriteString("mode", "saved-snapshot");
        writer.WriteString("freshness", "unverified");
        writer.WriteString("warning", Warning);
        if (VanillaArchivesOmitted) writer.WriteString("asset_coverage_warning", VanillaArchiveWarning);
        writer.WriteEndObject();
    }

    /// <summary>Read provenance from a saved artifact without claiming that its generation is still current.</summary>
    /// <param name="value">The enclosing result or artifact manifest.</param>
    /// <returns>The original profile and generation identity.</returns>
    public static AmethystSnapshotProvenance? Read(JsonElement value) => value.TryGetProperty("amethyst_snapshot", out var snapshot)
        ? new(snapshot.GetProperty("profile").GetString()!, snapshot.GetProperty("generation").GetInt64(),
            snapshot.GetProperty("inventory_generation").GetUInt64(), snapshot.TryGetProperty("asset_coverage_warning", out _)) : null;
}

/// <summary>One tool's pinned snapshot, carried through synchronous engines and artifact writers via async context.</summary>
public static class AmethystSnapshotContext
{
    /// <summary>Per-call state; parallel MCP calls cannot overwrite another call's provenance.</summary>
    static readonly AsyncLocal<AmethystSavedSnapshot?> Active = new();

    /// <summary>The captured snapshot, or null for manager-independent and legacy calls.</summary>
    public static AmethystSavedSnapshot? Current => Active.Value;

    /// <summary>Bind one snapshot until the returned scope is disposed; nested scopes restore their predecessor.</summary>
    /// <param name="snapshot">Validated complete saved snapshot for this call.</param>
    /// <returns>A scope that restores the previous async context.</returns>
    public static IDisposable Use(AmethystSavedSnapshot snapshot) => new Scope(snapshot);

    /// <summary>Restores the previous snapshot after a tool and its artifact writers finish.</summary>
    sealed class Scope : IDisposable
    {
        /// <summary>The calling scope, normally null at the MCP boundary.</summary>
        readonly AmethystSavedSnapshot? _previous;
        /// <summary>Save the calling context and select the current snapshot.</summary>
        public Scope(AmethystSavedSnapshot snapshot) { _previous = Active.Value; Active.Value = snapshot; }
        /// <summary>Restore the calling context without opening files or changing manager state.</summary>
        public void Dispose() => Active.Value = _previous;
    }
}
