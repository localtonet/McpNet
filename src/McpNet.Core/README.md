# McpNet.Core

Foundation library containing MCP protocol types, JSON-RPC message definitions, and serialization utilities.

Targets **netstandard2.0** - usable from any .NET platform (.NET 5+, .NET Framework 4.6.1+, Xamarin, Unity).

No runtime dependencies other than `System.Text.Json`.

---

## Contents

| File | Description |
|---|---|
| `JsonRpcMessage.cs` | `JsonRpcRequest`, `JsonRpcResponse`, `JsonRpcNotification`, `JsonRpcError` |
| `McpMethods.cs` | MCP method name constants (`initialize`, `tools/list`, `tools/call`, etc.) |
| `McpParams.cs` | Request / response parameter types for all MCP methods |
| `McpCapabilities.cs` | `ClientCapabilities`, `ServerCapabilities` |
| `McpAttributes.cs` | `[McpTool]`, `[McpDescription]` attributes for tool discovery |
| `IMcpTransport.cs` | `IMcpTransport` interface implemented by all transport libraries |
| `McpJsonOptions.cs` | Shared `JsonSerializerOptions` + `Serialize<T>` / `Deserialize<T>` helpers |

---

## Key Types

### IMcpTransport

```csharp
public interface IMcpTransport
{
    Task<JsonRpcResponse?> SendRequestAsync(JsonRpcRequest request, CancellationToken ct);
    Task SendNotificationAsync(JsonRpcNotification notification, CancellationToken ct);
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);
}
```

### McpJsonOptions

```csharp
// Serialize
string json = McpJsonOptions.Serialize(myObject);

// Deserialize
var obj = McpJsonOptions.Deserialize<MyType>(json);

// Raw JsonSerializerOptions
var opts = McpJsonOptions.Default;
```

### MCP Method Constants

```csharp
McpMethods.Initialize        // "initialize"
McpMethods.ToolsList         // "tools/list"
McpMethods.ToolsCall         // "tools/call"
McpMethods.Initialized       // "notifications/initialized"
```

---

## Dependency Graph

```
McpNet.Core          (netstandard2.0)
  └── System.Text.Json 8.0.5
```

All other McpNet libraries depend on this one.
