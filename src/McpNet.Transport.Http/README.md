# McpNet.Transport.Http

MCP protocol handler built on `System.Net.HttpListener`. Accepts inbound MCP connections over **Streamable HTTP** and **SSE** without any ASP.NET Core dependency.

Used exclusively by `McpNet.Gateway.Standalone`.

---

## Key Type

### HttpListenerMcpTransport

```csharp
// Registered in DI by McpGatewayBuilder
services.AddSingleton<HttpListenerMcpTransport>();

// Called by McpGatewayServer for every /mcp* request
await _mcpTransport.HandleContextAsync(context, cancellationToken);
```

`HandleContextAsync` is the single entry point - it receives a raw `HttpListenerContext` and handles the full MCP session lifecycle (handshake, session, tool dispatch, teardown).

---

## Protocol Support

| Method | Transport |
|---|---|
| `POST /mcp` | Streamable HTTP (request/response) |
| `GET /mcp` | SSE stream (persistent connection) |

---

## Configuration

```csharp
services.AddSingleton(new HttpListenerMcpOptions
{
    Port    = 5050,
    McpPath = "/mcp"
});
```

---

## No ASP.NET Core

This library uses only:
- `System.Net.HttpListener` (built into .NET)
- `McpNet.Gateway` (business logic)

No `Microsoft.AspNetCore` packages are referenced.

---

## Dependency Graph

```
McpNet.Transport.Http  (net8.0)
  └── McpNet.Gateway
```
