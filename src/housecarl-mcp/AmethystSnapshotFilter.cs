using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>Pins one saved graph around each dependent MCP read, qualifies results, and guards the unfinished write lane.</summary>
internal static class AmethystSnapshotFilter
{
    /// <summary>Tools whose results depend on the selected load order or VFS.</summary>
    static readonly HashSet<string> Reads = [ToolNames.Records, ToolNames.LoadOrderStatus, ToolNames.AssetStatus,
        ToolNames.Check, ToolNames.NifInspect, ToolNames.Skse, ToolNames.SkypatcherLayer, ToolNames.DecompileScript];
    /// <summary>Managed mutations and previews; refused before any tool body or consent side effect runs.</summary>
    static readonly HashSet<string> Writes = [ToolNames.Apply, ToolNames.Create, ToolNames.CreatePlugin, ToolNames.Forward,
        ToolNames.Remove, ToolNames.Copy, ToolNames.CompactPlugin, ToolNames.MergePlugins, ToolNames.NifSet,
        ToolNames.Place, ToolNames.WriteSeq, ToolNames.CompileScript];
    /// <summary>Record-writing tools with a verified new-plugin path shared by all record types.</summary>
    static readonly HashSet<string> TransactionRoutedRecordWrites = [ToolNames.Apply, ToolNames.Create, ToolNames.CreatePlugin, ToolNames.Forward, ToolNames.Remove, ToolNames.Copy, ToolNames.CompactPlugin, ToolNames.MergePlugins];

    /// <summary>Create the filter over the server's single service. Calls and connection changes run serially.</summary>
    internal static McpRequestFilter<CallToolRequestParams, CallToolResult> For(LoadOrderService service) => next => async (request, cancellationToken) =>
    {
        var name = request.Params?.Name;
        if (!OperatingSystem.IsLinux() || name is null) return await next(request, cancellationToken);
        var write = Writes.Contains(name);
        var arguments = request.Params?.Arguments;
        bool Names(string key) => arguments is not null && arguments.TryGetValue(key, out var value) &&
            (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()));
        bool existingDestination = Names("into") || Names("in_place");
        if (write && existingDestination && (!Confirmed("confirm_amethyst_redeploy") || !Confirmed("confirm_live_deployment_change")))
            return Error("Updating an existing staged plugin requires both confirm_amethyst_redeploy=true and confirm_live_deployment_change=true on this call; an enabled target may be visible to the game immediately.");
        if (write && (!service.UsesSavedSnapshot || !TransactionRoutedRecordWrites.Contains(name) ||
            arguments is not null && new[] { "into", "in_place" }.Any(key => Names(key) && !existingDestination)))
            return Error(LoadOrderService.SnapshotWriteRefusal);

        bool Confirmed(string key) => arguments is not null && arguments.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.True;
        if (name is ToolNames.UpdateStatus or ToolNames.NexusCheckUpdates)
            return Error("Amethyst update-cache integration is unavailable; the upstream update tools require MO2's metadata. Nexus tools with explicit mod IDs remain available.");
        if (!Reads.Contains(name) && !write && name != ToolNames.SetAmethystConnection) return await next(request, cancellationToken);
        await service.SnapshotCalls.WaitAsync(cancellationToken);
        AmethystSavedSnapshot? snapshot = null;
        AmethystStagingWrite? staging = null;
        try
        {
            if (name == ToolNames.SetAmethystConnection || !service.UsesSavedSnapshot)
            {
                var unscoped = await next(request, cancellationToken);
                MarkErrors(unscoped);
                return unscoped;
            }
            snapshot = service.CaptureSavedSnapshot();
            using var scope = AmethystSnapshotContext.Use(snapshot);
            if (write)
            {
                var reshape = name is ToolNames.CompactPlugin or ToolNames.MergePlugins;
                staging = new AmethystStagingWrite(snapshot, name!, JsonSerializer.Serialize(arguments), existingDestination,
                    reshape ? [new HousecarlCore.AmethystTransactionUnit("reshape-output")] : null,
                    reshape ? "reshape-output" : "record-output", reshape ? "reshape-output" : "owner-metadata");
            }
            var result = await next(request, cancellationToken);
            MarkErrors(result);
            if (result.IsError == true && staging is not null) staging.Abort();
            if (staging is null) snapshot.CheckUnchanged(); else staging.ValidateSnapshotInputs();
            Decorate(result, snapshot.Provenance);
            if (result.IsError != true) staging?.Complete(result);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { if (staging is not null) staging.Abort(); else snapshot?.RemoveFailedArtifacts(); throw; }
        catch (Exception ex)
        {
            var message = $"Amethyst saved-snapshot {(write ? "write" : "read")} refused: {ex.Message}";
            try { if (staging is not null) staging.Abort(); else snapshot?.RemoveFailedArtifacts(); }
            catch (Exception cleanup) { message += $"; could not remove a result artifact from the failed call: {cleanup.Message}"; }
            return Error(message);
        }
        finally { staging?.Dispose(); service.SnapshotCalls.Release(); }
    };

    /// <summary>Attach provenance to each text/JSON result and MCP structured content without changing the result rows.</summary>
    internal static void Decorate(CallToolResult result, AmethystSnapshotProvenance provenance)
    {
        MarkErrors(result);
        foreach (var block in result.Content.OfType<TextContentBlock>())
        {
            block.Text = AddMetadata(block.Text, provenance);
        }
        using var metadataDocument = JsonDocument.Parse(AddMetadata("{}", provenance));
        var metadata = metadataDocument.RootElement.Clone();
        if (result.StructuredContent is { ValueKind: JsonValueKind.Object } existing)
        {
            using var merged = JsonDocument.Parse(AddMetadata(existing.GetRawText(), provenance));
            result.StructuredContent = merged.RootElement.Clone();
        }
        else result.StructuredContent = metadata;
    }

    /// <summary>Translate named text/JSON refusals into MCP's error flag without mistaking per-row failures for a whole-call refusal.</summary>
    static void MarkErrors(CallToolResult result)
    {
        foreach (var block in result.Content.OfType<TextContentBlock>())
        {
            if (block.Text.TrimStart().StartsWith("error:", StringComparison.OrdinalIgnoreCase)) result.IsError = true;
            try
            {
                using var json = JsonDocument.Parse(block.Text);
                if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                    result.IsError = true;
            }
            catch (JsonException) { /* Plain text is handled by its named error prefix above. */ }
        }
    }

    /// <summary>Keep JSON results machine-readable; text results carry a visible warning and generation header.</summary>
    static string AddMetadata(string text, AmethystSnapshotProvenance provenance)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    foreach (var property in document.RootElement.EnumerateObject())
                        if (property.Name != "amethyst_snapshot") property.WriteTo(writer);
                    provenance.WriteTo(writer);
                    writer.WriteEndObject();
                }
                return System.Text.Encoding.UTF8.GetString(stream.ToArray());
            }
        }
        catch (JsonException) { /* A normal text result has no JSON object to extend. */ }
        return $"WARNING: {AmethystSnapshotProvenance.Warning}\nAmethyst profile: {provenance.Profile}; saved generation: {provenance.Generation}; freshness: unverified\n" +
            (provenance.VanillaArchivesOmitted ? "Asset coverage warning: " + AmethystSnapshotProvenance.VanillaArchiveWarning + "\n" : "") + "\n" + text;
    }

    /// <summary>Return a named MCP tool error, rather than a silently empty or successful result.</summary>
    static CallToolResult Error(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = "error: " + message }] };
}
