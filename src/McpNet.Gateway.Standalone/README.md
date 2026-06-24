# McpNet.Gateway.Standalone

A self-contained MCP Gateway that can be embedded in any .NET 8 application with **no ASP.NET Core dependency**.

Uses `System.Net.HttpListener` - Kestrel and `Microsoft.AspNetCore` are **not required**.

---

## Installation

```xml
<PackageReference Include="McpNet.Gateway.Standalone" Version="1.0.3" />
```

---

## Quick Start

```csharp
var gateway = McpGatewayBuilder.Create()
    .ListenOn(5050)
    .Build();

await gateway.StartAsync();

// ... your application runs ...

await gateway.StopAsync();
await gateway.DisposeAsync();
```

---

## Configuration

| Method | Description | Default |
|---|---|---|
| `.ListenOn(port)` | TCP port to bind | `5050` |
| `.WithPrefix(prefix)` | Full HttpListener prefix | `http://localhost:5050/` |
| `.WithDataDirectory(dir)` | Directory for JSON data files and encryption keys | `mcp-data` |
| `.WithMode(GatewayMode.Dev)` | `Dev` = no auth, `Enterprise` = token required | `Dev` |
| `.WithAdminToken(token)` | Admin token for the management API (Enterprise mode) | _(empty)_ |
| `.WithMetaTools()` | Expose gateway management as MCP tools | `false` |

### Binding all interfaces (Windows)

```csharp
McpGatewayBuilder.Create()
    .WithPrefix("http://*:5050/")
    .Build();
```

> On Windows, `http://*` requires: `netsh http add urlacl url=http://*:5050/ user=Everyone`
> On Linux, run as root or grant `NET_BIND_SERVICE` capability.

### Enterprise mode

```csharp
McpGatewayBuilder.Create()
    .ListenOn(5050)
    .WithMode(GatewayMode.Enterprise)
    .WithAdminToken("your-secret-token")
    .Build();
```

In Enterprise mode all `/api/*` requests require an `X-Admin-Token` header.

---

## Endpoints

| Endpoint | Description |
|---|---|
| `GET /health` | Health check (`{"status":"ok"}`) |
| `GET /dashboard` | Web management UI |
| `GET /dashboard.js` / `.css` | Dashboard static assets |
| `GET /catalog.json` | MCP server catalog |
| `/mcp` | MCP protocol (Streamable HTTP / SSE) |
| `GET /api/servers` | List registered servers |
| `POST /api/servers` | Register a new server |
| `GET /api/tools` | List all aggregated tools |
| `POST /api/tools/refresh` | Refresh tools from all servers |
| `GET /api/groups` | Tool groups |
| `GET /api/clients` | Clients (Enterprise) |
| `GET /api/audit` | Audit log |
| `GET /api/export` | Export configuration |
| `POST /api/import` | Import configuration |
| `GET /api/catalog` | Merged catalog (built-in + custom entries) |

---

## Usage Scenarios

### Embed in a WinForms / WPF app

```csharp
// Form1.cs
private McpGatewayServer? _gateway;

protected override async void OnLoad(EventArgs e)
{
    base.OnLoad(e);
    _gateway = McpGatewayBuilder.Create()
        .ListenOn(5050)
        .WithDataDirectory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MyApp", "mcp-data"))
        .Build();
    await _gateway.StartAsync();
}

protected override async void OnFormClosed(FormClosedEventArgs e)
{
    if (_gateway != null) await _gateway.DisposeAsync();
    base.OnFormClosed(e);
}
```

### Console application

```csharp
using var gateway = McpGatewayBuilder.Create()
    .ListenOn(5050)
    .Build();

await gateway.StartAsync();
Console.WriteLine("MCP Gateway ready -> http://localhost:5050/dashboard");

await Task.Delay(Timeout.Infinite, cancellationToken);
```

### Graceful shutdown with CancellationToken

```csharp
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var gateway = McpGatewayBuilder.Create().ListenOn(5050).Build();
await gateway.StartAsync();

await Task.Delay(Timeout.Infinite, cts.Token).ContinueWith(_ => { });

await gateway.StopAsync();
await gateway.DisposeAsync();
```

---

## Architecture

```
McpNet.Gateway.Standalone
|
+-- McpGatewayBuilder     -> Fluent configuration and DI setup
+-- McpGatewayServer      -> HttpListener accept loop, request dispatch
+-- McpGatewayOptions     -> Configuration values
|
+-- Dashboard/
|   +-- DashboardHandler  -> Serves /dashboard and static files
|
+-- Management/
|   +-- ManagementHandler -> Handles all /api/* management endpoints
|
+-- Security/
    +-- AesGcmSecretProtector -> AES-256-GCM secret encryption

Dependencies:
  McpNet.Gateway           -> Business logic, persistence, wwwroot resources
  McpNet.Transport.Http    -> MCP protocol over HttpListener
  Microsoft.Extensions.DependencyInjection 8.0.0
```

---

## Data Files

All data is stored under `DataDirectory` (default: `mcp-data/`):

```
mcp-data/
  servers.json          <- Registered MCP servers
  groups.json           <- Tool groups
  clients.json          <- Clients (Enterprise)
  audit.json            <- Audit log entries
  custom-catalog.json   <- User-added catalog entries
  dp-keys/
    aes.key             <- AES-256 encryption key (auto-generated)
```

---

## Comparison with McpNet.Host

| | McpNet.Host | McpNet.Gateway.Standalone |
|---|---|---|
| HTTP engine | Kestrel (ASP.NET Core) | HttpListener (.NET built-in) |
| ASP.NET Core dependency | Yes | **No** |
| Deployment | Server / Docker / IIS | Desktop / embedded / NuGet package |
| HTTPS | Via Kestrel | Via HttpListener |
| Dashboard | Yes | Yes |
| MCP protocol | Yes | Yes |

Both use the same business logic (`McpNet.Gateway`) and the same dashboard assets.
