using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Text;
using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Pex;
using ModelContextProtocol.Server;

namespace HousecarlMcp;

/// <summary>housecarl_compile_script — drives an explicitly selected Papyrus compiler backend and lands validated .pex output through the active write lane.</summary>
[McpServerToolType]
public static class CompileTools
{
    /// <summary>The import path plus each entry's provenance; the counts are derived from <see cref="Entries"/>, because dedup and the vanilla re-slot change what survives.</summary>
    public sealed record ImportPlan(
        IReadOnlyList<(string Dir, string Origin)> Entries,
        bool AutoEnabled,
        string? ImportSetName,
        string? Warning,
        int AutoScanned = 0,
        PapyrusDependencyScan? Scan = null,
        IReadOnlyList<string>? ReferencedProviders = null,
        bool VanillaMissing = false,
        bool ScanFailed = false,
        string ManagerName = "MO2")
    {
        /// <summary>Provenance labels: the exact strings the renders print, and the keys the counts group on.</summary>
        public const string OwnFolder = "the script's own folder";
        public const string Vanilla = "vanilla sources";
        public const string CallerDirs = "import_dirs=";
        public const string AutoPrefix = "MO2: ";

        /// <summary>The ordered list handed to the compiler.</summary>
        public IReadOnlyList<string> Dirs { get; } = Entries.Select(e => e.Dir).ToList();

        /// <summary>Dirs that came from the caller (import_dirs= / import_set=) and survived into the final path.</summary>
        public int CallerCount => Entries.Count(e => e.Origin == CallerDirs);

        /// <summary>The mod folders behind entries the scan put on the path; one the caller also passed takes the caller slot.</summary>
        public IReadOnlyList<string> AutoProviders =>
            Entries.Where(e => e.Origin.StartsWith(ManagerName + ": ", StringComparison.Ordinal))
                   .Select(e => e.Origin[(ManagerName.Length + 2)..]).ToList();

        /// <summary>The mods the walk matched, whichever slot each took — the one count not derived from <see cref="Entries"/>, which a caller-passed folder would drop out of.</summary>
        public IReadOnlyList<string> Referenced { get; } = ReferencedProviders ?? Array.Empty<string>();
    }

    [McpServerTool(Name = ToolNames.CompileScript, Title = "Compile a Papyrus script (.psc → .pex)"),
     Description(
         "Compile a Papyrus script (.psc) to .pex with the selected backend: native Caprica on Linux or the Creation Kit compiler. The .pex lands in a new " +
         "houseCARL patch folder in the selected manager's staging area for you to review and activate/deploy, or in a folder you name with out_path=; originals are " +
         "untouched.\n\n" +
         "Import path, highest precedence first: the script's own folder; your import_dirs= and import_set=; the Papyrus " +
         "source folders supplied by active mods in the selected profile (Source\\Scripts or Scripts\\Source, in manager precedence order), narrowed to " +
         "the ones this script references directly or transitively; the vanilla sources last. So SKSE, PapyrusUtil, SkyUI " +
         "and similar need no retyping, and mod-extended copies of vanilla scripts (SKSE's Actor.psc) win. Pass anything the " +
         "scan cannot reach (local stubs, a dev tree, sources extracted from a BSA, since the compiler cannot read archives) " +
         "with import_dirs=. Every reply lists the import path searched.\n\n" +
         "On failure it returns the errors as 'name(line,col): message' to fix and recompile (look functions up with the " +
         "papyrus-reference skill); on success, the .pex path. The default patch-folder lane needs a configured MO2 instance or Amethyst connection and the compiler " +
         "path; if the selected compiler is not set, the reply says how to set it. The CK compiler ships with the vanilla Steam game " +
         "install, not a Wabbajack 'Stock Game' copy.")]
    public static string CompileScript(
        LoadOrderService svc,
        ToolPathResolver bridge,
        UserConfigStore store,
        [Description("Full path to the .psc source file to compile.")]
            string script,
        [Description("Optional. Extra import directories holding dependency sources (.psc), separated by ';'. They rank after the script's own folder and ahead of the discovered mods and vanilla.")]
            string? import_dirs = null,
        [Description("Default true: add source folders from active mods in the selected profile when this script references them, in manager precedence order. false: compile against only the script's own folder, import_dirs=, import_set= and vanilla sources.")]
            bool auto_imports = true,
        [Description("Optional. Name of an import-directory set saved earlier with save_import_set=; its dirs rank after import_dirs=.")]
            string? import_set = null,
        [Description("Optional. Save this call's import_dirs= (plus any import_set= it loaded) under this name for later import_set= calls. Kept in houseCARL's user config across restarts; saving an existing name replaces it.")]
            string? save_import_set = null,
        [Description("Optional. Base name for the new patch-mod folder the .pex lands in (default 'houseCARL_Scripts'); auto-suffixed if taken.")]
            string? patch = null,
        [Description("Optional. An existing houseCARL patch folder to add the .pex into instead of a fresh folder: its plugin filename, if present, or its mod-folder name. A script folder holds no plugin: pass its base name (into='houseCARL_Scripts' for the default) or its mod-folder name.")]
            string? into = null,
        [Description("Optional. Absolute path to a mod-folder root of your choosing; houseCARL appends Scripts\\ unless the path already ends in it. patch= and into= are then ignored. Scripts load only from a mod's Scripts\\ folder, the manager's overwrite folder or the game's <Data>\\Scripts; elsewhere the .pex compiles with a warning that it will not deploy.")]
            string? out_path = null,
        [Description("Optional backend: 'caprica' or 'creation-kit'. If omitted, an explicitly configured CK compiler remains selected; otherwise Linux selects Caprica and Windows selects the CK compiler. No backend fallback occurs after a failure.")]
            string? compiler_backend = null) => Guard.Tool(ToolNames.CompileScript, () =>
    {
        // 1) the selected manager must be configured — the .pex lands under its staging/mods folder.
        if (svc.ConfigPromptOrNull() is { } cfgPrompt) return cfgPrompt;
        // Lane exclusivity, out_path= first (it supersedes both); contract in docs/architecture/write-path.md.
        if (string.IsNullOrWhiteSpace(out_path) && LaneSentences.PatchIntoFolderRefusal(patch, into, ".pex") is { } patchInto)
            return "error: " + patchInto;
        string? outputNote = !string.IsNullOrWhiteSpace(out_path) &&
                             (!string.IsNullOrWhiteSpace(patch) || !string.IsNullOrWhiteSpace(into))
            ? "note: out_path= was given, so patch=/into= are ignored (the .pex lands in out_path, not a houseCARL patch folder)."
            : null;
        // Reject a relative explicit output before backend selection. This is a path-contract error and should not be
        // masked by a missing Linux Proton selection or compiler tool, since out_path= owns this lane by precedence.
        if (!string.IsNullOrWhiteSpace(out_path) &&
            PathArguments.NotAbsolute(out_path.Trim().Trim('"'), "out_path", "the mod-folder root to write into (houseCARL appends Scripts\\)", "C:\\MO2\\mods\\MyMod") is { } outputPathError)
            return "error: " + outputPathError + (outputNote is null ? "" : "\n" + outputNote);

        // 2) validate the script path.
        if (string.IsNullOrWhiteSpace(script))
            return "error: no script given. Pass script= the full path to the .psc file to compile.";
        script = script.Trim().Trim('"');
        if (!File.Exists(script))
            return $"error: no such file: '{script}'. Pass the full path to the .psc source.";
        if (!script.EndsWith(".psc", StringComparison.OrdinalIgnoreCase))
            return $"error: '{Path.GetFileName(script)}' is not a .psc source file.";
        script = Path.GetFullPath(script);
        var objectName = Path.GetFileNameWithoutExtension(script);
        var scriptDir = Path.GetDirectoryName(script)!;

        // 3) select one backend for the whole call. An explicitly configured legacy CK path wins the omitted-parameter
        // default; otherwise Linux uses Caprica and Windows retains the CK. There is no fallback after a failed run.
        string selectedBackend;
        ToolDependency compilerDependency;
        if (!string.IsNullOrWhiteSpace(compiler_backend))
        {
            selectedBackend = compiler_backend.Trim().ToLowerInvariant();
            compilerDependency = selectedBackend switch
            {
                "caprica" => ToolDependency.Caprica,
                "creation-kit" => ToolDependency.PapyrusCompiler,
                _ => (ToolDependency)(-1),
            };
            if ((int)compilerDependency < 0) return "error: compiler_backend must be 'caprica' or 'creation-kit'; no compiler was run.";
        }
        else if (OperatingSystem.IsLinux() && bridge.Saved(ToolDependency.PapyrusCompiler) is { } savedCk)
        {
            selectedBackend = "creation-kit"; compilerDependency = ToolDependency.PapyrusCompiler;
            var validation = ToolBridge.Validate(ToolDependency.PapyrusCompiler, savedCk);
            if (!validation.ok) return $"error: the configured Creation Kit compiler path is invalid ({validation.error}); fix it with {ToolNames.SetToolPath}(tool='papyrus_compiler', path=...). No Caprica fallback was attempted.";
        }
        else if (OperatingSystem.IsLinux())
        { selectedBackend = "caprica"; compilerDependency = ToolDependency.Caprica; }
        else
        { selectedBackend = "creation-kit"; compilerDependency = ToolDependency.PapyrusCompiler; }
        if (bridge.RequireOrPrompt(compilerDependency, out var compilerExe, svc.CompilerGameDirHints()) is { } toolPrompt) return toolPrompt;
        HousecarlCore.SteamProtonSelection? protonRunner = null;
        if (selectedBackend == "creation-kit" && OperatingSystem.IsLinux())
        {
            if (AmethystSnapshotContext.Current is not { } saved)
                return "error: Creation Kit is a Windows executable; Linux execution requires the selected Amethyst Skyrim SE prefix and native Steam CompatToolMapping. Run it through an active Amethyst connection.";
            try { protonRunner = HousecarlCore.SteamProtonResolver.Resolve(saved); }
            catch (InvalidOperationException ex) { return "error: cannot resolve the selected Skyrim SE Proton runner: " + ex.Message; }
            catch (Exception ex) { return $"error: cannot read the selected Skyrim SE Proton runner configuration ({ex.GetType().Name}: {ex.Message})."; }
        }

        // 4) caller extras: import_dirs= then the named set. An unknown set is refused and names the saved sets.
        var callerExtras = SplitDirs(import_dirs).ToList();
        string? importSetName = null;
        if (!string.IsNullOrWhiteSpace(import_set))
        {
            importSetName = import_set.Trim();
            var saved = store.GetImportSet(importSetName);
            if (saved is null)
            {
                var known = store.ImportSetNames();
                return $"error: no saved import set named '{importSetName}'. " + (known.Count == 0
                    ? "None are saved yet — pass import_dirs= once together with save_import_set= to create one."
                    : "Saved sets: " + string.Join(", ", known) + ".");
            }
            callerExtras.AddRange(saved);
        }
        callerExtras = callerExtras.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // 5) persist the set before compiling, and report a save failure: it then holds for this session only.
        string? saveNote = null;
        if (!string.IsNullOrWhiteSpace(save_import_set))
        {
            var setName = save_import_set.Trim();
            if (callerExtras.Count == 0)
                saveNote = $"note: save_import_set='{setName}' was ignored — there were no import_dirs=/import_set= dirs to save " +
                           "(the auto-discovered and vanilla folders are re-derived every call, so a set of them would be meaningless).";
            else
            {
                var (ok, err) = store.SaveImportSet(setName, callerExtras);
                saveNote = ok
                    ? $"saved import set '{setName}' ({callerExtras.Count} dir(s)) — later calls can pass import_set={setName}."
                    : $"warning: import set '{setName}' could NOT be saved ({err}) — its dirs apply to this compile but will not survive a restart.";
            }
        }

        // 6) the import path, in order; auto_imports=false skips the scan except to locate missing vanilla sources.
        IReadOnlyList<PapyrusSourceRoot> autoRoots = Array.Empty<PapyrusSourceRoot>();
        string? gameDataSources = null, autoWarning = null;
        bool scanFailed = false;
        if (selectedBackend == "caprica" || NeedsModlistScan(auto_imports, compilerExe!))
        {
            (autoRoots, gameDataSources, autoWarning, scanFailed) = svc.PapyrusSourceImportDirs();
            if (!auto_imports) autoRoots = Array.Empty<PapyrusSourceRoot>();   // read for the vanilla fallback only
        }
        string flagsFile;
        if (selectedBackend == "creation-kit")
            flagsFile = Path.Combine(Path.GetDirectoryName(compilerExe!)!, "TESV_Papyrus_Flags.flg");
        else
        {
            var candidates = svc.CompilerGameDirHints().SelectMany(game => new[]
            {
                Path.Combine(game, "Papyrus Compiler", "TESV_Papyrus_Flags.flg"),
                Path.Combine(game, "Data", "Source", "Scripts", "TESV_Papyrus_Flags.flg"),
            }).ToList();
            if (bridge.Saved(ToolDependency.PapyrusCompiler) is { } configuredCk && ToolBridge.Validate(ToolDependency.PapyrusCompiler, configuredCk).ok)
                candidates.Insert(0, Path.Combine(Path.GetDirectoryName(configuredCk)!, "TESV_Papyrus_Flags.flg"));
            if (gameDataSources is not null) candidates.Add(Path.Combine(gameDataSources, "TESV_Papyrus_Flags.flg"));
            flagsFile = candidates.FirstOrDefault(File.Exists) ?? "";
            if (flagsFile.Length == 0)
                return "error: Caprica requires the actual Skyrim TESV_Papyrus_Flags.flg definitions; none were found beside the selected Skyrim install or its vanilla source directory. Set a valid CK compiler path or select compiler_backend='creation-kit'.";
        }
        if (!File.Exists(flagsFile)) return $"error: the selected Skyrim Papyrus flags file is missing: '{flagsFile}'. No compiler was run.";
        // The warning rides along whenever the scan ran, on either branch.
        var plan = PlanImports(script, scriptDir, compilerExe!, callerExtras, autoRoots, auto_imports, importSetName,
                               autoWarning, gameDataSources, scanFailed, useGameDataVanilla: selectedBackend == "caprica",
                               managerName: AmethystSnapshotContext.Current is null ? "MO2" : "Amethyst");

        // The whole path travels as one `-i=` argument, so a path past the command-line cap is refused with the way out.
        var joinedLength = string.Join(";", plan.Dirs).Length;
        if (joinedLength > 30_000)
            return $"error: the assembled import path is too long for one compiler command line ({plan.Dirs.Count} dirs, " +
                   $"{joinedLength} chars; the limit is about 32000). Re-run with auto_imports=false and pass only the " +
                   "dependencies this script needs via import_dirs= (save_import_set= will keep that list for next time).";

        // 7) output folder: out_path= is user-owned, takes Scripts\ appended, and supersedes patch=/into= saying so.
        OutputLocations.RiderFolder rf;
        string? deployWarning = null;
        if (!string.IsNullOrWhiteSpace(out_path))
        {
            try { rf = svc.ResolveExplicitScriptFolder(out_path, out deployWarning); }
            // The ignored-lane note rides the refusal too — it is still true of the call about to be retyped.
            catch (InvalidOperationException ex) { return "error: " + ex.Message + (outputNote is null ? "" : "\n" + outputNote); }
        }
        else
        {
            try { rf = svc.ResolveCompiledScriptFolder(patch, into); }
            catch (InvalidOperationException ex) { return "error: " + ex.Message; }
        }

        // 8) compile into a private empty directory under Amethyst; only validated bytes enter staging.
        var transactional = AmethystStagingWrite.IsActive;
        var snapshot = transactional ? AmethystSnapshotContext.Current : null;
        if (snapshot is not null)
            _ = AmethystSnapshotFilter.CapturePapyrusImportInputs(snapshot, plan.Dirs, compilerExe!, flagsFile);
        var scratch = transactional ? AmethystStagingWrite.CreateScratchDirectory("papyrus-compile") : CreatePrivateScratch();
        var outputDir = Path.Combine(scratch, "output");
        Directory.CreateDirectory(outputDir);
        CompileResult result;
        try
        {
            var (capturedSource, capturedImports) = MaterializeImports(script, plan.Dirs, scratch, snapshot);
            if (selectedBackend == "caprica")
            {
                var privateConfig = Path.Combine(scratch!, "caprica.cfg");
                File.WriteAllText(privateConfig, "");
                result = HousecarlCore.PapyrusCompile.CompileCaprica(compilerExe!, capturedSource, objectName, capturedImports,
                    flagsFile, outputDir, privateConfig);
            }
            else result = HousecarlCore.PapyrusCompile.CompileObject(compilerExe!, objectName, capturedImports, outputDir, flagsFile, proton: protonRunner);
            if (result.Success)
            {
                var produced = Directory.EnumerateFiles(outputDir, "*", SearchOption.TopDirectoryOnly).ToArray();
                var pexPath = Path.Combine(outputDir, objectName + ".pex");
                if (produced.Length != 1 || !string.Equals(Path.GetFullPath(produced[0]), Path.GetFullPath(pexPath), StringComparison.OrdinalIgnoreCase))
                    return "error: the compiler did not produce exactly the expected PEX file in its private output directory; nothing was published.";
                PexFile parsed;
                try { parsed = PexFile.CreateFromFile(pexPath, GameCategory.Skyrim); }
                catch (Exception ex) { return $"error: compiler output '{Path.GetFileName(pexPath)}' is not a valid Skyrim PEX ({ex.Message}); nothing was published."; }
                if (parsed.Objects.Count != 1 || !string.Equals(parsed.Objects[0].Name, objectName, StringComparison.OrdinalIgnoreCase))
                    return $"error: compiler output did not contain exactly the requested Skyrim script '{objectName}'; nothing was published.";
                var destination = Path.Combine(rf.OutputDir, objectName + ".pex");
                if (transactional)
                {
                    AmethystStagingWrite.RegisterOutputPath(destination);
                    AmethystStagingWrite.PublishActiveArtifact(pexPath, destination, "pex");
                }
                else HousecarlCore.AtomicFile.WriteAllBytes(destination, File.ReadAllBytes(pexPath));
                result = result with { PexPath = destination };
            }
        }
        finally { if (scratch is not null && Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
        var rendered = Render(result, plan, userChoseOutputDir: !string.IsNullOrWhiteSpace(out_path));
        if (result.Success)
        {
            // Never report a clean "done" for a .pex that will not deploy from where out_path= put it.
            if (deployWarning is not null) rendered += "\n" + deployWarning;
        }
        else
        {
            // No .pex: an empty fresh folder is deleted, a partial one named, an into= or out_path= folder left alone.
            var left = transactional ? null : svc.RemoveOrNameRiderResidue(rf);
            if (transactional) rendered += "\nAmethyst will record whether the failed compiler output was rolled back or requires recovery.";
            else if (left is not null)
                rendered += $"\nThe freshly created mod folder at '{left}' still holds partial output — delete it or retry with into=.";
        }
        if (saveNote is not null) rendered = saveNote + "\n" + rendered;
        if (outputNote is not null) rendered = outputNote + "\n" + rendered;
        rendered += $"\ncompiler backend: {selectedBackend} (Skyrim SE).";
        if (selectedBackend == "caprica" && compilerExe is not null && HousecarlCore.PapyrusCompile.ProbeCapricaVersion(compilerExe) is { } capricaVersion)
            rendered += $" Caprica version: {capricaVersion}.";
        if (transactional && !result.Success) rendered = "error: " + rendered;
        return rendered;
    });

    /// <summary>Split a ';'-separated import-dir spec into trimmed, unquoted directories, dropping blanks.</summary>
    internal static IEnumerable<string> SplitDirs(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) yield break;
        foreach (var raw in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var d = raw.Trim('"').Trim();
            if (d.Length > 0) yield return d;
        }
    }

    static string CreatePrivateScratch()
    {
        var path = Path.Combine(Path.GetTempPath(), $"housecarl-papyrus-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    internal static (string Source, IReadOnlyList<string> Imports) MaterializeImports(string sourcePath, IReadOnlyList<string> imports,
        string scratch, HousecarlCore.AmethystSavedSnapshot? snapshot)
    {
        const int MaximumSources = 20_000;
        var materialized = new List<string>(imports.Count);
        var source = Path.GetFullPath(sourcePath);
        var tiers = new List<(string OriginalDir, string TargetDir, Dictionary<string, (string Path, byte[] Bytes)> Providers,
            HashSet<string> Selected)>();
        int count = 0;
        for (var i = 0; i < imports.Count; i++)
        {
            var originalDir = Path.GetFullPath(imports[i]);
            var targetDir = Path.Combine(scratch, "imports", i.ToString("D4"));
            Directory.CreateDirectory(targetDir);
            IEnumerable<string> sourceFiles;
            if (snapshot is not null) sourceFiles = snapshot.CapturePapyrusSourceDirectory(originalDir);
            else
            {
                if (!Directory.Exists(originalDir)) throw new InvalidOperationException($"Papyrus import directory is missing: '{originalDir}'");
                sourceFiles = Directory.EnumerateFiles(originalDir, "*", SearchOption.TopDirectoryOnly)
                    .Where(file => file.EndsWith(".psc", StringComparison.OrdinalIgnoreCase)).ToArray();
            }
            var namesAtTier = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var basenamesAtTier = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var providers = new Dictionary<string, (string Path, byte[] Bytes)>(StringComparer.OrdinalIgnoreCase);
            foreach (var sourceFile in sourceFiles)
            {
                if (++count > MaximumSources)
                    throw new InvalidOperationException($"Papyrus import materialization reached its {MaximumSources}-source ceiling; refusing because the compiler input closure is incomplete.");
                var basename = Path.GetFileName(sourceFile);
                var filenameStem = Path.GetFileNameWithoutExtension(basename);
                if (!basenamesAtTier.Add(filenameStem))
                    throw new InvalidOperationException($"Papyrus import directory '{originalDir}' contains filenames that collide case-insensitively for the compiler lookup model: '{basename}'.");
                var bytes = snapshot?.CapturedInput(sourceFile);
                if (snapshot is not null && bytes is null)
                    throw new InvalidOperationException($"Papyrus source was not captured before materialization: '{sourceFile}'");
                bytes ??= File.ReadAllBytes(sourceFile);
                var declaredName = Regex.Match(System.Text.Encoding.UTF8.GetString(bytes), @"(?im)^\s*ScriptName\s+([^\s;]+)");
                if (declaredName.Success && (declaredName.Groups[1].Value.Contains('/') || declaredName.Groups[1].Value.Contains('\\') || declaredName.Groups[1].Value.Contains(':')))
                    throw new InvalidOperationException($"Papyrus source '{sourceFile}' declares an unsafe script name '{declaredName.Groups[1].Value}'; refusing to materialize it.");
                var identity = declaredName.Success ? declaredName.Groups[1].Value : Path.GetFileNameWithoutExtension(basename);
                if (!namesAtTier.Add(identity))
                    throw new InvalidOperationException($"Papyrus import directory '{originalDir}' contains ambiguous same-precedence providers for script '{identity}'.");
                providers.Add(identity, (sourceFile, bytes));
            }
            tiers.Add((originalDir, targetDir, providers, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
            materialized.Add(targetDir);
        }

        // Follow the same first-provider-wins search order as the compiler, and copy only files reached
        // from the main source. The original directories never remain on Caprica's import path.
        var sourceName = Path.GetFileNameWithoutExtension(source);
        var mainTierIndex = tiers.FindIndex(tier => tier.Providers.Values.Any(provider => provider.Path.Equals(source, StringComparison.Ordinal)));
        if (mainTierIndex < 0)
            throw new InvalidOperationException("The main Papyrus source was not present in the captured import path; refusing to compile an uncaptured file.");
        var mainTier = tiers[mainTierIndex];
        var mainProvider = mainTier.Providers.First(pair => pair.Value.Path.Equals(source, StringComparison.Ordinal));
        if (!string.Equals(mainProvider.Key, sourceName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Main source filename '{Path.GetFileName(source)}' declares script name '{mainProvider.Key}'; the selected compiler output name would be inconsistent.");
        var queue = new Queue<string>(PapyrusIdentifiers(mainProvider.Value.Bytes));
        mainTier.Selected.Add(mainProvider.Key);
        var visitedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (!visitedNames.Add(name)) continue;
            for (var i = 0; i < tiers.Count; i++)
            {
                if (!tiers[i].Providers.TryGetValue(name, out var provider)) continue;
                tiers[i].Selected.Add(name);
                foreach (var dependency in PapyrusIdentifiers(provider.Bytes)) queue.Enqueue(dependency);
                break;
            }
        }
        foreach (var tier in tiers)
        foreach (var name in tier.Selected)
        {
            if (!tier.Providers.TryGetValue(name, out var provider)) continue;
            var outputName = string.Equals(Path.GetFileNameWithoutExtension(provider.Path), name, StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileName(provider.Path) : name + ".psc";
            File.WriteAllBytes(Path.Combine(tier.TargetDir, outputName), provider.Bytes);
        }
        var copiedMain = Path.Combine(mainTier.TargetDir, Path.GetFileName(mainProvider.Value.Path));
        if (!File.Exists(copiedMain))
            throw new InvalidOperationException("The main Papyrus source was not materialized; refusing to compile an uncaptured file.");
        return (copiedMain, materialized);
    }

    static IEnumerable<string> PapyrusIdentifiers(byte[] source)
    {
        var text = new UTF8Encoding(false, true).GetString(source);
        foreach (Match match in Regex.Matches(text, @"[A-Za-z_][A-Za-z0-9_]*")) yield return match.Value;
    }

    /// <summary>Whether the MO2 modlist must be read at all: when the scan was asked for, and when the compiler has no vanilla sources beside it. Pinned by <c>import-order-guard</c>.</summary>
    internal static bool NeedsModlistScan(bool autoImports, string compilerExe)
        => autoImports || VanillaSourceDir(compilerExe) is null;

    /// <summary>The vanilla sources shipped with the compiler's own game, or null — one definition, because the path and its labelling must recognise the same folder.</summary>
    public static string? VanillaSourceDir(string compilerExe)
    {
        var gameRoot = Path.GetDirectoryName(Path.GetDirectoryName(compilerExe));
        if (gameRoot is null) return null;
        var vanilla = Path.Combine(gameRoot, "Data", "Source", "Scripts");
        return Directory.Exists(vanilla) ? vanilla : null;
    }

    /// <summary>Assemble the import-directory list, whose ORDER is semantics; the order is in docs/architecture/papyrus.md, pinned by <c>import-order-guard</c>.</summary>
    public static List<string> BuildImports(string scriptDir, string compilerExe, string? import_dirs,
                                            IReadOnlyList<string>? autoDirs = null, string? resolvedVanilla = null)
    {
        var imports = new List<string> { scriptDir };
        imports.AddRange(SplitDirs(import_dirs));
        if (autoDirs is not null) imports.AddRange(autoDirs);
        // resolvedVanilla, when given, IS the vanilla dir: the plan resolves it once and hands the answer down.
        var vanilla = resolvedVanilla ?? VanillaSourceDir(compilerExe);
        if (vanilla is not null)
        {
            // The auto-added vanilla dir stays last, re-passed or re-found; the own-folder slot is the one exception.
            imports.RemoveAll(d => d.Equals(vanilla, StringComparison.OrdinalIgnoreCase)
                                   && !d.Equals(scriptDir, StringComparison.OrdinalIgnoreCase));
            imports.Add(vanilla);
        }
        return imports.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Label each surviving dir of <see cref="BuildImports"/> off the FINAL list: own folder, vanilla, a caller dir, then a discovered mod.</summary>
    internal static ImportPlan PlanImports(
        string targetScript, string scriptDir, string compilerExe, IReadOnlyList<string> callerExtras,
        IReadOnlyList<PapyrusSourceRoot> autoRoots, bool autoEnabled, string? importSetName, string? warning,
        string? gameDataSources = null, bool scanFailed = false, bool useGameDataVanilla = false,
        string managerName = "MO2")
    {
        // The compiler's own game dir first, its sources matching the flags file, then the modlist's as the fallback.
        var vanilla = useGameDataVanilla ? gameDataSources : VanillaSourceDir(compilerExe) ?? gameDataSources;

        // Narrow the scan to what this script reaches, vanilla held out of the candidates as it is appended last anyway.
        var candidates = autoRoots.Where(r => vanilla is null || !r.Dir.Equals(vanilla, StringComparison.OrdinalIgnoreCase)).ToList();
        PapyrusDependencyScan? scan = null;
        IReadOnlyList<string> autoDirs = Array.Empty<string>();
        if (candidates.Count > 0)
        {
            var seeds = new List<string> { scriptDir };
            seeds.AddRange(callerExtras);
            scan = PapyrusDependencyFilter.Relevant(targetScript, seeds, candidates.Select(c => c.Dir).ToList());
            autoDirs = scan.Folders;
        }

        var dirs = BuildImports(scriptDir, compilerExe, string.Join(";", callerExtras), autoDirs, vanilla);

        // Providers are indexed from the KEPT folders only: a dropped candidate reaches the list only as a caller dir.
        var keptSet = new HashSet<string>(autoDirs, StringComparer.OrdinalIgnoreCase);
        var providers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in candidates) if (keptSet.Contains(r.Dir)) providers.TryAdd(r.Dir, r.Provider);
        var callerSet = new HashSet<string>(callerExtras, StringComparer.OrdinalIgnoreCase);

        var entries = new List<(string Dir, string Origin)>(dirs.Count);
        foreach (var d in dirs)
        {
            // The caller outranks the scan here, mirroring the slot BuildImports gave it.
            string origin =
                d.Equals(scriptDir, StringComparison.OrdinalIgnoreCase) ? ImportPlan.OwnFolder
                : vanilla is not null && d.Equals(vanilla, StringComparison.OrdinalIgnoreCase) ? ImportPlan.Vanilla
                : callerSet.Contains(d) ? ImportPlan.CallerDirs
                : providers.TryGetValue(d, out var mod) ? managerName + ": " + mod
                : ImportPlan.CallerDirs;
            entries.Add((d, origin));
        }

        // The scan's own answer, kept separate from the slot labels, because the two answer different questions.
        var referenced = scan is null
            ? (IReadOnlyList<string>)Array.Empty<string>()
            : scan.Folders.Select(f => providers.TryGetValue(f, out var m) ? m : Path.GetFileName(f)).ToList();

        return new ImportPlan(entries, autoEnabled, importSetName, warning, candidates.Count, scan, referenced,
                              VanillaMissing: vanilla is null, ScanFailed: scanFailed, ManagerName: managerName);
    }

    /// <summary>The one-line "what was searched" summary, printed on every call, provider names capped with a "+N more" tail.</summary>
    internal static string ImportSummary(ImportPlan p)
    {
        var sb = new StringBuilder();
        sb.Append("imports: ").Append(p.Dirs.Count).Append(" dir(s) searched — the script's own folder");
        if (p.CallerCount > 0)
        {
            sb.Append("; ").Append(p.CallerCount).Append(" from import_dirs=");
            if (p.ImportSetName is not null) sb.Append("/import_set=").Append(p.ImportSetName);
        }
        if (!p.AutoEnabled)
            sb.Append("; auto_imports=false (active mods in the ").Append(p.ManagerName).Append(" profile are NOT on the import path)");
        else if (p.ScanFailed)
            // A read that threw reached no conclusion, so the failure is flagged rather than rendered as "0 of 0".
            sb.Append("; the ").Append(p.ManagerName == "MO2" ? "MO2 modlist" : "Amethyst profile")
              .Append(" could NOT be read, so installed mod source folders were not scanned");
        else
        {
            // Report the narrowing as the WALK's count, with no arithmetic against the import_dirs= clause.
            var provs = p.Referenced;
            sb.Append("; the modlist scan matched ").Append(provs.Count).Append(" of ").Append(p.AutoScanned)
              // "referenced by this script" is a claim about contents, so it is only made when the source was read.
              .Append(p.Scan is { TargetUnreadable: true }
                          ? " scanned mod source folder(s) (the script could NOT be read — see below)"
                          : " scanned mod source folder(s) referenced by this script");
            if (provs.Count > 0)
            {
                const int Show = 8;
                sb.Append(" (").Append(string.Join(", ", provs.Take(Show)));
                if (provs.Count > Show) sb.Append(", +").Append(provs.Count - Show).Append(" more");
                sb.Append(')');
            }
        }
        if (p.Entries.Count > 0 && p.Entries[^1].Origin == ImportPlan.Vanilla)
            sb.Append("; vanilla sources last");
        sb.Append('.');
        sb.Append(ImportCaveats(p));
        return sb.ToString();
    }

    /// <summary>The full ordered import path with provenance, printed on a failure.</summary>
    internal static string ImportDetail(ImportPlan p)
    {
        var sb = new StringBuilder("import path searched, in order (the compiler takes the FIRST match):");
        for (int i = 0; i < p.Entries.Count; i++)
            sb.Append("\n  ").Append((i + 1).ToString().PadLeft(2)).Append(". ")
              .Append(p.Entries[i].Dir).Append("   [").Append(p.Entries[i].Origin).Append(']');
        sb.Append(ImportCaveats(p));
        return sb.ToString();
    }

    /// <summary>Everything that makes the printed path less than a complete answer, emitted from inside both renders.</summary>
    internal static string ImportCaveats(ImportPlan p)
    {
        var sb = new StringBuilder();
        // No vanilla sources anywhere: every vanilla type will fail to resolve, and the banner would blame a mod.
        if (p.VanillaMissing)
            sb.Append("\n⚠ NO vanilla Papyrus sources are on the import path — houseCARL found none beside the compiler " +
                      "(<game>\\Data\\Source\\Scripts)")
              // A failed read never reached the data folder, and which of the two it is changes the fix.
              .Append(p.ScanFailed
                          ? p.ManagerName == "MO2"
                              ? " and could not read your MO2 modlist to look under the data folder (see below)."
                              : " and could not read the selected Amethyst profile to look under the game data folder (see below)."
                          : " and none under the game data folder.")
              .Append(" Every vanilla type will fail to resolve until you unpack them (the CK ships them as " +
                      "Scripts.zip) or pass their folder via import_dirs=.");
        if (p.Scan is { TargetUnreadable: true })
            sb.Append("\n⚠ the script's own source could not be READ when the import path was assembled (locked or moved " +
                      "after houseCARL first checked it), so NOTHING was resolved from its contents and the modlist scan " +
                      "contributed nothing — this is not a statement that the script references none of your mods.");
        if (p.Scan is { BudgetExhausted: true })
            sb.Append("\n⚠ the reference walk stopped at its ").Append(PapyrusDependencyFilter.MaxFilesRead)
              .Append("-file ceiling, so a dependency reached only through the unread tail may be missing from the path — " +
                      "if the compile fails on unresolved symbols, pass that folder via import_dirs=.");
        if (p.Warning is not null) sb.Append('\n').Append(p.Warning);
        return sb.ToString();
    }

    internal static string Render(HousecarlCore.CompileResult r, ImportPlan plan, bool userChoseOutputDir)
    {
        var sb = new StringBuilder();
        if (!r.Ran) return "error: " + r.RunError;   // the compiler couldn't be run at all

        if (r.Success)
        {
            sb.Append("compile OK: ").Append(r.ObjectName).Append(".psc → ").Append(r.PexPath).Append('\n');
            // The destination line must match where the .pex went; a deployability caveat rides on deployWarning.
            sb.Append(userChoseOutputDir
                ? "the .pex is in the output folder you chose (path above)."
                : plan.ManagerName == "Amethyst"
                    ? "the .pex is in a houseCARL staging folder — review it in Amethyst, then Refresh and Deploy to use it."
                    : "the .pex is in a houseCARL patch-mod folder — enable it in MO2 to use it.");
            sb.Append('\n').Append(ImportSummary(plan));
            if (r.Diagnostics.Count > 0)   // a .pex was produced but the compiler emitted notes — surface them as warnings
            {
                sb.Append('\n').Append(r.Diagnostics.Count).Append(" warning(s) (the .pex compiled anyway):");
                foreach (var d in r.Diagnostics) sb.Append("\n  ").Append(d);
            }
            return sb.ToString();
        }

        // failed: this run wrote no .pex, and any previous build is left untouched
        sb.Append("compile FAILED: ").Append(r.ObjectName).Append(".psc — no new .pex produced (any previous build is left unchanged).");
        if (r.Diagnostics.Count > 0)
        {
            // A failure dominated by unresolved-symbol errors leads with the import-path banner, gated on three diagnostics and a two-thirds supermajority (<c>compile-ergonomics-guard</c>).
            int unresolved = 0;
            foreach (var d in r.Diagnostics) if (HousecarlCore.PapyrusCompile.IsUnresolvedSymbol(d.Message)) unresolved++;
            bool dominatedByMissingImports = unresolved >= 3 && unresolved * 3 >= r.Diagnostics.Count * 2;
            if (dominatedByMissingImports)
            {
                sb.Append("\n⚠ This looks like an INCOMPLETE import path, not a bug in the script: ")
                  .Append(unresolved).Append(" of ").Append(r.Diagnostics.Count)
                  .Append(" diagnostics are unresolved-symbol/type errors (e.g. 'unknown type …', '… is undefined'). The CK " +
                          "compiler resolves every referenced script against the import path, so a dependency whose source " +
                          "folder is missing makes ALL its calls and types fail.");
                // The remedy differs once the modlist has been scanned, and again when the narrowing was incomplete.
                bool narrowingIncomplete = plan.Scan is { TargetUnreadable: true } or { BudgetExhausted: true };
                sb.Append(!plan.AutoEnabled
                    ? $" auto_imports=false, so active mods in the {plan.ManagerName} profile are NOT on the import path — re-run with auto_imports=true, or pass " +
                      "EVERY dependency's source folder via import_dirs= (SKSE, SkyUI, PapyrusUtil, PO3, JContainers, …; " +
                      "';'-separated) — the same set your project's compile .bat passes via -i=."
                    : plan.ScanFailed
                    ? $" The {(plan.ManagerName == "MO2" ? "modlist" : "Amethyst profile")} could NOT be read (see the ⚠ note below), so installed mod source folders " +
                      "reached the path — that is the most likely cause here, ahead of anything about this script. Fix the " +
                      "modlist read, or pass the dependency's source folder via import_dirs= for now."
                    : narrowingIncomplete
                    ? " START WITH THE ⚠ NOTE BELOW: the modlist scan did not complete, so the " + plan.Referenced.Count +
                      " folder(s) it matched out of " + plan.AutoScanned + " scanned are NOT the full set this script " +
                      "needs — the missing dependency was most likely dropped by that, not by anything wrong with your setup. " +
                      "Pass its source folder via import_dirs= (and save_import_set= to keep it for next time)."
                    : $" The {plan.ManagerName} profile scan found " + plan.AutoScanned + " mod source folder(s) and matched the " +
                      plan.Referenced.Count + " this script REFERENCES BY NAME (listed below). So the missing dependency " +
                      "is most likely one the source never names outright, or one that is not active in the selected profile, " +
                      "or one shipping its sources inside a BSA (the CK compiler cannot read archives — extract them first, " +
                      "e.g. with " + ToolNames.BsaExtract + "), or one keeping them in a SUBFOLDER of a listed dir. Pass that folder " +
                      "via import_dirs= (and save_import_set= to keep it for next time).");
            }

            // "diagnostic(s)", not "error(s)": the compiler mixes warnings in and the parser splits no severities.
            sb.Append('\n').Append(r.Diagnostics.Count).Append(" diagnostic(s) (errors and possibly warnings — the CK compiler mixes them):");
            foreach (var d in r.Diagnostics) sb.Append("\n  ").Append(d);
            sb.Append('\n').Append(ImportDetail(plan));   // carries the caveats, warning included
            // The generic tail is for the non-dominated case; after the banner it would repeat the import line.
            sb.Append("\nfix the .psc and recompile (look unfamiliar functions/types up with the papyrus-reference skill).");
            if (!dominatedByMissingImports)
                sb.Append(" If a dependency type is 'not found', its source folder may be missing from the import path listed above — pass it via import_dirs=.");
        }
        else
        {
            // no parseable diagnostics — surface the raw compiler output rather than an empty failure
            sb.Append("\nthe compiler reported failure but no per-line diagnostics were parsed. Raw output:");
            if (r.Stderr.Trim().Length > 0) sb.Append("\n[stderr] ").Append(r.Stderr.Trim());
            if (r.Stdout.Trim().Length > 0) sb.Append("\n[stdout] ").Append(r.Stdout.Trim());
            sb.Append('\n').Append(ImportDetail(plan));
        }
        return sb.ToString();
    }
}
