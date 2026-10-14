using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HousecarlCore;

/// <summary>Durable transaction lifecycle. Unknown persisted values are never coerced into a writable state.</summary>
public enum AmethystTransactionState
{
    Preparing, Prepared, Committing, Completed, PendingDeployment, Partial,
    RecoveryRequired, Verified, RolledBack, Aborted
}

/// <summary>One independently recoverable operation unit.</summary>
public enum AmethystUnitState { Prepared, Committing, Completed, Failed, RolledBack, RecoveryRequired }

/// <summary>Action requested through the staging management surface.</summary>
public enum AmethystTransactionAction { Verify, Recover, Resume, Rollback }

/// <summary>Typed refusal when Amethyst holds its own catalog lock.</summary>
public sealed class AmethystManagerCloseRequiredException(string message) : IOException(message)
{
    public string Code => "manager_close_required";
}

/// <summary>Typed refusal when the installed manager exposes no usable native lock contract.</summary>
public sealed class AmethystLockContractException(string message) : IOException(message)
{
    public string Code => "manager_lock_contract_unavailable";
}

/// <summary>Request context captured before a transaction may publish bytes.</summary>
public sealed record AmethystTransactionRequest(
    string Operation,
    string GameId,
    string ProfileId,
    string ManifestPath,
    string LibraryPath,
    string ManagerLockPath,
    long SnapshotGeneration,
    ulong InventoryGeneration,
    string ProducerContract,
    string RequestSha256,
    IReadOnlyList<AmethystTransactionUnit> Units,
    IReadOnlyList<string>? InputSha256 = null,
    IReadOnlyList<string>? ConsentScope = null,
    IReadOnlyList<string>? ToolVersions = null,
    bool Managed = true,
    bool ImmediateVisibility = false);

/// <summary>An ordered recovery unit with explicit dependencies and optional resume metadata.</summary>
public sealed record AmethystTransactionUnit(
    string Id,
    IReadOnlyList<string>? DependsOn = null,
    string? SourceRecord = null,
    string? RemapJson = null,
    AmethystUnitState State = AmethystUnitState.Prepared,
    string? Diagnostic = null);

/// <summary>One enrolled artifact and its expected old, prepared and committed identities.</summary>
public sealed record AmethystTransactionArtifact(
    string Id,
    string UnitId,
    string Role,
    string Destination,
    bool OriginalExisted,
    string? OriginalIdentity,
    string? OriginalSha256,
    string? PayloadRelativePath,
    string? BackupRelativePath,
    long PreparedSize,
    string? PreparedSha256,
    string? CommittedIdentity = null,
    string? CommittedSha256 = null,
    string State = "prepared");

/// <summary>Persisted deployment facts; pending is not equivalent to verified.</summary>
public sealed record AmethystTransactionVisibility(
    string Destination,
    string? LinkMode,
    bool Immediate,
    bool Pending,
    bool Verified,
    string? Evidence);

/// <summary>Schema-versioned durable journal. Paths and private payload metadata stay in the owner-only state directory.</summary>
public sealed record AmethystTransactionJournal(
    int SchemaVersion,
    string Id,
    string Operation,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string RequestSha256,
    string GameId,
    string ProfileId,
    string ManifestPath,
    string LibraryPath,
    string LibraryIdentity,
    string ManagerLockPath,
    long SnapshotGeneration,
    ulong InventoryGeneration,
    string ProducerContract,
    IReadOnlyList<string> InputSha256,
    IReadOnlyList<string> ConsentScope,
    IReadOnlyList<string> ToolVersions,
    IReadOnlyList<AmethystTransactionUnit> Units,
    IReadOnlyList<AmethystTransactionArtifact> Artifacts,
    IReadOnlyList<AmethystTransactionVisibility> Visibility,
    IReadOnlyList<string> SuccessfulUnits,
    IReadOnlyList<string> FailedUnits,
    IReadOnlyList<string> SkippedUnits,
    IReadOnlyList<string> Diagnostics,
    AmethystTransactionState State,
    bool PendingDeployment,
    bool ImmediateVisibility,
    bool SafeToRestartAmethyst,
    string? RecoveryAction);

/// <summary>One status entry. Corrupt/unknown journals remain visible and are never deleted by listing.</summary>
public sealed record AmethystTransactionStatus(
    string Id,
    string State,
    string Operation,
    DateTimeOffset? UpdatedUtc,
    bool PendingDeployment,
    bool SafeToRestartAmethyst,
    IReadOnlyList<string> NextActions,
    string? Diagnostic = null);

/// <summary>Action result with typed state and no implicit replay.</summary>
public sealed record AmethystTransactionOutcome(
    bool Success,
    string Code,
    string TransactionId,
    string State,
    bool PendingDeployment,
    bool ImmediateVisibility,
    bool SafeToRestartAmethyst,
    IReadOnlyList<AmethystTransactionArtifact> Items,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<string> NextActions);

/// <summary>
/// Amethyst transaction storage, write-ahead journal and Linux advisory lock coordinator.
/// It is independent of MCP and never opens Amethyst's live SQLite catalog.
/// </summary>
public sealed class AmethystTransactionCoordinator
{
    const int JournalSchema = 1;
    static readonly JsonSerializerOptions Json = CreateJsonOptions();
    readonly string _root;
    readonly Action<string>? _fault;

    static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = false };
        options.Converters.Add(new JsonStringEnumConverter<AmethystTransactionState>(JsonNamingPolicy.SnakeCaseLower));
        options.Converters.Add(new JsonStringEnumConverter<AmethystUnitState>(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }

    /// <summary>Create a coordinator at an explicit state root; the default uses XDG_STATE_HOME.</summary>
    public AmethystTransactionCoordinator(string? stateRoot = null, Action<string>? faultInjector = null)
    {
        _root = Path.GetFullPath(stateRoot ?? DefaultStateRoot());
        _fault = faultInjector;
    }

    /// <summary>State directory used for journals and receipts.</summary>
    public string StateRoot => _root;

    /// <summary>Default durable state root, independent of the installed program directory.</summary>
    public static string DefaultStateRoot()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        string basePath;
        if (!string.IsNullOrWhiteSpace(xdg))
        {
            if (!Path.IsPathFullyQualified(xdg)) throw new IOException("XDG_STATE_HOME must be an absolute path for Amethyst transaction storage");
            basePath = xdg;
        }
        else
        {
            basePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(basePath)) throw new IOException("cannot determine the user's home directory for Amethyst transaction storage");
            basePath = Path.Combine(basePath, ".local", "state");
        }
        return Path.Combine(basePath, "housecarl-amethyst");
    }

    /// <summary>Acquire the shared-library lock first, then Amethyst's existing native filegraph.lock.</summary>
    public AmethystTransactionLockSet AcquireWriteLocks(string libraryPath, string managerLockPath, TimeSpan? timeout = null)
    {
        EnsurePrivateDirectory(_root);
        return AmethystTransactionLockSet.Acquire(_root, libraryPath, managerLockPath, timeout ?? TimeSpan.FromSeconds(2));
    }

    /// <summary>Create a private transaction directory and durably persist its initial journal before any output is allowed.</summary>
    public AmethystTransactionJournal Begin(AmethystTransactionRequest request)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Amethyst transaction coordination currently requires Linux advisory locks");
        ValidateRequest(request);
        EnsurePrivateDirectory(_root);
        var transactions = Path.Combine(_root, "transactions");
        EnsurePrivateDirectory(transactions);
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(transactions, id);
        Directory.CreateDirectory(directory);
        SetPrivateDirectory(directory);
        EnsurePrivateDirectory(Path.Combine(directory, "payloads"));
        EnsurePrivateDirectory(Path.Combine(directory, "backups"));
        var library = LinuxFileIdentity.Canonical(request.LibraryPath);
        var now = DateTimeOffset.UtcNow;
        var units = request.Units.Select(u => u with { DependsOn = u.DependsOn ?? [] }).ToArray();
        var journal = new AmethystTransactionJournal(JournalSchema, id, request.Operation, now, now,
            request.RequestSha256.ToLowerInvariant(), request.GameId, request.ProfileId,
            Path.GetFullPath(request.ManifestPath), library, StableIdentityString(LinuxFileIdentity.Of(library)), Path.GetFullPath(request.ManagerLockPath),
            request.SnapshotGeneration, request.InventoryGeneration, request.ProducerContract,
            request.InputSha256 ?? [], request.ConsentScope ?? [], request.ToolVersions ?? [], units, [],
            [], [], [], [], [], AmethystTransactionState.Preparing, request.Managed, request.ImmediateVisibility,
            false, null);
        WriteJournal(journal);
        _fault?.Invoke("after_begin_journal");
        return journal;
    }

    /// <summary>Stream prepared bytes into private storage, independently copy an existing destination, and journal both hashes.</summary>
    public AmethystTransactionArtifact PrepareArtifact(string transactionId, string unitId, string destination,
        string role, Stream preparedContents, string allowedRoot)
    {
        var journal = ReadJournal(transactionId);
        RequireWritable(journal);
        if (!journal.Units.Any(u => u.Id == unitId)) throw new InvalidOperationException($"transaction unit '{unitId}' is not in the journal");
        var root = LinuxFileIdentity.Canonical(allowedRoot);
        if (!IsWithin(root, journal.LibraryPath)) throw new IOException("transaction allowed root is outside the enrolled Amethyst library");
        var target = ValidateDestination(destination, root, allowMissingLeaf: true);
        if (journal.Artifacts.Any(a => StringComparer.OrdinalIgnoreCase.Equals(a.Destination, target)))
            throw new InvalidOperationException("transaction contains a duplicate artifact destination");

        var artifactId = Guid.NewGuid().ToString("N");
        var payloadRel = $"payloads/{artifactId}.bin";
        var payload = Path.Combine(TransactionDirectory(transactionId), payloadRel);
        var (size, hash) = CopyAndHash(preparedContents, payload, createNew: true);
        _fault?.Invoke("after_payload_flush");

        var exists = File.Exists(target);
        string? originalIdentity = null, originalHash = null, backupRel = null;
        if (exists)
        {
            RefuseSymlink(target);
            var identity = LinuxFileIdentity.Of(target);
            originalIdentity = IdentityString(identity);
            originalHash = HashFile(target);
            backupRel = $"backups/{artifactId}.bak";
            CopyFileDurable(target, Path.Combine(TransactionDirectory(transactionId), backupRel));
            if (HashFile(Path.Combine(TransactionDirectory(transactionId), backupRel)) != originalHash)
                throw new IOException("transaction recovery copy did not match the original destination");
        }
        var artifact = new AmethystTransactionArtifact(artifactId, unitId, role, target, exists,
            originalIdentity, originalHash, payloadRel, backupRel, size, hash);
        var next = journal with
        {
            UpdatedUtc = DateTimeOffset.UtcNow,
            State = AmethystTransactionState.Prepared,
            Units = journal.Units.Select(u => u.Id == unitId ? u with { State = AmethystUnitState.Prepared } : u).ToArray(),
            Artifacts = journal.Artifacts.Append(artifact).ToArray(),
            SafeToRestartAmethyst = true
        };
        WriteJournal(next);
        _fault?.Invoke("after_prepared_journal");
        return artifact;
    }

    /// <summary>Publish one enrolled artifact through a unique same-directory file after rechecking the old destination.</summary>
    public AmethystTransactionJournal Publish(string transactionId, string artifactId, AmethystTransactionLockSet locks)
    {
        var journal = ReadJournal(transactionId);
        locks.RequireFor(journal.LibraryPath, journal.ManagerLockPath);
        RequireWritable(journal);
        var artifact = journal.Artifacts.SingleOrDefault(a => a.Id == artifactId)
            ?? throw new InvalidOperationException("transaction artifact is not enrolled");
        if (artifact.State == "committed") return journal;
        if (artifact.State != "prepared") throw new InvalidOperationException($"artifact transition {artifact.State} → committing is not allowed");
        var unit = journal.Units.Single(u => u.Id == artifact.UnitId);
        foreach (var dependency in unit.DependsOn ?? [])
            if (!journal.Units.Any(u => u.Id == dependency && u.State == AmethystUnitState.Completed))
                throw new InvalidOperationException($"unit '{unit.Id}' is waiting for dependency '{dependency}'");

        journal = UpdateArtifact(journal, artifactId, a => a with { State = "committing" },
            state: AmethystTransactionState.Committing,
            unitState: AmethystUnitState.Committing,
            diagnostic: null);
        _fault?.Invoke("after_committing_journal");
        var target = artifact.Destination;
        ValidateDestination(target, journal.LibraryPath, allowMissingLeaf: true);
        VerifyOriginalUnchanged(artifact);
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        ValidateDestination(target, journal.LibraryPath, allowMissingLeaf: true);
        var sibling = Path.Combine(parent, $".housecarl-{journal.Id}-{artifact.Id}.tmp");
        if (File.Exists(sibling)) throw new IOException("transaction-owned publication temp already exists; recovery is required");
        CopyFileDurable(PrivateArtifactPath(transactionId, artifact.PayloadRelativePath!), sibling);
        VerifyOriginalUnchanged(artifact);
        try { File.Move(sibling, target, overwrite: artifact.OriginalExisted); }
        catch
        {
            try { if (File.Exists(sibling)) File.Delete(sibling); } catch { }
            throw;
        }
        SyncDirectory(parent);
        var committedIdentity = LinuxFileIdentity.Of(target);
        var committedHash = HashFile(target);
        if (committedHash != artifact.PreparedSha256)
            return MarkRecoveryRequired(journal, $"published destination '{target}' does not match the prepared bytes");
        _fault?.Invoke("after_publish");

        journal = UpdateArtifact(journal, artifactId, a => a with
        {
            State = "committed", CommittedIdentity = IdentityString(committedIdentity), CommittedSha256 = committedHash
        });
        if (journal.Artifacts.Where(a => a.UnitId == artifact.UnitId).All(a => a.State == "committed"))
        {
            journal = journal with
            {
                Units = journal.Units.Select(u => u.Id == artifact.UnitId ? u with { State = AmethystUnitState.Completed } : u).ToArray(),
                SuccessfulUnits = journal.SuccessfulUnits.Append(artifact.UnitId).Distinct(StringComparer.Ordinal).ToArray(),
                UpdatedUtc = DateTimeOffset.UtcNow
            };
            WriteJournal(journal);
        }
        var allDone = journal.Units.All(u => u.State == AmethystUnitState.Completed);
        if (allDone)
        {
            journal = journal with
            {
                State = journal.PendingDeployment ? AmethystTransactionState.PendingDeployment : AmethystTransactionState.Completed,
                UpdatedUtc = DateTimeOffset.UtcNow,
                SafeToRestartAmethyst = false,
                SuccessfulUnits = journal.Units.Where(u => u.State == AmethystUnitState.Completed).Select(u => u.Id).ToArray()
            };
            WriteJournal(journal);
            WriteReceipt(journal);
            _fault?.Invoke("after_completed_journal");
        }
        return journal;
    }

    /// <summary>Inspect journals without changing them; corrupt and unknown schemas are surfaced in place.</summary>
    public IReadOnlyList<AmethystTransactionStatus> Status(string? transactionId = null)
    {
        var transactions = Path.Combine(_root, "transactions");
        if (!Directory.Exists(transactions)) return [];
        if (new DirectoryInfo(_root).LinkTarget is not null || new DirectoryInfo(transactions).LinkTarget is not null)
            throw new IOException("transaction state root is a symlink; preserve journals and inspect manually");
        IEnumerable<string> directories = Directory.EnumerateDirectories(transactions).OrderByDescending(Directory.GetLastWriteTimeUtc);
        if (transactionId is not null) directories = directories.Where(d => Path.GetFileName(d) == transactionId);
        var result = new List<AmethystTransactionStatus>();
        foreach (var directory in directories)
        {
            var id = Path.GetFileName(directory);
            try
            {
                if (new DirectoryInfo(directory).LinkTarget is not null)
                    throw new IOException("transaction directory is a symlink; preserve it and inspect manually");
                var journal = ReadJournal(id);
                result.Add(ToStatus(journal));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
            { result.Add(new(id, "corrupt", "unknown", null, false, false, [], ex.Message)); }
        }
        return result;
    }

    /// <summary>Small pending-work summary for the existing load-order status response.</summary>
    public IReadOnlyList<AmethystTransactionStatus> PendingSummary(int limit = 5)
    {
        if (limit is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(limit));
        return Status().Where(s => s.State == "corrupt" || s.State is "preparing" or "prepared" or "committing" or "pending_deployment" or "partial" or "recovery_required")
            .Take(limit).ToArray();
    }

    /// <summary>Read one validated journal; unsupported schema or malformed content is fail-closed.</summary>
    public AmethystTransactionJournal ReadJournal(string transactionId)
    {
        var id = ParseId(transactionId);
        if (new DirectoryInfo(TransactionDirectory(id)).LinkTarget is not null)
            throw new IOException("transaction directory is a symlink; preserve it and inspect manually");
        var path = Path.Combine(TransactionDirectory(id), "journal.json");
        if (new FileInfo(path).LinkTarget is not null) throw new IOException("transaction journal is a symlink; preserve it and inspect manually");
        var bytes = File.ReadAllBytes(path);
        var journal = JsonSerializer.Deserialize<AmethystTransactionJournal>(bytes, Json)
            ?? throw new JsonException("transaction journal is empty");
        if (journal.SchemaVersion != JournalSchema) throw new InvalidOperationException($"transaction journal schema {journal.SchemaVersion} is unsupported; evidence was preserved and mutation is refused");
        if (journal.Id != id) throw new InvalidOperationException("transaction journal identity disagrees with its directory");
        ValidateJournal(journal);
        return journal;
    }

    void ValidateJournal(AmethystTransactionJournal journal)
    {
        if (string.IsNullOrWhiteSpace(journal.Operation) || !Path.IsPathFullyQualified(journal.LibraryPath) ||
            !Path.IsPathFullyQualified(journal.ManifestPath) || !Path.IsPathFullyQualified(journal.ManagerLockPath))
            throw new InvalidOperationException("transaction journal has invalid operation or context paths");
        if (LinuxFileIdentity.Canonical(journal.LibraryPath) != journal.LibraryPath ||
            StableIdentityString(LinuxFileIdentity.Of(journal.LibraryPath)) != journal.LibraryIdentity)
            throw new InvalidOperationException("transaction library identity changed; mutation is refused");
        if (journal.Units.Select(u => u.Id).Distinct(StringComparer.Ordinal).Count() != journal.Units.Count ||
            journal.Units.Any(u => (u.DependsOn ?? []).Any(d => !journal.Units.Any(x => x.Id == d))))
            throw new InvalidOperationException("transaction journal has duplicate units or invalid dependencies");
        if (journal.Artifacts.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count() != journal.Artifacts.Count ||
            journal.Artifacts.Select(a => a.Destination).Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Artifacts.Count)
            throw new InvalidOperationException("transaction journal has duplicate artifact identity or destination");
        foreach (var artifact in journal.Artifacts)
        {
            if (!journal.Units.Any(u => u.Id == artifact.UnitId) || string.IsNullOrWhiteSpace(artifact.Role))
                throw new InvalidOperationException("transaction artifact references an unknown unit or has no role");
            if (ValidateDestination(artifact.Destination, journal.LibraryPath, allowMissingLeaf: true) != artifact.Destination)
                throw new InvalidOperationException("transaction artifact path is not canonical");
            if (artifact.PayloadRelativePath is not null) ValidatePrivateRelativePath(artifact.PayloadRelativePath, "payloads/");
            if (artifact.BackupRelativePath is not null) ValidatePrivateRelativePath(artifact.BackupRelativePath, "backups/");
            if (artifact.PreparedSha256 is not null && !IsSha256(artifact.PreparedSha256) ||
                artifact.OriginalSha256 is not null && !IsSha256(artifact.OriginalSha256) ||
                artifact.CommittedSha256 is not null && !IsSha256(artifact.CommittedSha256))
                throw new InvalidOperationException("transaction artifact contains a malformed SHA-256 digest");
            if (artifact.OriginalExisted != (artifact.OriginalIdentity is not null && artifact.OriginalSha256 is not null) ||
                artifact.OriginalExisted != (artifact.BackupRelativePath is not null))
                throw new InvalidOperationException("transaction artifact original and recovery-copy metadata disagree");
        }
    }

    void ValidatePrivateRelativePath(string path, string expectedPrefix)
    {
        var parts = path.Split('/');
        if (!path.StartsWith(expectedPrefix, StringComparison.Ordinal) || parts.Length != 2 ||
            parts.Any(p => p.Length == 0 || p is "." or ".." || p.Contains('\\') || p.Contains(':')))
            throw new InvalidOperationException("transaction journal contains an unsafe private artifact path");
        var full = Path.GetFullPath(Path.Combine(TransactionDirectory("00000000000000000000000000000000"), path));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("transaction private artifact escapes state storage");
    }

    string PrivateArtifactPath(string transactionId, string relativePath)
    {
        var id = ParseId(transactionId);
        var root = TransactionDirectory(id);
        var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("transaction private artifact escapes its transaction directory");
        var parent = Path.GetDirectoryName(full)!;
        if (new DirectoryInfo(parent).LinkTarget is not null || new DirectoryInfo(root).LinkTarget is not null)
            throw new IOException("transaction private artifact directory is a symlink; preserve evidence and inspect manually");
        if (File.Exists(full) && new FileInfo(full).LinkTarget is not null)
            throw new IOException("transaction private artifact is a symlink; preserve evidence and inspect manually");
        return full;
    }

    /// <summary>Manage one transaction. No wildcard, implicit replay, or automatic Amethyst/game process control.</summary>
    public AmethystTransactionOutcome Manage(string transactionId, AmethystTransactionAction action,
        bool confirmAmethystRedeploy = false, bool confirmLiveDeploymentChange = false)
    {
        var journal = ReadJournal(transactionId);
        var requested = action.ToString().ToLowerInvariant();
        if (!Enum.IsDefined(action) || !AllowedActions(journal).Contains(requested, StringComparer.Ordinal))
            return Outcome(false, "invalid_transition", journal, [$"Action '{requested}' is not valid for state '{StateName(journal.State)}'."], AllowedActions(journal));
        if (action == AmethystTransactionAction.Verify)
            return Outcome(false, "verification_unavailable", journal,
                ["Deployment verification requires the refreshed native evidence adapter; no deployment state was changed."], ["verify"]);
        using var locks = AcquireWriteLocks(journal.LibraryPath, journal.ManagerLockPath);
        journal = ReadJournal(transactionId);
        if (journal.ImmediateVisibility && !confirmLiveDeploymentChange)
            return Outcome(false, "live_deployment_confirmation_required", journal,
                ["This transaction may change bytes immediately visible through a deployed symlink."], ["confirm_live_deployment_change=true"]);
        if (action is AmethystTransactionAction.Rollback && journal.PendingDeployment && !confirmAmethystRedeploy)
            return Outcome(false, "amethyst_redeploy_confirmation_required", journal,
                ["Rolling back staged files may require a later Amethyst redeployment before the game view is safe."], ["confirm_amethyst_redeploy=true"]);

        var outcome = action switch
        {
            AmethystTransactionAction.Recover => Recover(journal, locks),
            AmethystTransactionAction.Rollback => Rollback(journal, locks),
            AmethystTransactionAction.Resume => Resume(journal, locks),
            _ => Outcome(false, "invalid_action", journal, ["Unsupported transaction action."], AllowedActions(journal))
        };
        locks.Dispose();
        if (outcome.Success && outcome.State is not "recovery_required")
        {
            var completed = ReadJournal(journal.Id) with { SafeToRestartAmethyst = true, UpdatedUtc = DateTimeOffset.UtcNow };
            WriteJournal(completed);
            return Outcome(true, outcome.Code, completed, outcome.Diagnostics, outcome.NextActions);
        }
        return outcome;
    }

    internal AmethystTransactionJournal MarkLocksReleased(string transactionId)
    {
        var journal = ReadJournal(transactionId);
        if (journal.State is AmethystTransactionState.Committing or AmethystTransactionState.RecoveryRequired ||
            journal.Units.Any(u => u.State is AmethystUnitState.Committing or AmethystUnitState.RecoveryRequired))
            throw new InvalidOperationException("transaction has an unresolved unit; restart safety cannot be asserted");
        var updated = journal with { SafeToRestartAmethyst = true, UpdatedUtc = DateTimeOffset.UtcNow };
        WriteJournal(updated);
        return updated;
    }

    /// <summary>Recover interrupted commits only when destination bytes exactly match the recorded old or new hash.</summary>
    AmethystTransactionOutcome Recover(AmethystTransactionJournal journal, AmethystTransactionLockSet locks)
    {
        var diagnostics = new List<string>();
        var interruptedUnitIds = journal.Artifacts.Where(a => a.State == "committing").Select(a => a.UnitId).ToHashSet(StringComparer.Ordinal);
        foreach (var artifact in journal.Artifacts.Where(a => interruptedUnitIds.Contains(a.UnitId) && (a.State is "committing" or "committed")).Reverse())
        {
            var current = File.Exists(artifact.Destination) ? HashFile(artifact.Destination) : null;
            var currentIdentity = File.Exists(artifact.Destination) ? IdentityString(LinuxFileIdentity.Of(artifact.Destination)) : null;
            if ((current == artifact.OriginalSha256 && currentIdentity == artifact.OriginalIdentity && artifact.OriginalExisted) || current is null && !artifact.OriginalExisted)
            {
                journal = UpdateArtifact(journal, artifact.Id, a => a with { State = "rolled_back" }, unitState: AmethystUnitState.RolledBack);
                continue;
            }
            var expectedCommitted = artifact.State == "committed";
            if (expectedCommitted
                    ? current != artifact.CommittedSha256 || currentIdentity != artifact.CommittedIdentity
                    : current != artifact.PreparedSha256)
            {
                journal = MarkRecoveryRequired(journal, $"destination '{artifact.Destination}' matches neither recorded old nor new bytes; external content was preserved");
                diagnostics.Add("Destination differs from the recorded original and prepared hashes; no bytes were changed.");
                return Outcome(false, "external_conflict", journal, diagnostics, ["manual_resolution"]);
            }
            RestoreOriginal(journal.Id, artifact,
                expectedCommitted ? artifact.CommittedSha256 : artifact.PreparedSha256,
                expectedCommitted ? artifact.CommittedIdentity : null);
            journal = UpdateArtifact(journal, artifact.Id, a => a with { State = "rolled_back" }, unitState: AmethystUnitState.RolledBack);
        }
        journal = journal with
        {
            UpdatedUtc = DateTimeOffset.UtcNow,
            State = journal.Artifacts.Any(a => a.State == "committed") ? AmethystTransactionState.Partial : AmethystTransactionState.RolledBack,
            SafeToRestartAmethyst = false,
            RecoveryAction = null
        };
        WriteJournal(journal);
        WriteReceipt(journal);
        return Outcome(true, "recovered", journal, diagnostics, AllowedActions(journal));
    }

    /// <summary>Roll back completed items in reverse order, refusing any destination changed since commit.</summary>
    AmethystTransactionOutcome Rollback(AmethystTransactionJournal journal, AmethystTransactionLockSet locks)
    {
        var diagnostics = new List<string>();
        foreach (var artifact in journal.Artifacts.Where(a => a.State == "committed").Reverse())
        {
            var current = File.Exists(artifact.Destination) ? HashFile(artifact.Destination) : null;
            if (current != artifact.CommittedSha256 || IdentityString(LinuxFileIdentity.Of(artifact.Destination)) != artifact.CommittedIdentity)
            {
                journal = MarkRecoveryRequired(journal, $"destination '{artifact.Destination}' changed after commit; external content was preserved");
                return Outcome(false, "external_conflict", journal,
                    ["Destination content changed after this transaction. No external bytes were overwritten."], ["manual_resolution"]);
            }
            RestoreOriginal(journal.Id, artifact, artifact.CommittedSha256, artifact.CommittedIdentity);
            journal = UpdateArtifact(journal, artifact.Id, a => a with { State = "rolled_back" }, unitState: AmethystUnitState.RolledBack);
        }
        journal = journal with
        {
            UpdatedUtc = DateTimeOffset.UtcNow,
            State = AmethystTransactionState.RolledBack,
            PendingDeployment = false,
            SafeToRestartAmethyst = false,
            RecoveryAction = null
        };
        WriteJournal(journal);
        WriteReceipt(journal);
        return Outcome(true, "rolled_back", journal, diagnostics, ["refresh_amethyst", "redeploy_if_needed"]);
    }

    /// <summary>Resume only already-prepared remaining artifacts; never rerun source transformations.</summary>
    AmethystTransactionOutcome Resume(AmethystTransactionJournal journal, AmethystTransactionLockSet locks)
    {
        if (journal.State is not (AmethystTransactionState.Partial or AmethystTransactionState.Prepared or AmethystTransactionState.RecoveryRequired))
            return Outcome(false, "invalid_transition", journal, ["This transaction has no prepared remaining units to resume."], AllowedActions(journal));
        var remaining = journal.Artifacts.Where(a => a.State == "prepared").ToArray();
        if (remaining.Length == 0)
            return Outcome(false, "invalid_transition", journal, ["No recorded prepared artifact remains to resume."], AllowedActions(journal));
        var diagnostics = new List<string>();
        var failed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in remaining)
        {
            journal = ReadJournal(journal.Id);
            var unit = journal.Units.Single(u => u.Id == artifact.UnitId);
            if (failed.Contains(unit.Id))
                continue;
            if ((unit.DependsOn ?? []).Any(failed.Contains))
            {
                failed.Add(unit.Id);
                var reason = "a required dependency failed earlier in this transaction";
                journal = journal with
                {
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    State = AmethystTransactionState.Partial,
                    SafeToRestartAmethyst = false,
                    Units = journal.Units.Select(u => u.Id == unit.Id ? u with { State = AmethystUnitState.Failed, Diagnostic = reason } : u).ToArray(),
                    FailedUnits = journal.FailedUnits.Append(unit.Id).Distinct(StringComparer.Ordinal).ToArray(),
                    Diagnostics = journal.Diagnostics.Append($"Unit '{unit.Id}' skipped: {reason}.").ToArray()
                };
                WriteJournal(journal);
                continue;
            }
            try { journal = Publish(journal.Id, artifact.Id, locks); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                journal = ReadJournal(journal.Id);
                var current = journal.Artifacts.Single(a => a.Id == artifact.Id);
                if (current.State == "committed") continue; // the response was lost after the durable commit
                if (current.State == "committing")
                {
                    var recovered = Recover(journal, locks);
                    journal = ReadJournal(journal.Id);
                    if (recovered.Code == "external_conflict") return recovered;
                }
                failed.Add(unit.Id);
                diagnostics.Add($"Unit '{unit.Id}' stopped: {ex.Message}");
                journal = journal with
                {
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    State = AmethystTransactionState.Partial,
                    SafeToRestartAmethyst = false,
                    Units = journal.Units.Select(u => u.Id == unit.Id || (u.DependsOn ?? []).Any(failed.Contains)
                        ? u with { State = AmethystUnitState.Failed, Diagnostic = ex.Message } : u).ToArray(),
                    FailedUnits = journal.FailedUnits.Append(unit.Id).Distinct(StringComparer.Ordinal).ToArray(),
                    Diagnostics = journal.Diagnostics.Append(ex.Message).ToArray()
                };
                WriteJournal(journal);
            }
        }
        if (failed.Count > 0)
        {
            journal = ReadJournal(journal.Id) with { State = AmethystTransactionState.Partial, SafeToRestartAmethyst = false,
                FailedUnits = journal.FailedUnits.Concat(failed).Distinct(StringComparer.Ordinal).ToArray(), UpdatedUtc = DateTimeOffset.UtcNow };
            WriteJournal(journal);
            return Outcome(false, "partial_failure", journal, diagnostics, AllowedActions(journal));
        }
        return Outcome(true, "resumed", journal, [], AllowedActions(journal));
    }

    AmethystTransactionJournal UpdateArtifact(AmethystTransactionJournal journal, string artifactId,
        Func<AmethystTransactionArtifact, AmethystTransactionArtifact> update,
        AmethystTransactionState? state = null, AmethystUnitState? unitState = null, string? diagnostic = null)
    {
        var artifact = journal.Artifacts.Single(a => a.Id == artifactId);
        var updated = journal with
        {
            UpdatedUtc = DateTimeOffset.UtcNow,
            State = state ?? journal.State,
            Artifacts = journal.Artifacts.Select(a => a.Id == artifactId ? update(a) : a).ToArray(),
            Units = unitState is null ? journal.Units : journal.Units.Select(u => u.Id == artifact.UnitId ? u with { State = unitState.Value, Diagnostic = diagnostic } : u).ToArray(),
            Diagnostics = diagnostic is null ? journal.Diagnostics : journal.Diagnostics.Append(diagnostic).ToArray()
        };
        WriteJournal(updated);
        return updated;
    }

    AmethystTransactionJournal MarkRecoveryRequired(AmethystTransactionJournal journal, string diagnostic)
    {
        var updated = journal with
        {
            UpdatedUtc = DateTimeOffset.UtcNow,
            State = AmethystTransactionState.RecoveryRequired,
            SafeToRestartAmethyst = false,
            RecoveryAction = "Resolve the recorded destination conflict manually, then inspect this transaction again.",
            Diagnostics = journal.Diagnostics.Append(diagnostic).ToArray(),
            Units = journal.Units.Select(u => u.State == AmethystUnitState.Committing ? u with { State = AmethystUnitState.RecoveryRequired, Diagnostic = diagnostic } : u).ToArray()
        };
        WriteJournal(updated);
        return updated;
    }

    void RestoreOriginal(string transactionId, AmethystTransactionArtifact artifact, string? expectedCurrentHash, string? expectedIdentity)
    {
        if (!artifact.OriginalExisted)
        {
            if (File.Exists(artifact.Destination))
            {
                if (HashFile(artifact.Destination) != expectedCurrentHash || (expectedIdentity is not null && IdentityString(LinuxFileIdentity.Of(artifact.Destination)) != expectedIdentity))
                    throw new IOException("destination changed during recovery; external content was preserved");
                File.Delete(artifact.Destination);
            }
            SyncDirectory(Path.GetDirectoryName(artifact.Destination)!);
            return;
        }
        var backup = PrivateArtifactPath(transactionId, artifact.BackupRelativePath
            ?? throw new InvalidOperationException("recorded original has no recovery copy"));
        if (HashFile(backup) != artifact.OriginalSha256) throw new IOException("transaction recovery copy is missing or corrupt; original was not replaced");
        var destination = artifact.Destination;
        var parent = Path.GetDirectoryName(destination)!;
        var temp = Path.Combine(parent, $".housecarl-{transactionId}-{artifact.Id}-restore.tmp");
        CopyFileDurable(backup, temp);
        if (File.Exists(destination))
        {
            var actualHash = HashFile(destination);
            var actualIdentity = IdentityString(LinuxFileIdentity.Of(destination));
            if (actualHash != expectedCurrentHash || (expectedIdentity is not null && actualIdentity != expectedIdentity))
            { File.Delete(temp); throw new IOException("destination changed during recovery; external content was preserved"); }
        }
        else if (expectedCurrentHash is not null)
        { File.Delete(temp); throw new IOException("destination disappeared during recovery; external change was preserved"); }
        File.Move(temp, destination, overwrite: true);
        SyncDirectory(parent);
        if (HashFile(destination) != artifact.OriginalSha256) throw new IOException("restored original hash verification failed");
    }

    void VerifyOriginalUnchanged(AmethystTransactionArtifact artifact)
    {
        if (!artifact.OriginalExisted)
        {
            if (File.Exists(artifact.Destination) || Directory.Exists(artifact.Destination))
                throw new IOException($"transaction destination appeared after preflight: '{artifact.Destination}'");
            return;
        }
        if (!File.Exists(artifact.Destination) || IdentityString(LinuxFileIdentity.Of(artifact.Destination)) != artifact.OriginalIdentity ||
            HashFile(artifact.Destination) != artifact.OriginalSha256)
            throw new IOException($"transaction destination changed after preflight: '{artifact.Destination}'");
    }

    void WriteJournal(AmethystTransactionJournal journal)
    {
        var directory = TransactionDirectory(journal.Id);
        EnsurePrivateDirectory(directory);
        var journalPath = Path.Combine(directory, "journal.json");
        var temp = Path.Combine(directory, $"journal.{Guid.NewGuid():N}.tmp");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, Json);
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { stream.Write(bytes); stream.Flush(flushToDisk: true); }
        File.Move(temp, journalPath, overwrite: true);
        SyncDirectory(directory);
    }

    void WriteReceipt(AmethystTransactionJournal journal)
    {
        var directory = TransactionDirectory(journal.Id);
        var temp = Path.Combine(directory, $"receipt.{Guid.NewGuid():N}.tmp");
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, journal, Json); stream.Flush(flushToDisk: true); }
        File.Move(temp, Path.Combine(directory, "receipt.json"), overwrite: true);
        SyncDirectory(directory);
    }

    static (long Size, string Sha256) CopyAndHash(Stream source, string destination, bool createNew)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var target = new FileStream(destination, createNew ? FileMode.CreateNew : FileMode.Create,
            FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long size = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
        { target.Write(buffer, 0, read); hash.AppendData(buffer, 0, read); size = checked(size + read); }
        target.Flush(flushToDisk: true);
        SyncDirectory(Path.GetDirectoryName(destination)!);
        return (size, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    static void CopyFileDurable(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        _ = CopyAndHash(input, destination, createNew: true);
    }

    static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    static string ValidateDestination(string destination, string allowedRoot, bool allowMissingLeaf)
    {
        if (!Path.IsPathFullyQualified(destination)) throw new IOException("transaction destination must be an absolute path");
        var rootPrefix = Path.GetFullPath(allowedRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(rootPrefix, StringComparison.Ordinal))
            throw new IOException("transaction destination is outside its enrolled staging root");
        var rawRelative = destination[rootPrefix.Length..];
        if (rawRelative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "." or ".."))
            throw new IOException("transaction destination contains traversal components");
        var full = Path.GetFullPath(destination);
        var root = Path.GetFullPath(allowedRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.Ordinal) || full == allowedRoot)
            throw new IOException("transaction destination is outside its enrolled staging root");
        var relative = Path.GetRelativePath(allowedRoot, full);
        var current = allowedRoot;
        var parts = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "." or ".." || p.Contains('\0')))
            throw new IOException("transaction destination has an unsafe path component");
        for (var i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            if (File.Exists(current) || Directory.Exists(current))
            {
                RefuseSymlink(current);
                var canonical = LinuxFileIdentity.Canonical(current);
                if (i < parts.Length - 1 && !Directory.Exists(current)) throw new IOException("transaction destination parent is not a directory");
                var prefix = allowedRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!canonical.StartsWith(prefix, StringComparison.Ordinal)) throw new IOException("transaction destination escapes its enrolled staging root through a symlink");
            }
            else if (i < parts.Length - 1 && !allowMissingLeaf)
                throw new DirectoryNotFoundException($"transaction destination parent is missing: '{current}'");
        }
        return full;
    }

    static bool IsWithin(string path, string root)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path) == Path.GetFullPath(root) || Path.GetFullPath(path).StartsWith(normalizedRoot, StringComparison.Ordinal);
    }

    static void RefuseSymlink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"transaction destination is a symlink or reparse point: '{path}'");
    }

    static string IdentityString(LinuxFileIdentity identity) => $"{identity.DeviceMajor}:{identity.DeviceMinor}:{identity.Inode}:{identity.Size}:{identity.MtimeNs}";
    static string StableIdentityString(LinuxFileIdentity identity) => $"{identity.DeviceMajor}:{identity.DeviceMinor}:{identity.Inode}";
    static bool IsSha256(string hash) => hash.Length == 64 && hash.All(Uri.IsHexDigit);

    static void ValidateRequest(AmethystTransactionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Operation) || string.IsNullOrWhiteSpace(request.GameId) ||
            string.IsNullOrWhiteSpace(request.ProfileId) || string.IsNullOrWhiteSpace(request.ProducerContract))
            throw new ArgumentException("transaction operation, game, profile and producer contract are required", nameof(request));
        if (request.RequestSha256.Length != 64 || request.RequestSha256.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("transaction request digest must be a SHA-256 hex string", nameof(request));
        if (request.Units.Count == 0 || request.Units.Select(u => u.Id).Distinct(StringComparer.Ordinal).Count() != request.Units.Count ||
            request.Units.Any(u => string.IsNullOrWhiteSpace(u.Id) || u.Id.Contains('/') || u.Id.Contains('\\')))
            throw new ArgumentException("transaction requires uniquely named safe units", nameof(request));
        foreach (var unit in request.Units)
            if ((unit.DependsOn ?? []).Any(d => !request.Units.Any(u => u.Id == d) || d == unit.Id))
                throw new ArgumentException($"transaction unit '{unit.Id}' has an invalid dependency", nameof(request));
    }

    static void RequireWritable(AmethystTransactionJournal journal)
    {
        if (journal.State is AmethystTransactionState.RecoveryRequired or AmethystTransactionState.Verified or AmethystTransactionState.RolledBack or AmethystTransactionState.Aborted)
            throw new InvalidOperationException($"transaction state '{journal.State}' does not allow publication");
    }

    static string ParseId(string value)
        => Guid.TryParseExact(value, "N", out var id) ? id.ToString("N") : throw new ArgumentException("transaction_id must be a 32-character transaction UUID", nameof(value));
    string TransactionDirectory(string id) => Path.Combine(_root, "transactions", ParseId(id));

    static AmethystTransactionStatus ToStatus(AmethystTransactionJournal journal) => new(
        journal.Id, StateName(journal.State), journal.Operation, journal.UpdatedUtc,
        journal.PendingDeployment, journal.SafeToRestartAmethyst, AllowedActions(journal));

    static IReadOnlyList<string> AllowedActions(AmethystTransactionJournal journal) => journal.State switch
    {
        AmethystTransactionState.Preparing or AmethystTransactionState.Committing => ["recover", "rollback"],
        AmethystTransactionState.Prepared => ["recover", "resume", "rollback"],
        AmethystTransactionState.PendingDeployment => ["verify", "rollback"],
        AmethystTransactionState.Partial => ["recover", "resume", "rollback"],
        AmethystTransactionState.RecoveryRequired => ["recover", "rollback"],
        AmethystTransactionState.Completed => ["verify", "rollback"],
        _ => []
    };

    static AmethystTransactionOutcome Outcome(bool success, string code, AmethystTransactionJournal journal,
        IReadOnlyList<string> diagnostics, IReadOnlyList<string> actions) => new(
        success, code, journal.Id, StateName(journal.State), journal.PendingDeployment,
        journal.ImmediateVisibility, journal.SafeToRestartAmethyst, journal.Artifacts, diagnostics, actions);

    static string StateName(AmethystTransactionState state) => JsonNamingPolicy.SnakeCaseLower.ConvertName(state.ToString());

    internal static void EnsurePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        SetPrivateDirectory(path);
    }

    static void SetPrivateDirectory(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("private Amethyst transaction directories require Linux");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || !Directory.Exists(path))
            throw new IOException($"transaction state path is not a real directory: '{path}'");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        SyncDirectory(Path.GetDirectoryName(path)!);
    }

    static void SyncDirectory(string directory)
    {
        var fd = Native.open(directory, Native.O_RDONLY | Native.O_DIRECTORY | Native.O_CLOEXEC | Native.O_NOFOLLOW, 0);
        if (fd < 0) throw new IOException($"cannot open directory for durability sync '{directory}' (errno {Marshal.GetLastPInvokeError()})");
        using var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        if (Native.fsync(fd) != 0) throw new IOException($"cannot sync directory '{directory}' (errno {Marshal.GetLastPInvokeError()})");
    }

    internal static class Native
    {
        internal const int O_RDONLY = 0;
        internal const int O_RDWR = 2;
        internal const int O_CREAT = 0x40;
        internal const int O_NONBLOCK = 0x800;
        internal const int O_DIRECTORY = 0x10000;
        internal const int O_NOFOLLOW = 0x20000;
        internal const int O_CLOEXEC = 0x80000;
        internal const int LOCK_EX = 2;
        internal const int LOCK_NB = 4;
        internal const int LOCK_UN = 8;
        internal const int EWOULDBLOCK = 11;
        [DllImport("libc", EntryPoint = "open", SetLastError = true)] internal static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);
        [DllImport("libc", EntryPoint = "flock", SetLastError = true)] internal static extern int flock(int fd, int operation);
        [DllImport("libc", EntryPoint = "fsync", SetLastError = true)] internal static extern int fsync(int fd);
        [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)] internal static extern int fchmod(int fd, uint mode);
        [DllImport("libc", EntryPoint = "statx", SetLastError = true)] internal static extern int statx(int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] result);
        [DllImport("libc", EntryPoint = "geteuid")] internal static extern uint geteuid();
    }

}

/// <summary>Exclusive flock set with canonical library-before-manager ordering and retained handles.</summary>
public sealed class AmethystTransactionLockSet : IDisposable
{
    readonly List<SafeFileHandle> _handles;
    readonly string _libraryPath;
    readonly string _managerLockPath;
    AmethystTransactionLockSet(List<SafeFileHandle> handles, string libraryPath, string managerLockPath)
    { _handles = handles; _libraryPath = libraryPath; _managerLockPath = managerLockPath; }

    /// <summary>Acquire houseCARL's shared-library lock, then the existing Amethyst catalog lock.</summary>
    internal static AmethystTransactionLockSet Acquire(string stateRoot, string libraryPath, string managerLockPath, TimeSpan timeout)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Amethyst transaction locks currently require Linux flock");
        var canonicalLibrary = LinuxFileIdentity.Canonical(libraryPath);
        var lockRoot = Path.Combine(stateRoot, "locks");
        AmethystTransactionCoordinator.EnsurePrivateDirectory(lockRoot);
        var libraryKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalLibrary))).ToLowerInvariant();
        var libraryLock = Path.Combine(lockRoot, libraryKey + ".lock");
        var handles = new List<SafeFileHandle>();
        try
        {
            handles.Add(AcquireFileLock(libraryLock, create: true, timeout, manager: false));
            handles.Add(AcquireFileLock(managerLockPath, create: false, timeout, manager: true));
            return new(handles, canonicalLibrary, Path.GetFullPath(managerLockPath));
        }
        catch
        {
            for (var i = handles.Count - 1; i >= 0; i--) handles[i].Dispose();
            throw;
        }
    }

    internal void RequireFor(string libraryPath, string managerLockPath)
    {
        if (_handles.Count != 2 || LinuxFileIdentity.Canonical(libraryPath) != _libraryPath ||
            Path.GetFullPath(managerLockPath) != _managerLockPath)
            throw new InvalidOperationException("publication requires the active lock set for this transaction's library and Amethyst manager lock");
    }

    /// <summary>Release both locks, then durably mark restart safety when no incomplete unit remains.</summary>
    public AmethystTransactionJournal ReleaseAndSeal(AmethystTransactionCoordinator coordinator, string transactionId)
    {
        Dispose();
        return coordinator.MarkLocksReleased(transactionId);
    }

    static SafeFileHandle AcquireFileLock(string path, bool create, TimeSpan timeout, bool manager)
    {
        var flags = (create ? AmethystTransactionCoordinator.Native.O_RDWR | AmethystTransactionCoordinator.Native.O_CREAT : AmethystTransactionCoordinator.Native.O_RDONLY)
            | AmethystTransactionCoordinator.Native.O_CLOEXEC | AmethystTransactionCoordinator.Native.O_NOFOLLOW | AmethystTransactionCoordinator.Native.O_NONBLOCK;
        var fd = AmethystTransactionCoordinator.Native.open(path, flags, 0x180);
        if (fd < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (manager && errno == 2) throw new AmethystLockContractException($"Amethyst's existing manager lock is missing: '{path}'; this producer does not expose the required locking contract");
            if (manager && errno is 40 or 20) throw new AmethystLockContractException("Amethyst's manager lock path is a symlink or not a regular file; mutation is refused");
            throw new IOException($"cannot open {(manager ? "Amethyst" : "houseCARL")} transaction lock '{path}' (errno {errno})");
        }
        var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        try
        {
            var identity = Statx(fd);
            if (identity.Uid != AmethystTransactionCoordinator.Native.geteuid() || (identity.Mode & 0xF000) != 0x8000)
                throw new AmethystLockContractException($"transaction lock is not an owned regular file: '{path}'");
            if (create && AmethystTransactionCoordinator.Native.fchmod(fd, 0x180) != 0)
                throw new IOException($"cannot restrict houseCARL lock permissions at '{path}' (errno {Marshal.GetLastPInvokeError()})");
            if (manager)
            {
                var named = LinuxFileIdentity.Of(path);
                if ((named.DeviceMajor, named.DeviceMinor, named.Inode) != (identity.DeviceMajor, identity.DeviceMinor, identity.Inode))
                    throw new AmethystLockContractException("Amethyst replaced filegraph.lock while houseCARL opened it; retry after the manager is closed");
            }
            var stopwatch = Stopwatch.StartNew();
            do
            {
                if (AmethystTransactionCoordinator.Native.flock(fd, AmethystTransactionCoordinator.Native.LOCK_EX | AmethystTransactionCoordinator.Native.LOCK_NB) == 0)
                {
                    if (manager)
                    {
                        var named = LinuxFileIdentity.Of(path);
                        if ((named.DeviceMajor, named.DeviceMinor, named.Inode) != (identity.DeviceMajor, identity.DeviceMinor, identity.Inode))
                            throw new AmethystLockContractException("Amethyst replaced filegraph.lock while houseCARL acquired it; retry after the manager is closed");
                    }
                    return handle;
                }
                var errno = Marshal.GetLastPInvokeError();
                if (errno != AmethystTransactionCoordinator.Native.EWOULDBLOCK)
                    throw new IOException($"cannot acquire transaction lock '{path}' (errno {errno})");
                if (manager && stopwatch.Elapsed >= timeout)
                    throw new AmethystManagerCloseRequiredException("Amethyst holds its filegraph lock. Close Amethyst normally, let it finish any prompt or restore, then retry; houseCARL did not prepare managed changes.");
                if (stopwatch.Elapsed >= timeout)
                    throw new TimeoutException("timed out waiting for another houseCARL process holding the shared staging-library lock");
                Thread.Sleep(20);
            } while (true);
        }
        catch { handle.Dispose(); throw; }
    }

    static (uint Uid, ushort Mode, uint DeviceMajor, uint DeviceMinor, ulong Inode) Statx(int fd)
    {
        var buffer = new byte[256];
        if (AmethystTransactionCoordinator.Native.statx(fd, "", 0x1000, 0x7ff, buffer) != 0)
            throw new IOException($"cannot validate opened transaction lock (statx errno {Marshal.GetLastPInvokeError()})");
        return (BitConverter.ToUInt32(buffer, 20), BitConverter.ToUInt16(buffer, 28),
            BitConverter.ToUInt32(buffer, 136), BitConverter.ToUInt32(buffer, 140), BitConverter.ToUInt64(buffer, 32));
    }

    /// <summary>Release locks in reverse acquisition order.</summary>
    public void Dispose()
    {
        for (var i = _handles.Count - 1; i >= 0; i--)
        {
            var fd = _handles[i].DangerousGetHandle().ToInt32();
            _ = AmethystTransactionCoordinator.Native.flock(fd, AmethystTransactionCoordinator.Native.LOCK_UN);
            _handles[i].Dispose();
        }
        _handles.Clear();
    }
}
