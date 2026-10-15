using HousecarlMcp;

namespace HousecarlMcpTests;

/// <summary>An isolated saved profile and its real MCP server for positive write-binding checks.</summary>
sealed class AmethystWireFixture : IDisposable
{
    public AmethystSnapshotWorld World { get; } = new();
    public ServerFixture Server { get; }
    public AmethystWireFixture(int? schemaDepth = null)
    {
        File.WriteAllText(Path.Combine(World.Library, "filegraph.lock"), "");
        var environment = new Dictionary<string, string>
        {
            ["HouseCarl__ConnectionManifest"] = World.Manifest,
            ["HouseCarl__FilegraphMode"] = "saved-snapshot",
            ["HouseCarl__CorpusPath"] = TestCorpus.Path,
            ["HOUSECARL_DATA_DIR"] = Path.GetDirectoryName(World.Store.FilePath)!,
            ["XDG_STATE_HOME"] = Path.Combine(World.Root, "state")
        };
        if (schemaDepth is { } depth) environment[SchemaDepthCap.Variable] = depth.ToString();
        Server = new ServerFixture(environment);
    }
    public void Dispose() { Server.Dispose(); World.Dispose(); }
}
