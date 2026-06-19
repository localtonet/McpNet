# McpNet.Gateway

Core business logic library for the MCP Gateway. Contains all domain services, persistence abstractions, authentication, tool aggregation, and upstream client management.

**No HTTP dependency** - usable from any host (ASP.NET Core, HttpListener, desktop apps).

---

## Responsibilities

| Area | Classes |
|---|---|
| Upstream connections | `McpUpstreamClient`, `ServerRegistry`, `StdioCommandHelper`, `OAuthTokenProvider` |
| Tool aggregation | `ToolAggregator`, `ToolGroupManager` |
| Request routing | `GatewayRequestRouter` |
| Authentication | `GatewayAuthenticator`, `GatewaySessionManager` |
| Persistence abstractions | `IServerRepository`, `IToolGroupRepository`, `IClientRepository`, `IAuditLogRepository` |
| JSON persistence | `JsonPersistenceExtensions`, `JsonRepositories` |
| Dashboard resources | `GatewayDashboardResources`, `GatewayCatalogService` |
| Security | `ISecretProtector`, `NullSecretProtector` |
| Telemetry | `McpTelemetry` |
| Models | `RegisteredServer`, `McpClient`, `ToolGroup`, `AuditLog`, `OAuthConfig` |

---

## Dashboard Resources

Static files (`dashboard.html`, `dashboard.css`, `dashboard.js`, `catalog.json`) are embedded in this assembly. Any host can serve them without carrying its own copy:

```csharp
// Read a file as bytes
byte[]? bytes = await GatewayDashboardResources.GetAsync("dashboard.html", ct);

// Read as text
string? text = await GatewayDashboardResources.GetTextAsync("catalog.json", ct);

// List all embedded files
IEnumerable<string> files = GatewayDashboardResources.ListFiles();
```

---

## Catalog Service

```csharp
var catalog = new GatewayCatalogService(dataDirectory);

// Get merged list (built-in curated + user custom entries)
List<object> entries = await catalog.GetMergedAsync(ct);

// Add a custom entry
await catalog.AddCustomAsync(myEntry, ct);

// Remove by name
await catalog.RemoveCustomAsync("custom-abc123", ct);
```

Custom entries are persisted to `{dataDirectory}/custom-catalog.json`.

---

## Service Registration

```csharp
// Register all gateway core services
builder.Services.AddMcpGateway(o =>
{
    o.AuthOptions.Mode       = GatewayMode.Dev;        // or Enterprise
    o.AuthOptions.AdminToken = "your-secret-token";
});

// Register JSON file persistence (zero-dependency)
builder.Services.AddMcpJsonPersistence(dataDirectory);

// Register catalog service
builder.Services.AddSingleton(new GatewayCatalogService(dataDirectory));
```

---

## Authentication Modes

| Mode | Behavior |
|---|---|
| `GatewayMode.Dev` | No authentication - all requests are accepted |
| `GatewayMode.Enterprise` | `/api/*` requires `X-Admin-Token` header; MCP requires `Authorization: Bearer <token>` |

---

## Secret Protection

`ISecretProtector` is used to encrypt bearer tokens and env var values at rest:

```csharp
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string ciphertext);
}
```

- `NullSecretProtector` - stores values in plain text (Dev mode)
- `AesGcmSecretProtector` - AES-256-GCM (Standalone)
- `DataProtectionSecretProtector` - ASP.NET Core Data Protection (Host)

---

## Dependency Graph

```
McpNet.Gateway  (net8.0)
  └── McpNet.Core
  └── Microsoft.Extensions.DependencyInjection.Abstractions 8.0.2
```
