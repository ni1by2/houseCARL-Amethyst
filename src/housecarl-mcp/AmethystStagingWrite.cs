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
    readonly bool _managed;
    readonly string _outputUnitId;
    /// <summary>New plugin paths registered before any output bytes are written.</summary>
    readonly List<string> _plugins = [];
    readonly List<(string Path, string Role)> _publishedOutputs = [];
    readonly HashSet<string> _folders = new(StringComparer.Ordinal);
    readonly HashSet<string> _registeredArtifacts = new(StringComparer.Ordinal);
    /// <summary>Whether the caller is inside the guarded new-patch lane.</summary>
    internal static bool IsActive => Active.Value is not null;
    internal static bool IsExternalActive => Active.Value is { _managed: false };
    internal static bool IsManagedActive => Active.Value is { _managed: true };

    internal static string CreateScratchDirectory(string purpose)
    {
        if (Active.Value is null) throw new InvalidOperationException("Amethyst staging transaction scope is unavailable");
        var path = Path.Combine(Path.GetTempPath(), $"housecarl-amethyst-{purpose}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    internal static void RegisterOutputPath(string path)
    {
        var call = Active.Value ?? throw new InvalidOperationException("Amethyst staging transaction scope is unavailable");
        call._snapshot.RegisterArtifact(Path.GetFullPath(path), null);
    }

    internal static void PublishManagedArtifact(string stagedPath, string destination, string role = "asset")
        => PublishManagedArtifactGroup("managed-output", [(stagedPath, destination, role)]);

    internal static void PublishActiveArtifactGroup(IReadOnlyList<(string StagedPath, string Destination, string Role)> artifacts)
    {
        var call = Active.Value ?? throw new InvalidOperationException("Amethyst or external output transaction scope is unavailable");
        call.CommitArtifactGroup(call._outputUnitId, artifacts);
    }

    internal static void PublishActiveArtifact(string stagedPath, string destination, string role)
        => PublishActiveArtifactGroup([(stagedPath, destination, role)]);

    internal static void FailManagedUnit(string unitId, string diagnostic)
    {
        var call = Active.Value ?? throw new InvalidOperationException("Amethyst staging transaction scope is unavailable");
        call._transaction.FailUnit(unitId, diagnostic);
    }

    /// <summary>Bind output permission to one captured manager generation.</summary>
    internal AmethystStagingWrite(AmethystSavedSnapshot snapshot, string operation, string requestJson, bool immediateVisibility = false,
        IReadOnlyList<AmethystTransactionUnit>? units = null, string outputUnitId = "record-output", string metadataUnitId = "owner-metadata",
        string? externalRoot = null)
    {
        _snapshot = snapshot; _previous = Active.Value;
        _managed = externalRoot is null;
        _outputUnitId = outputUnitId;
        _coordinator = new AmethystTransactionCoordinator();
        var requestHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(requestJson))).ToLowerInvariant();
        var transactionRoot = externalRoot ?? snapshot.ModsDir;
        var request = new AmethystTransactionRequest(operation, "skyrim_se", snapshot.ProfileDir,
            snapshot.ManifestPath, transactionRoot, snapshot.ManagerLockPath,
            snapshot.Provenance.Generation, snapshot.Provenance.InventoryGeneration,
            "amethyst-schema9-engine1-rules13", requestHash,
            units ?? [new AmethystTransactionUnit("record-output"), new AmethystTransactionUnit("owner-metadata", ["record-output"])],
            InputSha256: snapshot.InputSha256,
            ConsentScope: immediateVisibility ? ["confirm_amethyst_redeploy", "confirm_live_deployment_change"] : [],
            Managed: _managed, ImmediateVisibility: immediateVisibility);
        _locks = _managed
            ? _coordinator.AcquireWriteLocks(transactionRoot, snapshot.ManagerLockPath)
            : _coordinator.AcquireExternalWriteLock(transactionRoot);
        try
        {
            var journal = _coordinator.Begin(request);
            _transaction = new AmethystTransactionWriteScope(_coordinator, journal, _locks, transactionRoot, ValidateSnapshotInputs, outputUnitId, metadataUnitId);
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
        var beforeRecovery = _transaction.Journal;
        if (beforeRecovery.Artifacts.Any(a => a.State == "committing") ||
            beforeRecovery.Units.Any(u => u.State is HousecarlCore.AmethystUnitState.Committing or HousecarlCore.AmethystUnitState.RecoveryRequired))
            _transaction.Abort(); // Recover only interrupted units; completed independent units remain committed.
        if (_transaction.Journal.State != HousecarlCore.AmethystTransactionState.RecoveryRequired)
            _transaction.PublishOwnerMetadata();
        var outputs = new JsonArray();
        var verified = new Dictionary<string, LinuxFileIdentity>();
        var committedArtifacts = _transaction.Journal.Artifacts.Where(a => a.State == "committed").ToArray();
        var outputPaths = _publishedOutputs.Select(p => p.Path).Concat(_transaction.PublishedPlugins.Select(p => p.Path))
            .Concat(committedArtifacts.Where(a => !a.Role.Equals("owner_metadata", StringComparison.OrdinalIgnoreCase)).Select(a => a.Destination))
            .Distinct(StringComparer.Ordinal).Where(File.Exists).ToArray();
        foreach (var path in outputPaths)
        {
            var before = LinuxFileIdentity.Of(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
            if (LinuxFileIdentity.Of(path) != before)
                throw new InvalidOperationException("Amethyst staged patch changed while verifying its output; retry");
            var meta = Path.Combine(Path.GetDirectoryName(path)!, "meta.ini");
            verified[path] = before;
            if (File.Exists(meta)) verified[meta] = LinuxFileIdentity.Of(meta);
            var role = _publishedOutputs.FirstOrDefault(p => p.Path == path).Role
                ?? _transaction.Journal.Artifacts.FirstOrDefault(a => a.Destination == path)?.Role ?? "plugin";
            outputs.Add(new JsonObject { ["path"] = path, ["sha256"] = hash, ["role"] = role });
        }
        ValidateSnapshotInputs();
        foreach (var (path, stamp) in verified)
            if (LinuxFileIdentity.Of(path) != stamp)
                throw new InvalidOperationException("Amethyst staged output changed before the response; retry");
        var beforeSeal = _transaction.Journal;
        if (outputs.Count == 0 && beforeSeal.State != HousecarlCore.AmethystTransactionState.Partial)
        {
            _transaction.Abort();
            _transaction.Seal();
            RemoveEmptyFolders();
            return;
        }
        var journal = _transaction.Journal.State == HousecarlCore.AmethystTransactionState.RecoveryRequired
            ? _transaction.ReleaseForRecovery()
            : _transaction.Seal();
        var state = journal.State switch
        {
            AmethystTransactionState.PendingDeployment => "pending_deployment",
            AmethystTransactionState.RecoveryRequired => "recovery_required",
            AmethystTransactionState.RolledBack => "rolled_back",
            AmethystTransactionState.Partial => "partial",
            AmethystTransactionState.Completed => "completed",
            AmethystTransactionState.Aborted => "aborted",
            _ => journal.State.ToString().ToLowerInvariant()
        };
        var nextStep = !_managed
            ? "External export completed at the named destination. This result does not mean the files were deployed, enabled, or verified in-game."
            : journal.State == AmethystTransactionState.Partial
                ? "Some placement units were refused while other units were retained. Review the per-item results and transaction journal before continuing."
                : journal.State == AmethystTransactionState.RecoveryRequired
                    ? "The transaction needs recovery. Do not restart Amethyst or rely on these outputs until housecarl_amethyst_manage resolves the journal."
                    : journal.ImmediateVisibility
                        ? "Refresh Amethyst and verify the updated staged file is the deployed winner. This write was explicitly authorized as potentially immediately game-visible; success does not confirm what is loaded in-game."
                        : "Refresh Amethyst, place and enable the patch, rebuild and deploy. Staging success does not confirm what is loaded in-game.";
        var metadata = new JsonObject
        {
            ["transaction_id"] = journal.Id, ["state"] = state,
            ["pending_deployment"] = journal.PendingDeployment, ["immediate_visibility"] = journal.ImmediateVisibility,
            ["safe_to_restart_amethyst"] = journal.SafeToRestartAmethyst, ["profile"] = _snapshot.ProfileDir,
            ["outputs"] = outputs, ["deployment_verified"] = false,
            ["next_step"] = nextStep
        };
        foreach (var block in result.Content.OfType<TextContentBlock>())
        {
            JsonObject? json = null;
            try { json = JsonNode.Parse(block.Text) as JsonObject; }
            catch (JsonException) { /* Text replies retain their normal format. */ }
            if (json is not null)
            { json[_managed ? "amethyst_staging" : "external_export"] = metadata.DeepClone(); block.Text = json.ToJsonString(); }
            else block.Text += _managed
                ? "\n\nOutput recorded in Amethyst staging; pending deployment. " + metadata["next_step"]!.GetValue<string>()
                : "\n\nExternal export transaction completed. " + metadata["next_step"]!.GetValue<string>();
        }
        var structured = result.StructuredContent is { ValueKind: JsonValueKind.Object } value
            ? JsonNode.Parse(value.GetRawText())!.AsObject() : new JsonObject();
        structured[_managed ? "amethyst_staging" : "external_export"] = metadata.DeepClone();
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
        call.CommitArtifactGroup("reshape-output", artifacts);
    }

    internal static void PublishManagedArtifactGroup(string unitId, IReadOnlyList<(string StagedPath, string Destination, string Role)> artifacts)
    {
        var call = Active.Value ?? throw new InvalidOperationException("Amethyst staging transaction scope is unavailable");
        call.CommitArtifactGroup(unitId, artifacts);
    }

    void CommitArtifactGroup(string unitId, IReadOnlyList<(string StagedPath, string Destination, string Role)> artifacts)
    {
        _transaction.CommitArtifactGroup(unitId, artifacts);
        foreach (var (_, destination, role) in artifacts)
        {
            var full = Path.GetFullPath(destination);
            if (role == "plugin") _plugins.Add(full);
            _publishedOutputs.Add((full, role));
        }
    }

    internal void Abort()
        => AbortTransaction();

    internal void FinalizeFailure(CallToolResult result)
    {
        var outcome = AbortTransaction();
        var journal = _transaction.Journal;
        AttachFailureEvidence(result, journal, outcome, _snapshot.ProfileDir, _managed);
    }

    internal static void AttachFailureEvidence(CallToolResult result, AmethystTransactionJournal journal,
        AmethystTransactionOutcome outcome, string profile, bool managed)
    {
        var outputs = new JsonArray();
        foreach (var artifact in journal.Artifacts.Where(a => a.State == "committed" &&
                     !a.Role.Equals("owner_metadata", StringComparison.OrdinalIgnoreCase)))
            outputs.Add(new JsonObject { ["path"] = artifact.Destination, ["sha256"] = artifact.CommittedSha256, ["role"] = artifact.Role });
        var diagnostics = journal.Diagnostics.Concat(outcome.Diagnostics).Distinct(StringComparer.Ordinal).ToArray();
        var state = journal.State switch
        {
            AmethystTransactionState.RolledBack => "rolled_back",
            AmethystTransactionState.RecoveryRequired => "recovery_required",
            AmethystTransactionState.PendingDeployment => "pending_deployment",
            AmethystTransactionState.Partial => "partial",
            AmethystTransactionState.Aborted => "aborted",
            _ => journal.State.ToString().ToLowerInvariant()
        };
        var recovery = journal.State == AmethystTransactionState.RecoveryRequired;
        var evidence = new JsonObject
        {
            ["transaction_id"] = journal.Id, ["state"] = state,
            ["pending_deployment"] = journal.PendingDeployment, ["immediate_visibility"] = journal.ImmediateVisibility,
            ["safe_to_restart_amethyst"] = journal.SafeToRestartAmethyst,
            ["profile"] = profile, ["outputs"] = outputs, ["deployment_verified"] = false,
            ["diagnostics"] = JsonSerializer.SerializeToNode(diagnostics),
            ["next_step"] = recovery
                ? "Recovery is required. Do not restart Amethyst or rely on these outputs until housecarl_amethyst_manage resolves the journal."
                : journal.State == AmethystTransactionState.RolledBack
                    ? "The failed write was rolled back. No game-visible success is claimed."
                    : "Review the transaction journal before continuing."
        };
        foreach (var block in result.Content.OfType<TextContentBlock>())
            block.Text += $"\n\nAmethyst transaction {journal.Id}: {state}. " + evidence["next_step"]!.GetValue<string>();
        var structured = result.StructuredContent is { ValueKind: JsonValueKind.Object } value
            ? JsonNode.Parse(value.GetRawText())!.AsObject() : new JsonObject();
        structured[managed ? "amethyst_staging" : "external_export"] = evidence;
        result.StructuredContent = JsonSerializer.SerializeToElement(structured);
    }

    HousecarlCore.AmethystTransactionOutcome AbortTransaction()
    {
        try
        {
            var outcome = _transaction.Abort();
            if (_transaction.Journal.State == HousecarlCore.AmethystTransactionState.RecoveryRequired)
                _transaction.ReleaseForRecovery();
            else if (outcome.Success) _transaction.Seal();
            return outcome;
        }
        finally { RemoveEmptyFolders(); }
    }

    internal void ValidateSnapshotInputs()
    {
        var outputs = _transaction.PublishedPlugins.Select(p => Path.GetFullPath(p.Path))
            .Concat(_transaction.Journal.Artifacts.Where(a => a.State == "committed").Select(a => Path.GetFullPath(a.Destination)))
            .ToHashSet(StringComparer.Ordinal);
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
