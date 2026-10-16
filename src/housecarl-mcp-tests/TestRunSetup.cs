using System.Reflection;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestFramework("HousecarlMcpTests.TestRunSetup", "housecarl-mcp-tests")]

namespace HousecarlMcpTests;

/// <summary>Sets up what every test in the run shares, once, before the first test runs and never on discovery: the
/// test corpus and private transaction state. A failure here stops the run with its own message.</summary>
public sealed class TestRunSetup : XunitTestFramework
{
    static readonly Lazy<string> PrivateStateHome = new(CreateStateHome);

    internal static string StateHome => PrivateStateHome.Value;

    static string CreateStateHome()
    {
        var path = Path.Combine(Path.GetTempPath(), "hc-test-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(path, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Console.Error.WriteLine($"Could not remove private test state '{path}': {ex.Message}"); }
        };
        return path;
    }

    public TestRunSetup(IMessageSink messageSink) : base(messageSink) { }

    protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName) =>
        new Executor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);

    sealed class Executor : XunitTestFrameworkExecutor
    {
        public Executor(AssemblyName assemblyName, ISourceInformationProvider sourceInformationProvider, IMessageSink diagnosticMessageSink)
            : base(assemblyName, sourceInformationProvider, diagnosticMessageSink) { }

        protected override void RunTestCases(IEnumerable<IXunitTestCase> testCases, IMessageSink executionMessageSink,
                                             ITestFrameworkExecutionOptions executionOptions)
        {
            Environment.SetEnvironmentVariable("XDG_STATE_HOME", StateHome);
            _ = TestCorpus.Path;
            base.RunTestCases(testCases, executionMessageSink, executionOptions);
        }
    }
}
