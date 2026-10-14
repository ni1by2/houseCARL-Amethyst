using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using HousecarlCore;

namespace HousecarlMcp;

/// <summary>One new-patch transaction in staging; profile and deployment remain manager-owned.</summary>
internal sealed class AmethystStagingWrite : IDisposable
{
    /// <summary>The current MCP call's output permission; direct read callers cannot allocate patches.</summary>
    static readonly AsyncLocal<AmethystStagingWrite?> Active = new();
    /// <summary>The enclosing call, restored when this scope ends.</summary>
    readonly AmethystStagingWrite? _previous;
    /// <summary>Saved inputs whose stability is checked before reporting a staged patch.</summary>
    readonly AmethystSavedSnapshot _snapshot;
    readonly AmethystTransactionCoordinator _coordinator;
    readonly AmethystTransactionLockSet _locks;
    readonly AmethystTransactionWriteScope _transaction;
    /// <summary>New plugin paths registered before any output bytes are written.</summary>
    readonly List<string> _plugins = [];
    readonly HashSet<string> _folders = new(StringComparer.Ordinal);
    readonly HashSet<string> _registeredArtifacts = new(StringComparer.Ordinal);
    /// <summary>Whether the caller is inside the guarded new-patch lane.</summary>
    internal static bool IsActive => Active.Value is not null;

    /// <summary>Bind output permission to one captured manager generation.</summary>
    internal AmethystStagingWrite(AmethystSavedSnapshot snapshot, string operation, string requestJson, bool immediateVisibility = false,
        IReadOnlyList<AmethystTransactionUnit>? units = null, string outputUnitId = "record-output", string metadataUnitId = "owner-metadata")
    {
        _snapshot = snapshot; _previous = Active.Value;
        _coordinator = new AmethystTransactionCoordinator();
        var requestHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(requestJson))).ToLowerInvariant();
        var request = new AmethystTransactionRequest(operation, "skyrim_se", snapshot.ProfileDir,
            snapshot.ManifestPath, snapshot.ModsDir, snapshot.ManagerLockPath,
            snapshot.Provenance.Generation, snapshot.Provenance.InventoryGeneration,
            "amethyst-schema9-engine1-rules13", requestHash,
            units ?? [new AmethystTransactionUnit("record-output"), new AmethystTransactionUnit("owner-metadata", ["record-output"])],
            InputSha256: snapshot.InputSha256,
            ConsentScope: immediateVisibility ? ["confirm_amethyst_redeploy", "confirm_live_deployment_change"] : [],
            Managed: true, ImmediateVisibility: immediateVisibility);
        _locks = _coordinator.AcquireWriteLocks(snapshot.ModsDir, snapshot.ManagerLockPath);
        try
        {
            var journal = _coordinator.Begin(request);
            _transaction = new AmethystTransactionWriteScope(_coordinator, journal, _locks, snapshot.ModsDir, ValidateSnapshotInputs, outputUnitId, metadataUnitId);
            Active.Value = this;
        }
        catch { _locks.Dispose(); throw; }
    }

    internal static void DeferOwnerMetadata(string destination, string contents)
    {
        if (Active.Value is not { } call) { File.WriteAllText(destination, contents); return; }
        call._transaction.DeferOwnerMetadata(destination, contents);
    }

    internal static void DeferExistingOwnerMetadata(string destination, string contents, string? expectedOriginalSha256, bool trackPluginHash = true)
    {
        if (Active.Value is not { } call) { File.WriteAllText(destination, contents); return; }
        call._transaction.DeferOwnerMetadata(destination, contents, expectedOriginalSha256, trackPluginHash);
    }

    /// <summary>Enroll an existing owned patch and its unchanged owner metadata in one update transaction.</summary>
    internal static void RegisterExistingOwnedPatch(string folder, string plugin)
    {
        if (Active.Value is not { } call) return;
        var fullFolder = Path.GetFullPath(folder);
        if (!string.Equals(Path.GetDirectoryName(fullFolder), call._snapshot.ModsDir, StringComparison.Ordinal) ||
            Path.GetFileName(plugin) != plugin || !plugin.EndsWith(".esp", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Amethyst patch updates must target a direct staging mod containing its plugin");
        var pluginPath = Path.Combine(fullFolder, plugin);
        var metaPath = Path.Combine(fullFolder, "meta.ini");
        if (!File.Exists(pluginPath) || !File.Exists(metaPath))
            throw new InvalidOperationException("the owned Amethyst patch or its owner metadata disappeared before update; retry");
        var bytes = File.ReadAllBytes(metaPath);
        var contents = System.Text.Encoding.UTF8.GetString(bytes);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        if (call._plugins.Contains(pluginPath, StringComparer.Ordinal))
            throw new InvalidOperationException("the same Amethyst patch was enrolled twice in one transaction");
        call._transaction.DeferOwnerMetadata(metaPath, contents, hash);
        call._plugins.Add(pluginPath);
    }

    /// <summary>Register only a new direct staging folder; rollback cannot delete existing mods.</summary>
    internal static string Register(string folder, string plugin)
    {
        if (Active.Value is not { } call) return "";
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(folder)), call._snapshot.ModsDir, StringComparison.Ordinal) ||
            Path.GetFileName(plugin) != plugin)
            throw new InvalidOperationException("Amethyst outputs must be direct staging mods with a safe owner name");
        var hasPlugin = plugin.EndsWith(".esp", StringComparison.OrdinalIgnoreCase);
        if (!hasPlugin && plugin != "(houseCARL output)")
            throw new InvalidOperationException("Amethyst plugin outputs must use an .esp filename");
        var path = Path.Combine(folder, plugin);
        var meta = Path.Combine(folder, "meta.ini");
        if (hasPlugin && File.Exists(path) || File.Exists(meta))
            throw new InvalidOperationException("Amethyst patch output appeared during allocation; retry with a fresh patch name");
        if (hasPlugin)
        {
            if (call._registeredArtifacts.Add(Path.GetFullPath(path))) call._snapshot.RegisterArtifact(path, null);
            call._plugins.Add(path);
        }
        if (call._registeredArtifacts.Add(Path.GetFullPath(meta))) call._snapshot.RegisterArtifact(meta, null);
        call._folders.Add(Path.GetFullPath(folder));
        return $"amethystProfile={call._snapshot.ProfileDir}\r\n" +
            $"amethystGeneration={call._snapshot.Provenance.Generation}\r\n" +
            "pendingDeployment=true\r\n";
    }

    /// <summary>Persist output hashes and describe staging success without claiming game-visible success.</summary>
    internal void Complete(CallToolResult result)
    {
        ValidateSnapshotInputs();
        _transaction.PublishOwnerMetadata();
        var outputs = new JsonArray();
        var verified = new Dictionary<string, LinuxFileIdentity>();
        foreach (var path in _transaction.PublishedPlugins.Select(p => p.Path).Where(File.Exists))
        {
            var before = LinuxFileIdentity.Of(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
            if (LinuxFileIdentity.Of(path) != before)
                throw new InvalidOperationException("Amethyst staged patch changed while verifying its output; retry");
            var meta = Path.Combine(Path.GetDirectoryName(path)!, "meta.ini");
            verified[path] = before;
            if (File.Exists(meta)) verified[meta] = LinuxFileIdentity.Of(meta);
            outputs.Add(new JsonObject { ["path"] = path, ["sha256"] = hash });
        }
        ValidateSnapshotInputs();
        foreach (var (path, stamp) in verified)
            if (LinuxFileIdentity.Of(path) != stamp)
                throw new InvalidOperationException("Amethyst staged output changed before the response; retry");
        if (outputs.Count == 0)
        {
            _transaction.Abort();
            _transaction.Seal();
            return;
        }
        var journal = _transaction.Seal();
        var metadata = new JsonObject
        {
            ["transaction_id"] = journal.Id, ["state"] = "pending_deployment",
            ["pending_deployment"] = journal.PendingDeployment, ["immediate_visibility"] = journal.ImmediateVisibility,
            ["safe_to_restart_amethyst"] = journal.SafeToRestartAmethyst, ["profile"] = _snapshot.ProfileDir,
            ["outputs"] = outputs,
            ["next_step"] = journal.ImmediateVisibility
                ? "Refresh Amethyst and verify the updated staged file is the deployed winner. This write was explicitly authorized as potentially immediately game-visible; success does not confirm what is loaded in-game."
                : "Refresh Amethyst, place and enable the patch, rebuild and deploy. Staging success does not confirm what is loaded in-game."
        };
        foreach (var block in result.Content.OfType<TextContentBlock>())
        {
            JsonObject? json = null;
            try { json = JsonNode.Parse(block.Text) as JsonObject; }
            catch (JsonException) { /* Text replies retain their normal format. */ }
            if (json is not null)
            { json["amethyst_staging"] = metadata.DeepClone(); block.Text = json.ToJsonString(); }
            else block.Text += "\n\nPatch written to Amethyst staging; pending deployment. " + metadata["next_step"]!.GetValue<string>();
        }
        var structured = result.StructuredContent is { ValueKind: JsonValueKind.Object } value
            ? JsonNode.Parse(value.GetRawText())!.AsObject() : new JsonObject();
        structured["amethyst_staging"] = metadata.DeepClone();
        result.StructuredContent = JsonSerializer.SerializeToElement(structured);
    }

    /// <summary>Remove only empty output directories after registered artifacts have been rolled back.</summary>
    internal void RemoveEmptyFolders()
    {
        foreach (var folder in _folders.Concat(_plugins.Select(p => Path.GetDirectoryName(p)!)).Distinct(StringComparer.Ordinal))
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
    }

    internal static void PublishArtifactGroup(IReadOnlyList<(string StagedPath, string Destination, string Role)> artifacts)
    {
        var call = Active.Value ?? throw new InvalidOperationException("Amethyst reshape transaction scope is unavailable");
        call._transaction.CommitArtifactGroup("reshape-output", artifacts);
    }

    internal void Abort()
    {
        try
        {
            var outcome = _transaction.Abort();
            if (outcome.Success) _transaction.Seal();
        }
        finally { RemoveEmptyFolders(); }
    }

    internal void ValidateSnapshotInputs()
    {
        var outputs = _transaction.PublishedPlugins.Select(p => Path.GetFullPath(p.Path)).ToHashSet(StringComparer.Ordinal);
        _snapshot.CheckUnchanged(outputs);
    }

    /// <summary>Restore the enclosing call's permission; files are committed or rolled back explicitly.</summary>
    public void Dispose()
    {
        _transaction.Dispose();
        Active.Value = _previous;
        _locks.Dispose();
    }
}
