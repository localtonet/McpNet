# McpNet.Dashboard

ASP.NET Core endpoint extensions that add the web management UI and REST management API to any ASP.NET Core application.

This library is a thin HTTP adapter - all business logic and static assets live in `McpNet.Gateway`.

---

## Registration

```csharp
// In Program.cs
app.MapMcpDashboard();      // GET /dashboard, /dashboard.js, /dashboard.css, /catalog.json
app.MapMcpManagement();     // GET+POST+PUT+DELETE /api/*
```

Both methods accept an optional `pattern` argument to change the base path:

```csharp
app.MapMcpDashboard("/ui");
app.MapMcpManagement("/management");
```

---

## Endpoints registered by MapMcpDashboard

| Route | Content |
|---|---|
| `GET /dashboard` | `dashboard.html` |
| `GET /dashboard.js` | JavaScript bundle |
| `GET /dashboard.css` | Stylesheet |
| `GET /catalog.json` | MCP server catalog |

Static files are loaded from `GatewayDashboardResources` (embedded in `McpNet.Gateway.dll`) - no `wwwroot` folder needed.

---

## Endpoints registered by MapMcpManagement

| Route | Description |
|---|---|
| `GET /api/info` | Auth mode info (public, no token needed) |
| `GET/POST /api/servers` | List / register servers |
| `GET/PUT/DELETE /api/servers/{id}` | Get / update / delete a server |
| `GET /api/servers/{id}/tools` | Tools exposed by a server |
| `PATCH /api/servers/{id}/tools/{name}/toggle` | Enable / disable a tool |
| `GET /api/servers/{id}/health` | Live health check |
| `GET /api/servers/{id}/stdio-probe` | Raw stdio protocol probe |
| `GET/POST /api/groups` | Tool groups |
| `GET/PUT/DELETE /api/clients` | Clients (Enterprise mode) |
| `GET /api/audit` | Audit log (last 200 entries) |
| `GET /api/tools` | All aggregated tools |
| `POST /api/tools/refresh` | Trigger tool refresh (async) |
| `GET /api/tools/status` | Refresh status + diagnostics |
| `POST /api/tools/call` | Call a tool from the dashboard |
| `GET /api/export` | Export full configuration as JSON |
| `POST /api/import` | Import configuration from JSON |
| `GET/POST /api/catalog` | Merged catalog |
| `POST /api/catalog/custom` | Add custom catalog entry |
| `DELETE /api/catalog/custom/{name}` | Remove custom catalog entry |

All `/api/*` routes (except `/api/info`) are protected by `AdminAuthFilter` in Enterprise mode.

---

## Authentication

In `GatewayMode.Enterprise`, every management request must include:

```
X-Admin-Token: your-secret-token
```

In `GatewayMode.Dev`, all requests are accepted without a token.

---

## Required Services

The following services must be registered in DI before calling `MapMcpManagement`:

```csharp
// Core
builder.Services.AddMcpGateway(o => { ... });
builder.Services.AddSingleton<ServerRegistry>();
builder.Services.AddSingleton<ToolAggregator>();
builder.Services.AddSingleton(new GatewayCatalogService(dataDir));

// Persistence (choose one)
builder.Services.AddMcpJsonPersistence(dataDir);
// or
builder.Services.AddMcpEfRepositories();
```

---

## Dependency Graph

```
McpNet.Dashboard  (net8.0)
  └── McpNet.Gateway
  └── McpNet.Gateway.Persistence
  └── Microsoft.AspNetCore.App (framework reference)
```
