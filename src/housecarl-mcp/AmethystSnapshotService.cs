namespace HousecarlMcp;

/// <summary>The Amethyst read adapter; one request consumes one saved generation and never invokes manager writes.</summary>
public sealed partial class LoadOrderService
{
    /// <summary>Explicit connection selected for saved-snapshot reads, or null for upstream instance mode.</summary>
    string? _snapshotManifest;
    /// <summary>The current request's recorded graph and exact source paths.</summary>
    AmethystSavedSnapshot? _savedSnapshot;
    /// <summary>Serializes snapshot requests and connection changes so an index cannot switch beneath a read.</summary>
    internal readonly SemaphoreSlim SnapshotCalls = new(1, 1);
    /// <summary>Whether the user explicitly enabled the warned saved-snapshot lane.</summary>
    public bool UsesSavedSnapshot => _snapshotManifest is not null;
    /// <summary>Named refusal shared by managed output paths and the MCP write guard.</summary>
    public const string SnapshotWriteRefusal = "Amethyst saved-snapshot mode supports reads and analysis only. Managed writes, previews and in-place editing are unavailable until Amethyst staging and redeployment verification are implemented.";

    /// <summary>Select a valid connection at startup; its graph is captured lazily for each tool call.</summary>
    public static LoadOrderService WithAmethystSnapshot(string manifest, UserConfigStore store)
    {
        var connection = AmethystConnection.Open(manifest);
        return new(null, "", "", "", configured: true, maxPlugins: 0, store) { _snapshotManifest = connection.ManifestPath };
    }

    /// <summary>Capture and bind the saved graph; the request filter supplies the request scope and final validation.</summary>
    internal AmethystSavedSnapshot CaptureSavedSnapshot()
    {
        lock (_gate)
        {
            if (_snapshotManifest is null) throw new InvalidOperationException("Amethyst saved-snapshot mode is not selected");
            var snapshot = AmethystSavedSnapshot.Capture(_snapshotManifest);
            ApplySavedSnapshot(snapshot);
            return snapshot;
        }
    }

    /// <summary>Bind roots and recorded order without passing through MO2 discovery; caller holds the service gate.</summary>
    void ApplySavedSnapshot(AmethystSavedSnapshot snapshot)
    {
        if (ReferenceEquals(_savedSnapshot, snapshot)) return;
        if (_savedSnapshot?.Provenance != snapshot.Provenance || !_resolvedPaths.SequenceEqual(snapshot.OrderedPluginPaths, StringComparer.Ordinal))
        {
            _resolver?.Dispose(); _resolver = null;
            InvalidateClassParents();
            System.Threading.Interlocked.Increment(ref _gameRootsGen);
        }
        _savedSnapshot = snapshot;
        _profileDir = snapshot.ProfileDir; _profileName = Path.GetFileName(snapshot.ProfileDir);
        _modsDir = snapshot.ModsDir; _dataDir = snapshot.VanillaDataDir; _overwriteDir = snapshot.OverwriteDir;
        _resolvedPaths = snapshot.OrderedPluginPaths;
        _orderWarnings = [AmethystSnapshotProvenance.Warning];
        InvalidateAssetResolver();
    }

    /// <summary>Use the request's capture, or capture for a direct engine caller; caller holds the service gate.</summary>
    AmethystSavedSnapshot CurrentSavedSnapshot()
    {
        var snapshot = AmethystSnapshotContext.Current ?? CaptureSavedSnapshot();
        ApplySavedSnapshot(snapshot);
        return snapshot;
    }

    /// <summary>Build the full Mutagen index from exact recorded paths, with no plugin-count cap or staging rescan.</summary>
    LoadOrderResolver SavedResolverLocked()
    {
        var snapshot = CurrentSavedSnapshot();
        if (snapshot.OrderedPluginPaths.Count == 0)
            throw new InvalidOperationException("No active plugins resolved from Amethyst's saved profile; enable plugins and Refresh the selected profile in Amethyst");
        _resolver ??= LoadOrderResolver.Build(snapshot.OrderedPluginPaths,
            name => $"'{name}' is absent from Amethyst's saved active plugin order; refresh the selected profile or select a saved source explicitly", Counters);
        _resolver.RefreshIfStale();
        return _resolver;
    }

    /// <summary>Report composition and counts from the same saved request generation.</summary>
    LoadOrderStatusData SavedStatusData()
    {
        lock (_gate)
        {
            var snapshot = CurrentSavedSnapshot();
            var view = SavedResolverLocked().Capture();
            return new(snapshot.Composition, _orderWarnings, view.PluginCount, 0, false,
                _profileDir, _modsDir, _dataDir, _overwriteDir, _profileName, null, view.ExcludedPlugins,
                view.Epoch, view.ContainedRecordCount);
        }
    }

    /// <summary>Validate and select the warned mode before persisting it; a rejected connection changes nothing.</summary>
    public (AmethystSnapshotProvenance Snapshot, bool Persisted, string? Error, string? Note) SetAmethystSnapshot(string manifest)
    {
        var snapshot = AmethystSavedSnapshot.Capture(manifest);
        lock (_gate)
        {
            _snapshotManifest = Path.GetFullPath(manifest);
            _configured = true; _unavailableManagerReason = null; _instanceDir = null;
            ApplySavedSnapshot(snapshot);
            var (ok, error, note) = _store.Update(cfg =>
            {
                cfg.AmethystConnectionManifest = _snapshotManifest;
                cfg.AmethystFilegraphMode = "saved-snapshot";
            });
            return (snapshot.Provenance, ok, error, note);
        }
    }
}
