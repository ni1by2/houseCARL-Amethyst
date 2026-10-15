namespace HousecarlMcpTests;

/// <summary>Deny directory mutation while retaining reads, and restore the mode even after a failed assertion.</summary>
sealed class WriteBlock : IDisposable
{
    readonly string _directory;
    readonly UnixFileMode _mode;
    public WriteBlock(string file)
    {
        _directory = Path.GetDirectoryName(file)!;
        _mode = File.GetUnixFileMode(_directory);
        File.SetUnixFileMode(_directory, _mode & ~(UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
        var probe = Path.Combine(_directory, ".deny-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(probe, "must fail");
            File.Delete(probe);
            throw new InvalidOperationException("this account bypasses directory write permissions; failure fixture is unproven");
        }
        catch (UnauthorizedAccessException) { }
        catch { Dispose(); throw; }
    }
    public void Dispose() => File.SetUnixFileMode(_directory, _mode);
}
