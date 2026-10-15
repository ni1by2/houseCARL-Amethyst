using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Pex;
using Xunit;

namespace HousecarlMcpTests;

/// <summary><see cref="PapyrusCompile.ParseDiagnostics"/> on the exact stderr the CK compiler emits (captured
/// 2026-06-05). Migrated from the <c>compile-probe</c> generator probe, layer 1.</summary>
[Trait("tier", "unit")]
public sealed class PapyrusCompileParseTests
{
    static readonly string RealStderr = string.Join("\n", new[]
    {
        @"C:\mod\Source\Scripts\HCBad.psc(4,4): no viable alternative at character '@'",
        @"C:\mod\Source\Scripts\HCBad.psc(4,18): no viable alternative at input 'papyrus'",
        @"C:\mod\Source\Scripts\HCBad.psc(4,12): Unknown user flag papyrus",
        @"C:\mod\Source\Scripts\HCBad.psc(5,0): missing EOF at 'EndFunction'",
    });

    // probe: "parses 4 diagnostics"
    [Fact]
    public void ParsesFourDiagnostics() => Assert.Equal(4, PapyrusCompile.ParseDiagnostics(RealStderr).Count);

    // probe: "first diagnostic: line=4 col=4 message intact"
    [Fact]
    public void FirstDiagnostic_LineFourColFour_MessageIntact()
    {
        var first = PapyrusCompile.ParseDiagnostics(RealStderr)[0];
        Assert.Equal((4, 4, "no viable alternative at character '@'"), (first.Line, first.Col, first.Message));
    }

    // probe: "later diagnostic line/col parsed"
    [Fact]
    public void LaterDiagnostic_LineAndColParsed()
    {
        var fourth = PapyrusCompile.ParseDiagnostics(RealStderr)[3];
        Assert.Equal((5, 0), (fourth.Line, fourth.Col));
    }

    // probe: "empty stderr → no diagnostics"
    [Fact]
    public void EmptyStderr_NoDiagnostics() => Assert.Empty(PapyrusCompile.ParseDiagnostics(""));

    [Fact]
    public void SemicolonInImportDirectoryIsRefusedBeforeCompilerLaunch() =>
        Assert.Equal("/tmp/scripts;other", PapyrusCompile.FindSemicolonImport(new[] { "/tmp/scripts;other", "/tmp/valid" }));

    // probe: "a non-diagnostic line is ignored (not mis-parsed)"
    [Fact]
    public void ANonDiagnosticLine_IsIgnored() =>
        Assert.Empty(PapyrusCompile.ParseDiagnostics("Batch compile of 1 files finished. 1 succeeded, 0 failed."));

    [Fact]
    public void CapricaDiagnosticRangesPreserveFilenameStartAndMessage()
    {
        // Actual pinned Caprica syntax, including its unsigned end-column on a lexer error.
        var diagnostics = PapyrusCompile.ParseDiagnostics(
            "Bad.psc (3, 2:4294967263): Error: Unexpected character '@'!\n" +
            "source (copy).psc (4, 5:12): Warning W1001: fixture warning\n" +
            "Compilation of 'Bad.psc' failed; 0 warnings and 3 errors were encountered.");
        Assert.Equal(2, diagnostics.Count);
        Assert.Equal(new PapyrusDiagnostic("Bad.psc", 3, 2, "Error: Unexpected character '@'!"), diagnostics[0]);
        Assert.Equal(new PapyrusDiagnostic("source (copy).psc", 4, 5, "Warning W1001: fixture warning"), diagnostics[1]);
    }

    [Fact]
    public void CapricaInvocationPinsGameFlagsConfigImportsAndOutput()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "hc-caprica-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var output = Path.Combine(root, "output"); Directory.CreateDirectory(output);
            var imports = Path.Combine(root, "imports"); Directory.CreateDirectory(imports);
            var source = Path.Combine(root, "CapProbe.psc"); File.WriteAllText(source, "Scriptname CapProbe\n");
            var flags = Path.Combine(root, "TESV_Papyrus_Flags.flg"); File.WriteAllText(flags, "Flag Hidden 0\n");
            var config = Path.Combine(root, "private.cfg"); File.WriteAllText(config, "");
            var pex = Path.Combine(root, "valid.pex"); PexWriter.WritePex(pex, "CapProbe", null);
            var executable = Path.Combine(root, "caprica-test");
            File.WriteAllText(executable, "#!/bin/sh\nout=\ngame=\nflags=\nconfig=\nimports=\n\nfor arg do\n case \"$arg\" in\n  --output=*) out=\"${arg#--output=}\";;\n  --game=skyrim) game=yes;;\n  --flags=*) flags=\"${arg#--flags=}\";;\n  --config-file=*) config=\"${arg#--config-file=}\";;\n  --import=*) imports=\"${arg#--import=}\";;\n  --ignorecwd) ignorecwd=yes;;\n  --enable-language-extensions=false) extensions=no;;\n esac\ndone\n[ \"$game\" = yes ] && [ \"$ignorecwd\" = yes ] && [ \"$extensions\" = no ] && [ \"$flags\" = '" + flags + "' ] && [ \"$config\" = '" + config + "' ] && [ \"$imports\" = '" + imports + "' ] || exit 9\ncp '" + pex + "' \"$out/CapProbe.pex\"\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var result = PapyrusCompile.CompileCaprica(executable, source, "CapProbe", new[] { imports }, flags, output, config);

            Assert.True(result.Success, result.RunError + "\n" + result.Stdout + "\n" + result.Stderr);
            Assert.Equal(Path.Combine(output, "CapProbe.pex"), result.PexPath);
            Assert.Single(Mutagen.Bethesda.Pex.PexFile.CreateFromFile(result.PexPath!, Mutagen.Bethesda.GameCategory.Skyrim).Objects);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void CapricaVersionProbeReadsTheExecutableBanner()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "hc-caprica-version-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var executable = Path.Combine(root, "caprica-version");
            File.WriteAllText(executable, "#!/bin/sh\necho 'Caprica Papyrus Compiler v0.3.0' >&2\nexit 255\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            Assert.Equal("v0.3.0", PapyrusCompile.ProbeCapricaVersion(executable));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ChampollionRunnerUsesSinglePexAndValidatesExactSourceSet()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "hc-champollion-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var pex = Path.Combine(root, "Input.pex"); PexWriter.WritePex(pex, "CapProbe", null);
            var output = Path.Combine(root, "output"); Directory.CreateDirectory(output);
            var executable = Path.Combine(root, "champollion-test");
            File.WriteAllText(executable, "#!/bin/sh\nif [ \"$1\" = --version ]; then echo 'Champollion PEX decompiler v1.3.2'; exit 0; fi\n[ \"$1\" = --psc ] || exit 7\nprintf 'Scriptname CapProbe\\n' > \"$2/CapProbe.psc\"\n[ \"$3\" = '" + pex + "' ] || exit 8\necho '1 files processed'\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var result = PapyrusCompile.DecompileChampollion(executable, pex, new[] { "CapProbe" }, output);

            Assert.True(result.Success, result.RunError + "\n" + result.Stdout + "\n" + result.Stderr);
            Assert.Equal("1.3.2", result.Version);
            Assert.Equal(Path.Combine(output, "CapProbe.psc"), Assert.Single(result.Sources));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ChampollionRejectsUnsafeObjectNamesBeforeStartingTheProcess()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "hc-champollion-unsafe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var pex = Path.Combine(root, "Input.pex"); PexWriter.WritePex(pex, "Safe", null);
            var output = Path.Combine(root, "output"); Directory.CreateDirectory(output);
            var marker = Path.Combine(root, "started");
            var executable = Path.Combine(root, "Champollion-test");
            File.WriteAllText(executable, "#!/bin/sh\ntouch '" + marker + "'\n");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var result = PapyrusCompile.DecompileChampollion(executable, pex, new[] { "../Escape" }, output);

            Assert.False(result.Success);
            Assert.Contains("safe plain script basename", result.RunError);
            Assert.False(File.Exists(marker));
            Assert.Empty(Directory.EnumerateFiles(output));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}

/// <summary>Reports an unavailable native compiler explicitly; an installed but broken compiler must fail.</summary>
public sealed class CapricaFactAttribute : FactAttribute
{
    /// <summary>Use an explicit native tool path or the reproducible source-build output in this checkout.</summary>
    public static string Compiler =>
        Environment.GetEnvironmentVariable("HOUSECARL_TEST_CAPRICA") is { Length: > 0 } set
            ? Path.GetFullPath(set)
            : Path.Combine(HarnessPaths.RepoRoot, "dist", "linux-tools", "caprica");

    public CapricaFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Native Caprica backend requires Linux.";
        else if (!File.Exists(Compiler))
            Skip = $"No native Caprica at '{Compiler}'; build scripts/build-linux-tools.py or set HOUSECARL_TEST_CAPRICA to run this.";
    }
}

/// <summary>Real Linux Caprica compilation with authored sources; no game installation or substitute compiler.</summary>
[Trait("tier", "integration")]
public sealed class PapyrusCompileEndToEndTests : IDisposable
{
    const string Good = "Scriptname HCGood\n\nInt Function Foo()\n    return 17\nEndFunction\n";
    const string Bad = "Scriptname HCBad\n\nInt Function Foo()\n    @@@ not valid papyrus\nEndFunction\n";

    readonly string _work = Path.Combine(Path.GetTempPath(), "hc-caprica fixture-" + Guid.NewGuid().ToString("N"));
    readonly string _out;
    readonly string _imports;
    readonly string _flags;
    readonly string _config;
    int _run;

    public PapyrusCompileEndToEndTests()
    {
        _out = Path.Combine(_work, "out");
        Directory.CreateDirectory(_out);
        _imports = Path.Combine(_work, "source imports");
        Directory.CreateDirectory(_imports);
        _flags = Path.Combine(_work, "Skyrim.flg");
        _config = Path.Combine(_work, "private.cfg");
        File.WriteAllText(_flags, "flag Hidden 0 { script property }\nflag Conditional 1 { script variable }\n");
        File.WriteAllText(_config, "");
        File.WriteAllText(Path.Combine(_work, "HCGood.psc"), Good);
        File.WriteAllText(Path.Combine(_work, "HCBad.psc"), Bad);
        File.WriteAllText(Path.Combine(_imports, "HCParent.psc"), "Scriptname HCParent Hidden\n");
        File.WriteAllText(Path.Combine(_work, "HCImported.psc"),
            "Scriptname HCImported extends HCParent\n\nInt Function Foo()\n    return 23\nEndFunction\n");
    }

    public void Dispose()
    {
        Directory.Delete(_work, recursive: true);
    }

    CompileResult Compile(string name)
    {
        var fresh = Path.Combine(_out, "run-" + (++_run).ToString("D3"));
        Directory.CreateDirectory(fresh);
        return PapyrusCompile.CompileCaprica(CapricaFactAttribute.Compiler, Path.Combine(_work, name + ".psc"),
            name, new[] { _imports }, _flags, fresh, _config);
    }

    static PexObject ProducedObject(CompileResult result, string name, int returned)
    {
        Assert.True(result.Ran, result.RunError);
        Assert.True(result.Success, result.Stdout + "\n" + result.Stderr);
        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.PexPath);
        var pex = PexFile.CreateFromFile(result.PexPath, GameCategory.Skyrim);
        Assert.Equal(1, pex.GameId);
        var obj = (PexObject)Assert.Single(pex.Objects);
        Assert.Equal(name, obj.Name);
        var foo = Assert.Single(obj.States.SelectMany(s => s.Functions), f => f.FunctionName == "Foo").Function!;
        Assert.Equal("Int", foo.ReturnTypeName, ignoreCase: true);
        var instruction = Assert.Single(foo.Instructions, i => i.OpCode == InstructionOpcode.RETURN);
        var argument = Assert.Single(instruction.Arguments);
        Assert.Equal(VariableType.Integer, argument.VariableType);
        Assert.Equal(returned, argument.IntValue);
        return obj;
    }

    // Source discovery now proves that a separate import is used, and its absence is diagnosed.
    [CapricaFact]
    public void SeparateImportSources_AreUsed_AndMissingDependencyIsDiagnosed()
    {
        var good = Compile("HCImported");
        Assert.Equal("HCParent", ProducedObject(good, "HCImported", 23).ParentClassName);
        var bytes = File.ReadAllBytes(good.PexPath!);
        File.Delete(Path.Combine(_imports, "HCParent.psc"));

        var missing = Compile("HCImported");
        Assert.True(missing.Ran, missing.RunError);
        Assert.False(missing.Success);
        Assert.Null(missing.PexPath);
        Assert.NotEqual(0, missing.ExitCode);
        Assert.Contains(missing.Diagnostics, d => d.Message.Contains("HCParent", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(bytes, File.ReadAllBytes(good.PexPath!));
        Assert.Empty(Directory.GetFiles(Path.Combine(_out, "run-002"), "*.pex", SearchOption.AllDirectories));
    }

    // probe: "good: compiler ran", "good: Success + .pex written", "good: no diagnostics"
    [CapricaFact]
    public void GoodScript_Runs_WritesPex_NoDiagnostics()
    {
        var good = Compile("HCGood");
        ProducedObject(good, "HCGood", 17);
        Assert.Empty(good.Diagnostics);
    }

    // probe: "bad: compiler ran", "bad: Success=false + no .pex", "bad: structured diagnostics produced"
    [CapricaFact]
    public void BadScript_Runs_Fails_NoPex_WithDiagnostics()
    {
        var bad = Compile("HCBad");
        Assert.True(bad.Ran, bad.RunError);
        Assert.False(bad.Success);
        Assert.Null(bad.PexPath);
        Assert.NotEqual(0, bad.ExitCode);
        Assert.Contains(bad.Diagnostics, d => Path.GetFileName(d.File) == "HCBad.psc" && d.Line == 4 &&
            d.Col >= 0 && !string.IsNullOrWhiteSpace(d.Message));
        Assert.Empty(Directory.GetFiles(_out, "*.pex", SearchOption.AllDirectories));
    }

    // Each invocation has a fresh output directory so stale PEX cannot be reported as fresh.
    [CapricaFact]
    public void RecompileStillValid_Succeeds_AndRewritesThePex()
    {
        var first = Compile("HCGood");
        ProducedObject(first, "HCGood", 17);
        var bytes = File.ReadAllBytes(first.PexPath!);
        File.WriteAllText(Path.Combine(_work, "HCGood.psc"), Good.Replace("return 17", "return 29"));
        var again = Compile("HCGood");
        ProducedObject(again, "HCGood", 29);
        Assert.NotEqual(first.PexPath, again.PexPath);
        Assert.Equal(bytes, File.ReadAllBytes(first.PexPath!));
    }

    // probe: "recompile (now broken): reports failure, no new .pex", "the PRIOR .pex is LEFT INTACT (non-destructive)"
    [CapricaFact]
    public void RecompileNowBroken_ReportsFailure_AndLeavesThePriorPex()
    {
        var first = Compile("HCGood");
        ProducedObject(first, "HCGood", 17);
        var bytes = File.ReadAllBytes(first.PexPath!);

        File.WriteAllText(Path.Combine(_work, "HCGood.psc"), Good.Replace("return 17", "@@@ broken edit"));
        var broken = Compile("HCGood");
        Assert.True(broken.Ran, broken.RunError);
        Assert.False(broken.Success);
        Assert.Null(broken.PexPath);
        Assert.NotEqual(0, broken.ExitCode);
        Assert.Contains(broken.Diagnostics, d => Path.GetFileName(d.File) == "HCGood.psc" && d.Line == 4);
        Assert.Equal(bytes, File.ReadAllBytes(first.PexPath!));
        Assert.Empty(Directory.GetFiles(Path.Combine(_out, "run-002"), "*.pex", SearchOption.AllDirectories));
    }
}
