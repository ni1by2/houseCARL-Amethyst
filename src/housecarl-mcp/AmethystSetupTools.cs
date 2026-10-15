using System.ComponentModel;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>Explicit connection selection and consent for warned saved-snapshot reads.</summary>
[McpServerToolType]
public static class AmethystSetupTools
{
    /// <summary>Validate a saved graph and persist the chosen connection and read mode.</summary>
    [McpServerTool(Name = ToolNames.SetAmethystConnection, Title = "Select Amethyst's saved filegraph"),
     Description("Select an Amethyst schema-v1 connection manifest and explicitly enable warned saved-snapshot access. Uses the last saved filegraph as provided by Amethyst. Changes made since Refresh may be missing; results do not verify deployment. Apply, create, forward and copy can write new patches to staging, awaiting Refresh, enablement and deployment. Existing-plugin edits and other managed writes remain unavailable. Selecting the connection does not alter Amethyst files.")]
    public static string SetAmethystConnection(LoadOrderService svc,
        [Description("Absolute native path to the schema-v1 Amethyst connection manifest.")] string manifest_path,
        [Description("Required explicit mode: 'saved-snapshot'. Confirms acceptance of unverified freshness warnings.")] string filegraph_mode)
        => Guard.Tool(ToolNames.SetAmethystConnection, () =>
        {
            if (!OperatingSystem.IsLinux()) return "error: Amethyst saved-snapshot mode requires Linux";
            if (filegraph_mode != "saved-snapshot") return "error: filegraph_mode must explicitly be 'saved-snapshot'";
            var result = svc.SetAmethystSnapshot(manifest_path);
            return $"Amethyst saved-snapshot mode selected: {result.Snapshot.Profile}; generation {result.Snapshot.Generation}.\n" +
                "WARNING: " + AmethystSnapshotProvenance.Warning + "\nNew record patches can be written to staging; Refresh, enable and deploy them in Amethyst. Existing-plugin edits remain unavailable.\n" +
                (result.Persisted ? "Connection and mode saved for restart.\n" : $"Connection is active for this session, but could not be saved: {result.Error}\n") +
                (result.Note is null ? "" : "User config recovery: " + result.Note);
        });
}
