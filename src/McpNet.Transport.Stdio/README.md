# McpNet.Transport.Stdio

MCP transport for inbound connections over **stdin / stdout**. Enables the gateway itself to act as an MCP server that tools like Claude Desktop can launch as a child process.

---

## Use Case

Normally the gateway aggregates upstream MCP servers. With this transport, the gateway **is** the MCP server - any MCP client that supports the stdio protocol can connect by launching the gateway executable directly.

```
Claude Desktop
  └── spawns mcpnet-gateway as a child process
        └── communicates over stdin/stdout (JSON-RPC NDJSON)
              └── gateway proxies to upstream MCP servers
```

---

## Key Type

### StdioMcpTransport

```csharp
// Reads JSON-RPC messages from stdin, writes responses to stdout
var transport = new StdioMcpTransport(gatewayRequestRouter, sessionManager);
await transport.RunAsync(cancellationToken);
```

---

## Claude Desktop Configuration

Add this to your `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "mcpnet-gateway": {
      "command": "/path/to/mcpnet-gateway",
      "args": ["--stdio"]
    }
  }
}
```

---

## NDJSON Protocol

Each message is a single JSON object on its own line (`\n` terminated):

```
→ {"jsonrpc":"2.0","id":1,"method":"initialize","params":{...}}\n
← {"jsonrpc":"2.0","id":1,"result":{...}}\n
→ {"jsonrpc":"2.0","method":"notifications/initialized","params":{}}\n
→ {"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}\n
← {"jsonrpc":"2.0","id":2,"result":{"tools":[...]}}\n
```

---

## Dependency Graph

```
McpNet.Transport.Stdio  (net8.0)
  └── McpNet.Gateway
```
