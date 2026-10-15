using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using ModelContextProtocol.Server;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Pex;

namespace HousecarlMcp;

/// <summary>housecarl_decompile_script — reconstructs Papyrus source from a .pex into a patch folder's Source\Scripts or the <c>out_path=</c> folder; contracts in docs/architecture/papyrus-decompile.md.</summary>
[McpServerToolType]
public static class DecompileTools
{
    [McpServerTool(Name = ToolNames.DecompileScript, Title = "Decompile a compiled Papyrus script (.pex → .psc)"),
     Description(
         "Decompile a compiled Papyrus script (.pex) back to source (.psc). The .psc lands in Source\\Scripts of a new " +
         "houseCARL staging folder for review and activation/deployment, or in a folder you name with out_path= (for a look without a mod folder). Originals " +
         "are untouched and an existing .psc is never overwritten. The default folder lane needs a configured MO2 instance or Amethyst connection, " +
         "except with out_path=; needs no compiler or external tool.\n\n" +
         "Names, types, properties, states, events and docstrings survive; control flow is reconstructed and proven. Losses " +
         "every decompiler shares: .pex has no parameter defaults, so a '= None' default comes back only where a call in the " +
         "same script omitted that argument, and comments and blank-line layout are gone. A script built by an optimizing " +
         "compiler (Caprica) decompiles to correct source that will not recompile byte-identical under the CK compiler; the " +
         "result notes it when it detects optimizer patterns, but a result without the note does not prove the .pex came " +
         "from the CK compiler. A function that cannot be proven is emitted as a failure comment with its raw bytecode, so " +
         "that .psc will not compile as is. When part of the class hierarchy could not be read, the result says so.")]
    public static string DecompileScript(
        LoadOrderService svc,
        [Description("Full path to the .pex to decompile. For a script inside a BSA, run " + ToolNames.BsaExtract + " first and pass the extracted path.")]
            string pex,
        [Description("Optional. Base name for the new houseCARL staging folder the .psc lands in (default 'houseCARL_Scripts'); auto-suffixed if taken, so each call makes its own folder. To keep compile and decompile output together, add to one folder with into= (e.g. into='houseCARL_Scripts').")]
            string? patch = null,
        [Description("Optional. An existing houseCARL staging folder to add the .psc into instead of a fresh folder: its plugin filename, if present, or its mod-folder name. A script folder holds no plugin: pass its base name (into='houseCARL_Scripts' for the default) or its mod-folder name.")]
            string? into = null,
        [Description("Optional. Absolute path to a folder of your choosing, created if missing; the .psc is written straight into it and houseCARL never deletes it. patch= and into= are then ignored.")]
            string? out_path = null,
        [Description("Optional backend: 'builtin' (default) or 'champollion'. The external backend is Linux-only, isolated, and reports its version and reconstruction limits.")]
            string? decompiler_backend = null,
        ToolPathResolver? bridge = null) => Guard.Tool(ToolNames.DecompileScript, () =>
    {
        var selectedBackend = string.IsNullOrWhiteSpace(decompiler_backend) ? "builtin" : decompiler_backend.Trim().ToLowerInvariant();
        if (selectedBackend is not ("builtin" or "champollion"))
            return "error: decompiler_backend must be 'builtin' or 'champollion'; no output was written.";
        if (selectedBackend == "champollion" && !OperatingSystem.IsLinux())
            return "error: the Champollion backend is supported only on Linux; no output was written.";
        string? champollionPath = null;
        if (selectedBackend == "champollion")
        {
            if (bridge is null) return "error: tool-path configuration is unavailable, so Champollion cannot be selected.";
            var prompt = bridge.RequireOrPrompt(ToolDependency.Champollion, out champollionPath);
            if (prompt is not null) return prompt;
        }
        // 1) lane: out_path= supersedes patch=/into= saying so; the note is APPENDED, so a refusal opens with "error:".
        bool chosenOutput = !string.IsNullOrWhiteSpace(out_path);
        bool ignoredLane = chosenOutput && (!string.IsNullOrWhiteSpace(patch) || !string.IsNullOrWhiteSpace(into));
        string LaneNoteOk() => ignoredLane
            ? "\nnote: out_path= was given, so patch=/into= are ignored (the .psc lands in out_path, not a houseCARL patch folder)."
            : "";
        // On a refusal the note claims no destination, and says "nothing was written" only where the sentence does not.
        string LaneNoteFail(bool sayNothingWritten = true) => ignoredLane
            ? "\nnote: out_path= was given, so patch=/into= were ignored" + (sayNothingWritten ? "; nothing was written." : ".")
            : "";

        // 2) the instance, which the default lane needs and out_path= runs without, all it adds being the top-up.
        if (!chosenOutput && svc.ConfigPromptOrNull() is { } cfgPrompt) return cfgPrompt;
        if (!chosenOutput && LaneSentences.PatchIntoFolderRefusal(patch, into, ".psc") is { } patchInto)
            return "error: " + patchInto;

        // 3) validate the pex path.
        if (string.IsNullOrWhiteSpace(pex))
            return "error: no pex given. Pass pex= the full path to the .pex file to decompile." + LaneNoteFail();
        pex = pex.Trim().Trim('"');
        if (!File.Exists(pex))
            return $"error: no such file: '{pex}'. Pass the full path to the .pex (for a BSA member, {ToolNames.BsaExtract} it first)." + LaneNoteFail();
        if (!pex.EndsWith(".pex", StringComparison.OrdinalIgnoreCase))
            return $"error: '{Path.GetFileName(pex)}' is not a .pex compiled script." + LaneNoteFail();
        pex = Path.GetFullPath(pex);

        // 4) read the pex via Mutagen; an unreadable one fails loud naming the file.
        PexFile pexFile;
        byte[] pexBytes;
        try
        {
            var saved = AmethystSnapshotContext.Current;
            var capturedPex = saved?.CapturedInput(pex);
            if (saved is not null && capturedPex is null)
                return "error: the requested PEX was not captured into the Amethyst saved snapshot; refusing to decompile an unpinned input." + LaneNoteFail();
            pexBytes = capturedPex ?? File.ReadAllBytes(pex);
            using var pexStream = new MemoryStream(pexBytes, writable: false);
            pexFile = PexFile.CreateFromStream(pexStream, GameCategory.Skyrim);
        }
        catch (Exception ex)
        {
            return $"error: Mutagen cannot read '{Path.GetFileName(pex)}' ({ex.GetType().Name}: {ex.Message}). " +
                   "This is the known unreadable-pex class (corrupt, non-standard, or obfuscated) — no output was written."
                   + LaneNoteFail(sayNothingWritten: false);
        }
        if (pexFile.Objects.Count == 0)
            return $"error: '{Path.GetFileName(pex)}' contains no script objects — no output was written."
                   + LaneNoteFail(sayNothingWritten: false);

        // 5) output folder, resolved before the hierarchy build so a folder error costs nothing.
        OutputLocations.RiderFolder rf;
        try
        {
            rf = chosenOutput
                ? OutputLocations.ResolveExplicitSourceFolder(out_path!)
                : svc.ResolveDecompiledSourceFolder(patch, into);
        }
        catch (InvalidOperationException ex)
        {
            // This refusal is out_path='s own, so the note points at the parameter to fix.
            return "error: " + ex.Message + (ignoredLane
                ? "\nnote: patch=/into= were ignored because out_path= was given, and nothing was written. Fix out_path=, or drop it to write into the patch folder patch=/into= names."
                : "");
        }

        // 6) class hierarchy: the baseline, the mods-tree sources, the input pex and its siblings; every source that
        //    read less than all of itself names the reason at the render (docs/architecture/papyrus.md).
        var hierarchy = svc.ClassParentsForDecompile();
        var edges = new Dictionary<string, string>(hierarchy.Edges, StringComparer.OrdinalIgnoreCase);
        HousecarlCore.PapyrusClassParents.AddFromPex(edges, pexFile);
        var siblingDir = Path.GetDirectoryName(pex)!;
        HousecarlCore.PapyrusClassParents.PexFolderScan pexScan;
        if (AmethystSnapshotContext.Current is { } savedSnapshot)
        {
            var siblings = savedSnapshot.CapturePapyrusTree(siblingDir, ".pex", recursive: false);
            var capturedSiblings = siblings.Select(path => (path, savedSnapshot.CapturedInput(path)!)).ToArray();
            pexScan = HousecarlCore.PapyrusClassParents.AddFromCapturedPexFolder(edges, capturedSiblings);
        }
        else pexScan = HousecarlCore.PapyrusClassParents.AddFromPexFolder(edges, siblingDir);
        // The walk sees the input .pex too and it was read at step 4, so it is not one of the siblings counted here.
        var siblingsSeen = Math.Max(pexScan.FilesSeen - 1, pexScan.FilesFailed);
        // Both facts are kept: a count of unreadable files, and a listing that ended early, which can happen together.
        var failedClause = pexScan.FilesFailed > 0
            ? $"{pexScan.FilesFailed} of {siblingsSeen} sibling .pex file(s) in '{siblingDir}' could not be read"
            : null;
        var listingClause =
            pexScan.FolderMissing ? $"the folder '{siblingDir}' is no longer there"
            : pexScan.ListingFailed ? $"the folder '{siblingDir}' could not be listed to the end"
            : null;
        hierarchy = hierarchy with
        {
            SiblingPexMissing = (failedClause, listingClause) switch
            {
                (not null, not null) => $"{failedClause}, and {listingClause}",
                (not null, null) => failedClause,
                (null, not null) => listingClause,
                _ => null,
            },
        };

        // A refused decompile leaves no orphan: an empty fresh folder is deleted, a partial one named, into= alone.
        string Refuse(string msg)
        {
            var left = AmethystStagingWrite.IsActive ? null : svc.RemoveOrNameRiderResidue(rf);
            return (left is null ? msg
                : msg + $" The freshly created mod folder at '{left}' still holds partial output — delete it or retry with into=.")
                // Every message reaching here accounts for what landed, so the note only states the lane.
                + LaneNoteFail(sayNothingWritten: false);
        }

        // 7) decompile + write. Under Amethyst, every source is first written to private scratch,
        // then the complete group is validated and published through the transaction coordinator.
        var transactional = AmethystStagingWrite.IsActive;
        var scratch = transactional ? AmethystStagingWrite.CreateScratchDirectory("papyrus-decompile")
            : selectedBackend == "champollion" ? CreatePrivateScratch() : null;
        DecompileOutcome o;
        string? externalVersion = null;
        try
        {
            if (selectedBackend == "champollion")
            {
                if (AmethystSnapshotContext.Current is { } saved) _ = saved.CaptureAdditionalInput(champollionPath!);
                var names = pexFile.Objects.Where(obj => !string.IsNullOrWhiteSpace(obj.Name)).Select(obj => obj.Name!).ToArray();
                if (names.Length != pexFile.Objects.Count || names.Any(name => !IsPlainObjectName(name)))
                    return Refuse("error: a PEX object has no safe plain script name; Champollion was not launched.");
                var champInputDir = Path.Combine(scratch!, "input");
                var champOutputDir = Path.Combine(scratch!, "output");
                Directory.CreateDirectory(champInputDir); Directory.CreateDirectory(champOutputDir);
                var champInput = Path.Combine(champInputDir, Path.GetFileName(pex));
                File.WriteAllBytes(champInput, pexBytes);
                var run = HousecarlCore.PapyrusCompile.DecompileChampollion(champollionPath!, champInput, names, champOutputDir);
                externalVersion = run.Version;
                if (!run.Success)
                    return Refuse($"error: Champollion decompilation failed: {run.RunError ?? "the isolated output did not validate"}." +
                        (string.IsNullOrWhiteSpace(run.Stderr + run.Stdout) ? "" : $"\nChampollion diagnostics:\n{run.Stderr}\n{run.Stdout}"));
                o = new DecompileOutcome(run.Sources.ToList(), null, false, 0, 0, 0, new List<string>());
            }
            else o = WriteObjects(pexFile, edges, scratch ?? rf.OutputDir);
            if (o.UnnamedObject)
                return Refuse($"error: '{Path.GetFileName(pex)}' carries an unnamed script object. " +
                       (o.Written.Count > 0 ? $"Already written this call: {string.Join(", ", o.Written)}." : "Nothing was written."));
            if (o.EscapingObject is not null)
                return Refuse($"error: '{Path.GetFileName(pex)}' names a script object '{o.EscapingObject}', which is not a plain script name, " +
                       $"so its .psc would not land in '{rf.OutputDir}' — rename the object in the .pex, or decompile a copy that carries a plain name. " +
                       "Nothing was written.");
            if (o.ExistingTarget is not null)
                return Refuse($"error: '{o.ExistingTarget}' already exists — houseCARL never overwrites a source file. " +
                       (chosenOutput
                           ? "Move/delete it, or pass a different out_path=. "
                           : "Move/delete it, or pass a different patch= (or into= another patch folder). ") +
                       (o.Written.Count > 0 ? $"Already written this call: {string.Join(", ", o.Written)}." : "Nothing was written."));
            if (transactional)
            {
                var artifacts = o.Written.Select(path =>
                {
                    var destination = Path.Combine(rf.OutputDir, Path.GetFileName(path));
                    if (File.Exists(destination)) throw new IOException($"'{destination}' already exists — houseCARL never overwrites a source file");
                    return (path, destination, "papyrus-source");
                }).ToArray();
                if (artifacts.Length > 0) AmethystStagingWrite.PublishActiveArtifactGroup(artifacts);
                o = o with { Written = artifacts.Select(a => a.destination).ToList() };
            }
            else if (selectedBackend == "champollion")
            {
                var destinations = o.Written.Select(source => Path.Combine(rf.OutputDir, Path.GetFileName(source))).ToArray();
                var collision = destinations.FirstOrDefault(File.Exists);
                if (collision is not null)
                    return Refuse($"error: '{collision}' already exists — houseCARL never overwrites a source file; no Champollion output was published.");
                var sourceBytes = o.Written.Select(File.ReadAllBytes).ToArray();
                var published = new List<string>(destinations.Length);
                var hashes = new List<string>(destinations.Length);
                try
                {
                    for (var i = 0; i < destinations.Length; i++)
                    {
                        WriteNewAtomic(destinations[i], sourceBytes[i]);
                        published.Add(destinations[i]);
                        hashes.Add(Convert.ToHexString(SHA256.HashData(sourceBytes[i])));
                    }
                }
                catch (Exception ex)
                {
                    var residue = new List<string>();
                    for (var i = published.Count - 1; i >= 0; i--)
                    {
                        try
                        {
                            if (File.Exists(published[i]) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(published[i]))) == hashes[i])
                                File.Delete(published[i]);
                            else residue.Add(published[i]);
                        }
                        catch { residue.Add(published[i]); }
                    }
                    return Refuse(residue.Count == 0
                        ? $"error: Champollion output publication failed ({ex.Message}); all files created by this call were rolled back."
                        : $"error: Champollion output publication failed ({ex.Message}); partial output remains at {string.Join(", ", residue)} because those files could not be safely removed.");
                }
                o = o with { Written = published };
            }
        }
        finally { if (scratch is not null && Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }

        // 8) render: totals, failures, and the degraded modes the outcome carries.
        var outSb = new StringBuilder();
        outSb.Append("decompile ").Append(o.FunctionsFailed == 0 ? "OK" : "PARTIAL").Append(": ")
             .Append(Path.GetFileName(pex)).Append(" → ").Append(string.Join(", ", o.Written)).Append('\n');
        outSb.Append(selectedBackend == "champollion" ? "external decompiler output validated" : o.FunctionsTotal + " function(s)");
        if (o.FunctionsFailed > 0)
        {
            outSb.Append(", ").Append(o.FunctionsFailed).Append(" FAILED — each is a loud comment block with its raw bytecode in the .psc, which will NOT compile as-is:");
            foreach (var f in o.Failures) outSb.Append("\n  FAIL ").Append(f);
        }
        else outSb.Append(", all structured clean.");
        if (o.OptimizerHints > 0)
            outSb.Append("\nnote: optimizer-compiled patterns detected (Caprica class) — source is correct; byte-identity vs the original .pex under the CK compiler is not expected.");
        outSb.Append(HierarchySentence(hierarchy));
        outSb.Append("\ndecompiler backend: ").Append(selectedBackend);
        if (externalVersion is not null) outSb.Append(" v").Append(externalVersion);
        if (selectedBackend == "champollion") outSb.Append(". Champollion reconstruction is unverified: review the source and test it by recompiling before treating it as a replacement.");
        outSb.Append("\nknown format losses (every decompiler): a parameter default is not in the .pex, so it survives only where a call in the same script omitted the argument — a parameter with no such call comes back with its default missing; comments/layout are gone (docstrings survive).");
        // The destination line must match where the .psc went; an out_path= folder is no patch to recompile into.
        outSb.Append(chosenOutput
            ? "\nthe .psc is in the folder you named with out_path= (path above) — review it, edit it, and recompile with " + ToolNames.CompileScript + "."
            : AmethystSnapshotContext.Current is not null
                ? "\nthe .psc is in a houseCARL staging folder — review it in Amethyst, then Refresh and Deploy before using it; recompile with " + ToolNames.CompileScript + " (into= the same patch)."
                : "\nthe .psc is in a houseCARL patch-mod folder — review it, enable it in MO2, and recompile with " + ToolNames.CompileScript + " (into= the same patch).");
        return outSb.ToString() + LaneNoteOk();
    });

    static string CreatePrivateScratch()
    {
        var path = Path.Combine(Path.GetTempPath(), $"housecarl-papyrus-decompile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    static void WriteNewAtomic(string destination, byte[] bytes)
    {
        var parent = Path.GetDirectoryName(destination) ?? throw new IOException("output has no parent directory");
        var temp = Path.Combine(parent, ".housecarl-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, destination, overwrite: false);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    /// <summary>One sentence saying what the class hierarchy IS when a source is missing; two could contradict each other.</summary>
    internal static string HierarchySentence(ClassParents h)
    {
        if (h.BaselineNote is null && h.TopUpMissing is null && h.SiblingPexMissing is null) return "";
        var modsLabel = AmethystSnapshotContext.Current is null ? "MO2 mods-tree" : "Amethyst mods folder";
        // What a thinner hierarchy costs, said once however many sources are thin.
        const string cost = ", so the source keeps an explicit cast wherever an edge is missing (cosmetic; the source stays correct).";
        // One clause per source: why it is thin, or what it still contributes. The input .pex is always read, so the
        // "is" half is never empty.
        var thin = new List<string>();
        var kept = new List<string>();
        if (h.BaselineNote is not null) thin.Add(h.BaselineNote);
        else kept.Add("the shipped vanilla baseline");
        if (h.TopUpMissing is not null)
        {
            thin.Add($"the mods-tree sources were not all read ({h.TopUpMissing})");
            // TopUpMissing covers a tree that was not read at all AND one whose walk lost some files, so the credit is
            // worded for both: a partial .psc failure never disowns the edges the walk did add.
            kept.Add($"the {modsLabel} sources that could be read");
        }
        else kept.Add($"the {modsLabel} sources");
        if (h.SiblingPexMissing is not null)
        {
            thin.Add($"the .pex files beside this one were not all read ({h.SiblingPexMissing})");
            // True whether some siblings were read or none were, so a partial failure never disowns the edges it added.
            kept.Add("what this .pex and the sibling .pex files that could be read declare");
        }
        else kept.Add("what this .pex and the .pex files beside it declare");
        return $"\nnote: {string.Join(", and ", thin)} — the class hierarchy is {string.Join(" plus ", kept)}{cost}";
    }

    /// <summary>The decompile-and-write outcome.</summary>
    public sealed record DecompileOutcome(
        List<string> Written, string? ExistingTarget, bool UnnamedObject,
        int FunctionsTotal, int FunctionsFailed, int OptimizerHints, List<string> Failures,
        string? EscapingObject = null);

    /// <summary>True when a .pex object name is a plain script name, so its .psc lands directly in the output folder;
    /// both separators are checked on every platform. The refusal is pinned by
    /// <c>DecompileOutPathTests.AnObjectNameThatWouldLeaveTheOutputFolderIsRefusedAndNothingIsWritten</c> (#783).</summary>
    static bool IsPlainObjectName(string name) =>
        name.IndexOf('/') < 0 && name.IndexOf('\\') < 0 && name.IndexOf(':') < 0 &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        name != "." && name != "..";

    /// <summary>Decompile every object in <paramref name="pexFile"/> and write one .psc per object into
    /// <paramref name="outDir"/>, named for the object as the compiler requires; an existing target stops the call
    /// with that path reported and the file untouched.</summary>
    public static DecompileOutcome WriteObjects(
        PexFile pexFile, IReadOnlyDictionary<string, string>? edges, string outDir)
    {
        var written = new List<string>();
        int totalFns = 0, failedFns = 0, optimizerHints = 0;
        var failures = new List<string>();
        // Every name is checked before the FIRST write, so a bad one last does not refuse with its neighbours on disk.
        foreach (var obj in pexFile.Objects)
            if (!string.IsNullOrWhiteSpace(obj.Name) && !IsPlainObjectName(obj.Name))
                return new(written, null, false, totalFns, failedFns, optimizerHints, failures, obj.Name);

        foreach (var obj in pexFile.Objects)
        {
            if (string.IsNullOrWhiteSpace(obj.Name))
                return new(written, null, true, totalFns, failedFns, optimizerHints, failures);

            var target = Path.Combine(outDir, obj.Name + ".psc");
            if (File.Exists(target))   // cheap refusal before any decompile work
                return new(written, target, false, totalFns, failedFns, optimizerHints, failures);

            var r = HousecarlCore.PapyrusDecompiler.DecompileFile(SingleObjectView(pexFile, obj), edges);
            totalFns += r.FunctionsTotal;
            failedFns += r.FunctionsFailed;
            optimizerHints += r.OptimizerHints;
            failures.AddRange(r.Failures);

            var source = r.OptimizerHints > 0
                ? "; NOTE: optimizer-compiled patterns detected (Caprica class) — this source is correct, but the CK\n" +
                  "; compiler will not reproduce the original .pex byte-for-byte (the optimizer's output is not its form).\n"
                  + r.Source
                : r.Source;
            // CreateNew is atomic create-or-fail; the catch wraps the CONSTRUCTOR only, a write fault having to propagate.
            FileStream fs;
            try { fs = new FileStream(target, FileMode.CreateNew, FileAccess.Write); }
            catch (IOException) when (File.Exists(target))
            {
                return new(written, target, false, totalFns, failedFns, optimizerHints, failures);
            }
            using (fs)
            using (var w = new StreamWriter(fs))
                w.Write(source);
            written.Add(target);
        }
        return new(written, null, false, totalFns, failedFns, optimizerHints, failures);
    }

    /// <summary>A PexFile view carrying one object; the user-flag table is copied across, every flag resolving through it.</summary>
    static PexFile SingleObjectView(PexFile pex, PexObject obj)
    {
        if (pex.Objects.Count == 1) return pex;
        var view = new PexFile(GameCategory.Skyrim)
        {
            MajorVersion = pex.MajorVersion,
            MinorVersion = pex.MinorVersion,
            GameId = pex.GameId,
            CompilationTime = pex.CompilationTime,
            SourceFileName = pex.SourceFileName,
            Username = pex.Username,
            MachineName = pex.MachineName,
            DebugInfo = pex.DebugInfo,
        };
        for (int i = 0; i < pex.UserFlags.Length && i < view.UserFlags.Length; i++)
            view.UserFlags[i] = pex.UserFlags[i];
        view.Objects.Add(obj);
        return view;
    }
}
