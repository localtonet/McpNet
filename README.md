# McpNet Gateway

> **A high-performance, enterprise-ready Model Context Protocol (MCP) gateway for .NET 8.**  
> Aggregate any number of upstream MCP servers behind a single authenticated endpoint — with a web dashboard, management CLI, per-client access control, rate limiting, audit logging, and optional OpenTelemetry observability.

---

## Table of Contents

1. [Overview](#overview)
2. [Architecture](#architecture)
3. [Project Layout](#project-layout)
4. [Quick Start](#quick-start)
5. [Configuration Reference](#configuration-reference)
6. [Authentication Modes](#authentication-modes)
7. [Upstream Servers](#upstream-servers)
   - [StreamableHTTP / SSE](#streamablehttp--sse)
   - [Stdio (npx / local process)](#stdio-npx--local-process)
   - [Upstream Bearer & OAuth 2.0](#upstream-bearer--oauth-20)
8. [Tool Aggregation](#tool-aggregation)
9. [Tool Groups](#tool-groups)
10. [Client Management & Per-Client Access Control](#client-management--per-client-access-control)
11. [Rate Limiting](#rate-limiting)
12. [Server Health Checks](#server-health-checks)
13. [Audit Log](#audit-log)
14. [MCP Catalog](#mcp-catalog)
15. [Web Dashboard](#web-dashboard)
16. [Management REST API](#management-rest-api)
17. [Management CLI — `mcpnet`](#management-cli--mcpnet)
18. [Meta-Tools (gateway self-management via MCP)](#meta-tools-gateway-self-management-via-mcp)
19. [Secrets at Rest](#secrets-at-rest)
20. [Persistence Backends](#persistence-backends)
21. [OpenTelemetry](#opentelemetry)
22. [Session Management](#session-management)
23. [Docker](#docker)
24. [Import / Export](#import--export)
25. [Testing](#testing)
26. [Security Notes](#security-notes)

---

## Overview

McpNet Gateway sits between your AI agents (Claude, Cursor, any MCP client) and one or more upstream MCP servers. Instead of configuring every agent to know about every server, agents connect to the gateway once and see a unified, namespaced tool catalogue.

```
AI Agent (MCP client)
        │  Bearer token
        ▼
 ┌─────────────────────────────────────┐
 │          McpNet Gateway             │
 │  ┌──────────────────────────────┐   │
 │  │  Auth · Rate Limit · Audit   │   │
 │  └──────────────┬───────────────┘   │
 │  ┌──────────────▼───────────────┐   │
 │  │      Tool Aggregator         │   │
 │  │  (parallel refresh cache)    │   │
 │  └──┬──────────┬──────────┬─────┘   │
 └─────│──────────│──────────│─────────┘
       │          │          │
       ▼          ▼          ▼
  Server A    Server B    Server C
 (HTTP/MCP)  (stdio/npx) (OAuth API)
```

**Key properties:**

- **Single MCP endpoint** at `/mcp` — all upstream tools namespaced as `serverName__toolName`
- **Parallel refresh** — each upstream is probed concurrently; a slow or unresponsive server never blocks others
- **Incremental cache** — tools from fast servers appear in the catalogue immediately, without waiting for slow ones
- **Zero-dependency default** — JSON file persistence, no database required
- **Enterprise-ready** — token-authenticated clients, per-client ACL, per-server rate limits, full audit trail

---

## Architecture

```
src/
├── McpNet.Core                  Protocol models, JSON-RPC, McpJsonOptions
├── McpNet.Gateway               Routing, aggregation, upstream clients, OAuth, telemetry
├── McpNet.Gateway.Persistence   EF Core repositories (SQLite / PostgreSQL)
├── McpNet.Transport.AspNetCore  ASP.NET Core MCP transport middleware
├── McpNet.Transport.Http        Streamable-HTTP & SSE upstream client
├── McpNet.Transport.Stdio       Stdio (child-process) upstream client
├── McpNet.Dashboard             Web dashboard + REST management API (embedded resources)
├── McpNet.Host                  Composition root — entry point
└── McpNet.Cli                   `mcpnet` management CLI
```

---

## Project Layout

| Project | Description |
|---|---|
| `McpNet.Core` | MCP protocol types, JSON-RPC request/response, `McpJsonOptions` (System.Text.Json) |
| `McpNet.Gateway` | Request router, tool aggregator, server registry, session manager, OAuth token provider, OpenTelemetry |
| `McpNet.Gateway.Persistence` | EF Core `DbContext` + repositories for SQLite and PostgreSQL |
| `McpNet.Transport.AspNetCore` | Maps `/mcp` using ASP.NET Core minimal APIs |
| `McpNet.Transport.Http` | HTTP upstream client for StreamableHTTP and SSE transport |
| `McpNet.Transport.Stdio` | Stdio upstream client — spawns child processes (`npx`, `uvx`, executables) |
| `McpNet.Dashboard` | Embedded web dashboard (`dashboard.html`, `dashboard.js`) + REST API endpoints |
| `McpNet.Host` | `Program.cs` — wires all services, starts Kestrel |
| `McpNet.Cli` | .NET global tool — `mcpnet` CLI for scripted management |

---

## Quick Start

### From source

```bash
git clone https://github.com/localtonet/McpNet
cd McpNet

# Build
dotnet build src/McpNet.Host/McpNet.Host.csproj -c Release

# Run (from binary directory so it finds mcp-data/)
cd src/McpNet.Host/bin/Release/net8.0/
./mcpnet-gateway
```

Gateway starts on **port 5050**. Open **http://localhost:5050/dashboard** to access the web UI.

### Docker (recommended)

```bash
# Default — JSON file persistence, no database
docker compose up -d

# With PostgreSQL
docker compose --profile postgres up -d
```

### Connect an MCP client

Point your AI agent at:

```
http://localhost:5050/mcp
Authorization: Bearer <client-bearer-token>
```

In **Dev mode** (default) the `Authorization` header is optional — all requests are allowed. In **Enterprise mode** a valid client bearer token is required.

---

## Configuration Reference

Configuration is read from `appsettings.json` under the `McpGateway` section, with environment variable overrides (`McpGateway__Key=value`):

```jsonc
{
  "McpGateway": {
    // Authentication mode: Dev (open) | Enterprise (token-required)
    "Mode": "Dev",

    // Admin token for the management API and dashboard
    // Override with env: MCPGATEWAY_ADMIN_TOKEN
    "AdminToken": "change-me-in-production",

    // Listening port. Override with env: MCPGATEWAY_PORT
    "Port": 5050,

    // Directory for JSON persistence files and custom catalog
    "DataDirectory": "mcp-data",

    // Persistence backend: Json (default) | sqlite | postgres
    "Persistence": "Json",

    // Connection strings (only needed for sqlite/postgres)
    "ConnectionStrings": {
      "Sqlite": "Data Source=mcp-data/mcpnet.db",
      "Postgres": "Host=localhost;Database=mcpnet;Username=mcpnet;Password=secret"
    },

    // Enable gateway self-management via MCP tools (default: false)
    "EnableMetaTools": false,

    // OpenTelemetry (default: disabled)
    "Telemetry": {
      "Enabled": false,
      "OtlpEndpoint": ""        // e.g. http://localhost:4317
    }
  }
}
```

All `McpGateway__*` keys can be set as environment variables, which take precedence over `appsettings.json`.

---

## Authentication Modes

| Mode | Behaviour |
|---|---|
| `Dev` | No authentication required. All MCP and API requests are allowed. Suitable for local development. |
| `Enterprise` | The management API requires `X-Admin-Token: <AdminToken>`. MCP endpoint requires `Authorization: Bearer <clientToken>`. |

Switch mode via config: `McpGateway__Mode=Enterprise`.

### Admin token

The admin token protects all `/api/*` endpoints and the dashboard. Set via `MCPGATEWAY_ADMIN_TOKEN` environment variable or `McpGateway.AdminToken` in `appsettings.json`.

---

## Upstream Servers

### StreamableHTTP / SSE

```jsonc
{
  "Name": "my-remote-server",
  "Url": "https://mcp.example.com/mcp",
  "TransportType": "StreamableHttp",  // or "Sse"
  "BearerToken": "optional-static-token"
}
```

### Stdio (npx / local process)

```jsonc
{
  "Name": "filesystem",
  "TransportType": "Stdio",
  "StdioCommand": "npx",
  "StdioArgs": ["-y", "@modelcontextprotocol/server-filesystem", "C:\\Users\\me\\Documents"],
  "StdioWorkingDirectory": null,
  "StdioEnvVars": {
    "API_KEY": "secret"            // injected into the child process environment
  }
}
```

`StdioEnvVars` lets you pass secrets to the child process without embedding them in `StdioArgs`.

The gateway probes the runtime automatically (`node --version` / `python --version` etc.) and reports this in the health endpoint response.

> **First-run note:** `npx` packages are downloaded on first connect. This can take 60–120 s. The gateway uses a 120 s per-server timeout and will succeed on the second background refresh (~60 s later) once the package is cached.

### Upstream Bearer & OAuth 2.0

Each registered server can authenticate to its upstream independently:

**Static Bearer token:**
```jsonc
{ "BearerToken": "upstream-token" }
```

**OAuth 2.0 Client Credentials:**
```jsonc
{
  "OAuth": {
    "Enabled": true,
    "TokenUrl": "https://auth.example.com/oauth/token",
    "ClientId": "my-client-id",
    "ClientSecret": "my-client-secret",
    "Scopes": ["mcp.read", "mcp.write"]
  }
}
```

OAuth behaviour:
- Access tokens are **cached in memory** and refreshed automatically ~30 s before expiry.
- On a `401` from the upstream the cached token is **invalidated and the request is retried once** with a fresh token.
- Custom HTTP headers per server are also supported (`CustomHeaders` dict).

---

## Tool Aggregation

When the gateway starts and periodically (every 60 s via `ToolRefreshBackgroundService`), it connects to each enabled upstream server and discovers its tools. All tools are merged into a single in-memory catalogue namespaced as:

```
{serverName}__{toolName}
```

### Parallel refresh with incremental cache

Each upstream server is refreshed **concurrently** (`Task.WhenAll`). When a server responds, its tools are written to the live cache **immediately** — without waiting for other servers to finish. This means:

- Fast HTTP servers (typically 1–3 s) are available right away.
- A slow or unresponsive stdio server (e.g. 120 s npx download timeout) does not block fast servers.
- The `totalTools` counter in `/api/tools/status` increases incrementally as each server responds.

### Individual tool enable/disable

Any tool can be disabled without removing the upstream server. Disabled tools are hidden from the tool list and return an error if called directly.

---

## Tool Groups

Tool groups let you bundle related tools and assign them to clients as a unit.

```
POST /api/groups          — create group
POST /api/groups/{id}/tools — add tool to group
DELETE /api/groups/{id}/tools/{toolName} — remove tool from group
```

A client assigned a group only sees (and can call) tools in that group. Groups can be managed from the dashboard **Tool Groups** tab or via the CLI.

---

## Client Management & Per-Client Access Control

In **Enterprise mode** every external MCP consumer is a `McpClient` with its own bearer token. Access is controlled at three levels:

| Level | Field | Behaviour |
|---|---|---|
| **Global** | `Enabled` | When `false`, all requests from this client are rejected |
| **Server ACL** | `AllowedServerIds` | If non-empty, client only sees tools from listed servers |
| **Group ACL** | `AllowedGroupIds` | If non-empty, client only sees tools in listed groups |

Leave both `AllowedServerIds` and `AllowedGroupIds` empty to grant access to everything.

Client tokens are 32-byte cryptographically random values (URL-safe Base64). Tokens can be regenerated at any time from the dashboard.

---

## Rate Limiting

Rate limiting is evaluated **per client, per tool call**, after tool and server resolution.

### Global limit

```jsonc
{ "RateLimitPerMinute": 60 }   // all tool calls across all servers combined
```

### Per-server override

```jsonc
{
  "RateLimitPerMinute": 200,
  "ServerRateLimits": [
    { "ServerId": "<guid>", "LimitPerMinute": 10 },   // tighter limit for expensive API
    { "ServerId": "<guid>", "LimitPerMinute": 500 }   // looser limit for local server
  ]
}
```

Precedence: **per-server limit** (if defined) > **global limit**. A value of `0` means unlimited.

When the limit is exceeded the gateway returns JSON-RPC error code `-32029`.

---

## Server Health Checks

```
GET /api/servers/{id}/health
```

Returns live health status for any registered server. For stdio servers, also probes the runtime:

```jsonc
{
  "id": "535fc449-...",
  "status": "healthy",          // healthy | warning | unhealthy
  "latencyMs": 18,
  "toolCount": 14,
  "note": null,
  "command": "npx",
  "runtimeInfo": "node v18.20.4 / npm 10.7.0"
}
```

The **Ping** button in the dashboard triggers this endpoint and updates the health chip inline.

---

## Audit Log

Every tool call is recorded in the audit log:

| Field | Description |
|---|---|
| `clientId` / `clientName` | Which client made the call |
| `method` | MCP method (`tools/call`) |
| `toolName` | Full tool name (`server__tool`) |
| `serverName` | Upstream server |
| `success` | Whether the call succeeded |
| `durationMs` | End-to-end latency |
| `timestamp` | UTC timestamp |

The audit log is viewable from the dashboard **Activity** tab and via `GET /api/audit`. It is also available through the `mcpnet audit` CLI command.

---

## MCP Catalog

The gateway includes a built-in catalog of **61 popular MCP servers** (embedded in the binary, no internet required). You can:

- **Browse** the catalog from the dashboard **Catalog** tab
- **Search** by name, category, or description
- **Add** any catalog entry to the gateway as a new upstream server in one click
- **Add custom entries** to a local `mcp-data/custom-catalog.json` file
- **Remove** custom entries

The catalog is served from `/api/catalog` and searched via `/api/catalog/search`.

---

## Web Dashboard

Access at **http://localhost:5050/dashboard** (or `/`).

### Tabs

| Tab | Description |
|---|---|
| **Dashboard** | Overview: server count, tool count, client count, recent activity feed |
| **Servers** | Register, edit, enable/disable, delete upstream servers; inline health chips; Ping button |
| **Catalog** | Browse / search 61+ built-in MCP servers; add custom entries; one-click install |
| **Tools** | Browse all aggregated tools; enable/disable individual tools; refresh trigger with live progress |
| **Tool Groups** | Create groups, manage tool membership |
| **Clients** | Create clients, manage permissions (server ACL, group ACL, rate limits, per-server rate limits), regenerate tokens |
| **Activity** | Filterable audit log table |
| **Settings** | Import / export gateway config; gateway info |

### Background auto-refresh

The dashboard polls `/api/tools/status` every **60 seconds** in the background and automatically updates tool counts and health chips without requiring a manual refresh.

### Incremental tool loading

When a manual **Refresh Tools** is triggered, the dashboard polls for status every 2 s and loads tools incrementally as fast servers respond — the tool list updates in real-time without waiting for slow servers.

---

## Management REST API

All endpoints are under `/api` and require `X-Admin-Token: <token>` in Enterprise mode.

### Servers

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/servers` | List all registered servers |
| `GET` | `/api/servers/{id}` | Get server details |
| `POST` | `/api/servers` | Register a new server |
| `PUT` | `/api/servers/{id}` | Update server |
| `DELETE` | `/api/servers/{id}` | Remove server |
| `PATCH` | `/api/servers/{id}/toggle` | Enable / disable server |
| `GET` | `/api/servers/{id}/tools` | List tools from a specific server |
| `GET` | `/api/servers/{id}/health` | Live health check (latency, tool count, runtime info) |
| `GET` | `/api/servers/{id}/stdio-probe` | Probe stdio command path and runtime |

### Tools

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/tools` | List all aggregated tools |
| `POST` | `/api/tools/refresh` | Trigger async refresh (returns `202 Accepted`) |
| `GET` | `/api/tools/status` | Refresh status (`refreshing`, `totalTools`, diagnostics) |
| `GET` | `/api/tools/diagnostics` | Last refresh diagnostics per server |
| `POST` | `/api/tools/call` | Call a tool directly from the dashboard |
| `PATCH` | `/api/servers/{id}/tools/{toolName}/toggle` | Enable / disable individual tool |

### Groups

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/groups` | List all groups |
| `POST` | `/api/groups` | Create group |
| `DELETE` | `/api/groups/{id}` | Delete group |
| `POST` | `/api/groups/{id}/tools` | Add tool to group |
| `DELETE` | `/api/groups/{id}/tools/{toolName}` | Remove tool from group |

### Clients

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/clients` | List all clients |
| `GET` | `/api/clients/{id}` | Get client details |
| `POST` | `/api/clients` | Create client |
| `PUT` | `/api/clients/{id}` | Update client (ACL, rate limits, enabled) |
| `DELETE` | `/api/clients/{id}` | Delete client |
| `POST` | `/api/clients/{id}/regenerate` | Regenerate bearer token |

### Catalog

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/catalog` | Get full catalog (built-in + custom) |
| `GET` | `/api/catalog/search` | Search catalog (`?q=`) |
| `POST` | `/api/catalog/custom` | Add custom catalog entry |
| `DELETE` | `/api/catalog/custom/{name}` | Remove custom catalog entry |

### Other

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/audit` | Recent audit log entries |
| `GET` | `/api/export` | Export full gateway config (servers, groups, clients) |
| `POST` | `/api/import` | Import config (adds new entries, skips existing names) |
| `GET` | `/health` | Process health check (status, version, mode) |

---

## Management CLI — `mcpnet`

Install as a .NET global tool:

```bash
dotnet tool install --global McpNet.Cli
```

Or run from source:

```bash
dotnet run --project src/McpNet.Cli -- <command>
```

### First-time setup

Save your gateway URL and admin token once — no need to pass flags on every command:

```bash
mcpnet configure --url http://localhost:5050 --token 111111111111111111
mcpnet configure --show     # print current values (token is masked)
mcpnet configure --clear    # delete config file
mcpnet whoami               # show resolved URL and token source
```

Config is stored in `~/.mcpnet/config.json`. On Unix/macOS the file is automatically `chmod 600`.

**Priority chain** (highest → lowest):
1. `--url` / `--token` flags
2. `MCPNET_URL` / `MCPNET_ADMIN_TOKEN` env vars
3. `~/.mcpnet/config.json`
4. Default: `http://localhost:5050` / no token (Dev mode)

### Command reference

```bash
# ── Servers ──────────────────────────────────────────────────────────────────
mcpnet servers
mcpnet register --name myserver --server-url https://mcp.example.com/mcp
mcpnet register --name fs --transport Stdio --command npx \
    --arg -y --arg @modelcontextprotocol/server-filesystem --arg ~/docs \
    --env SOME_API_KEY=secret          # --arg and --env can be repeated
mcpnet deregister myserver
mcpnet refresh

# ── Tools ────────────────────────────────────────────────────────────────────
mcpnet tools [--server <name>]
mcpnet enable  myserver__search
mcpnet disable myserver__search

# ── Groups ───────────────────────────────────────────────────────────────────
mcpnet groups
mcpnet group create --name search-tools [--description ".."]
mcpnet group delete search-tools
mcpnet group add-tool    search-tools myserver__search
mcpnet group remove-tool search-tools myserver__search

# ── Clients ──────────────────────────────────────────────────────────────────
mcpnet clients
mcpnet client myagent                              # show detail
mcpnet client create --name myagent
mcpnet client delete myagent
mcpnet client regenerate myagent                   # new bearer token
mcpnet client update myagent --rate-limit 120
mcpnet client update myagent --enabled false

# ── Audit & meta ─────────────────────────────────────────────────────────────
mcpnet audit
mcpnet version
```

---

## Meta-Tools (gateway self-management via MCP)

When `EnableMetaTools: true`, the gateway exposes its own management surface as MCP tools, allowing an AI agent to administer the gateway autonomously.

Available meta-tools (all prefixed `mcpnet__`):

| Tool | Description |
|---|---|
| `mcpnet__list_servers` | List registered servers |
| `mcpnet__register_server` | Register a new upstream server |
| `mcpnet__deregister_server` | Remove a server |
| `mcpnet__list_tools` | List aggregated tools |
| `mcpnet__enable_tool` | Enable a tool |
| `mcpnet__disable_tool` | Disable a tool |
| `mcpnet__refresh` | Trigger tool refresh |
| `mcpnet__create_group` | Create a tool group |
| `mcpnet__list_groups` | List tool groups |

> Disabled by default. Enable only in trusted environments.

---

## Secrets at Rest

All sensitive values stored in JSON persistence files are **encrypted at rest** using ASP.NET Core Data Protection.

### Encrypted fields

| Field | Model | Description |
|---|---|---|
| `BearerToken` | `RegisteredServer` | Upstream auth token sent to the server |
| `OAuth.ClientSecret` | `OAuthConfig` | OAuth 2.0 client credentials secret |
| `BearerToken` | `McpClient` | Bearer token that clients use to authenticate to the gateway |

### Storage format

Encrypted values are stored with an `enc:` prefix:

```json
{
  "name": "my-server",
  "bearerToken": "enc:CfDJ8Kx7mN3p..."
}
```

Plaintext values (from existing files before encryption was enabled) are **automatically detected** (no `enc:` prefix) and loaded as-is — no manual migration needed. The value is re-encrypted the next time that record is saved.

### Key management

Encryption keys are stored in `mcp-data/dp-keys/` as XML files:

- **Windows**: keys protected with DPAPI (bound to the machine account)
- **Linux / macOS**: AES-256-CBC, key files in `dp-keys/`

> Back up `mcp-data/dp-keys/` together with your data files. Deleting this folder makes all encrypted secrets unrecoverable.

For Docker / multi-instance deployments, mount `dp-keys/` as a persistent volume so keys survive container restarts.

---

## Persistence Backends

| Backend | Config | Notes |
|---|---|---|
| **JSON** (default) | `Persistence: Json` | Zero-dependency. Secrets encrypted at rest via Data Protection. Files in `DataDirectory/`. |
| **SQLite** | `Persistence: sqlite` + connection string | Single-file relational DB. Good for moderate load. |
| **PostgreSQL** | `Persistence: postgres` + connection string | Full relational DB. Required for multi-instance / HA deployments. |

Switching backends requires migrating data manually (use export/import).

---

## OpenTelemetry

Disabled by default. Enable in `appsettings.json`:

```jsonc
"Telemetry": {
  "Enabled": true,
  "OtlpEndpoint": "http://localhost:4317"   // omit for console exporter
}
```

**Traces:**
- `mcp.tool.call` — span per tool call (source: `McpNet.Gateway`)
- Tags: `mcp.tool.name`, `mcp.server.name`
- ASP.NET Core + HttpClient instrumentation included

**Metrics:**
- `mcpnet.tool_calls` — counter, tagged by `tool` and `server`
- `mcpnet.tool_call.duration` — histogram (milliseconds)

If `OtlpEndpoint` is set, uses the OTLP gRPC exporter. Otherwise uses the console exporter (useful for local debugging).

---

## Session Management

The gateway maintains lightweight MCP sessions (`Mcp-Session-Id` header) with a 24-hour TTL. Sessions are stored in-memory and purged automatically every 30 minutes. Each session tracks created/last-seen timestamps for connection lifecycle management.

---

## Docker

```bash
# Default — JSON persistence, port 5050
docker compose up -d

# PostgreSQL persistence
docker compose --profile postgres up -d

# Override admin token
MCPNET_ADMIN_TOKEN=mysecret docker compose up -d
```

**Image properties:**
- Multi-stage build (SDK → runtime)
- Runs as non-root user
- Exposes port `5050`
- Includes Docker healthcheck (`/health` endpoint)
- Data volume mounted at `/app/mcp-data`

```dockerfile
# Environment variables recognised at runtime
McpGateway__Mode=Enterprise
McpGateway__AdminToken=<secret>
McpGateway__Port=5050
McpGateway__Persistence=Json|sqlite|postgres
McpGateway__DataDirectory=/app/mcp-data
McpGateway__EnableMetaTools=false
```

---

## Import / Export

Export the full gateway configuration (servers, groups, clients) to a JSON file:

```bash
GET /api/export
# → mcpnet-config-2026-06-18.json
```

Import into another gateway instance (adds new entries, skips existing names):

```bash
POST /api/import
Content-Type: application/json

{ "servers": [...], "groups": [...], "clients": [...] }
```

Also available from the dashboard **Settings** tab.

---

## Testing

```bash
dotnet test McpNet.slnx -c Release
```

Test coverage includes:

- MCP protocol serialisation / deserialisation
- JSON-RPC request/response handling
- Session management (create, resolve, expiry)
- Authentication (Dev mode, Enterprise mode, admin token validation)
- Gateway routing (tool list filtering, ACL, rate limiting)
- OAuth token provider (caching, refresh, 401-retry)
- Meta-tool handler
- JSON persistence round-trips (servers, groups, clients, audit)
- CLI argument parsing

---

## Security Notes

| Area | Status | Recommendation |
|---|---|---|
| `ClientSecret` (OAuth) | **Encrypted** — `enc:` prefix in `servers.json` | Back up `mcp-data/dp-keys/`; see [Secrets at Rest](#secrets-at-rest) |
| `BearerToken` (servers & clients) | **Encrypted** — `enc:` prefix in JSON files | Same as above |
| Admin token | Env var `MCPGATEWAY_ADMIN_TOKEN` takes precedence | Always set this in production; do not use the default |
| Dev mode | No authentication | Never expose Dev mode to the internet |
| Client bearer tokens | 32-byte cryptographically random (URL-safe Base64) | Rotate via `POST /api/clients/{id}/regenerate` or `mcpnet client regenerate <name>` |
| GET `/mcp` SSE | Authenticated in Enterprise mode (same Bearer check as POST) | Enforce Enterprise mode in production |
| TLS | Not handled by the gateway | Place behind a reverse proxy (nginx, Caddy, YARP) for TLS termination in production |

---

## Built With

- [.NET 8](https://dotnet.microsoft.com/) — runtime and framework
- [ASP.NET Core](https://learn.microsoft.com/aspnet/core) — HTTP server and minimal APIs
- [System.Text.Json](https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/overview) — all serialisation (no Newtonsoft)
- [Entity Framework Core](https://learn.microsoft.com/ef/core/) — optional SQLite / PostgreSQL persistence
- [OpenTelemetry .NET](https://opentelemetry.io/docs/languages/net/) — observability (opt-in)
- [Model Context Protocol](https://modelcontextprotocol.io/) — AI tool-call protocol

---

*Built on .NET 8 · System.Text.Json only · Nullable enabled · No implicit usings*


The CLI talks to the gateway's `/api` surface (uses `AdminToken`).

```bash
# Servers
mcpnet servers                       # list registered upstream servers
mcpnet register --name ctx7 --url http://localhost:9000/mcp
mcpnet deregister --name ctx7
mcpnet refresh                       # re-discover upstream tools

# Tools
mcpnet tools                         # list aggregated tools
mcpnet enable  --name ctx7__search
mcpnet disable --name ctx7__search

# Groups
mcpnet groups                        # list tool groups
mcpnet group create <name>

# Clients & audit
mcpnet clients
mcpnet client <name>
mcpnet audit                         # recent audit log
mcpnet version
```

Global flags: `--url <gateway>` (default `http://localhost:5051`), `--token <adminToken>`.

---

## Upstream OAuth

Any registered server can authenticate to its upstream using **OAuth 2.0 client-credentials**. Configure `OAuth` on the `RegisteredServer`:

```jsonc
{
  "Name": "secure-api",
  "Url": "https://upstream/mcp",
  "OAuth": {
    "Enabled": true,
    "TokenUrl": "https://auth.example.com/oauth/token",
    "ClientId": "my-client",
    "ClientSecret": "my-secret",
    "Scopes": ["mcp.read", "mcp.write"]
  }
}
```

Behavior:
- Tokens are cached and refreshed automatically ~30s before expiry.
- On a `401` from the upstream, the cached token is invalidated and the request is retried **once** with a fresh token.
- If `OAuth` is omitted, a static `BearerToken` (if set) is used instead.

> **Security note:** `ClientSecret` is currently stored in plaintext in `servers.json`. Restrict file permissions on `DataDirectory`, or inject secrets via environment/secret store in production. This is a known limitation.

---

## Meta-tools (gateway self-management) — _off by default_

When `EnableMetaTools: true`, the gateway exposes its own management surface as MCP tools prefixed `mcpnet__`, so an MCP client can administer the gateway:

`mcpnet__list_servers`, `mcpnet__register_server`, `mcpnet__deregister_server`, `mcpnet__list_tools`, `mcpnet__enable_tool`, `mcpnet__disable_tool`, `mcpnet__refresh`, and (when groups are configured) `mcpnet__create_group`, `mcpnet__list_groups`.

Disabled by default to keep the default deployment minimal and safe.

---

## OpenTelemetry — _off by default_

Set `Telemetry:Enabled: true` to emit traces and metrics:
- Traces: `mcp.tool.call` spans (source `McpNet.Gateway`), plus ASP.NET Core & HttpClient instrumentation.
- Metrics: `mcpnet.tool_calls` (counter), `mcpnet.tool_call.duration` (histogram).

Exporter selection:
- If `OtlpEndpoint` is set → OTLP exporter.
- Otherwise → Console exporter (useful for local debugging).

---

## Docker

```bash
# Default (JSON persistence)
docker compose up --build

# With Postgres
docker compose --profile postgres up --build
```

The image is a multi-stage build, runs as a non-root user, exposes port `5050`, and includes a healthcheck.

---

## Testing

```bash
dotnet test McpNet.slnx -c Release
```

Covers protocol serialization, session management, auth, gateway models, OAuth token provider (caching/refresh/retry), meta-tool handler, JSON persistence round-trips, and CLI argument parsing.
