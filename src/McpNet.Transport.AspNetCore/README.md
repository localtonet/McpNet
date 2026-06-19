# McpNet.Transport.AspNetCore

ASP.NET Core integration for accepting inbound MCP connections over **Streamable HTTP** and **SSE (Server-Sent Events)**.

Adds a `MapMcp()` extension method that wires the MCP protocol handler into the ASP.NET Core routing pipeline.

---

## Registration

```csharp
// In Program.cs
app.MapMcp();               // binds to /mcp (default)
app.MapMcp("/mcp/v1");      // custom path
```

---

## How It Works

```
Client → POST /mcp        → Streamable HTTP session (JSON-RPC over HTTP body)
Client → GET  /mcp        → SSE stream (JSON-RPC over Server-Sent Events)
```

All incoming JSON-RPC requests are forwarded to `GatewayRequestRouter`, which resolves the correct upstream MCP server and proxies the call.

---

## Authentication (Enterprise mode)

In `GatewayMode.Enterprise`, every MCP request must include:

```
Authorization: Bearer <client-bearer-token>
```

Tokens are managed via `/api/clients` in the management API.

---

## Required Services

```csharp
builder.Services.AddMcpGateway(o => { ... });
builder.Services.AddSingleton<ServerRegistry>();
builder.Services.AddSingleton<ToolAggregator>();
builder.Services.AddSingleton<GatewayRequestRouter>();
builder.Services.AddSingleton<GatewaySessionManager>();
// persistence ...
```

`McpNet.Host` registers all of these automatically.

---

## Dependency Graph

```
McpNet.Transport.AspNetCore  (net8.0)
  └── McpNet.Gateway
  └── Microsoft.AspNetCore.App (framework reference)
```
