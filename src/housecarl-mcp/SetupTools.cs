using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>
/// houseCARL-Amethyst setup tools. User-owned values are validated before an atomic config update.
/// <c>housecarl_set_tool_path</c> stores native diagnostic-log directories. External Windows
/// executables remain absent until the post-v1 structured Proton runner is implemented.
/// </summary>
[McpServerToolType]
public static class SetupTools
{
    /// <summary>Validates, activates, and persists an Amethyst connection manifest.</summary>
    /// <param name="svc">Singleton service whose manager state will be replaced after validation.</param>
    /// <param name="manifest_path">Absolute native path to schema-v1 connection.json.</param>
    /// <returns>Validated roots and persistence status, or a guarded actionable error.</returns>
    /// <remarks>Validation completes before live state or user configuration changes.</remarks>
    [McpServerTool(Name = "housecarl_set_amethyst_connection", Title = "Connect houseCARL to Amethyst"),
     Description(
         "Validate and activate a schema-v1 connection.json exported for Amethyst's Skyrim Special Edition profile. " +
         "The file supplies stable native Linux roots; houseCARL re-reads deploy_state.json to follow profile switches. " +
         "Unknown schemas, stale roots, malformed state, and unsafe merged Data layouts fail without changing the saved connection.")]
    public static string SetAmethystConnection(
        LoadOrderService svc,
        [Description("Absolute native Linux path to the exported .housecarl-amethyst/connection.json.")]
            string manifest_path) => Guard.Tool("housecarl_set_amethyst_connection", () =>
    {
        if (string.IsNullOrWhiteSpace(manifest_path))
            return "error: manifest_path is required.";
        try
        {
            var result = svc.SetAmethystConnection(manifest_path.Trim().Trim('"'));
            return Render(result.snapshot, result.persisted, result.persistError, result.persistNote);
        }
        catch (AmethystConfigurationException ex) { return "error: " + ex.Message; }
    });

    /// <summary>Validates and stores one optional diagnostic-log directory.</summary>
    [McpServerTool(Name = "housecarl_set_tool_path", Title = "Tell houseCARL where diagnostic logs are"),
     Description(
         "Store a native Linux directory containing Papyrus script logs or SKSE crash logs. The directory must already " +
         "exist. Nothing is saved on validation failure. On success the path persists in houseCARL.user.json alongside " +
         "the Amethyst connection. Valid tool keys are papyrus_logs and crash_logs. PapyrusCompiler and BSArch execution " +
         "remain deferred until the post-v1 structured Proton runner.")]
    public static string SetToolPath(
        ToolPathResolver bridge,
        [Description("Which log directory: 'papyrus_logs' or 'crash_logs'.")]
            string tool,
        [Description("Absolute native Linux path to the existing log directory.")]
            string path) => Guard.Tool("housecarl_set_tool_path", () =>
    {
        if (string.IsNullOrWhiteSpace(tool))
            return "error: no tool named. Pass tool= one of: " + ToolBridge.WireKeys + ".";
        if (!ToolBridge.TryParse(tool, out var dep))
            return $"error: unknown tool '{tool}'. Expected one of: {ToolBridge.WireKeys}.";
        if (string.IsNullOrWhiteSpace(path))
            return $"error: no path given for '{tool}'. Pass the full path to {ToolBridge.Info(dep).Display}.";

        var (ok, error, persisted, persistError, persistNote, resolved) = bridge.Save(dep, path);
        if (!ok) return "error: " + error;   // validation failed — nothing saved (Q3)

        var info = ToolBridge.Info(dep);
        var sb = new StringBuilder();
        sb.Append("configured houseCARL -> ").Append(info.Display).Append('\n');
        sb.Append("  path: ").Append(resolved).Append('\n');
        sb.Append(persisted
            ? "saved to houseCARL.user.json — persists across restarts (coexists with your Amethyst connection)."
            : $"NOTE: could not save ({persistError}) — works this session, but you'll need to set it again after a restart.");
        if (persistNote is not null) sb.Append("\nRECOVERED: ").Append(persistNote);   // corrupt prior config — never silent (hunt F3)
        return sb.ToString();
    });

    /// <summary>Renders validated roots and whether the connection persisted.</summary>
    /// <param name="p">Snapshot activated by the service.</param>
    /// <param name="persisted">Whether houseCARL.user.json was updated atomically.</param>
    /// <param name="persistError">Write failure when persistence was unsuccessful.</param>
    /// <param name="persistNote">Optional corrupt-config recovery note that must remain visible.</param>
    /// <returns>Connection confirmation that distinguishes live success from restart persistence.</returns>
    internal static string Render(ManagerSnapshot p, bool persisted, string? persistError, string? persistNote)
    {
        var sb = new StringBuilder();
        sb.Append("connected houseCARL-Amethyst\n");
        sb.Append("manifest: ").Append(p.ManifestPath).Append("  (schema ").Append(p.SchemaVersion).Append(")\n");
        sb.Append("active profile: ").Append(p.ActiveProfileName).Append('\n');
        sb.Append("  staging     : ").Append(p.ProfileSpecificMods ? "profile-specific" : "shared").Append('\n');
        sb.Append("  mods folder: ").Append(p.ModsDir).Append('\n');
        sb.Append("  overwrite  : ").Append(p.OverwriteDir).Append('\n');
        sb.Append("  vanilla Data: ").Append(p.VanillaDataDir).Append('\n');
        sb.Append("  deployment : ").Append(p.DeploymentActive ? "active" : "inactive")
          .Append(p.LastDeploymentMode is null ? "" : $" ({p.LastDeploymentMode})").Append('\n');
        sb.Append(persisted
            ? "saved to houseCARL.user.json — persists across restarts."
            : $"NOTE: could not save ({persistError}) — reconnect after restart.");
        if (persistNote is not null) sb.Append("\nRECOVERED: ").Append(persistNote);
        return sb.ToString();
    }
}
