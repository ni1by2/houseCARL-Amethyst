namespace HousecarlMcpTests;

/// <summary>Re-roots legacy physical-path examples on the host root; never used for virtual asset paths.</summary>
static class NativeTestPath
{
    public static string Of(string example) => OperatingSystem.IsWindows() ? example
        : example.StartsWith(@"C:\", StringComparison.Ordinal)
            ? "/" + example[3..].Replace('\\', '/') : example;
}
