using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>Read-only manager diagnostics independent of the record index.</summary>
[McpServerToolType]
public static class AmethystTools
{
    [McpServerTool(Name = "housecarl_amethyst_status", ReadOnly = true, Title = "Amethyst connection status"),
     Description("Report the active Amethyst profile, native staging roots, deployment state, and freshness inputs. " +
                 "Validates connection.json, paths.json, deploy_state.json, and profile_state.json without writing them.")]
    public static string Status(LoadOrderService svc) =>
        Guard.Tool("housecarl_amethyst_status", () => Render(svc.AmethystSnapshot()));

    [McpServerTool(Name = "housecarl_refresh", ReadOnly = true, Title = "Refresh Amethyst state"),
     Description("Re-read the Amethyst connection and active profile now. Normal tools also refresh lazily.")]
    public static string Refresh(LoadOrderService svc) =>
        Guard.Tool("housecarl_refresh", () =>
            svc.RefreshAmethyst() ? "refreshed Amethyst state." : "Amethyst state is already current.");

    internal static string Render(ManagerSnapshot s)
    {
        var text = new StringBuilder()
            .Append("Amethyst connection — schema ").Append(s.SchemaVersion).Append('\n')
            .Append("manifest: ").Append(s.ManifestPath).Append('\n')
            .Append("profile: ").Append(s.ActiveProfileName).Append(" (")
            .Append(s.ProfileSpecificMods ? "profile-specific" : "shared").Append(" staging)\n")
            .Append("profile dir: ").Append(s.ProfileDir).Append('\n')
            .Append("game: ").Append(s.GamePath).Append('\n')
            .Append("vanilla Data: ").Append(s.VanillaDataDir).Append('\n')
            .Append("mods: ").Append(s.ModsDir).Append('\n')
            .Append("overwrite: ").Append(s.OverwriteDir).Append('\n')
            .Append("filemap: ").Append(s.FilemapPath).Append('\n')
            .Append("mod index: ").Append(s.ModIndexPath).Append('\n')
            .Append("deployment: ").Append(s.DeploymentActive ? "active" : "inactive");
        if (s.LastDeploymentMode is not null) text.Append(" (").Append(s.LastDeploymentMode).Append(')');
        text.Append("\nfreshness inputs:\n");
        foreach (var input in s.FreshnessInputs)
            text.Append("  ").Append(input.Value == DateTime.MinValue ? "missing" : input.Value.ToString("O"))
                .Append("  ").Append(input.Key).Append('\n');
        if (s.Warnings.Count > 0)
            foreach (var warning in s.Warnings) text.Append("warning: ").Append(warning).Append('\n');
        return text.ToString().TrimEnd();
    }
}
