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
            Assert.False(File.Exists(Path.Combine(dir, "Example.pex")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
