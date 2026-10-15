using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

[Trait("tier", "unit")]
public sealed class WindowsProgramGuardTests
{
    [Fact]
    public void WindowsCompilerIsRefusedBeforeExecutionOnLinux()
    {
        if (!OperatingSystem.IsLinux()) return;
        var dir = Path.Combine(Path.GetTempPath(), "hc-windows-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var exe = Path.Combine(dir, "PapyrusCompiler.exe");
            File.WriteAllBytes(exe, "MZ"u8.ToArray());

            var result = PapyrusCompile.CompileObject(exe, "Example", Array.Empty<string>(), dir);

            Assert.False(result.Ran);
            Assert.Contains("Proton", result.RunError);
            Assert.Contains("CompatToolMapping", result.RunError);
            Assert.Contains("config_info may be stale", result.RunError);
            Assert.False(File.Exists(Path.Combine(dir, "Example.pex")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LinuxNeverExecutesAFileMasqueradingAsTheWindowsCompiler()
    {
        if (!OperatingSystem.IsLinux()) return;
        var dir = Path.Combine(Path.GetTempPath(), "hc-windows-guard-script-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var marker = Path.Combine(dir, "executed");
            var exe = Path.Combine(dir, "PapyrusCompiler.exe");
            File.WriteAllText(exe, "#!/bin/sh\ntouch '" + marker + "'\n");
            File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var result = PapyrusCompile.CompileObject(exe, "Example", Array.Empty<string>(), dir);

            Assert.False(result.Ran);
            Assert.Contains("CompatToolMapping", result.RunError);
            Assert.False(File.Exists(marker));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ExistingPexIsRefusedBeforeAnyCompilerLaunch()
    {
        if (!OperatingSystem.IsLinux()) return;
        var dir = Path.Combine(Path.GetTempPath(), "hc-existing-pex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var marker = Path.Combine(dir, "executed");
            var exe = Path.Combine(dir, "PapyrusCompiler.exe");
            File.WriteAllText(exe, "#!/bin/sh\ntouch '" + marker + "'\n");
            File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var pex = Path.Combine(dir, "Example.pex"); File.WriteAllText(pex, "preserve");

            var result = PapyrusCompile.CompileObject(exe, "Example", Array.Empty<string>(), dir);

            Assert.False(result.Ran);
            Assert.Contains("already exists", result.RunError);
            Assert.False(File.Exists(marker));
            Assert.Equal("preserve", File.ReadAllText(pex));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
