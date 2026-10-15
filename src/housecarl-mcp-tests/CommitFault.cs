using Xunit;

namespace HousecarlMcpTests;

/// <summary>Fail only the selected commit after its real payload is staged; isolate the fault to this async test.</summary>
sealed class CommitFault : IDisposable
{
    readonly Action<string, string>? _previous = AtomicFile.BeforeCommitForTest.Value;
    public bool Reached { get; private set; }
    public CommitFault(string target)
    {
        AtomicFile.BeforeCommitForTest.Value = (staged, destination) =>
        {
            if (Path.GetFullPath(destination) != Path.GetFullPath(target)) { _previous?.Invoke(staged, destination); return; }
            Assert.True(File.Exists(staged));
            Assert.NotEmpty(File.ReadAllBytes(staged));
            Reached = true;
            throw new IOException("injected commit failure after staging");
        };
    }
    public void Dispose() => AtomicFile.BeforeCommitForTest.Value = _previous;
}
