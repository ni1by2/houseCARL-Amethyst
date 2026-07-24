using System.Text;

namespace HousecarlCore;

/// <summary>Reports high-signal paths and plugin names embedded as strings in one DLL image.</summary>
/// <param name="ConfigPaths">Distinct configuration-like strings.</param>
/// <param name="PluginRefs">Distinct plugin filenames.</param>
/// <param name="RunsScanned">Printable ASCII and UTF-16LE runs considered.</param>
/// <param name="BytesScanned">Image byte count.</param>
/// <param name="Note">Failure reason, or null after a complete scan.</param>
/// <remarks>Embedded strings describe image contents, not runtime behavior or complete dependency coverage.</remarks>
public sealed record SksePeekResult(
    IReadOnlyList<string> ConfigPaths,
    IReadOnlyList<string> PluginRefs,
    int RunsScanned,
    long BytesScanned,
    string? Note)
{
    /// <summary>Gets why the complete scan could not run, or null after success.</summary>
    public string? Note { get; init; } = Note;

    /// <summary>Gets whether the scan was refused or failed.</summary>
    public bool Failed => Note is not null;
}

/// <summary>Performs a bounded static string scan over an SKSE DLL image.</summary>
public static class SksePeek
{
    /// <summary>Maximum image size accepted for a complete string scan.</summary>
    public const long SizeCap = 64L * 1024 * 1024;

    /// <summary>Minimum printable run length, matching the conventional <c>strings(1)</c> default.</summary>
    const int MinRun = 4;

    /// <summary>Plugin extensions recognized by the classifier.</summary>
    static readonly string[] PluginExts = [".esp", ".esm", ".esl"];

    /// <summary>Configuration extensions recognized by the classifier.</summary>
    static readonly string[] ConfigExts = [".ini", ".toml", ".json", ".yaml", ".yml"];

    /// <summary>Reads and scans one DLL without retaining a file handle.</summary>
    /// <param name="filePath">Native DLL path.</param>
    /// <returns>A complete scan result or an explicit failure result.</returns>
    public static SksePeekResult Scan(string filePath)
    {
        try
        {
            var fi = new FileInfo(filePath);
            if (fi.Length > SizeCap)
                return new SksePeekResult([], [], 0, 0,
                    $"image is {fi.Length / (1024 * 1024)} MB — past the " +
                    $"{SizeCap / (1024 * 1024)} MB peek cap; NOT scanned");
            return ScanBytes(File.ReadAllBytes(filePath));
        }
        catch (Exception ex)
        {
            return new SksePeekResult([], [], 0, 0, $"could not read the image: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Scans in-memory image bytes for printable ASCII and UTF-16LE runs.</summary>
    /// <param name="bytes">Complete image bytes.</param>
    /// <returns>Filtered configuration paths and plugin references.</returns>
    public static SksePeekResult ScanBytes(ReadOnlySpan<byte> bytes)
    {
        var configs = new List<string>();
        var plugins = new List<string>();
        var seenCfg = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenPlg = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int runs = 0;

        foreach (var run in Runs(bytes))
        {
            runs++;
            // Plugin references take priority because they can also look like paths.
            // the sharper signal (it cross-checks against the load order), so it wins the classification.
            if (PluginRefIn(run) is { } p) { if (seenPlg.Add(p)) plugins.Add(p); }
            else if (IsConfigPath(run) && seenCfg.Add(run)) configs.Add(run);
        }
        return new SksePeekResult(configs, plugins, runs, bytes.Length, null);
    }

    /// <summary>Extracts printable ASCII and UTF-16LE runs at both wide-string alignments.</summary>
    /// <param name="b">Image bytes.</param>
    /// <returns>Eagerly collected runs, safe to return beyond the span lifetime.</returns>
    static IEnumerable<string> Runs(ReadOnlySpan<byte> b)
    {
        // A span can't cross an iterator boundary, so collect eagerly — bounded by SizeCap.
        var outp = new List<string>();
        var sb = new StringBuilder(64);

        // The final sentinel iteration flushes a run ending at the buffer boundary.
        for (int i = 0; i <= b.Length; i++)
        {
            if (i < b.Length && IsPrintable(b[i])) { sb.Append((char)b[i]); continue; }
            if (sb.Length >= MinRun) outp.Add(sb.ToString());
            sb.Clear();
        }

        for (int align = 0; align < 2; align++)
        {
            sb.Clear();
            for (int i = align; i <= b.Length; i += 2)
            {
                if (i + 1 < b.Length && IsPrintable(b[i]) && b[i + 1] == 0) { sb.Append((char)b[i]); continue; }
                if (sb.Length >= MinRun) outp.Add(sb.ToString());
                sb.Clear();
            }
        }
        return outp;
    }

    /// <summary>Tests whether a byte is printable seven-bit ASCII.</summary>
    /// <param name="c">Byte to inspect.</param>
    /// <returns>True for bytes from space through tilde.</returns>
    static bool IsPrintable(byte c) => c is >= 0x20 and < 0x7F;

    /// <summary>Extracts a strict plugin filename from the end of a printable run.</summary>
    /// <param name="run">Printable string.</param>
    /// <returns>The filename without directories, or null when the run is ambiguous or templated.</returns>
    /// <remarks>
    /// The strict classifier prevents log-format templates from becoming false missing-plugin alarms.
    /// </remarks>
    static string? PluginRefIn(string run)
    {
        if (!PluginExts.Any(e => run.EndsWith(e, StringComparison.OrdinalIgnoreCase))) return null;
        int cut = run.LastIndexOfAny(['\\', '/']);
        string name = cut >= 0 ? run[(cut + 1)..] : run;
        if (name.Length <= 4) return null;                        // A bare extension is not a filename.
        // A plugin filename is a filename: a run carrying quotes/separators past the last slash is a sentence about a
        // plugin, not the name of one. Bethesda names allow spaces, dashes, apostrophes, parens — so the shape check is
        // this forbidden-char set, and it must carry BOTH format-string dialects:
        //   %  → printf ("%s.esp")
        //   {} → fmt / spdlog / std::format ("{}.esp", "loading {}.esp") — the DOMINANT modern shape, because
        //        CommonLibSSE-NG plugins log through spdlog. Missing these would adjudicate a log template against the
        //        load order and flag it ABSENT on every healthy install.
        bool invalid = name.Any(
            ch => ch is '"' or '\'' or '<' or '>' or '|' or '*' or '?' or ':' or '%' or '{' or '}');
        return invalid ? null : name;
    }

    /// <summary>Tests whether a run is suggestive of an embedded configuration path.</summary>
    /// <param name="run">Printable string.</param>
    /// <returns>True for supported extensions or paths under Data/SKSE Plugins after noise filtering.</returns>
    static bool IsConfigPath(string run)
    {
        bool hasCfgExt = ConfigExts.Any(e => run.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        bool underData = run.Contains("Data\\", StringComparison.OrdinalIgnoreCase)
                      || run.Contains("Data/", StringComparison.OrdinalIgnoreCase)
                      || run.Contains("SKSE\\Plugins", StringComparison.OrdinalIgnoreCase)
                      || run.Contains("SKSE/Plugins", StringComparison.OrdinalIgnoreCase);
        if (!hasCfgExt && !underData) return false;
        // Drop the compiler's own noise: a bare extension, and the C++ type/format soup that trips the extension test
        // ("%s.json", "basic_string<...>.ini"). A real config path has a separator or is a plain filename.
        if (run.Length <= 5) return false;
        // NOTE the asymmetry with PluginRefIn, which also rejects fmt's {} placeholder: a {}-bearing path is a TEMPLATE
        // the DLL fills in, and it is still useful configuration-surface evidence.
        // this plugin reads Address Library, which is worth showing. A config path is only ever SHOWN, so a template
        // costs nothing; a plugin name is ADJUDICATED, so a template would become a false "NOT in your load order".
        return !run.Contains('%') && !run.Contains('<') && !run.Contains('"');
    }
}
