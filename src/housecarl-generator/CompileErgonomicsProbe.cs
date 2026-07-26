using HousecarlCore;
using HousecarlMcp;

namespace HousecarlGenerator;

/// <summary>
/// Compile-rider ergonomics guard (HCBR-2026-06-15-01 / PR-J, items 6.2 + 6.3). The service-layer (housecarl-mcp) half of
/// the compiler/BSA ergonomics work — the pure-core ToolBridge half (auto-detect candidates, the looked-here prompt, the
/// cross-instance sharing lock) lives in <see cref="ToolBridgeProbe"/>. Two LoadOrderService seams, both asserted on
/// Synthetic native host paths — no Amethyst installation, game data, or record index:
///
///   A  <see cref="LoadOrderService.GameDirOrNull"/> (6.2 auto-detect hint) — NULL-SAFE by contract: it feeds the compiler
///      auto-detect, so a failure must fall through to the forcing prompt, never throw and abort the compile.
///        • explicit-paths mode → DataDir's parent (the game dir), derived without an ini read;
///        • unconfigured → null (no _configured guard would otherwise hit EnsurePathsDerived's NotConfigured throw);
///        • an UNUSABLE instance dir → null, NOT a thrown exception (the rider's own config gate names the real problem).
///   B  <see cref="LoadOrderService.ScriptOutputContract"/> (6.3 output_dir=) — the DECIDED contract (Aaron 2026-06-16):
///      output_dir is a mod-folder ROOT and houseCARL appends Scripts\ (so the .pex deploys), with a double-Scripts guard
///      and a Q3 deployability warning. PURE (no I/O) so the riskiest change's only proof isn't punted:
///        • a bare root gets Scripts\ appended; a root already ending in Scripts\ does NOT get a second one (any case);
///        • a path under Amethyst staging or game Data/Scripts is deployable (no warning); one outside any deploy root
///          still lands but carries the Q3 note (never a clean "done" for a .pex Amethyst will not deploy);
///        • the output path is the chosen contract WITHOUT calling ResolvePatchModFolder (no houseCARL mod folder cut),
///          and the rider's RiderFolder is user-owned (CreatedFresh=false) so residue cleanup never deletes it.
///
/// Run: dotnet run --project src/housecarl-generator compile-ergonomics-guard
/// </summary>
internal static class CompileErgonomicsProbe
{
    public static int RunGuard(string[] args)
    {
        Console.WriteLine("================================================================");
        Console.WriteLine(" compile-ergonomics guard — GameDirOrNull null-safety + output_dir= contract (PR-J)");
        Console.WriteLine("================================================================");
        Console.WriteLine();
        int fail = 0;
        void Check(bool c, string label) { Console.WriteLine((c ? "  PASS  " : "  FAIL  ") + label); if (!c) fail++; }

        var pathRoot = Path.Combine(Path.GetTempPath(), "hc-comperg-paths-" + Guid.NewGuid().ToString("N"));
        var game = Path.Combine(pathRoot, "game");
        var data = Path.Combine(game, "Data");
        var mods = Path.Combine(pathRoot, "staging", "mods");
        var profile = Path.Combine(pathRoot, "profile");
        var tmpStore = Path.Combine(pathRoot, "user.json");
        try
        {
            var store = new UserConfigStore(tmpStore);

            // ---------------------------------------------------------- A) GameDirOrNull — the compiler auto-detect hint
            Console.WriteLine("--- A: LoadOrderService.GameDirOrNull — game dir derived; null-safe when it can't be ---");

            // explicit-paths mode: DataDir is set directly, so the game dir = DataDir's parent (no ini read, no instance).
            var explicitSvc = LoadOrderService.WithExplicitPaths(data, mods, profile, 0, store);
            Check(explicitSvc.GameDirOrNull() == game,
                  "explicit mode: GameDirOrNull = DataDir's parent (the game install dir)");

            // Unconfigured Amethyst mode has no manifest or game root and must remain null-safe.
            var unconfigured = LoadOrderService.WithAmethystConnection(null, 0, store);
            bool unconfThrew = false; string? unconfResult = null;
            try { unconfResult = unconfigured.GameDirOrNull(); } catch { unconfThrew = true; }
            Check(!unconfThrew && unconfResult is null, "unconfigured: GameDirOrNull returns null (never throws)");

            // A configured but missing manifest fails derivation internally; this best-effort hint still returns null.
            var badInstance = LoadOrderService.WithAmethystConnection(
                Path.Combine(Path.GetTempPath(), "hc-no-such-manifest-" + Guid.NewGuid().ToString("N"), "connection.json"),
                0, store);
            bool badThrew = false; string? badResult = null;
            try { badResult = badInstance.GameDirOrNull(); } catch { badThrew = true; }
            Check(!badThrew && badResult is null,
                  "unusable Amethyst connection: GameDirOrNull returns null, does not throw");

            // CompilerGameDirHints: the ordered auto-detect hint list. [0] = the load-order game dir; then the located real
            // Steam install (environment-dependent — absent on a CI runner with no Skyrim, verified on Aaron's rig). NULL-SAFE
            // end to end: the GameFinder/registry call is wrapped, so a hiccup yields fewer hints, never throws.
            bool hintsThrew = false; IReadOnlyList<string>? hints = null;
            try { hints = explicitSvc.CompilerGameDirHints(); } catch { hintsThrew = true; }
            Check(!hintsThrew && hints is not null && hints.Contains(game),
                  "CompilerGameDirHints includes the load-order game dir as the first hint, and never throws (locator is best-effort)");
            bool unconfHintsThrew = false; IReadOnlyList<string>? unconfHints = null;
            try { unconfHints = unconfigured.CompilerGameDirHints(); } catch { unconfHintsThrew = true; }
            Check(!unconfHintsThrew && unconfHints is not null,
                  "CompilerGameDirHints on an unconfigured service returns a list (no load-order hint; locator-only), never throws");
        }
        finally { try { File.Delete(tmpStore); } catch { /* non-fatal */ } }

        // ---------------------------------------------------------- B) output_dir= contract (6.3): pure double-Scripts guard
        Console.WriteLine();
        Console.WriteLine("--- B1: ScriptOutputContract (pure) — append Scripts\\ with the double-Scripts guard + deployability ---");
        var chosen = Path.Combine(pathRoot, "chosen", "MyMod");
        var scripts = Path.Combine(chosen, "Scripts");
        var bare = LoadOrderService.ScriptOutputContract(chosen, mods, data);
        Check(bare.scriptsDir == scripts && bare.appendedScripts, "a bare mod-folder root gets Scripts appended");

        var already = LoadOrderService.ScriptOutputContract(scripts, mods, data);
        Check(already.scriptsDir == scripts && !already.appendedScripts, "double-Scripts guard: a root already ending in Scripts is NOT doubled");

        var lowerScripts = Path.Combine(chosen, "scripts");
        var lower = LoadOrderService.ScriptOutputContract(lowerScripts, mods, data);
        Check(lower.scriptsDir == lowerScripts && !lower.appendedScripts, "double-Scripts guard accepts the canonical segment case-insensitively");

        var trailing = LoadOrderService.ScriptOutputContract(scripts + Path.DirectorySeparatorChar, mods, data);
        Check(trailing.scriptsDir == scripts && !trailing.appendedScripts, "double-Scripts guard tolerates a trailing host separator");

        Console.WriteLine();
        Console.WriteLine("--- B2: ScriptOutputContract — deployability warning (Q3: never a clean done for a .pex that won't load) ---");
        Check(bare.deployWarning is not null, "a path under NEITHER mods nor Data carries the Q3 deploy warning");
        // DEPLOYABLE (no warning): exactly <mods>\<modFolder>\Scripts, or <data>\Scripts.
        Check(LoadOrderService.ScriptOutputContract(Path.Combine(mods, "MyPatch"), mods, data).deployWarning is null,
              "a real Amethyst staging mod folder is deployable (no warning)");
        Check(LoadOrderService.ScriptOutputContract(data, mods, data).deployWarning is null,
              "the game's Data folder (-> <data>\\Scripts) is deployable (no warning)");
        // NON-deployable (warns) — the tightened rule (review nit): "under mods" alone is not enough.
        Check(LoadOrderService.ScriptOutputContract(mods, mods, data).deployWarning is not null,
              "the mods root itself has no provider folder and therefore warns");
        Check(LoadOrderService.ScriptOutputContract(Path.Combine(mods, "X", "Sub"), mods, data).deployWarning is not null,
              "a nested staging path warns because it would not land at Data/Scripts");
        Check(LoadOrderService.ScriptOutputContract(Path.Combine(data, "Sub"), mods, data).deployWarning is not null,
              "a nested Data path warns because the game loads only Data/Scripts");
        Check(LoadOrderService.ScriptOutputContract(Path.Combine(pathRoot, "staging", "modsX", "Foo"), mods, data).deployWarning is not null,
              "segment-boundary safe: a modsX sibling is not inside the mods root");

        // ---------------------------------------------------------- B3: ResolveExplicitScriptFolder — no patch folder cut
        Console.WriteLine();
        Console.WriteLine("--- B3: ResolveExplicitScriptFolder — user-owned, ResolvePatchModFolder NOT called, cleanup bypassed ---");
        var bRoot = Path.Combine(Path.GetTempPath(), "hc-comperg-b-" + Guid.NewGuid().ToString("N"));
        var tStore = Path.Combine(bRoot, "user.json");
        var tMods = Path.Combine(bRoot, "mods");
        var tData = Path.Combine(bRoot, "game", "Data");
        var tOut = Path.Combine(bRoot, "elsewhere", "MyMod");           // OUTSIDE the mods tree
        try
        {
            Directory.CreateDirectory(tMods); Directory.CreateDirectory(tData);
            var svc = LoadOrderService.WithExplicitPaths(tData, tMods, "", 0, new UserConfigStore(tStore));

            var rf = svc.ResolveExplicitScriptFolder(tOut, out var warn);
            Check(rf.OutputDir == Path.Combine(tOut, "Scripts"), "output path = output_dir\\Scripts (the chosen contract)");
            Check(!rf.CreatedFresh, "the folder is USER-OWNED (CreatedFresh=false), so residue cleanup never deletes it");
            Check(Directory.Exists(rf.OutputDir), "the Scripts\\ folder is created under output_dir");
            Check(!Directory.EnumerateFileSystemEntries(tMods).Any(),
                  "ResolvePatchModFolder was NOT called — no houseCARL patch folder cut under ModsDir");
            Check(warn is not null, "output_dir outside the mods tree carries the deploy warning");
            // The load-bearing bypass: on a failed compile RemoveOrNameRiderResidue must NOT delete the user's folder.
            // (Asserting it returns null alone is too weak — an EMPTY fresh folder also returns null because it gets
            // deleted; the real tooth is that a user-owned folder SURVIVES.)
            var residue = svc.RemoveOrNameRiderResidue(rf);
            Check(residue is null && Directory.Exists(rf.OutputDir),
                  "residue cleanup never deletes a user-owned output_dir folder (CreatedFresh=false: returns null, folder survives)");

            // A path UNDER the mods tree is deployable → no warning.
            var rfIn = svc.ResolveExplicitScriptFolder(Path.Combine(tMods, "MyPatch"), out var warnIn);
            Check(rfIn.OutputDir == Path.Combine(tMods, "MyPatch", "Scripts") && warnIn is null,
                  "an output_dir under Amethyst staging deploys cleanly (no warning)");

            // review nit #4: if <output_dir>\Scripts already exists AS A FILE, the create throws IOException — it must be
            // re-stamped as a friendly InvalidOperationException (which the rider renders as a clean "error: ...") rather
            // than escaping to Guard.Tool's generic "internal failure" wording.
            var tColl = Path.Combine(bRoot, "collision");
            Directory.CreateDirectory(tColl);
            File.WriteAllText(Path.Combine(tColl, "Scripts"), "a file where the Scripts folder should go");
            bool friendly = false;
            try { svc.ResolveExplicitScriptFolder(tColl, out _); }
            catch (InvalidOperationException) { friendly = true; }
            catch { /* any other exception type → not friendly */ }
            Check(friendly, "a file at <output_dir>\\Scripts yields a friendly InvalidOperationException, not a raw IOException");
        }
        finally { try { Directory.Delete(bRoot, recursive: true); } catch { /* non-fatal */ } }

        // ---------------------------------------------------------- C: success message matches the actual destination (Q3)
        Console.WriteLine();
        Console.WriteLine("--- C: CompileTools.Render — the success line names the RIGHT destination (review nit #1) ---");
        var ok = new HousecarlCore.CompileResult(
            Success: true, ObjectName: "MyScript", PexPath: Path.Combine(pathRoot, "out", "Scripts", "MyScript.pex"),
            Diagnostics: Array.Empty<HousecarlCore.PapyrusDiagnostic>(), Stdout: "", Stderr: "", ExitCode: 0, RunError: null);
        var defaultMsg = CompileTools.Render(ok, Array.Empty<string>(), userChoseOutputDir: false);
        var outDirMsg = CompileTools.Render(ok, Array.Empty<string>(), userChoseOutputDir: true);
        Check(defaultMsg.Contains("houseCARL staging mod folder") && defaultMsg.Contains("refresh Amethyst"),
              "default destination: success names the staging mod folder and Amethyst refresh step");
        Check(outDirMsg.Contains("output folder you chose") && !outDirMsg.Contains("houseCARL patch-mod folder"),
              "output_dir= destination: success names the user's chosen folder, NOT a houseCARL patch folder (no over-claim)");

        // ---------------------------------------------------------- D: missing-imports LEAD on a dominated failure (HCBR-2026-06-25)
        Console.WriteLine();
        Console.WriteLine("--- D: a failure dominated by unresolved-symbol errors LEADS with the import_dirs hint (not 'fix the code') ---");

        // D1: PapyrusCompile.IsUnresolvedSymbol on the REAL captured CK compiler wording (missing PO3/SkyUI/JContainers
        // sources, 2026-06-26) — the four resolution-error shapes are import-class; the syntax shapes are NOT.
        Check(HousecarlCore.PapyrusCompile.IsUnresolvedSymbol("unknown type po3_sksefunctions"), "import-class: 'unknown type …'");
        Check(HousecarlCore.PapyrusCompile.IsUnresolvedSymbol("variable JValue is undefined"), "import-class: '… is undefined'");
        Check(HousecarlCore.PapyrusCompile.IsUnresolvedSymbol("none is not a known user-defined type"), "import-class: '… is not a known user-defined type' (cascade)");
        Check(HousecarlCore.PapyrusCompile.IsUnresolvedSymbol("HC_ImportOrderProbeExt is not a function or does not exist"), "import-class: '… is not a function or does not exist'");
        Check(!HousecarlCore.PapyrusCompile.IsUnresolvedSymbol("no viable alternative at character '@'"), "syntax (NOT import): 'no viable alternative …'");
        Check(!HousecarlCore.PapyrusCompile.IsUnresolvedSymbol("missing EOF at 'EndFunction'"), "syntax (NOT import): 'missing EOF …'");
        Check(!HousecarlCore.PapyrusCompile.IsUnresolvedSymbol("Unknown user flag papyrus"), "syntax (NOT import): 'Unknown user flag …'");

        // D2: a FAILED compile whose diagnostics are the real missing-import avalanche → Render LEADS with the banner + count.
        HousecarlCore.PapyrusDiagnostic Diag(string msg) =>
            new(Path.Combine(pathRoot, "mod", "Scripts", "HCMissingImports.psc"), 8, 1, msg);
        var missingImports = new HousecarlCore.CompileResult(
            Success: false, ObjectName: "HCMissingImports", PexPath: null,
            Diagnostics: new[]
            {
                Diag("unknown type po3_sksefunctions"),
                Diag("unknown type ski_configbase"),
                Diag("variable PO3_SKSEFunctions is undefined"),
                Diag("none is not a known user-defined type"),
                Diag("variable JValue is undefined"),
                Diag("variable JMap is undefined"),
            },
            Stdout: "", Stderr: "", ExitCode: 0, RunError: null);
        var miMsg = CompileTools.Render(missingImports, Array.Empty<string>(), userChoseOutputDir: false);
        Check(miMsg.Contains("INCOMPLETE import_dirs"), "dominated failure LEADS with the 'INCOMPLETE import_dirs' banner");
        Check(miMsg.Contains("6 of 6") && miMsg.IndexOf("INCOMPLETE import_dirs", StringComparison.Ordinal) < miMsg.IndexOf("diagnostic(s)", StringComparison.Ordinal),
              "the banner names the count (6 of 6) and precedes the diagnostic list");

        // D3: a SYNTAX failure (the real captured broken-script wording) must NOT mislabel a code bug as a missing import —
        // no banner, and the generic import tail still rides along as the fallback hint.
        var syntaxFail = new HousecarlCore.CompileResult(
            Success: false, ObjectName: "HCBad", PexPath: null,
            Diagnostics: new[]
            {
                Diag("no viable alternative at character '@'"),
                Diag("no viable alternative at input 'papyrus'"),
                Diag("Unknown user flag papyrus"),
                Diag("missing EOF at 'EndFunction'"),
            },
            Stdout: "", Stderr: "", ExitCode: 0, RunError: null);
        var synMsg = CompileTools.Render(syntaxFail, Array.Empty<string>(), userChoseOutputDir: false);
        Check(!synMsg.Contains("INCOMPLETE import_dirs"), "a syntax-only failure does NOT trigger the missing-imports banner (no false positive)");
        Check(synMsg.Contains("import path") && synMsg.Contains("import_dirs="), "a syntax-only failure keeps the generic import-path tail as the fallback hint");

        // Helper: build a failed compile from N unresolved + M syntax diagnostics, to pin the gate boundary exactly.
        HousecarlCore.CompileResult Fail(int unresolved, int syntax)
        {
            var ds = new List<HousecarlCore.PapyrusDiagnostic>();
            for (int i = 0; i < unresolved; i++) ds.Add(Diag($"unknown type frameworktype{i}"));
            for (int i = 0; i < syntax; i++) ds.Add(Diag($"no viable alternative at input 'tok{i}'"));
            return new HousecarlCore.CompileResult(false, "HCBoundary", null, ds, "", "", 0, null);
        }
        bool Banner(HousecarlCore.CompileResult r) =>
            CompileTools.Render(r, Array.Empty<string>(), userChoseOutputDir: false).Contains("INCOMPLETE import_dirs");

        // D4: COUNT floor — 2 unresolved is below the >=3 minimum regardless of ratio, so no banner (a 1-2 error typo).
        Check(!Banner(Fail(unresolved: 2, syntax: 0)), "2 unresolved (100% but < 3) → no banner (the >=3 count floor)");

        // D5: the reviewer's boundary — a near-EVEN 3-of-6 split (half could be real syntax bugs) must NOT earn the
        // confident "not a bug" banner under the >=2/3 gate (3/6 = 50% < 66%). It falls to the generic import tail instead.
        Check(!Banner(Fail(unresolved: 3, syntax: 3)), "3-of-6 (50%) → NO banner: a near-even split is below the 2/3 supermajority bar");

        // D6: exactly AT the 2/3 bar (4-of-6) DOES fire — the gate is inclusive at two-thirds.
        Check(Banner(Fail(unresolved: 4, syntax: 2)), "4-of-6 (exactly 2/3) → banner fires (the gate is inclusive at two-thirds)");
        // …and just over keeps firing (the overwhelming real signature: ~all unresolved).
        Check(Banner(Fail(unresolved: 5, syntax: 1)), "5-of-6 (>2/3) → banner fires");

        Console.WriteLine();
        Console.WriteLine(fail == 0
            ? "================ ALL PASS ================"
            : $"================ {fail} CHECK(S) FAILED ================");
        return fail == 0 ? 0 : 1;
    }
}
