using System.Diagnostics;
using System.IO.Abstractions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

namespace HousecarlCore;

/// <summary>Describes a native archive-list operation.</summary>
/// <param name="Success">Whether the archive was read completely and passed its declared-count check.</param>
/// <param name="Format">Header-derived BSA version label, or null when the header could not be read.</param>
/// <param name="DeclaredCount">
/// File count from the header, or the enumerated count when no header count is available.
/// </param>
/// <param name="Files">Paths enumerated by Mutagen; they use the archive's internal path representation.</param>
/// <param name="Raw">Human-readable validation detail, empty when no additional detail is needed.</param>
/// <param name="RunError">Named open/read failure, or null when the operation ran far enough to assess success.</param>
public sealed record BsaListResult(
    bool Success, string? Format, int DeclaredCount, IReadOnlyList<string> Files, string Raw, string? RunError)
{
    /// <summary>Whether the archive operation ran, regardless of whether its content was valid.</summary>
    public bool Ran => RunError is null;
}

/// <summary>Describes an archive extract or pack operation.</summary>
/// <param name="Success">Whether the requested output was proven complete.</param>
/// <param name="Raw">Human-readable operation output or partial-write detail.</param>
/// <param name="RunError">
/// Launch/open failure, or null when the operation ran and produced an assessable result.
/// </param>
public sealed record BsaResult(bool Success, string Raw, string? RunError)
{
    /// <summary>Whether the operation ran; success is reported separately by <see cref="Success"/>.</summary>
    public bool Ran => RunError is null;
}

/// <summary>
/// Lists and extracts BSAs through Mutagen's native reader and retains the guarded BSArch packing seam.
/// </summary>
/// <remarks>
/// Listing and extraction are Linux-native and require no external command. Extraction validates destination
/// containment, bounds individual allocations, and reports partial writes. Mutagen exposes no archive writer, so
/// packing remains deferred from the v1 product until a structured Proton command runner replaces direct executable
/// configuration. Compressed BSAs can break streamed sound and voice assets; uncompressed packing is the safe default.
/// </remarks>
public static class BsaArchive
{
    /// <summary>Filesystem adapter supplied to Mutagen's archive reader.</summary>
    static readonly IFileSystem Fs = new FileSystem();

    /// <summary>Largest declared entry size accepted for an in-process allocation.</summary>
    const long MaxEntryBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Lists an archive through Mutagen and cross-checks the header's declared file count.
    /// On an unreadable, corrupt, or non-archive file the failure is
    /// surfaced in <see cref="BsaListResult.RunError"/> (Ran=false); a count mismatch surfaces as Ran-but-not-Success
    /// with the discrepancy in <see cref="BsaListResult.Raw"/> — never a silent empty list.</summary>
    /// <param name="archive">Native path to the BSA to inspect.</param>
    /// <returns>Parsed archive metadata and a named read error when the archive is unusable.</returns>
    public static BsaListResult List(string archive)
    {
        // This header read is independent because IArchiveReader does not expose declared counts.
        var hdr = ReadBsaHeader(archive);
        IArchiveReader reader;
        try { reader = Archive.CreateReader(GameRelease.SkyrimSE, archive, Fs); }
        catch (Exception ex)
        {
            return new BsaListResult(false, null, 0, Array.Empty<string>(), "", OpenError(archive, ex));
        }
        try
        {
            var files = reader.Files.Select(f => f.Path).ToList();
            if (hdr is { fileCount: var declared } && declared != (uint)files.Count)
                return new BsaListResult(
                    false,
                    VersionLabel(hdr),
                    (int)declared,
                    files,
                    $"'{Path.GetFileName(archive)}': header declares {declared} file(s) " +
                    $"but the reader enumerated {files.Count} — the archive may be corrupt or unsupported.",
                    null);
            return new BsaListResult(
                true,
                VersionLabel(hdr),
                hdr is { fileCount: var c } ? (int)c : files.Count,
                files,
                "",
                null);
        }
        catch (Exception ex)
        {
            return new BsaListResult(false, null, 0, Array.Empty<string>(), "",
                $"could not read the file list of '{Path.GetFileName(archive)}' ({ex.GetType().Name}: {ex.Message}).");
        }
        finally { (reader as IDisposable)?.Dispose(); }
    }

    /// <summary>Unpacks the whole archive into <paramref name="destFolder"/> through Mutagen. Each file's
    /// DECOMPRESSED bytes are written to dest/{file path}. Writes are:
    ///   • path-traversal-guarded — an entry resolving outside the dest refuses loud (Q3), never writes out-of-tree;
    ///   • content-aware/idempotent — a file already present byte-identical is skipped, so re-extracting into a
    ///     populated dest reports "already present" rather than a spurious rewrite (this is why the managed flow's
    ///     pre-seeded meta.ini marker is left untouched).
    /// An archive that cannot be opened or read fails with a named reason.</summary>
    /// <param name="archive">Native path to the BSA to extract.</param>
    /// <param name="destFolder">Destination directory, created when absent.</param>
    /// <returns>Content-aware extraction result and any named archive-read failure.</returns>
    public static BsaResult Unpack(string archive, string destFolder)
    {
        Directory.CreateDirectory(destFolder);
        var hdr = ReadBsaHeader(archive);
        IArchiveReader reader;
        try { reader = Archive.CreateReader(GameRelease.SkyrimSE, archive, Fs); }
        catch (Exception ex) { return new BsaResult(false, "", OpenError(archive, ex)); }

        string destFull = Path.GetFullPath(destFolder);
        int written = 0, already = 0;
        try
        {
            foreach (var f in reader.Files)
            {
                if (f.Size > MaxEntryBytes)   // corrupt/hostile header — refuse loud rather than OOM the server (Q3)
                    return new BsaResult(
                        false,
                        $"archive entry '{f.Path}' declares {f.Size:N0} bytes, " +
                        $"over the {MaxEntryBytes:N0}-byte safety ceiling — refusing to read it in-process " +
                        "(the archive header may be corrupt).",
                        null);
                string outPath = Path.GetFullPath(Path.Combine(destFull, f.Path));
                if (!IsUnder(destFull, outPath))
                    return new BsaResult(
                        false,
                        $"archive entry '{f.Path}' resolves outside the destination folder " +
                        $"(path traversal) — refusing after {written} file(s).",
                        null);
                byte[] body = f.GetBytes();
                if (SameOnDisk(outPath, body)) { already++; continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                File.WriteAllBytes(outPath, body);
                written++;
            }
        }
        catch (Exception ex)
        {
            return new BsaResult(
                false,
                $"failed while extracting '{Path.GetFileName(archive)}' " +
                $"({ex.GetType().Name}: {ex.Message}); {written} file(s) written before the error.",
                null);
        }
        finally { (reader as IDisposable)?.Dispose(); }

        int total = written + already;
        // Cross-check against the header's own count. If the reader enumerated FEWER files than the archive declares
        // (a mis-parse down to zero being the worst case), the old BSArch provenance would have caught it — fail loud
        // here rather than report a partial/empty extract as success (Q3, #217's silent-wrong-output class).
        if (hdr is { fileCount: var declared } && declared != (uint)total)
            return new BsaResult(
                false,
                $"extracted {total} file(s) from '{Path.GetFileName(archive)}' " +
                $"but its header declares {declared} — the archive may be corrupt or unsupported; " +
                "refusing to report it as success.",
                null);

        string note = written > 0
            ? $"extracted {written} file(s)" + (already > 0 ? $" ({already} already present byte-identical)" : "") + "."
            : already > 0 ? $"all {already} file(s) were already present byte-identical — nothing to extract."
                          : "the archive contained no files.";
        return new BsaResult(true, note, null);
    }

    /// <summary>Formats a named archive-open failure without exposing a stack trace.</summary>
    /// <param name="archive">Native path that could not be opened.</param>
    /// <param name="ex">Underlying reader exception.</param>
    /// <returns>Concise diagnostic suitable for an MCP response.</returns>
    static string OpenError(string archive, Exception ex) =>
        $"could not open '{Path.GetFileName(archive)}' as a Bethesda archive ({ex.GetType().Name}: {ex.Message}). " +
        "Is it a real .bsa (not a .ba2 / renamed file), and not truncated?";

    /// <summary>Reads the independent version and count fields from the 24-byte BSA header.</summary>
    /// <param name="archive">Native path to inspect without Mutagen.</param>
    /// <returns>Header values, or null when the file is unreadable, short, or lacks the BSA signature.</returns>
    static (uint version, uint folderCount, uint fileCount)? ReadBsaHeader(string archive)
    {
        try
        {
            using var fs = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
            var h = new byte[24];
            if (fs.Read(h, 0, 24) < 24) return null;
            if (h[0] != 0x42 || h[1] != 0x53 || h[2] != 0x41 || h[3] != 0x00) return null;   // not "BSA\0"
            static uint U(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
            return (U(h, 4), U(h, 16), U(h, 20));   // version @4, folderCount @16, fileCount @20
        }
        catch { return null; }
    }

    /// <summary>Converts a parsed header version into a user-facing archive label.</summary>
    /// <param name="hdr">Previously read header values, or null.</param>
    /// <returns>A known or numeric version label, or null when no header was available.</returns>
    static string? VersionLabel((uint version, uint folderCount, uint fileCount)? hdr) => hdr?.version switch
    {
        null => null,
        103 => "BSA v103 (Oblivion)",
        104 => "BSA v104 (Skyrim LE / Fallout 3 / NV)",
        105 => "BSA v105 (Skyrim SE/AE)",
        var v => $"BSA v{v}",
    };

    /// <summary>Checks whether one normalized path is strictly below a normalized destination root.</summary>
    /// <param name="root">Full destination directory path.</param>
    /// <param name="candidate">Full candidate output path after resolving traversal segments.</param>
    /// <returns>True only when <paramref name="candidate"/> is a child of <paramref name="root"/>.</returns>
    static bool IsUnder(string root, string candidate)
    {
        root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Checks whether a file already contains the exact proposed bytes.</summary>
    /// <param name="path">Native file path to compare.</param>
    /// <param name="body">Proposed content.</param>
    /// <returns>True on byte equality; false for absence, size/content difference, or any read failure.</returns>
    static bool SameOnDisk(string path, byte[] body)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length != body.Length) return false;
            return File.ReadAllBytes(path).AsSpan().SequenceEqual(body);
        }
        catch { return false; }
    }

    /// <summary>Packs a directory into a BSA with the external BSArch utility.</summary>
    /// <remarks>
    /// This deferred compatibility path writes to a sibling scratch archive and replaces the requested target only
    /// after BSArch exits successfully and produces a non-empty file written during this call. A stuck scratch file,
    /// timeout, launch failure, or invalid output leaves any existing target untouched. The caller must also warn that
    /// compressed archives cannot safely contain sound or voice files.
    /// </remarks>
    /// <param name="bsarchExe">Configured BSArch executable or future structured-runner target.</param>
    /// <param name="srcFolder">Directory tree to package.</param>
    /// <param name="archive">Final BSA path; replaced only after a proven successful pack.</param>
    /// <param name="formatFlag">Validated BSArch game-format flag such as <c>-sse</c>.</param>
    /// <param name="compress">Whether to request BSArch compression.</param>
    /// <param name="timeoutMs">Positive process timeout in milliseconds.</param>
    /// <returns>Provenance-checked success, combined output, and any process-launch failure.</returns>
    public static BsaResult Pack(
        string bsarchExe,
        string srcFolder,
        string archive,
        string formatFlag,
        bool compress,
        int timeoutMs = 600_000)
    {
        // Keep the BSA extension because BSArch uses it to select archive behavior.
        var dir = Path.GetDirectoryName(archive) ?? Environment.CurrentDirectory;
        var tmp = Path.Combine(dir, Path.GetFileNameWithoutExtension(archive) + ".houseCARL-tmp.bsa");
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* checked next — a stuck scratch refuses loud */ }
        if (File.Exists(tmp))
            // A stale scratch from a previous run that we cannot remove: packing over it would let
            // "tmp exists and is non-empty" pass on the PREVIOUS run's bytes when BSArch fails this
            // run — a false success that ships wrong content over the target (2026-06-12 adversarial
            // hunt). Refuse loud instead; nothing is packed, the prior archive is untouched.
            return new BsaResult(false, "",
                $"a stale houseCARL scratch from a previous run is stuck at '{tmp}' and could not be removed " +
                "(another process may hold it). Delete it and retry — this run packed nothing; " +
                "the existing archive, if any, is untouched.");
        var baselineUtc = DateTime.UtcNow;

        var args = new List<string> { "pack", srcFolder, tmp, formatFlag, "-mt" };
        if (compress) args.Add("-z");
        var run = Run(bsarchExe, args, timeoutMs);

        // Provenance: THIS run must have written the scratch (mtime at/after the pre-run baseline) —
        // existence alone proved nothing about who made it.
        bool packed = run.runError is null && File.Exists(tmp) && new FileInfo(tmp).Length > 0
                      && File.GetLastWriteTimeUtc(tmp) >= baselineUtc;
        if (!packed)   // BSArch couldn't run, or produced no/empty output — leave any prior archive untouched
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
            return new BsaResult(false, (run.stdout + "\n" + run.stderr).Trim(), run.runError);
        }

        // Commit performs the same-volume atomic replacement only after the scratch has passed every check.
        try { AtomicFile.Commit(tmp, archive); }
        catch (Exception ex)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
            var output = run.stdout + "\n" + run.stderr +
                         $"\ncould not place the packed archive at '{archive}': {ex.Message}";
            return new BsaResult(false, output.Trim(), null);
        }
        var success = File.Exists(archive) && new FileInfo(archive).Length > 0;
        return new BsaResult(success, (run.stdout + "\n" + run.stderr).Trim(), null);
    }

    /// <summary>The legal format tokens, for refusal messages.</summary>
    public const string FormatTokens =
        "sse (default), tes3/morrowind, tes4/oblivion, fo3, fnv, tes5/le/skyrimle, " +
        "fo4, fo4dds, sf1/starfield, sf1dds";

    /// <summary>Maps a user-facing archive format token to its exact BSArch flag.</summary>
    /// <remarks>
    /// Null, empty, and Skyrim SE aliases select <c>-sse</c>. Unknown input returns null so callers can fail with the
    /// supported values instead of silently creating an archive for the wrong game.
    /// </remarks>
    /// <param name="format">User-facing format name or alias.</param>
    /// <returns>The exact BSArch flag, or null when the token is unsupported.</returns>
    public static string? TryFormatFlag(string? format) => (format?.Trim().ToLowerInvariant()) switch
    {
        null or "" or "sse" or "ae" or "skyrimse" => "-sse",
        "tes3" or "morrowind" => "-tes3",
        "tes4" or "oblivion" => "-tes4",
        "fo3" => "-fo3",
        "fnv" => "-fnv",
        "tes5" or "le" or "skyrimle" => "-tes5",
        "fo4" => "-fo4",
        "fo4dds" => "-fo4dds",
        "sf1" or "starfield" => "-sf1",
        "sf1dds" => "-sf1dds",
        _ => null,
    };

    /// <summary>Runs BSArch without a shell and bounds both execution and post-exit stream draining.</summary>
    /// <param name="exe">Executable path passed directly to <see cref="ProcessStartInfo"/>.</param>
    /// <param name="args">Already separated argument values; no command string is constructed.</param>
    /// <param name="timeoutMs">Maximum execution time before the process tree is killed.</param>
    /// <returns>Launch state, exit code, captured streams, and a named execution error when applicable.</returns>
    static (bool ran, int exit, string stdout, string stderr, string? runError) Run(
        string exe,
        IReadOnlyList<string> args,
        int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);   // one arg each; .NET quotes spaces/semicolons

        Process p;
        try { p = Process.Start(psi)!; }
        catch (Exception ex) { return (false, -1, "", "", $"could not run BSArch at '{exe}': {ex.Message}"); }

        const int StreamDrainMs = 5000;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return (false, -1, "", "", $"BSArch did not finish within {timeoutMs / 1000}s (killed).");
        }
        // The PROCESS exited, but a grandchild that inherited the stdout/stderr pipe could keep it open and hang the
        // stream reads forever (WaitForExit(int) does NOT flush async readers, unlike the parameterless overload).
        // Bound the post-exit drain: on a stuck pipe kill the tree to force the inherited handles closed and report
        // what was captured rather than blocking indefinitely (Q3 — a bounded, named degradation, never a hang).
        bool drained; try { drained = Task.WaitAll(new Task[] { o, e }, StreamDrainMs); } catch { drained = false; }
        if (!drained) { try { p.Kill(entireProcessTree: true); } catch { /* already gone */ } }
        var stdout = o.IsCompletedSuccessfully ? o.Result : "";
        var stderr = e.IsCompletedSuccessfully ? e.Result : "";
        return drained
            ? (true, p.ExitCode, stdout, stderr, null)
            : (true, p.ExitCode, stdout, stderr,
               $"BSArch exited but its output did not drain within {StreamDrainMs / 1000}s " +
               "(a child process may still hold the pipe) — captured output may be truncated.");
    }
}
