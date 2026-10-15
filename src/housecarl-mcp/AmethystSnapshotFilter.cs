using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>Pins one saved graph around each dependent MCP read, qualifies results, and guards the unfinished write lane.</summary>
internal static class AmethystSnapshotFilter
{
    /// <summary>Tools whose results depend on the selected load order or VFS.</summary>
    static readonly HashSet<string> Reads = [ToolNames.Records, ToolNames.LoadOrderStatus, ToolNames.AssetStatus,
        ToolNames.Check, ToolNames.NifInspect, ToolNames.Skse, ToolNames.SkypatcherLayer, ToolNames.DecompileScript, ToolNames.UpdateStatus];
    /// <summary>Managed mutations and previews; refused before any tool body or consent side effect runs.</summary>
    static readonly HashSet<string> Writes = [ToolNames.Apply, ToolNames.Create, ToolNames.CreatePlugin, ToolNames.Forward,
        ToolNames.Remove, ToolNames.Copy, ToolNames.CompactPlugin, ToolNames.MergePlugins, ToolNames.NifSet,
        ToolNames.Place, ToolNames.WriteSeq, ToolNames.CompileScript, ToolNames.DecompileScript, ToolNames.BsaExtract, ToolNames.BsaRepack];
    /// <summary>Record-writing tools with a verified new-plugin path shared by all record types.</summary>
    static readonly HashSet<string> TransactionRoutedRecordWrites = [ToolNames.Apply, ToolNames.Create, ToolNames.CreatePlugin, ToolNames.Forward, ToolNames.Remove, ToolNames.Copy,
        ToolNames.CompactPlugin, ToolNames.MergePlugins, ToolNames.NifSet, ToolNames.Place, ToolNames.WriteSeq, ToolNames.CompileScript,
        ToolNames.DecompileScript, ToolNames.BsaExtract, ToolNames.BsaRepack];

    /// <summary>Create the filter over the server's single service. Calls and connection changes run serially.</summary>
    internal static McpRequestFilter<CallToolRequestParams, CallToolResult> For(LoadOrderService service) => next => async (request, cancellationToken) =>
    {
        var name = request.Params?.Name;
        if (!OperatingSystem.IsLinux() || name is null) return await next(request, cancellationToken);
        var write = Writes.Contains(name);
        var arguments = request.Params?.Arguments;
        bool Names(string key) => arguments is not null && arguments.TryGetValue(key, out var value) &&
            (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()));
        // repoint_externals can mutate other existing plugins even when no target= / in_place= argument is present.
        // Until per-entry deployment visibility is available, conservatively treat every compact repoint as immediate.
        bool requestedExplicitOutput = (name is ToolNames.BsaExtract or ToolNames.WriteSeq or ToolNames.CompileScript or ToolNames.DecompileScript) && Names("out_path");
        // With no explicit WriteSeq destination the producer may auto-select the existing owned folder of its source
        // plugin. Until that resolved path is available to this preflight, require consent for the route conservatively.
        bool defaultSeqMayReusePluginFolder = name == ToolNames.WriteSeq && !Names("out_path") && !Names("into") && !Names("patch");
        bool existingDestination = Names("into") || Names("in_place") || name == ToolNames.CompactPlugin && Names("repoint_externals") || defaultSeqMayReusePluginFolder;
        if (write && existingDestination && (!Confirmed("confirm_amethyst_redeploy") || !Confirmed("confirm_live_deployment_change")))
            return Error(defaultSeqMayReusePluginFolder
                ? "The default WriteSeq destination may reuse the source plugin's existing owned folder; pass both confirm_amethyst_redeploy=true and confirm_live_deployment_change=true because the .seq may be immediately visible to the game."
                : "Rewriting an existing plugin or compacting with external repointing requires both confirm_amethyst_redeploy=true and confirm_live_deployment_change=true on this call; a rewritten plugin may be visible to the game immediately.");
        if (write && (!service.UsesSavedSnapshot || !TransactionRoutedRecordWrites.Contains(name) ||
            arguments is not null && new[] { "into", "in_place" }.Any(key => Names(key) && !existingDestination)))
            return Error(LoadOrderService.SnapshotWriteRefusal);

        bool Confirmed(string key) => arguments is not null && arguments.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.True;
        // Nexus checks are explicit-ID requests, independent of profile state. UpdateStatus, by contrast,
        // is pinned below to the selected snapshot so Amethyst's own cached meta.ini is read consistently.
        if (name == ToolNames.NexusCheckUpdates) return await next(request, cancellationToken);
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
                var managedAssetWrite = name is ToolNames.NifSet or ToolNames.Place or ToolNames.WriteSeq or ToolNames.CompileScript or
                    ToolNames.DecompileScript or ToolNames.BsaExtract or ToolNames.BsaRepack;
                var explicitSeqPath = name == ToolNames.WriteSeq && Names("out_path")
                    ? Path.GetFullPath(arguments!["out_path"].GetString()!.Trim().Trim('"')) : null;
                var managedExplicitSeq = explicitSeqPath is not null && IsWithin(explicitSeqPath, snapshot.ModsDir);
                if (managedExplicitSeq && (!Confirmed("confirm_amethyst_redeploy") || !Confirmed("confirm_live_deployment_change")))
                    return Error("Writing a SEQ into Amethyst's ModsDir requires both confirm_amethyst_redeploy=true and confirm_live_deployment_change=true on this call; the selected mod folder may be immediately visible to the game.");
                var explicitOutput = requestedExplicitOutput && (name is ToolNames.CompileScript or ToolNames.DecompileScript);
                if (explicitOutput)
                {
                    var outputPath = Path.GetFullPath(arguments!["out_path"].GetString()!.Trim().Trim('"'));
                    if (IsWithin(outputPath, snapshot.ModsDir))
                        return Error("Explicit Papyrus out_path= cannot target Amethyst's protected ModsDir; use patch= or into= for managed staging output.");
                }
                bool externalOutput = requestedExplicitOutput && !managedExplicitSeq;
                string? externalRoot = null;
                var immediate = existingDestination || managedExplicitSeq;
                if (externalOutput)
                {
                    var rawOutput = arguments!["out_path"].GetString()!;
                    var externalPath = Path.GetFullPath(rawOutput.Trim().Trim('"'));
                    externalRoot = NearestExistingExternalRoot(externalPath, snapshot);
                }
                int? placeCount = name == ToolNames.Place && arguments is not null && arguments.TryGetValue("assets", out var assetArg)
                    ? Math.Max(1, CapturePlaceInputCount(assetArg, snapshot) ?? 1) : null;
                var placeUnits = placeCount is { } count
                    ? Enumerable.Range(0, count).Select(i => new HousecarlCore.AmethystTransactionUnit($"place-{i}")).ToArray()
                    : null;
                var units = externalOutput
                    ? [new HousecarlCore.AmethystTransactionUnit("external-output")]
                    : reshape
                    ? [new HousecarlCore.AmethystTransactionUnit("reshape-output")]
                    : placeUnits is not null
                        ? placeUnits
                    : managedAssetWrite
                        ? [new HousecarlCore.AmethystTransactionUnit("managed-output")]
                        : null;
                if (name == ToolNames.CompileScript && arguments is not null && arguments.TryGetValue("script", out var sourceArg))
                    CapturePapyrusInput(snapshot, sourceArg);
                if (name == ToolNames.DecompileScript && arguments is not null && arguments.TryGetValue("pex", out var pexArg))
                    CapturePapyrusInput(snapshot, pexArg);
                staging = new AmethystStagingWrite(snapshot, name!, JsonSerializer.Serialize(arguments), immediate,
                    units,
                    externalOutput ? "external-output" : reshape ? "reshape-output" : placeUnits is { Length: > 0 } ? placeUnits[0].Id : managedAssetWrite ? "managed-output" : "record-output",
                    externalOutput ? "external-output" : managedAssetWrite ? "managed-output" : "owner-metadata",
                    externalRoot);
            }
            var result = await next(request, cancellationToken);
            MarkErrors(result);
            if (result.IsError == true && staging is not null) staging.FinalizeFailure(result);
            else if (staging is null) snapshot.CheckUnchanged();
            else staging.ValidateSnapshotInputs();
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

    static string NearestExistingExternalRoot(string path, HousecarlCore.AmethystSavedSnapshot snapshot)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException("out_path must be absolute");
        var full = Path.GetFullPath(path);
        snapshot.ValidateExternalDestination(full);
        var candidate = full;
        while (!Directory.Exists(candidate))
            candidate = Path.GetDirectoryName(candidate) ?? throw new InvalidOperationException("out_path has no existing parent directory");
        var canonical = HousecarlCore.LinuxFileIdentity.Canonical(candidate);
        if (!string.Equals(canonical, candidate, StringComparison.Ordinal))
            throw new InvalidOperationException("out_path passes through a symlinked directory; external export refused");
        snapshot.ValidateExternalDestination(canonical);
        return canonical;
    }

    static bool IsWithin(string path, string root)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(parent, StringComparison.Ordinal) || Path.GetFullPath(path) == Path.GetFullPath(root);
    }

    static int? CapturePlaceInputCount(JsonElement argument, HousecarlCore.AmethystSavedSnapshot snapshot)
    {
        string? path = argument.ValueKind == JsonValueKind.String ? argument.GetString() : null;
        if (argument.ValueKind == JsonValueKind.Array && argument.GetArrayLength() == 1 && argument[0].ValueKind == JsonValueKind.String)
            path = argument[0].GetString();
        if (path?.Trim().Trim('"', '\'') is { Length: > 1 } token && token[0] == '@')
        {
            var full = token[1..].Trim();
            var bytes = snapshot.CaptureAdditionalInput(full); // capture failures are a hard refusal, never a one-unit fallback
            using var captured = JsonDocument.Parse(bytes, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return captured.RootElement.ValueKind == JsonValueKind.Array ? captured.RootElement.GetArrayLength() : null;
        }
        return argument.ValueKind == JsonValueKind.Array ? argument.GetArrayLength() : null;
    }

    /// <summary>Capture an existing Papyrus input using the same quote trimming accepted by its tool body.</summary>
    internal static string? CapturePapyrusInput(HousecarlCore.AmethystSavedSnapshot snapshot, JsonElement argument)
    {
        if (argument.ValueKind != JsonValueKind.String) return null;
        var value = argument.GetString()?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(value) || !File.Exists(value)) return null;
        var fullPath = Path.GetFullPath(value);
        _ = snapshot.CaptureAdditionalInput(fullPath);
        return fullPath;
    }

    /// <summary>Capture every source visible on the selected compiler path and the exact compiler/flags inputs.</summary>
    internal static int CapturePapyrusImportInputs(HousecarlCore.AmethystSavedSnapshot snapshot,
        IEnumerable<string> importDirectories, string compilerPath, string flagsPath)
    {
        const int MaximumSources = 20_000;
        var compiler = Path.GetFullPath(compilerPath);
        _ = snapshot.CaptureAdditionalInput(compiler);
        _ = snapshot.CaptureAdditionalInput(Path.GetFullPath(flagsPath));
        var captured = 0;
        foreach (var directory in importDirectories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var fullDirectory = Path.GetFullPath(directory);
            if (!Directory.Exists(fullDirectory))
                throw new InvalidOperationException($"Papyrus import directory disappeared before capture: '{fullDirectory}'");
            foreach (var file in snapshot.CapturePapyrusSourceDirectory(fullDirectory))
            {
                if (++captured > MaximumSources)
                    throw new InvalidOperationException($"Papyrus input capture reached its {MaximumSources}-source ceiling; refusing compilation because dependency provenance is incomplete.");
                _ = snapshot.CaptureAdditionalInput(Path.GetFullPath(file));
            }
        }
        return captured;
    }

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
