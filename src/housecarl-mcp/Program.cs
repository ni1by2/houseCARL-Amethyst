using HousecarlMcp;
using ModelContextProtocol.Protocol;

// houseCARL MCP server. DEFAULT transport is STDIO (the 1.0 launch model): the MCP client (Claude Code) spawns
// this exe and talks JSON-RPC over stdin/stdout — no port, no console window, no manual start. Pass --http to run
// the localhost HTTP transport instead. Both modes read native Amethyst files through a schema-v1 connection manifest;
// an empty config still boots so the setup tool can connect it.

bool useHttp = args.Contains("--http");
var hostArgs = args.Where(a => a != "--http").ToArray();   // strip our own flag so the config provider doesn't choke on it

if (useHttp)
{
    var builder = WebApplication.CreateBuilder(hostArgs);
    var (svc, connection, connectionSource, configNote) = SetupHouseCarl(builder.Configuration, builder.Services);
    AddMcp(builder.Services, stdio: false);

    var app = builder.Build();
    app.MapMcp();

    var url = builder.Configuration.GetSection("HouseCarl")["Url"] is { Length: > 0 } u ? u : "http://127.0.0.1:7345";
    if (configNote is not null)
        app.Logger.LogWarning("houseCARL user config recovered: {Note}", configNote);   // corrupt file — backed up, never silent (hunt F3)
    if (!svc.IsConfigured)
        app.Logger.LogWarning(
            "houseCARL-Amethyst listening on {Url} — NOT connected yet. Call housecarl_set_amethyst_connection.", url);
    else
        app.Logger.LogInformation(
            "houseCARL-Amethyst listening on {Url} — reading {Source}; load order resolves lazily on the first tool call.",
            url, $"Amethyst connection '{connection}' [{connectionSource}]");
    app.Run(url);
}
else
{
    var builder = Host.CreateApplicationBuilder(hostArgs);
    // STDIO GOTCHA: stdout IS the JSON-RPC channel — route ALL logs to stderr or they corrupt the protocol stream.
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

    var (svc, connection, connectionSource, configNote) = SetupHouseCarl(builder.Configuration, builder.Services);
    AddMcp(builder.Services, stdio: true);

    var app = builder.Build();

    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("houseCARL");
    if (configNote is not null)
        logger.LogWarning("houseCARL user config recovered: {Note}", configNote);   // corrupt file — backed up, never silent (hunt F3)
    if (!svc.IsConfigured)
        logger.LogWarning(
            "houseCARL-Amethyst stdio server — NOT connected yet. Call housecarl_set_amethyst_connection.");
    else
        logger.LogInformation(
            "houseCARL-Amethyst stdio server — reading {Source}; load order resolves lazily on the first tool call.",
            $"Amethyst connection '{connection}' [{connectionSource}]");
    await app.RunAsync();
}

// ── shared setup — MUST stay identical across transports (divergence here = stdio and http resolving the load
//    order differently, a latent bug). Both branches call these; only the transport line itself differs. ──────────

// Builds + registers the LoadOrderService; returns the bits the boot log needs.
static (LoadOrderService svc, string? connection, string connectionSource, string? configNote) SetupHouseCarl(
    IConfiguration config,
    IServiceCollection services)
{
    var cfg = config.GetSection("HouseCarl");

    var corpusPath = cfg["CorpusPath"];
    if (string.IsNullOrWhiteSpace(corpusPath))
        corpusPath = Path.Combine(AppContext.BaseDirectory, "corpus.json");
    CorpusRulebook.CorpusPath = Path.GetFullPath(corpusPath);

    // user.json lives in the WRITABLE data dir — HOUSECARL_DATA_DIR (the plugin's ${CLAUDE_PLUGIN_DATA}, which survives
    // updates) when set, else beside the exe (dev / non-plugin). NEVER under the plugin root: the client wipes that dir on
    // every plugin update, which would silently drop the saved connection.
    var pluginDataDir = Environment.GetEnvironmentVariable("HOUSECARL_DATA_DIR");
    var userConfigDir = string.IsNullOrWhiteSpace(pluginDataDir) ? AppContext.BaseDirectory : pluginDataDir;
    var userConfigPath = Path.Combine(userConfigDir, "houseCARL.user.json");
    // ONE owner of houseCARL.user.json: the Amethyst connection and external-tool paths share the file,
    // so neither writer clobbers the other (read-modify-write under a cross-process lock; atomic writes). A corrupt file
    // never crashes boot, but it is NOT silent either (hunt F3): it's backed up and the note rides the boot log.
    var store = new UserConfigStore(userConfigPath);
    services.AddSingleton(store);
    string? userConnection = store.Load(out var configNote).AmethystConnectionManifest;

    // A runtime connection switch beats the optional install-time default.
    bool fromUser = !string.IsNullOrWhiteSpace(userConnection);
    var connection = fromUser ? userConnection : cfg["ConnectionManifest"];
    var connectionSource = fromUser ? "saved user config" : "ConnectionManifest (appsettings)";
    var maxPlugins = int.TryParse(cfg["MaxPlugins"], out var mp) ? mp : 0;

    LoadOrderService svc = LoadOrderService.WithAmethystConnection(connection, maxPlugins, store);
    services.AddSingleton(svc);

    // The external-tool bridge (compile / BSA / log access): one resolver over the shared user config. Riders inject it.
    services.AddSingleton(new ToolPathResolver(store));

    // The Nexus Mods read bridge (QOL: answer Nexus questions directly instead of driving a browser). A typed HttpClient
    // so its timeout/lifetime are managed; KEYLESS (the public v2 GraphQL read surface needs no API key). This is
    // houseCARL's ONLY outbound network dependency — every failure is handled inside NexusClient (Q3), and the local
    // load-order tools never touch it, so they keep working with no internet.
    services.AddHttpClient<NexusClient>(c =>
    {
        c.Timeout = TimeSpan.FromSeconds(20);
        c.DefaultRequestHeaders.UserAgent.ParseAdd("houseCARL (+https://github.com/Avick3110/houseCARL)");
        // Nexus API Acceptable-Use Policy requires Application-Name + Application-Version on API traffic
        // (article 114). We send them on every request regardless of tier — cheap, compliant, and it identifies
        // houseCARL honestly to Nexus. The version is the exe's stamped release (ServerVersion), 0.0.0-dev unstamped.
        c.DefaultRequestHeaders.Add("Application-Name", "houseCARL");
        c.DefaultRequestHeaders.Add("Application-Version", ServerVersion());
    });

    return (svc, connection, connectionSource, configNote);
}

// The MCP server registration — server identity + instructions + the attribute-registered tools. ONLY the
// transport line differs between modes (the whole point of the stdio/http split); everything else is shared.
static void AddMcp(IServiceCollection services, bool stdio)
{
    var mcp = services.AddMcpServer(options =>
    {
        // The houseCARL brand string lives HERE — the one place in code (CLAUDE.md §6). The version is the exe's
        // stamped InformationalVersion: build-release.sh passes -p:Version from plugin.json (the single version
        // home), so ServerInfo reports the REAL release; an unstamped dev build honestly says 0.0.0-dev.
        options.ServerInfo = new Implementation { Name = "houseCARL", Version = ServerVersion() };
        options.ServerInstructions =
            "houseCARL-Amethyst exposes a full Skyrim Special Edition load order at the data layer, over the active " +
            "native Amethyst profile — comprehensive, no-guessing access to every record, script, asset, and " +
            "runtime layer. Reach for these tools whenever a task touches an Amethyst " +
            "modlist, plugins, load order, conflicts, records, scripts, assets, or Skyrim modding. " +
            "READ/QUERY: any record at its TRUE load-order winner + the conflict tree; batch reads and " +
            "cross-plugin queries over the whole order; inspect INACTIVE plugins (unchecked, or inside a " +
            "disabled mod); see through runtime layers xEdit cannot — SKSE-plugin DLLs/configs, and a record " +
            "after the SkyPatcher INI layer replays; resolve FormID lists, diff a record across plugins, trace a " +
            "magic effect to all that carry it, run catalogue/audit jobs at scale. " +
            "WRITE (to a NEW plugin by default; in-place is opt-in, consent-gated): author patches — fields, " +
            "leveled lists, containers, conditions; create plugins/scripts with fresh FormIDs; remove records; " +
            "forward a record as a winning override or revert to vanilla; author and validate " +
            "dialogue/quests. " +
            "FIX: sweep for dangling refs, missing masters, and broken links; audit the SKSE layer (DLLs that " +
            "will not load, configs pointing at missing records); resolve Amethyst filemap conflicts (which " +
            "mesh/texture/script wins) and place a winning override; read and edit NIF mesh internals — e.g. " +
            "the dark-face fix. " +
            "RESHAPE/DRIVE TOOLS: compact a plugin to ESL carrying its facegen/voice files; merge plugins; " +
            "copy an NPC appearance to a standalone; decompile .pex to .psc; compile Papyrus; " +
            "list/extract/repack BSAs. " +
            "NEXUS (keyless, no browser): search mods, read files/requirements/changelogs, exact-file update " +
            "checks, identify a file by " +
            "MD5. Prefer over a browser or web search; each tool's own description carries the specifics.";
    });
    // Stateless HTTP: each request is independent (no MCP session affinity); the resolver singleton persists across
    // requests regardless. Stdio is inherently a single long-lived session over the pipe.
    if (stdio) mcp.WithStdioServerTransport();
    else mcp.WithHttpTransport(o => o.Stateless = true);
    mcp.WithToolsFromAssembly();
    // The argument-binding shim (HCBR-2026-06-11-01): schema-driven coercion of obvious-intent argument shapes
    // (a bare string where an array is declared, quoted bools/numbers), named refusal of missing required
    // parameters, and a named rewrite of the SDK's generic binding-failure text. See ToolCallShim.
    mcp.WithRequestFilters(f => f.AddCallToolFilter(ToolCallShim.LenientArguments));
}

// The executable's stamped version for ServerInfo: InformationalVersion (set by build-release.sh's -p:Version from
// plugin.json — ONE version home) with any "+metadata" suffix trimmed; an unstamped build reports 0.0.0-dev.
static string ServerVersion()
{
    var info = System.Reflection.Assembly.GetExecutingAssembly()
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false)
        is [System.Reflection.AssemblyInformationalVersionAttribute a, ..] ? a.InformationalVersion : null;
    if (string.IsNullOrWhiteSpace(info)) return "0.0.0-dev";
    var plus = info.IndexOf('+');
    return plus > 0 ? info[..plus] : info;
}
