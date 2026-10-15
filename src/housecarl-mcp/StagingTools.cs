using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>Inspect and manage durable Amethyst staging transactions.</summary>
[McpServerToolType]
public static class StagingTools
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [McpServerTool(Name = ToolNames.StagingStatus, ReadOnly = true, Title = "Staging transaction status"),
     Description("Read-only inspection of durable houseCARL-Amethyst transactions. Safe while Amethyst is open. Omit transaction_id to list a bounded recent set; corrupt journals remain visible and are never changed.")]
    public static string StagingStatus(
        [Description("Optional exact 32-character transaction UUID. Omit for the 20 most recent transactions.")] string? transaction_id = null)
        => Guard.Tool(ToolNames.StagingStatus, () => JsonSerializer.Serialize(
            new { transactions = new AmethystTransactionCoordinator().Status(transaction_id).Take(20) }, Json));

    [McpServerTool(Name = ToolNames.StagingManage, Title = "Manage one staging transaction"),
     Description("Recover, resume, roll back or verify exactly one transaction. Verification checks a fresh saved Amethyst snapshot and actual deployed winners/hashes. For a shared staging library, select, Refresh and Deploy each listed profile, then run verify again until all are checked; recovery copies remain until then. This never starts or stops Amethyst or the game. Managed mutations require Amethyst's existing filegraph lock and can return manager_close_required.")]
    public static string StagingManage(
        [Description("Exact 32-character transaction UUID. Wildcard management is not supported.")] string transaction_id,
        [Description("One of: verify, recover, resume, rollback.")] string action,
        [Description("Explicitly confirms that rollback may require a later Amethyst redeployment.")] bool confirm_amethyst_redeploy = false,
        [Description("Explicitly confirms immediate changes visible through a deployed symlink.")] bool confirm_live_deployment_change = false)
        => Guard.Tool(ToolNames.StagingManage, () =>
        {
            if (!Enum.TryParse<AmethystTransactionAction>(action, ignoreCase: true, out var parsed) ||
                !Enum.IsDefined(parsed))
                return JsonSerializer.Serialize(new { error = new { code = "invalid_action", message = "action must be verify, recover, resume, or rollback" } }, Json);
            AmethystTransactionOutcome outcome;
            try
            {
                outcome = new AmethystTransactionCoordinator().Manage(transaction_id, parsed,
                    confirm_amethyst_redeploy, confirm_live_deployment_change);
            }
            catch (AmethystManagerCloseRequiredException ex)
            { return JsonSerializer.Serialize(new { error = new { code = ex.Code, message = ex.Message } }, Json); }
            catch (AmethystLockContractException ex)
            { return JsonSerializer.Serialize(new { error = new { code = ex.Code, message = ex.Message } }, Json); }
            catch (TimeoutException ex)
            { return JsonSerializer.Serialize(new { error = new { code = "shared_library_busy", message = ex.Message } }, Json); }
            return JsonSerializer.Serialize(new
            {
                result = outcome,
                error = outcome.Success ? null : new { outcome.Code, outcome.Diagnostics }
            }, Json);
        });
}
