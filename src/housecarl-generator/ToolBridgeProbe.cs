using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>Proves native log-directory validation and shared-config isolation.</summary>
internal static class ToolBridgeProbe
{
    /// <summary>Runs the self-contained log-path and atomic-config guard.</summary>
    internal static int Run(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" diagnostic-path bridge — native directories + shared config");
        Console.WriteLine("================================================================");
        var failures = 0;
        void Check(bool condition, string label)
        {
            Console.WriteLine((condition ? "  PASS  " : "  FAIL  ") + label);
            if (!condition) failures++;
        }

        var root = Path.Combine(Path.GetTempPath(), "hc-tool-bridge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var configPath = Path.Combine(root, "houseCARL.user.json");
            var logs = Path.Combine(root, "Papyrus Logs");
            Directory.CreateDirectory(logs);
            var store = new UserConfigStore(configPath);
            var resolver = new ToolPathResolver(store);

            Check(ToolBridge.WireKeys == "papyrus_logs, crash_logs",
                "only native diagnostic-directory keys are public");
            Check(ToolBridge.TryParse("PAPYRUS_LOGS", out var parsed)
                  && parsed == ToolDependency.PapyrusLogs,
                "wire keys parse case-insensitively");
            Check(!ToolBridge.TryParse("papyrus_compiler", out _)
                  && !ToolBridge.TryParse("bsarch", out _),
                "deferred executable keys are rejected");

            Check(ToolBridge.Validate(ToolDependency.PapyrusLogs, logs).ok,
                "an existing native log directory validates");
            Check(!ToolBridge.Validate(
                    ToolDependency.PapyrusLogs,
                    Path.Combine(root, "missing")).ok,
                "a missing log directory is refused");

            var saved = resolver.Save(ToolDependency.PapyrusLogs, logs);
            Check(saved.ok && saved.persisted, "a valid directory persists");
            Check(resolver.Inspect(ToolDependency.PapyrusLogs) is { source: ToolPathSource.Saved },
                "status reports the saved directory");

            store.Update(config => config.AmethystConnectionManifest = "/profiles/.housecarl-amethyst/connection.json");
            var merged = store.Load();
            Check(merged.AmethystConnectionManifest is not null
                  && merged.ToolPaths is { } paths
                  && paths.TryGetValue("papyrus_logs", out var savedPath)
                  && savedPath == Path.GetFullPath(logs),
                "connection and diagnostic path coexist in one atomic config");

            Directory.Delete(logs);
            Check(resolver.Inspect(ToolDependency.PapyrusLogs) is
                  { path: null, source: ToolPathSource.Unset },
                "a removed saved directory becomes explicitly unset");

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "================ ALL PASS ================"
                                             : $"================ {failures} CHECK(S) FAILED ================");
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }
}
