using System.Text;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Generated reports become visible only when complete and never replace another writer's output.</summary>
[Trait("tier", "unit")]
public sealed class ArtifactPublicationTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-artifact-publication-" + Guid.NewGuid().ToString("N"));

    public ArtifactPublicationTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void AReportHasNoPublishedPathUntilAllItsBytesAreWritten()
    {
        using var target = ResultsStore.Reserve(_dir, ToolNames.Records, "test-epoch");
        Assert.Empty(Directory.GetFiles(_dir));
        var bytes = Encoding.UTF8.GetBytes("a complete generated report\n");
        target.Write(stream =>
        {
            stream.Write(bytes.AsSpan(0, 5));
            stream.Flush();
            Assert.False(File.Exists(target.Path));
            Assert.Empty(Directory.GetFiles(_dir, "*.jsonl"));
            stream.Write(bytes.AsSpan(5));
        });
        Assert.Equal(bytes, File.ReadAllBytes(Assert.Single(Directory.GetFiles(_dir))));
    }

    [Fact]
    public void AFailedWriteLeavesNoPublishedReportOrTemporaryFile()
    {
        using var target = ResultsStore.Reserve(_dir, ToolNames.Records, "test-epoch");
        Assert.Throws<IOException>(() => target.Write(stream =>
        {
            stream.WriteByte(42);
            throw new IOException("injected report write failure");
        }));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void AScannerCanHoldTheTemporaryFileAcrossPublication()
    {
        var bytes = Encoding.UTF8.GetBytes("the complete report\n");
        using var target = ResultsStore.Reserve(_dir, ToolNames.Records, "test-epoch");
        FileStream? scanner = null;
        try
        {
            ArtifactTarget.BeforePublishForTest.Value = (scratch, final) =>
            {
                Assert.False(File.Exists(final));
                scanner = new FileStream(scratch, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                Assert.Equal(bytes, File.ReadAllBytes(scratch));
            };
            target.Write(stream => stream.Write(bytes));
            Assert.NotNull(scanner);
            Assert.Equal(bytes, File.ReadAllBytes(target.Path));
            Assert.Equal(target.Path, Assert.Single(Directory.GetFiles(_dir)));
            using var retainedBytes = new MemoryStream();
            scanner.CopyTo(retainedBytes);
            Assert.Equal(bytes, retainedBytes.ToArray());
        }
        finally
        {
            ArtifactTarget.BeforePublishForTest.Value = null;
            scanner?.Dispose();
        }
    }

    [Fact]
    public void AFailureImmediatelyBeforePublicationRemovesOnlyOurScratch()
    {
        var foreign = Path.Combine(_dir, "foreign.jsonl");
        File.WriteAllText(foreign, "preserve this report");
        using var target = ResultsStore.Reserve(_dir, ToolNames.Records, "test-epoch");
        try
        {
            ArtifactTarget.BeforePublishForTest.Value = (_, _) => throw new IOException("injected publication failure");
            var error = Assert.Throws<IOException>(() => target.Write(s => s.WriteByte(42)));
            Assert.Contains("injected publication failure", error.Message);
            Assert.Equal(foreign, Assert.Single(Directory.GetFiles(_dir)));
            Assert.Equal("preserve this report", File.ReadAllText(foreign));
        }
        finally { ArtifactTarget.BeforePublishForTest.Value = null; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnEntryAppearingAtTheFinalPathIsNeverReplacedOrDeleted(bool symlink)
    {
        var foreign = Path.Combine(_dir, "foreign.txt");
        File.WriteAllText(foreign, "another writer's bytes");
        var target = ResultsStore.Reserve(_dir, ToolNames.Records, "test-epoch");
        try
        {
            ArtifactTarget.BeforePublishForTest.Value = (_, final) =>
            {
                if (symlink) File.CreateSymbolicLink(final, foreign);
                else File.WriteAllText(final, "the competing report");
            };
            Assert.Throws<IOException>(() => target.Write(s => s.WriteByte(42)));
        }
        finally
        {
            ArtifactTarget.BeforePublishForTest.Value = null;
            target.Dispose();
        }
        Assert.Equal("another writer's bytes", File.ReadAllText(foreign));
        Assert.Equal(symlink ? "another writer's bytes" : "the competing report", File.ReadAllText(target.Path));
        Assert.Equal(2, Directory.GetFiles(_dir).Length);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp-*"));
    }

    [Fact]
    public void DisposingAnUnwrittenTargetDoesNotDeleteACompetingReport()
    {
        var target = ResultsStore.Reserve(_dir, ToolNames.Records, "test-epoch");
        File.WriteAllText(target.Path, "a competing report");
        target.Dispose();
        Assert.Equal("a competing report", File.ReadAllText(target.Path));
        Assert.Equal(target.Path, Assert.Single(Directory.GetFiles(_dir)));
    }

    [Fact]
    public async Task ConcurrentWritersPublishDistinctCompleteReports()
    {
        var paths = await Task.WhenAll(Enumerable.Range(0, 32).Select(id => Task.Run(() =>
        {
            using var target = ResultsStore.Reserve(_dir, ToolNames.Records, "test-epoch");
            var bytes = Encoding.UTF8.GetBytes($"report {id}\n" + new string('x', 8192));
            target.Write(s => s.Write(bytes));
            Assert.Equal(bytes, File.ReadAllBytes(target.Path));
            return target.Path;
        })));
        Assert.Equal(32, paths.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(32, Directory.GetFiles(_dir).Length);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp-*"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
