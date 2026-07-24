using System.Diagnostics;
using System.Text.RegularExpressions;

namespace HousecarlCore;

/// <summary>Represents one location-bearing diagnostic parsed from PapyrusCompiler stderr.</summary>
/// <param name="File">Source file path reported by the compiler.</param>
/// <param name="Line">One-based source line.</param>
/// <param name="Col">One-based source column.</param>
/// <param name="Message">Compiler message.</param>
public sealed record PapyrusDiagnostic(string File, int Line, int Col, string Message)
{
    /// <summary>Formats a compact diagnostic using only the source filename.</summary>
    /// <returns><c>name(line,column): message</c>.</returns>
    public override string ToString() => $"{System.IO.Path.GetFileName(File)}({Line},{Col}): {Message}";
}

/// <summary>Reports process execution and output production for one Papyrus compile.</summary>
/// <param name="Success">Whether this run created or updated the expected PEX.</param>
/// <param name="ObjectName">Compiled Papyrus object name.</param>
/// <param name="PexPath">Output PEX path after success.</param>
/// <param name="Diagnostics">Parsed location-bearing stderr diagnostics.</param>
/// <param name="Stdout">Captured standard output.</param>
/// <param name="Stderr">Captured standard error.</param>
/// <param name="ExitCode">Process exit code, or -1 when the process did not complete.</param>
/// <param name="RunError">Start or timeout error; null when the compiler process ran.</param>
public sealed record CompileResult(
    bool Success, string ObjectName, string? PexPath, IReadOnlyList<PapyrusDiagnostic> Diagnostics,
    string Stdout, string Stderr, int ExitCode, string? RunError)
{
    /// <summary>Gets whether the compiler process started and completed within the timeout.</summary>
    public bool Ran => RunError is null;
}

/// <summary>Runs Bethesda's external Papyrus compiler with bounded process and stream handling.</summary>
/// <remarks>
/// This transitional runner expects a directly executable compiler path. Linux v1 does not claim native compiler
/// support; structured Proton command execution is deferred. Success is output production, not exit code, because the
/// Creation Kit compiler can return zero without compiling.
/// </remarks>
public static class PapyrusCompile
{
    /// <summary>Matches the compiler's file, line, column, and message stderr format.</summary>
    static readonly Regex DiagLine = new(
        @"^(?<file>.*?)\((?<line>\d+),(?<col>\d+)\):\s*(?<msg>.*)$",
        RegexOptions.Compiled);

    // The "symbol/type could not be resolved" message fragments — the SIGNATURE of a MISSING IMPORT (a dependency's
    // Source\Scripts folder not on the import path), as opposed to a syntax error. Grounded in the REAL CK compiler's
    // wording captured against deliberately missing dependency sources:
    //   "unknown type po3_sksefunctions"                     — a declared/return/param type whose source isn't found
    //   "variable JValue is undefined"                       — a static call on a script namespace not on the path
    //   "none is not a known user-defined type"              — the cascade when an unresolved expression types to none
    //   "X is not a function or does not exist"              — an extended function whose source copy isn't found
    //                                                          (captured during the import-order PEX gate)
    // Contrast SYNTAX errors, which are NOT this class: "no viable alternative", "missing EOF", "mismatched input",
    // "unknown user flag" — a code/grammar defect the import path can't fix.
    static readonly string[] UnresolvedSymbolFragments =
    {
        "unknown type ",
        " is undefined",
        "is not a known user-defined type",
        "is not a function or does not exist",
    };

    /// <summary>Tests whether a diagnostic indicates a missing import rather than a syntax error.</summary>
    /// <param name="message">Compiler message.</param>
    /// <returns>True when a known unresolved symbol or type fragment occurs.</returns>
    public static bool IsUnresolvedSymbol(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        foreach (var frag in UnresolvedSymbolFragments)
            if (message.Contains(frag, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Parses location-bearing diagnostics from compiler stderr.</summary>
    /// <param name="stderr">Complete captured stderr.</param>
    /// <returns>Matching diagnostics in stream order; unrelated lines are ignored.</returns>
    public static IReadOnlyList<PapyrusDiagnostic> ParseDiagnostics(string? stderr)
    {
        var list = new List<PapyrusDiagnostic>();
        if (string.IsNullOrEmpty(stderr)) return list;
        foreach (var raw in stderr.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var m = DiagLine.Match(line);
            if (!m.Success) continue;
            list.Add(new PapyrusDiagnostic(
                m.Groups["file"].Value.Trim(),
                int.Parse(m.Groups["line"].Value),
                int.Parse(m.Groups["col"].Value),
                m.Groups["msg"].Value.Trim()));
        }
        return list;
    }

    /// <summary>Compiles one Papyrus object while preserving any previous output.</summary>
    /// <param name="compilerExe">Directly executable compiler path.</param>
    /// <param name="objectName">Script object name without an extension.</param>
    /// <param name="importDirs">Import directories passed as one semicolon-delimited compiler argument.</param>
    /// <param name="outputDir">Native output directory.</param>
    /// <param name="flagsFile">Compiler flags filename or path.</param>
    /// <param name="timeoutMs">Process timeout in milliseconds.</param>
    /// <returns>A result that captures start, timeout, diagnostics, streams, and output production.</returns>
    public static CompileResult CompileObject(
        string compilerExe, string objectName, IReadOnlyList<string> importDirs, string outputDir,
        string flagsFile = "TESV_Papyrus_Flags.flg", int timeoutMs = 120_000)
    {
        var pexPath = Path.Combine(outputDir, objectName + ".pex");
        // Record the prior PEX write time to distinguish this run's output from an existing successful build.
        DateTime? pexBeforeUtc = File.Exists(pexPath) ? File.GetLastWriteTimeUtc(pexPath) : null;

        var psi = new ProcessStartInfo
        {
            FileName = compilerExe,
            WorkingDirectory = Path.GetDirectoryName(compilerExe) ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(objectName);
        psi.ArgumentList.Add($"-f={flagsFile}");
        // One arg: ';' is the CK compiler's import-dir LIST separator, not a path char. .NET quotes the whole value so
        // paths-with-spaces survive; a literal ';' INSIDE a path is the one thing that does NOT (it would split that
        // path at the separator) — but import directories effectively never contain one, so it's not a real concern.
        psi.ArgumentList.Add($"-i={string.Join(";", importDirs)}");
        psi.ArgumentList.Add($"-o={outputDir}");

        Process proc;
        try { proc = Process.Start(psi)!; }
        catch (Exception ex)
        {
            return new CompileResult(false, objectName, null, Array.Empty<PapyrusDiagnostic>(), "", "", -1,
                $"could not run the Papyrus compiler at '{compilerExe}': {ex.Message}");
        }

        const int StreamDrainMs = 5000;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return new CompileResult(false, objectName, null, Array.Empty<PapyrusDiagnostic>(), "", "", -1,
                $"the Papyrus compiler did not finish within {timeoutMs / 1000}s (killed).");
        }
        // The PROCESS exited, but a grandchild inheriting the stdout/stderr pipe could keep it open and hang the stream
        // reads forever (WaitForExit(int) does NOT flush async readers, unlike the parameterless overload). Bound the
        // post-exit drain: on a stuck pipe kill the tree to force the handles closed and use what was captured rather
        // than blocking indefinitely. Output production is decided from PEX mtime, independently of the streams.
        bool drained;
        try { drained = Task.WaitAll(new Task[] { outTask, errTask }, StreamDrainMs); }
        catch { drained = false; }
        if (!drained) { try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ } }
        var stdout = outTask.IsCompletedSuccessfully ? outTask.Result : "";
        var stderr = errTask.IsCompletedSuccessfully ? errTask.Result : "";

        var diags = ParseDiagnostics(stderr);
        // Success = THIS run WROTE the .pex. Warnings are NON-FATAL: if the compiler still emitted a .pex it's "good
        // enough" and the diagnostics ride along as warnings. NOT proc.ExitCode (unreliable, measured) and NOT
        // "no diagnostics" (that wrongly failed a compiled-with-warnings build).
        bool producedNow = File.Exists(pexPath)
            && (pexBeforeUtc is null || File.GetLastWriteTimeUtc(pexPath) > pexBeforeUtc.Value);
        return new CompileResult(
            producedNow,
            objectName,
            producedNow ? pexPath : null,
            diags,
            stdout,
            stderr,
            proc.ExitCode,
            null);
    }
}
