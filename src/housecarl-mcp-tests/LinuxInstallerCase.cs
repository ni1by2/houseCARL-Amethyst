using System.Diagnostics;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Include a named native-installer regression in the Release suite; its fixtures use temporary homes.</summary>
static class LinuxInstallerCase
{
    public static void Run(string name)
    {
        var start = new ProcessStartInfo("python3")
        { WorkingDirectory = Path.Combine(HarnessPaths.RepoRoot, "scripts", "tests"),
          RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "-m", "unittest", "test_install_linux.LinuxInstallerTests." + name }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000)) { process.Kill(true); throw new TimeoutException("native installer regression timed out"); }
        Assert.True(process.ExitCode == 0, output.GetAwaiter().GetResult() + errors.GetAwaiter().GetResult());
    }
}
