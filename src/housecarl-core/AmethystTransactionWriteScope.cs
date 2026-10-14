using System.Security.Cryptography;
using System.Text;

namespace HousecarlCore;

/// <summary>Request-scoped bridge from Mutagen's single plugin commit chokepoint to durable Amethyst transactions.</summary>
public sealed class AmethystTransactionWriteScope : IDisposable
{
    static readonly AsyncLocal<AmethystTransactionWriteScope?> Active = new();
    readonly AmethystTransactionWriteScope? _previous;
    readonly AmethystTransactionCoordinator _coordinator;
    readonly AmethystTransactionLockSet _locks;
    readonly string _transactionId;
    readonly string _allowedRoot;
    readonly string _unitId;
    readonly string _metadataUnitId;
    readonly Action _validateInputs;
    readonly Dictionary<string, (string Contents, string? ExpectedOriginalSha256, bool TrackPluginHash)> _metadata = new(StringComparer.Ordinal);
    readonly HashSet<string> _preparedMetadata = new(StringComparer.Ordinal);
    readonly HashSet<string> _publishedMetadata = new(StringComparer.Ordinal);
    readonly List<(string Path, string Hash)> _plugins = [];
    bool _disposed;

    public static AmethystTransactionWriteScope? Current => Active.Value;
    public AmethystTransactionJournal Journal => _coordinator.ReadJournal(_transactionId);

    public AmethystTransactionWriteScope(AmethystTransactionCoordinator coordinator,
        AmethystTransactionJournal journal, AmethystTransactionLockSet locks,
        string allowedRoot, Action validateInputs, string unitId = "record-output", string metadataUnitId = "owner-metadata")
    {
        _coordinator = coordinator; _transactionId = journal.Id; _locks = locks;
        _allowedRoot = allowedRoot; _validateInputs = validateInputs; _unitId = unitId; _metadataUnitId = metadataUnitId;
        _previous = Active.Value; Active.Value = this;
    }

    public void DeferOwnerMetadata(string destination, string contents, string? expectedOriginalSha256 = null, bool trackPluginHash = true)
    {
        var path = Path.GetFullPath(destination);
        if (!_metadata.TryAdd(path, (contents, expectedOriginalSha256, trackPluginHash)))
            throw new InvalidOperationException("transaction received duplicate owner-metadata destination");
        if (_plugins.Count > 0) PrepareMetadata(path, trackPluginHash ? _plugins[^1].Hash : null);
    }

    /// <summary>Prepare the serializer's completed sibling bytes, enroll paired owner metadata, then publish the plugin.</summary>
    public void CommitPlugin(string stagedPath, string destination)
    {
        _validateInputs();
        using var source = new FileStream(stagedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        var artifact = _coordinator.PrepareArtifact(_transactionId, _unitId, destination, "plugin", source, _allowedRoot);
        if (artifact.PreparedSha256 is null) throw new IOException("prepared plugin has no durable content hash");
        foreach (var path in _metadata.Keys.ToArray())
            PrepareMetadata(path, _metadata[path].TrackPluginHash ? artifact.PreparedSha256 : null);
        _validateInputs();
        _coordinator.Publish(_transactionId, artifact.Id, _locks);
        _plugins.Add((Path.GetFullPath(destination), artifact.PreparedSha256));
    }

    /// <summary>Finish paired owner metadata only after the caller's native plugin round-trip succeeded.</summary>
    public void PublishOwnerMetadata()
    {
        _validateInputs();
        var journal = Journal;
        foreach (var artifact in journal.Artifacts.Where(a => a.Role == "owner_metadata" && a.State == "prepared").ToArray())
        {
            _coordinator.Publish(_transactionId, artifact.Id, _locks);
            _publishedMetadata.Add(artifact.Destination);
        }
    }

    void PrepareMetadata(string path, string? pluginHash = null)
    {
        if (!_preparedMetadata.Add(path)) return;
        var item = _metadata[path];
        var contents = item.Contents;
        if (pluginHash is not null)
        {
            const string marker = "pendingDeployment=true\r\n";
            contents = contents.Contains(marker, StringComparison.Ordinal)
                ? contents.Replace(marker, marker + $"patchSha256={pluginHash}\r\n", StringComparison.Ordinal)
                : contents + $"\r\npatchSha256={pluginHash}\r\n";
        }
        using var metadata = new MemoryStream(Encoding.UTF8.GetBytes(contents));
        var metadataArtifact = _coordinator.PrepareArtifact(_transactionId, _metadataUnitId, path, "owner_metadata", metadata, _allowedRoot);
        if (item.ExpectedOriginalSha256 is not null && metadataArtifact.OriginalSha256 != item.ExpectedOriginalSha256 ||
            item.ExpectedOriginalSha256 is null && metadataArtifact.OriginalExisted)
        {
            _preparedMetadata.Remove(path);
            throw new IOException("owner metadata changed while preparing the transaction; retry without overwriting concurrent edits");
        }
    }

    public AmethystTransactionOutcome Abort()
        => _coordinator.AbortWithLocks(_transactionId, _locks);

    public AmethystTransactionJournal Seal()
        => _locks.ReleaseAndSeal(_coordinator, _transactionId);

    public IReadOnlyList<(string Path, string Hash)> PublishedPlugins => _plugins;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Active.Value = _previous;
    }
}
