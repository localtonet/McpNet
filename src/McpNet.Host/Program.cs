using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using McpNet.Gateway.Abstractions;
using McpNet.Gateway.Aggregation;
using McpNet.Gateway.Auth;
using McpNet.Gateway.Extensions;
using McpNet.Gateway.Groups;
using McpNet.Gateway.Persistence;
using McpNet.Gateway.Registry;
using McpNet.Gateway.Routing;
using McpNet.Gateway.Security;
using McpNet.Gateway.Sessions;
using McpNet.Gateway.Dashboard;
using McpNet.Transport.AspNetCore;
using McpNet.Dashboard;
using McpNet.Host;

// ─────────────────────────────────────────────────────────────────────────────
// McpNet Gateway - standalone host
// Usage: mcpnet-gateway  (reads appsettings.json from cwd)
//        MCPGateway__Mode=Enterprise MCPGateway__AdminToken=secret ./mcpnet-gateway
// ─────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

// ── Read config ───────────────────────────────────────────────────────────────
var cfg = builder.Configuration.GetSection("McpGateway");

var mode        = Enum.TryParse<GatewayMode>(cfg["Mode"], ignoreCase: true, out var m) ? m : GatewayMode.Dev;
var adminToken  = System.Environment.GetEnvironmentVariable("MCPGATEWAY_ADMIN_TOKEN")
                  ?? cfg["AdminToken"]
                  ?? string.Empty;
var port        = int.TryParse(System.Environment.GetEnvironmentVariable("MCPGATEWAY_PORT") ?? cfg["Port"], out var p) ? p : 5050;
var dataDir     = cfg["DataDirectory"] ?? "mcp-data";
var persistence = (cfg["Persistence"] ?? "Json").Trim().ToLowerInvariant();
var sqliteCs    = cfg["ConnectionStrings:Sqlite"] ?? string.Empty;
var postgresCs  = cfg["ConnectionStrings:Postgres"] ?? string.Empty;
var metaTools   = bool.TryParse(cfg["EnableMetaTools"], out var mt) && mt;

// ── Kestrel port ──────────────────────────────────────────────────────────────
builder.WebHost.ConfigureKestrel(o =>
{
    o.ListenAnyIP(port);
});

// ── Logging ───────────────────────────────────────────────────────────────────
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
// ── Data Protection (secret encryption at rest) ─────────────────────────────
// Keys stored in <dataDir>/dp-keys - persisted alongside the JSON data files so
// the gateway can decrypt secrets after a restart.
builder.Services
    .AddDataProtection()
    .PersistKeysToFileSystem(new System.IO.DirectoryInfo(System.IO.Path.Combine(dataDir, "dp-keys")));
builder.Services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
// ── Persistence layer ─────────────────────────────────────────────────────────
switch (persistence)
{
    case "sqlite" when !string.IsNullOrWhiteSpace(sqliteCs):
        builder.Services.AddMcpGatewayDbContext(sqliteCs, usePostgres: false);
        builder.Services.AddMcpEfRepositories();
        break;

    case "postgres" when !string.IsNullOrWhiteSpace(postgresCs):
        builder.Services.AddMcpGatewayDbContext(postgresCs, usePostgres: true);
        builder.Services.AddMcpEfRepositories();
        break;

    default: // "json" or anything else → zero-dependency JSON files
        builder.Services.AddMcpJsonPersistence(dataDir);
        break;
}

// IToolStateStore is registered by AddMcpJsonPersistence.
// For DB backends it is absent, so fall back to a small JSON sidecar file so that
// tool enable/disable state persists across restarts regardless of persistence backend.
builder.Services.TryAddSingleton<McpNet.Gateway.Abstractions.IToolStateStore>(
    new McpNet.Gateway.Persistence.Json.JsonToolStateStore(dataDir));

// ── Gateway core services ─────────────────────────────────────────────────────
builder.Services.AddMcpGateway(o =>
{
    o.AuthOptions.Mode        = mode;
    o.AuthOptions.AdminToken  = adminToken;
});

// Managed HttpClient factory (connection-pool reuse across upstream calls).
builder.Services.AddHttpClient();
// In-memory sliding-window rate limiter (replaces audit-log scan on every call).
builder.Services.AddSingleton<McpNet.Gateway.Routing.GatewayRateLimiter>();
// Feature 2: SSE notification manager for push notifications.
builder.Services.AddSingleton<McpNet.Gateway.Notifications.SseConnectionManager>();
// Feature 5: per-tool response cache.
builder.Services.AddSingleton<McpNet.Gateway.Routing.ToolResponseCache>();

builder.Services.AddSingleton<ServerRegistry>(sp =>
{
    // Wire the IHttpClientFactory so upstream HttpClients share the managed connection pool.
    var factory = sp.GetService<System.Net.Http.IHttpClientFactory>();
    Func<System.Net.Http.HttpClient>? httpFactory = factory != null ? () => factory.CreateClient("McpUpstream") : null;
    return new ServerRegistry(
        sp.GetRequiredService<McpNet.Gateway.Abstractions.IServerRepository>(),
        httpFactory);
});
builder.Services.AddSingleton<ToolAggregator>(sp =>
    new ToolAggregator(
        sp.GetRequiredService<ServerRegistry>(),
        sp.GetRequiredService<McpNet.Gateway.Abstractions.IServerRepository>(),
        sp.GetService<McpNet.Gateway.Abstractions.IToolStateStore>()));
builder.Services.AddSingleton<ToolGroupManager>();
builder.Services.AddSingleton(new GatewayCatalogService(dataDir));

// ── Optional OpenTelemetry (default off; audit log is the default observability) ──
builder.Services.AddMcpTelemetry(builder.Configuration);

// ── Meta-tools (manage the gateway over MCP itself) - opt-in ──────────────────
if (metaTools)
    builder.Services.AddSingleton<MetaToolHandler>();

builder.Services.AddSingleton<GatewayRequestRouter>(sp =>
    new GatewayRequestRouter(
        sp.GetRequiredService<ToolAggregator>(),
        sp.GetRequiredService<ServerRegistry>(),
        sp.GetRequiredService<McpNet.Gateway.Abstractions.IServerRepository>(),
        sp.GetRequiredService<ToolGroupManager>(),
        sp.GetRequiredService<McpNet.Gateway.Sessions.GatewaySessionManager>(),
        sp.GetService<McpNet.Gateway.Abstractions.IClientRepository>(),
        sp.GetService<McpNet.Gateway.Abstractions.IAuditLogRepository>(),
        sp.GetService<MetaToolHandler>(),
        sp.GetService<McpNet.Gateway.Routing.GatewayRateLimiter>(),
        sp.GetService<McpNet.Gateway.Routing.ToolResponseCache>()));

builder.Services.AddHostedService<ToolRefreshBackgroundService>();

// ── Build ─────────────────────────────────────────────────────────────────────
var app = builder.Build();

// ── MCP endpoint ──────────────────────────────────────────────────────────────
app.MapMcp("/mcp");

// ── Management API ────────────────────────────────────────────────────────────
app.MapMcpManagement("/api");

// ── Dashboard ─────────────────────────────────────────────────────────────────
app.MapMcpDashboard("/dashboard");

// ── Feature 6: Prometheus scraping endpoint (opt-in) ─────────────────────────
app.MapPrometheusIfEnabled();

// ── Root redirect ─────────────────────────────────────────────────────────────
app.MapGet("/", () => Results.Redirect("/dashboard"));

// ── Health ────────────────────────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new
{
    status  = "ok",
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "dev",
    mode    = mode.ToString()
}));

// ── Banner ────────────────────────────────────────────────────────────────────
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("╔═══════════════════════════════════════════╗");
Console.WriteLine("║        McpNet Gateway  ◆  Running         ║");
Console.WriteLine($"║  Mode : {mode,-34}║");
Console.WriteLine($"║  Port : {port,-34}║");
Console.WriteLine($"║  Store: {(persistence == "sqlite" ? "SQLite" : persistence == "postgres" ? "PostgreSQL" : "JSON files"),-34}║");
Console.WriteLine("╠═══════════════════════════════════════════╣");
Console.WriteLine($"║  MCP       → http://localhost:{port}/mcp     ║");
Console.WriteLine($"║  API       → http://localhost:{port}/api      ║");
Console.WriteLine($"║  Dashboard → http://localhost:{port}/dashboard║");
Console.WriteLine("╚═══════════════════════════════════════════╝");
Console.ResetColor();

await app.RunAsync();
