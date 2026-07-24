namespace HousecarlCore;

/// <summary>Identifies optional external programs and log directories.</summary>
public enum ToolDependency
{
    /// <summary>Bethesda Papyrus compiler executable.</summary>
    PapyrusCompiler,
    /// <summary>External BSArch archive utility.</summary>
    Bsarch,
    /// <summary>Papyrus script-log directory.</summary>
    PapyrusLogs,
    /// <summary>SKSE crash-log directory.</summary>
    CrashLogs,
}

/// <summary>Describes how an optional dependency path was resolved.</summary>
public enum ToolPathSource
{
    /// <summary>A valid user-saved path.</summary>
    Saved,
    /// <summary>A valid path found in a known location.</summary>
    AutoDetected,
    /// <summary>No usable path.</summary>
    Unset,
}

/// <summary>Describes one optional program or log-directory dependency.</summary>
/// <param name="Dep">Stable dependency enum.</param>
/// <param name="Key">Wire key accepted by configuration tools.</param>
/// <param name="Display">Human-readable dependency name.</param>
/// <param name="IsDirectory">Whether the dependency is a directory rather than an executable.</param>
/// <param name="ExeStem">Required executable filename fragment, or null for directories.</param>
/// <param name="Need">Capability requiring the dependency.</param>
/// <param name="WhereToGet">Installation or discovery guidance.</param>
public sealed record ToolInfo(
    ToolDependency Dep, string Key, string Display, bool IsDirectory, string? ExeStem, string Need, string WhereToGet);

/// <summary>Catalogs optional external dependencies and validates or discovers their paths.</summary>
/// <remarks>
/// The executable entries are transitional Windows-tool metadata. Linux v1 defers their Proton command specification.
/// Log-directory inspection remains read-only. This class performs existence checks but no persistence.
/// </remarks>
public static class ToolBridge
{
    /// <summary>Complete dependency catalog.</summary>
    static readonly ToolInfo[] All =
    {
        new(
            ToolDependency.PapyrusCompiler,
            "papyrus_compiler",
            "the Papyrus compiler (PapyrusCompiler.exe)",
            false,
            "papyruscompiler",
            "compiling .psc scripts to .pex",
            "it ships with the Creation Kit. Linux execution is deferred until the Proton command runner is added"),
        new(ToolDependency.Bsarch, "bsarch", "BSArch (BSArch.exe)", false, "bsarch",
            "listing, extracting, and repacking .bsa archives",
            "BSArch is a standalone Windows tool. Linux execution is deferred until the Proton runner is added"),
        new(ToolDependency.PapyrusLogs, "papyrus_logs", "the Papyrus script-log folder", true, null,
            "reading Papyrus script logs for triage",
            "supply the native path to the prefix's Skyrim Special Edition/Logs/Script directory"),
        new(ToolDependency.CrashLogs, "crash_logs", "the SKSE crash-log folder", true, null,
            "reading crash logs for diagnosis",
            "Crash Logger SSE (Nexus) writes them under Documents\\My Games\\Skyrim Special Edition\\SKSE\\Crashlogs"),
    };

    /// <summary>Dependency metadata keyed by enum.</summary>
    static readonly Dictionary<ToolDependency, ToolInfo> ByDep = All.ToDictionary(i => i.Dep);

    /// <summary>Dependency enums keyed by case-insensitive wire name.</summary>
    static readonly Dictionary<string, ToolDependency> ByKey =
        All.ToDictionary(i => i.Key, i => i.Dep, StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets metadata for a dependency.</summary>
    /// <param name="dep">Dependency.</param>
    /// <returns>Catalog entry.</returns>
    public static ToolInfo Info(ToolDependency dep) => ByDep[dep];

    /// <summary>Gets the comma-separated valid configuration wire keys.</summary>
    public static string WireKeys => string.Join(", ", All.Select(i => i.Key));

    /// <summary>Parses a case-insensitive configuration wire key.</summary>
    /// <param name="wire">Candidate key.</param>
    /// <param name="dep">Receives the dependency after success.</param>
    /// <returns>True when the key is known.</returns>
    public static bool TryParse(string? wire, out ToolDependency dep)
    {
        if (!string.IsNullOrWhiteSpace(wire) && ByKey.TryGetValue(wire.Trim(), out dep)) return true;
        dep = default; return false;
    }

    /// <summary>Validates a dependency path and executable identity.</summary>
    /// <param name="dep">Dependency being configured.</param>
    /// <param name="path">Candidate native host path.</param>
    /// <returns>Success plus a specific failure message.</returns>
    public static (bool ok, string? error) Validate(ToolDependency dep, string path)
    {
        var info = Info(dep);
        if (string.IsNullOrWhiteSpace(path)) return (false, "no path given.");
        if (info.IsDirectory)
            return Directory.Exists(path) ? (true, null)
                 : (false, $"no such folder: '{path}'. Give the {info.Display} (a directory).");
        if (!File.Exists(path))
            return (false, $"no such file: '{path}'. Give the full path to {info.Display}.");
        var name = Path.GetFileName(path);
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return (false, $"'{name}' is not an .exe — give the {info.Display}.");
        if (info.ExeStem is not null && name.IndexOf(info.ExeStem, StringComparison.OrdinalIgnoreCase) < 0)
            return (false, $"'{name}' does not look like {info.Display} " +
                           $"(expected a filename containing '{info.ExeStem}'). " +
                           "Double-check you pointed at the right .exe.");
        return (true, null);
    }

    /// <summary>Builds actionable guidance for an unresolved dependency.</summary>
    /// <param name="dep">Missing dependency.</param>
    /// <param name="gameDirHints">Candidate game roots for compiler discovery.</param>
    /// <returns>A user-facing prompt that names attempted paths and the configuration call.</returns>
    public static string RenderMissingPrompt(ToolDependency dep, IReadOnlyList<string>? gameDirHints = null)
    {
        var info = Info(dep);
        var tried = Candidates(dep, gameDirHints).ToList();
        var lookedNote = tried.Count == 0 ? "" :
            $" houseCARL already looked for it automatically at {string.Join(" and ", tried.Select(c => $"'{c}'"))} " +
            "and didn't find it there.";
        return
            $"houseCARL needs {info.Display} for {info.Need}, but no path is set yet.{lookedNote} " +
            $"Ask the user for the {(info.IsDirectory ? "folder" : "full path to the .exe")}, then call " +
            $"housecarl_set_tool_path(tool='{info.Key}', path='<the path they give>'). " +
            $"If they don't have it: {info.WhereToGet}. " +
            "Do not guess or skip the path — the operation cannot run without it, and a wrong path " +
            "is refused loud.";
    }

    /// <summary>Enumerates legacy canonical paths for a dependency in priority order.</summary>
    /// <param name="dep">Dependency.</param>
    /// <param name="gameDirHints">Game roots used only for PapyrusCompiler candidates.</param>
    /// <returns>Candidate paths without testing their existence.</returns>
    /// <remarks>
    /// These Windows-layout candidates remain transitional. The Linux installer must not treat them as native support.
    /// BSArch has no canonical candidate.
    /// </remarks>
    static IEnumerable<string> Candidates(ToolDependency dep, IReadOnlyList<string>? gameDirHints)
    {
        switch (dep)
        {
            case ToolDependency.PapyrusCompiler:
                foreach (var g in gameDirHints ?? Array.Empty<string>())
                    if (!string.IsNullOrWhiteSpace(g))
                        yield return Path.Combine(g, "Papyrus Compiler", "PapyrusCompiler.exe");
                break;
            case ToolDependency.PapyrusLogs:
                yield return Path.Combine(MyGames, "Logs", "Script");
                break;
            case ToolDependency.CrashLogs:
                yield return Path.Combine(MyGames, "SKSE", "Crashlogs");          // Crash Logger SSE
                yield return Path.Combine(MyGames, "NetScriptFramework", "Crash"); // .NET Script Framework
                break;
            // Bsarch: user-downloaded, no canonical home — no candidates.
        }
    }

    /// <summary>Returns the first existing canonical dependency path.</summary>
    /// <param name="dep">Dependency.</param>
    /// <param name="gameDirHints">Game roots used for compiler discovery.</param>
    /// <returns>An existing candidate, or null.</returns>
    public static string? Probe(ToolDependency dep, IReadOnlyList<string>? gameDirHints = null)
    {
        bool isDir = Info(dep).IsDirectory;
        foreach (var c in Candidates(dep, gameDirHints))
            if (isDir ? Directory.Exists(c) : File.Exists(c)) return c;
        return null;
    }

    /// <summary>Resolves status without persisting an auto-detected path.</summary>
    /// <param name="dep">Dependency.</param>
    /// <param name="savedPath">Previously saved candidate.</param>
    /// <param name="gameDirHints">Game roots used for compiler discovery.</param>
    /// <returns>The usable path and its source, or an unset result.</returns>
    public static (string? path, ToolPathSource source) Inspect(
        ToolDependency dep,
        string? savedPath,
        IReadOnlyList<string>? gameDirHints = null)
    {
        if (savedPath is not null && Validate(dep, savedPath).ok) return (savedPath, ToolPathSource.Saved);
        var found = Probe(dep, gameDirHints);
        return found is not null ? (found, ToolPathSource.AutoDetected) : (null, ToolPathSource.Unset);
    }

    /// <summary>Gets the legacy Windows Skyrim documents root used by log discovery.</summary>
    static string MyGames => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Skyrim Special Edition");
}
