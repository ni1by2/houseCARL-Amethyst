using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>
/// houseCARL-Amethyst setup tools. User-owned values are validated before an atomic config update.
///   • housecarl_set_tool_path — WHERE an external tool is (the Papyrus compiler, BSArch, or a log folder): the bridge the
///     compile / BSA / log-access riders sit on. Auto-detects canonical homes, so it's usually only needed for BSArch or a
///     non-standard install (<see cref="ToolPathResolver"/> + <see cref="ToolBridge"/>).
/// </summary>
[McpServerToolType]
public static class SetupTools
{
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

    [McpServerTool(Name = "housecarl_set_tool_path", Title = "Tell houseCARL where an external tool is"),
     Description(
         "Give houseCARL the path to an external tool it drives: 'papyrus_compiler' (the Creation Kit's " +
         "PapyrusCompiler.exe, for compiling .psc scripts to .pex), 'bsarch' (BSArch.exe, for .bsa archive " +
         "list/extract/repack), 'papyrus_logs' (the Papyrus script-log FOLDER), or 'crash_logs' (the SKSE crash-log " +
         "FOLDER) — the bridge houseCARL's compile / BSA / log-reading capabilities sit on. houseCARL AUTO-DETECTS the " +
         "canonical homes for the compiler and the log folders, so you usually only need this for BSArch (no fixed home) " +
         "or a non-standard install. VALIDATES the path — the .exe exists and looks like the right tool; the log folder " +
         "exists — and reports exactly what's wrong if not, saving NOTHING on failure (Q3). On success it SAVES the choice " +
         "to houseCARL.user.json so it persists across restarts, coexisting with your Amethyst connection. tool must be " +
         "one of: papyrus_compiler, bsarch, papyrus_logs, crash_logs.")]
    public static string SetToolPath(
        ToolPathResolver bridge,
        [Description("Which tool: 'papyrus_compiler' (CK PapyrusCompiler.exe), 'bsarch' (BSArch.exe), 'papyrus_logs' (script-log folder), or 'crash_logs' (SKSE crash-log folder).")]
            string tool,
        [Description("Full path to the tool: the .exe FILE for papyrus_compiler/bsarch, or the log DIRECTORY for papyrus_logs/crash_logs.")]
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

    /// <summary>Render the validated roots and whether the connection persisted.</summary>
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
