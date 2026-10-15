using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace HousecarlCore;

/// <summary>One compiler diagnostic parsed off PapyrusCompiler's stderr; <see cref="ToString"/> renders it as "name(line,col): message" with the file's basename.</summary>
public sealed record PapyrusDiagnostic(string File, int Line, int Col, string Message)
{
    public override string ToString() => $"{System.IO.Path.GetFileName(File)}({Line},{Col}): {Message}";
}

/// <summary>The outcome of one compile; what <see cref="RunError"/> and <see cref="Success"/> each mean is in docs/architecture/papyrus.md.</summary>
public sealed record CompileResult(
    bool Success, string ObjectName, string? PexPath, IReadOnlyList<PapyrusDiagnostic> Diagnostics,
    string Stdout, string Stderr, int ExitCode, string? RunError)
{
    public bool Ran => RunError is null;
}

/// <summary>One isolated Champollion run and the exact PSC files it produced.</summary>
public sealed record ChampollionResult(bool Success, IReadOnlyList<string> Sources, string Stdout, string Stderr,
    int ExitCode, string? RunError, string? Version);

/// <summary>Drives the Creation Kit's PapyrusCompiler.exe as a subprocess to compile a .psc to a .pex; the invocation
/// and the success rule it models are in docs/architecture/papyrus.md.</summary>
public static class PapyrusCompile
{
    static readonly Regex DiagLine = new(@"^(?<file>.*?)\((?<line>\d+),(?<col>\d+)\):\s*(?<msg>.*)$", RegexOptions.Compiled);
    static readonly Regex CapricaDiagLine = new(@"^(?<file>.*?)\s+\((?<line>\d+),\s*(?<col>\d+):\d+\):\s*(?<msg>.*)$", RegexOptions.Compiled);

    // The compiler's own "symbol/type could not be resolved" wording, which a syntax error never uses.
    static readonly string[] UnresolvedSymbolFragments =
    {
        "unknown type ",
        " is undefined",
        "is not a known user-defined type",
        "is not a function or does not exist",
    };

    /// <summary>True if a diagnostic is the missing-import class rather than a syntax error, case-insensitively; pinned
    /// by <c>compile-ergonomics-guard</c>.</summary>
    public static bool IsUnresolvedSymbol(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        foreach (var frag in UnresolvedSymbolFragments)
            if (message.Contains(frag, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Parse Creation Kit or Caprica diagnostics, ignoring non-diagnostic banner and progress lines.</summary>
    public static IReadOnlyList<PapyrusDiagnostic> ParseDiagnostics(string? stderr)
    {
        var list = new List<PapyrusDiagnostic>();
        if (string.IsNullOrEmpty(stderr)) return list;
        foreach (var raw in stderr.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var m = DiagLine.Match(line);
            if (!m.Success) m = CapricaDiagLine.Match(line);
            if (!m.Success) continue;
            list.Add(new PapyrusDiagnostic(
                m.Groups["file"].Value.Trim(),
                int.Parse(m.Groups["line"].Value),
                int.Parse(m.Groups["col"].Value),
                m.Groups["msg"].Value.Trim()));
        }
        return list;
    }

    /// <summary>Compile ONE object to a .pex in <paramref name="outputDir"/>, non-destructively and without throwing;
    /// the success signal and the stream handling are in docs/architecture/papyrus.md.</summary>
    public static CompileResult CompileObject(
        string compilerExe, string objectName, IReadOnlyList<string> importDirs, string outputDir,
        string flagsFile = "TESV_Papyrus_Flags.flg", int timeoutMs = 120_000, SteamProtonSelection? proton = null)
    {
        var pexPath = Path.Combine(outputDir, objectName + ".pex");
        if (File.Exists(pexPath))
            return new CompileResult(false, objectName, null, Array.Empty<PapyrusDiagnostic>(), "", "", -1,
                $"refusing to compile because '{pexPath}' already exists; use a newly created empty output directory so stale output cannot be mistaken for this run.");
        if (OperatingSystem.IsLinux() && proton is null)
            return new CompileResult(false, objectName, null, Array.Empty<PapyrusDiagnostic>(), "", "", -1,
                WindowsProgramGuard.Refusal(compilerExe, "PapyrusCompiler.exe") ?? "Creation Kit execution on Linux requires a selected Proton runner.");
        if (OperatingSystem.IsWindows() && WindowsProgramGuard.Refusal(compilerExe, "PapyrusCompiler.exe") is { } refusal)
            return new CompileResult(false, objectName, null, Array.Empty<PapyrusDiagnostic>(), "", "", -1, refusal);
        if (FindSemicolonImport(importDirs) is { } badImport)
            return new CompileResult(false, objectName, null, Array.Empty<PapyrusDiagnostic>(), "", "", -1,
                $"Papyrus import directory '{badImport}' contains ';', which the compiler treats as an import-path separator; refusing to split this path silently.");

        var effectiveFlags = proton?.TranslatePath(flagsFile) ?? flagsFile;
        var effectiveImports = proton is null ? importDirs : importDirs.Select(proton.TranslatePath).ToArray();
        var effectiveOutput = proton?.TranslatePath(outputDir) ?? outputDir;
        var effectiveCompiler = proton?.TranslatePath(compilerExe) ?? compilerExe;
        var psi = new ProcessStartInfo
        {
            FileName = effectiveCompiler,
            WorkingDirectory = Path.GetDirectoryName(compilerExe) ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(objectName);
        psi.ArgumentList.Add($"-f={effectiveFlags}");
        // One arg: ';' is the compiler's import-dir list separator, and .NET quotes the whole value.
        psi.ArgumentList.Add($"-i={string.Join(";", effectiveImports)}");
        psi.ArgumentList.Add($"-o={effectiveOutput}");
        if (proton is not null) proton.Configure(psi);

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
        // The post-exit drain is bounded, because a grandchild holding the pipe would hang the stream reads.
        bool drained; try { drained = Task.WaitAll(new Task[] { outTask, errTask }, StreamDrainMs); } catch { drained = false; }
        if (!drained) { try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ } }
        var stdout = outTask.IsCompletedSuccessfully ? outTask.Result : "";
        var stderr = errTask.IsCompletedSuccessfully ? errTask.Result : "";

        var diags = ParseDiagnostics(stderr);
        // Success = THIS run WROTE the .pex; warnings ride along.
        bool producedNow = File.Exists(pexPath);
        return new CompileResult(producedNow, objectName, producedNow ? pexPath : null, diags, stdout, stderr, proc.ExitCode, null);
    }

    /// <summary>Run Caprica with an explicit Skyrim target, flags, imports and isolated config/output paths.</summary>
    public static CompileResult CompileCaprica(string executable, string sourcePath, string objectName,
        IReadOnlyList<string> importDirs, string flagsFile, string outputDir, string privateConfigFile, int timeoutMs = 120_000)
    {
        if (!OperatingSystem.IsLinux())
            return new(false, objectName, null, Array.Empty<PapyrusDiagnostic>(), "", "", -1, "Caprica backend is supported only on Linux.");
        if (!File.Exists(flagsFile))
            return new(false, objectName, null, Array.Empty<PapyrusDiagnostic>(), "", "", -1,
                $"the selected Skyrim Papyrus flags file is missing: '{flagsFile}'");
        if (FindSemicolonImport(importDirs) is { } badImport)
            return new(false, objectName, null, Array.Empty<PapyrusDiagnostic>(), "", "", -1,
                $"Papyrus import directory '{badImport}' contains ';', which Caprica treats as an import-path separator; refusing to split this path silently.");
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = outputDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(Path.GetFullPath(sourcePath));
        psi.ArgumentList.Add("--game=skyrim");
        psi.ArgumentList.Add("--ignorecwd");
        psi.ArgumentList.Add("--enable-language-extensions=false");
        psi.ArgumentList.Add("--config-file=" + Path.GetFullPath(privateConfigFile));
        psi.ArgumentList.Add("--flags=" + Path.GetFullPath(flagsFile));
        psi.ArgumentList.Add("--import=" + string.Join(";", importDirs.Select(Path.GetFullPath)));
        psi.ArgumentList.Add("--output=" + Path.GetFullPath(outputDir));
        Process proc;
        try { proc = Process.Start(psi)!; }
        catch (Exception ex)
        { return new(false, objectName, null, Array.Empty<PapyrusDiagnostic>(), "", "", -1, $"could not run Caprica at '{executable}': {ex.Message}"); }
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return new(false, objectName, null, Array.Empty<PapyrusDiagnostic>(), "", "", -1,
                $"Caprica did not finish within {timeoutMs / 1000}s (its process tree was killed).");
        }
        bool drained; try { drained = Task.WaitAll(new Task[] { outTask, errTask }, 5000); } catch { drained = false; }
        if (!drained) { try { proc.Kill(entireProcessTree: true); } catch { } }
        var stdout = outTask.IsCompletedSuccessfully ? outTask.Result : "";
        var stderr = errTask.IsCompletedSuccessfully ? errTask.Result : "";
        var diagnostics = ParseDiagnostics(stderr + "\n" + stdout);
        var pexPath = Path.Combine(outputDir, objectName + ".pex");
        var success = proc.ExitCode == 0 && File.Exists(pexPath) && new FileInfo(pexPath).Length > 0;
        return new(success, objectName, success ? pexPath : null, diagnostics, stdout, stderr, proc.ExitCode,
            drained ? null : "Caprica exited but its output streams could not be drained completely.");
    }

    internal static string? FindSemicolonImport(IEnumerable<string> importDirs) =>
        importDirs.FirstOrDefault(path => path.Contains(';', StringComparison.Ordinal));

    /// <summary>Caprica has no --version option; its no-argument banner is the supported version probe (exit 255 is expected).</summary>
    public static string? ProbeCapricaVersion(string executable, int timeoutMs = 5000)
    {
        if (!OperatingSystem.IsLinux()) return null;
        var psi = new ProcessStartInfo { FileName = executable, UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() };
        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                try { Task.WaitAll(new Task[] { stdout, stderr }, 1000); } catch { }
                return null;
            }
            // Process exit and completion of the async pipe readers are separate events. Drain briefly so a fast
            // banner is not lost to a scheduling race; a child holding either pipe cannot stall the probe forever.
            try { Task.WaitAll(new Task[] { stdout, stderr }, 1000); } catch { }
            var banner = (stdout.IsCompletedSuccessfully ? stdout.Result : "") + "\n" + (stderr.IsCompletedSuccessfully ? stderr.Result : "");
            var match = Regex.Match(banner, @"Caprica Papyrus Compiler\s+(v?\d+(?:\.\d+)+)");
            return match.Success ? match.Groups[1].Value : null;
        }
        catch { return null; }
    }

    /// <summary>Run Champollion once against a prevalidated PEX, with only an explicit PSC destination.</summary>
    public static ChampollionResult DecompileChampollion(string executable, string inputPex, IReadOnlyList<string> objectNames,
        string outputDir, int timeoutMs = 120_000)
    {
        if (!OperatingSystem.IsLinux())
            return new(false, Array.Empty<string>(), "", "", -1, "Champollion backend is supported only on Linux.", null);
        if (objectNames.Count == 0 || objectNames.Any(name => !IsPlainScriptName(name)) ||
            objectNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != objectNames.Count)
            return new(false, Array.Empty<string>(), "", "", -1,
                "the PEX has no script objects or an object name is not a safe plain script basename; Champollion was not launched.", null);
        var psi = new ProcessStartInfo { FileName = executable, WorkingDirectory = outputDir, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        psi.ArgumentList.Add("--psc");
        psi.ArgumentList.Add(Path.GetFullPath(outputDir));
        psi.ArgumentList.Add(Path.GetFullPath(inputPex));
        Process proc;
        try { proc = Process.Start(psi)!; }
        catch (Exception ex)
        { return new(false, Array.Empty<string>(), "", "", -1, $"could not run Champollion at '{executable}': {ex.Message}", null); }
        using (proc)
        {
            var stdoutTask = ReadBoundedAsync(proc.StandardOutput, 1_000_000);
            var stderrTask = ReadBoundedAsync(proc.StandardError, 1_000_000);
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                try { Task.WaitAll(new Task[] { stdoutTask, stderrTask }, 5000); } catch { }
                return new(false, Array.Empty<string>(), stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : "",
                    stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "", -1,
                    $"Champollion did not finish within {timeoutMs / 1000}s (its process tree was killed).", null);
            }
            try { Task.WaitAll(new Task[] { stdoutTask, stderrTask }, 5000); } catch { }
            var stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : "";
            var stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "";
            string? error = !stdoutTask.IsCompletedSuccessfully || !stderrTask.IsCompletedSuccessfully
                ? "Champollion output streams could not be drained completely."
                : null;
            var expected = objectNames.Select(n => Path.GetFullPath(Path.Combine(outputDir, n + ".psc")))
                .ToHashSet(StringComparer.Ordinal);
            string[] produced;
            try { produced = Directory.EnumerateFiles(outputDir, "*", SearchOption.AllDirectories).Select(Path.GetFullPath).ToArray(); }
            catch (Exception ex) { produced = Array.Empty<string>(); error ??= $"could not validate Champollion output: {ex.Message}"; }
            if (produced.Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
                error ??= "Champollion produced a symlink/reparse-point output, which is not allowed.";
            if (produced.Length != expected.Count || produced.Any(path => !expected.Contains(path)))
                error ??= "Champollion did not produce exactly the expected source files in its private output directory.";
            foreach (var path in produced)
            {
                try
                {
                    var text = File.ReadAllText(path);
                    var declared = Regex.Match(text, @"(?im)^\s*Scriptname\s+([^\s;]+)");
                    if (!declared.Success || !objectNames.Contains(declared.Groups[1].Value, StringComparer.OrdinalIgnoreCase))
                    { error ??= $"Champollion output '{Path.GetFileName(path)}' has no matching Scriptname declaration."; break; }
                }
                catch (Exception ex) { error ??= $"could not validate Champollion source '{Path.GetFileName(path)}': {ex.Message}"; break; }
            }
            var combined = stdout + "\n" + stderr;
            if (combined.Contains("[diagnostics truncated after", StringComparison.Ordinal))
                error ??= "Champollion diagnostics exceeded the capture limit; refusing to infer that the run had no reported failures.";
            if (Regex.IsMatch(combined, @"(?im)\b(error|failed|failure)\b"))
                error ??= "Champollion reported a failure in its captured diagnostics.";
            var version = ProbeChampollionVersion(executable);
            if (proc.ExitCode != 0) error ??= $"Champollion exited with code {proc.ExitCode}.";
            return new(error is null, error is null ? produced : Array.Empty<string>(), stdout, stderr, proc.ExitCode, error, version);
        }
    }

    static bool IsPlainScriptName(string name) => !string.IsNullOrWhiteSpace(name) && name is not ("." or "..") &&
        name.IndexOfAny(new[] { '/', '\\', ':' }) < 0 && !name.Any(char.IsControl) && !name.Contains(Path.DirectorySeparatorChar) &&
        !name.Contains(Path.AltDirectorySeparatorChar);

    /// <summary>Bound memory while draining a native tool's output; excess text is consumed and explicitly marked.</summary>
    static async Task<string> ReadBoundedAsync(StreamReader reader, int maxChars)
    {
        var buffer = new char[8192];
        var output = new StringBuilder(Math.Min(maxChars, 8192));
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            var keep = Math.Min(read, Math.Max(0, maxChars - output.Length));
            if (keep > 0) output.Append(buffer, 0, keep);
            if (keep < read) truncated = true;
        }
        if (truncated) output.Append("\n[diagnostics truncated after 1000000 characters]");
        return output.ToString();
    }

    /// <summary>Champollion supports --version as a direct version probe.</summary>
    public static string? ProbeChampollionVersion(string executable, int timeoutMs = 5000)
    {
        if (!OperatingSystem.IsLinux()) return null;
        var psi = new ProcessStartInfo { FileName = executable, UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() };
        psi.ArgumentList.Add("--version");
        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var stdout = proc.StandardOutput.ReadToEndAsync(); var stderr = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(timeoutMs)) { try { proc.Kill(entireProcessTree: true); } catch { } return null; }
            try { if (!Task.WaitAll(new Task[] { stdout, stderr }, 2000)) return null; } catch { return null; }
            var text = (stdout.IsCompletedSuccessfully ? stdout.Result : "") + "\n" + (stderr.IsCompletedSuccessfully ? stderr.Result : "");
            var match = Regex.Match(text, @"(?im)Champollion.*?(\d+\.\d+(?:\.\d+)*)");
            return match.Success ? match.Groups[1].Value : null;
        }
        catch { return null; }
    }
}
