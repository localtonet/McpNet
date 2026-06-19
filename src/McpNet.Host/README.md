# McpNet.Host

The production-ready MCP Gateway executable. Built on ASP.NET Core / Kestrel.

Publishes as a **self-contained single file** for Windows, Linux, and macOS.

---

## Running

```bash
# Default (Dev mode, port 5000)
./mcpnet-gateway

# Custom port
./mcpnet-gateway --port 8080

# Enterprise mode with admin token
./mcpnet-gateway --mode Enterprise --admin-token your-secret-token

# Specific data directory
./mcpnet-gateway --data /var/lib/mcpnet
```

---

## Configuration

All settings can be provided via:
1. Command-line arguments
2. Environment variables (`MCPGATEWAY__PORT`, etc.)
3. `appsettings.json`

| Key | CLI | Default | Description |
|---|---|---|---|
| `McpGateway:Port` | `--port` | `5000` | HTTP port |
| `McpGateway:Mode` | `--mode` | `Dev` | `Dev` or `Enterprise` |
| `McpGateway:AdminToken` | `--admin-token` | _(empty)_ | Required in Enterprise mode |
| `McpGateway:DataDirectory` | `--data` | `mcp-data` | JSON data file directory |
| `McpGateway:Persistence` | `--persistence` | `json` | `json`, `sqlite`, or `postgres` |
| `McpGateway:ConnectionString` | `--connection-string` | _(empty)_ | Required for `sqlite` / `postgres` |
| `McpGateway:MetaTools` | `--meta-tools` | `false` | Enable gateway meta-tools over MCP |
| `McpGateway:Telemetry:Enabled` | - | `false` | Enable OpenTelemetry export |
| `McpGateway:Telemetry:Endpoint` | - | _(empty)_ | OTLP collector endpoint |

---

## Persistence Backends

```bash
# JSON files (default, zero config)
./mcpnet-gateway

# SQLite
./mcpnet-gateway --persistence sqlite --connection-string "Data Source=gateway.db"

# PostgreSQL
./mcpnet-gateway --persistence postgres --connection-string "Host=localhost;Database=mcpgw;Username=app;Password=secret"
```

---

## Docker

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY publish/ .
EXPOSE 5000
ENTRYPOINT ["./mcpnet-gateway", "--port", "5000"]
```

```bash
docker run -p 5000:5000 \
  -e MCPGATEWAY__MODE=Enterprise \
  -e MCPGATEWAY__ADMINTOKEN=your-secret-token \
  -v /host/data:/app/mcp-data \
  my-mcpnet-gateway
```

---

## Publishing

```bash
# Windows x64
dotnet publish src/McpNet.Host -c Release -r win-x64

# Linux x64
dotnet publish src/McpNet.Host -c Release -r linux-x64

# macOS ARM (Apple Silicon)
dotnet publish src/McpNet.Host -c Release -r osx-arm64
```

Output: `bin/Release/net8.0/{rid}/publish/mcpnet-gateway[.exe]`

---

## Endpoints

After startup, the following are available:

| URL | Description |
|---|---|
| `http://localhost:5000/dashboard` | Web management UI |
| `http://localhost:5000/mcp` | MCP protocol endpoint |
| `http://localhost:5000/api/info` | Auth mode info |
| `http://localhost:5000/api/servers` | Server management |
| `http://localhost:5000/health` | Health check |

---

## OpenTelemetry

```json
{
  "McpGateway": {
    "Telemetry": {
      "Enabled": true,
      "Endpoint": "http://localhost:4317",
      "ServiceName": "mcpnet-gateway"
    }
  }
}
```

Exports traces for all MCP requests via OTLP. Compatible with Jaeger, Zipkin, Grafana Tempo, and any OpenTelemetry collector.

---

## Dependency Graph

```
McpNet.Host  (net8.0, SDK=Microsoft.NET.Sdk.Web)
  └── McpNet.Gateway
  └── McpNet.Gateway.Persistence
  └── McpNet.Transport.AspNetCore
  └── McpNet.Dashboard
  └── OpenTelemetry.Extensions.Hosting 1.15.3
  └── OpenTelemetry.Instrumentation.AspNetCore 1.12.0
  └── OpenTelemetry.Exporter.OpenTelemetryProtocol 1.15.3
```
