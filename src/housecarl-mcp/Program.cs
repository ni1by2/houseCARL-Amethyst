using HousecarlMcp;
using ModelContextProtocol.Protocol;

// houseCARL MCP server. Stdio by default, --http for the localhost HTTP transport; either way it reads the active
// load order statically from the configured MO2 instance, and an empty config still boots.

bool useHttp = args.Contains("--http");
var hostArgs = args.Where(a => a != "--http").ToArray();   // strip our own flag so the config provider doesn't choke on it

// The optional cut on the published schemas, read before either host is built; a bad value stops the start here.
int? maxSchemaDepth;
try
{
    maxSchemaDepth = SchemaDepthCap.Configured();
}
catch (ArgumentException bad)
{
    Console.Error.WriteLine(bad.Message);
    return 1;
}

if (useHttp)
{
    var builder = WebApplication.CreateBuilder(hostArgs);
    var (svc, explicitMode, instanceDir, instanceSource, configNote) = SetupHouseCarl(builder.Configuration, builder.Services);
    AddMcp(builder.Services, stdio: false, maxSchemaDepth, svc);

    var app = builder.Build();
    app.MapMcp();

    var url = builder.Configuration.GetSection("HouseCarl")["Url"] is { Length: > 0 } u ? u : "http://127.0.0.1:7345";
    if (configNote is not null)
        app.Logger.LogWarning("houseCARL user config recovered: {Note}", configNote);   // corrupt file — backed up, never silent
    if (!svc.IsConfigured)
        app.Logger.LogWarning("houseCARL listening on {Url} — {Reason}", url, svc.ConfigPromptOrNull());
    else
        app.Logger.LogInformation(
            "houseCARL listening on {Url} — reading {Source}; load order resolves lazily on the first tool call.",
            url, svc.UsesSavedSnapshot ? "Amethyst saved snapshot (freshness unverified)" : explicitMode ? "explicit configured paths" : $"MO2 instance '{instanceDir}' [{instanceSource}]");
    app.Run(url);
}
else
{
    var builder = Host.CreateApplicationBuilder(hostArgs);
    // STDIO GOTCHA: stdout IS the JSON-RPC channel — route ALL logs to stderr or they corrupt the protocol stream.
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

    var (svc, explicitMode, instanceDir, instanceSource, configNote) = SetupHouseCarl(builder.Configuration, builder.Services);
    AddMcp(builder.Services, stdio: true, maxSchemaDepth, svc);

    var app = builder.Build();

    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("houseCARL");
    if (configNote is not null)
        logger.LogWarning("houseCARL user config recovered: {Note}", configNote);   // corrupt file — backed up, never silent
    if (!svc.IsConfigured)
        logger.LogWarning("houseCARL stdio server — {Reason}", svc.ConfigPromptOrNull());
    else
        logger.LogInformation(
            "houseCARL stdio server — reading {Source}; load order resolves lazily on the first tool call.",
            svc.UsesSavedSnapshot ? "Amethyst saved snapshot (freshness unverified)" : explicitMode ? "explicit configured paths" : $"MO2 instance '{instanceDir}' [{instanceSource}]");
    await app.RunAsync();
}

return 0;   // the refusal above returns 1, so the exit code is spelled on both paths

// Shared setup — both transports call these, so the load order resolves identically either way.

// Loads the rulebook and applies the MO2-instance precedence: saved user config > explicit DataDir+ModsDir+ProfileDir
// > Mo2InstanceDir > unconfigured; builds and registers the LoadOrderService.
static (LoadOrderService svc, bool explicitMode, string? instanceDir, string instanceSource, string? configNote) SetupHouseCarl(IConfiguration config, IServiceCollection services)
{
    var cfg = config.GetSection("HouseCarl");

    var corpusPath = cfg["CorpusPath"];
    if (string.IsNullOrWhiteSpace(corpusPath))
        corpusPath = Path.Combine(AppContext.BaseDirectory, "corpus.json");
    CorpusRulebook.CorpusPath = Path.GetFullPath(corpusPath);

    // Keep installed user state in the same XDG configuration directory as the Amethyst fork.
    var pluginDataDir = Environment.GetEnvironmentVariable("HOUSECARL_DATA_DIR");
    var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
    var configRoot = !string.IsNullOrWhiteSpace(xdgConfigHome) && Path.IsPathFullyQualified(xdgConfigHome)
        ? xdgConfigHome : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
    var userConfigDir = !string.IsNullOrWhiteSpace(pluginDataDir) ? pluginDataDir
        : OperatingSystem.IsLinux() ? Path.Combine(configRoot, "housecarl-amethyst") : AppContext.BaseDirectory;
    var userConfigPath = Path.Combine(userConfigDir, "houseCARL.user.json");
    // One owner of houseCARL.user.json, so the instance dir and the tool paths do not clobber each other.
    var store = new UserConfigStore(userConfigPath);
    services.AddSingleton(store);
    var saved = store.Load(out var configNote);
    string? userInstanceDir = saved.Mo2InstanceDir;

    // The saved user config wins over Mo2InstanceDir: the runtime switch beats the install default.
    bool fromUser = !string.IsNullOrWhiteSpace(userInstanceDir);
    var instanceDir = fromUser ? userInstanceDir : cfg["Mo2InstanceDir"];
    var instanceSource = fromUser ? "saved user config" : "Mo2InstanceDir (install dialog / appsettings)";
    var maxPlugins = int.TryParse(cfg["MaxPlugins"], out var mp) ? mp : 0;

    var dataDir = cfg["DataDir"]; var modsDir = cfg["ModsDir"]; var profileDir = cfg["ProfileDir"];
    bool explicitMode = !fromUser
        && !string.IsNullOrWhiteSpace(dataDir) && !string.IsNullOrWhiteSpace(modsDir) && !string.IsNullOrWhiteSpace(profileDir);

    LoadOrderService LinuxService()
    {
        var connection = saved.AmethystConnectionManifest ?? cfg["ConnectionManifest"];
        var mode = saved.AmethystFilegraphMode ?? cfg["FilegraphMode"];
        if (string.IsNullOrWhiteSpace(connection) || mode != "saved-snapshot")
            return LoadOrderService.WithUnavailableManager(store,
                "error: select an Amethyst connection and explicitly enable warned reads with " + ToolNames.SetAmethystConnection +
                " manifest_path=<absolute connection manifest> filegraph_mode='saved-snapshot'. New record patches are written to staging and require Amethyst deployment.");
        try { return LoadOrderService.WithAmethystSnapshot(connection, store); }
        catch (AmethystConfigurationException ex) { return LoadOrderService.WithUnavailableManager(store, "error: " + ex.Message); }
    }

    LoadOrderService svc = OperatingSystem.IsLinux()
        ? LinuxService()
        : explicitMode
            ? LoadOrderService.WithExplicitPaths(dataDir!, modsDir!, profileDir!, maxPlugins, store)
            : LoadOrderService.WithInstance(instanceDir, maxPlugins, store);
    services.AddSingleton(svc);

    // The external-tool bridge (compile / BSA / log access): one resolver over the shared user config.
    services.AddSingleton(new ToolPathResolver(store));

    // The Nexus Mods read bridge: a typed, keyless HttpClient, and houseCARL's only outbound network dependency.
    services.AddHttpClient<NexusClient>(c =>
    {
        c.Timeout = TimeSpan.FromSeconds(20);
        c.DefaultRequestHeaders.UserAgent.ParseAdd("houseCARL (+https://github.com/Avick3110/houseCARL)");
        // The Nexus Acceptable-Use Policy requires these two headers on API traffic, so they ride every request.
        c.DefaultRequestHeaders.Add("Application-Name", "houseCARL");
        c.DefaultRequestHeaders.Add("Application-Version", ServerVersion());
    });

    return (svc, explicitMode, instanceDir, instanceSource, configNote);
}

// The MCP server registration: identity, instructions, and the attribute-registered tools.
static void AddMcp(IServiceCollection services, bool stdio, int? maxSchemaDepth, LoadOrderService svc)
{
    var mcp = services.AddMcpServer(options =>
    {
        // The one place in code that carries the houseCARL brand string.
        options.ServerInfo = new Implementation { Name = "houseCARL", Version = ServerVersion() };
        // Claude Code delivers only the first 2,048 characters; ServerInstructionsText keeps safety rules first.
        options.ServerInstructions = ServerInstructionsText.Build(OperatingSystem.IsLinux());
    });
    // Stateless HTTP: each request is independent; the singletons persist across requests regardless.
    if (stdio) mcp.WithStdioServerTransport();
    else mcp.WithHttpTransport(o => o.Stateless = true);
    // Named, not implicit: the parameterless overload registers from the calling assembly.
    mcp.WithToolsFromAssembly(ToolSurface.Assembly);
    // The published-schema layer; see ToolSchemas.
    ToolSchemas.PublishSchemas(services, maxSchemaDepth);
    // The argument-binding shim; see ToolCallShim.
    mcp.WithRequestFilters(f => f.AddCallToolFilter(ToolCallShim.LenientArguments));
    mcp.WithRequestFilters(f => f.AddCallToolFilter(AmethystSnapshotFilter.For(svc)));
}

// The exe's stamped version for ServerInfo.
static string ServerVersion() => ServerBuild.Handshake;
