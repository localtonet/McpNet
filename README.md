# McpNet Gateway

> **A high-performance, enterprise-ready Model Context Protocol (MCP) gateway for .NET 8.**  
> Aggregate any number of upstream MCP servers behind a single authenticated endpoint - with a web dashboard, management CLI, per-client access control, rate limiting, audit logging, and optional OpenTelemetry observability.

---

## Table of Contents

1. [Overview](#overview)
2. [Architecture](#architecture)
3. [Project Layout](#project-layout)
4. [Installation](#installation)
5. [Quick Start](#quick-start)
6. [Configuration Reference](#configuration-reference)
7. [Authentication Modes](#authentication-modes)
8. [Upstream Servers](#upstream-servers)
   - [StreamableHTTP / SSE](#streamablehttp--sse)
   - [Stdio (npx / local process)](#stdio-npx--local-process)
   - [Stdio Auto-Restart](#stdio-auto-restart)
   - [Upstream Bearer & OAuth 2.0](#upstream-bearer--oauth-20)
9. [Tool Aggregation](#tool-aggregation)
   - [Auto Health Check & Auto-Disable](#auto-health-check--auto-disable)
   - [Tool Argument Validation](#tool-argument-validation)
   - [Tool Aliases](#tool-aliases)
   - [Gateway Chaining (Gateway-of-Gateways)](#gateway-chaining-gateway-of-gateways)
10. [Tool Response Cache](#tool-response-cache)
11. [Real-Time Notifications (SSE Push)](#real-time-notifications-sse-push)
12. [Tool Groups](#tool-groups)
13. [Client Management & Per-Client Access Control](#client-management--per-client-access-control)
14. [Rate Limiting](#rate-limiting)
15. [Server Health Checks](#server-health-checks)
16. [Audit Log](#audit-log)
17. [MCP Catalog](#mcp-catalog)
18. [Security Quarantine](#security-quarantine)
19. [BM25 Semantic Tool Search](#bm25-semantic-tool-search)
20. [Web Dashboard](#web-dashboard)
21. [Management REST API](#management-rest-api)
22. [Management CLI - `mcpnet`](#management-cli--mcpnet)
23. [Meta-Tools (gateway self-management via MCP)](#meta-tools-gateway-self-management-via-mcp)
24. [Secrets at Rest](#secrets-at-rest)
25. [Persistence Backends](#persistence-backends)
26. [OpenTelemetry](#opentelemetry)
27. [Session Management](#session-management)
28. [Docker](#docker)
29. [Import / Export](#import--export)
30. [Testing](#testing)
31. [Security Notes](#security-notes)

---

## Overview

McpNet Gateway sits between your AI agents (Claude, Cursor, any MCP client) and one or more upstream MCP servers. Instead of configuring every agent to know about every server, agents connect to the gateway once and see a unified, namespaced tool catalogue.

```
AI Agent (MCP client)
        │  Bearer token
        ▼
 ┌------------------─┐
 │          McpNet Gateway             │
 │  ┌---------------┐   │
 │  │  Auth · Rate Limit · Audit   │   │
 │  └-------┬-------─┘   │
 │  ┌-------▼-------─┐   │
 │  │      Tool Aggregator         │   │
 │  │  (parallel refresh cache)    │   │
 │  └-┬-----┬-----┬--─┘   │
 └--─│-----│-----│----─┘
       │          │          │
       ▼          ▼          ▼
  Server A    Server B    Server C
 (HTTP/MCP)  (stdio/npx) (OAuth API)
```

**Key properties:**

- **Single MCP endpoint** at `/mcp` - all upstream tools namespaced as `serverName__toolName`
- **Parallel refresh** - each upstream is probed concurrently; a slow or unresponsive server never blocks others
- **Incremental cache** - tools from fast servers appear in the catalogue immediately, without waiting for slow ones
- **Zero-dependency default** - JSON file persistence, no database required
- **Enterprise-ready** - token-authenticated clients, per-client ACL, per-server rate limits, full audit trail

---

## Architecture

```
src/
├- McpNet.Core                  Protocol models, JSON-RPC, McpJsonOptions
├- McpNet.Gateway               Routing, aggregation, upstream clients, OAuth, telemetry
├- McpNet.Gateway.Persistence   EF Core repositories (SQLite / PostgreSQL)
├- McpNet.Transport.AspNetCore  ASP.NET Core MCP transport middleware
├- McpNet.Transport.Http        Streamable-HTTP & SSE upstream client
├- McpNet.Transport.Stdio       Stdio (child-process) upstream client
├- McpNet.Dashboard             Web dashboard + REST management API (embedded resources)
├- McpNet.Host                  Composition root - entry point
└- McpNet.Cli                   `mcpnet` management CLI
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
| `McpNet.Transport.Stdio` | Stdio upstream client - spawns child processes (`npx`, `uvx`, executables) |
| `McpNet.Dashboard` | Embedded web dashboard (`dashboard.html`, `dashboard.js`) + REST API endpoints |
| `McpNet.Host` | `Program.cs` - wires all services, starts Kestrel |
| `McpNet.Cli` | .NET global tool - `mcpnet` CLI for scripted management |

---

## Installation

### Pre-built binaries (GitHub Releases)

Download the latest `mcpnet-gateway` binary for your platform from the [Releases](../../releases) page:

| Platform | Archive |
|---|---|
| Windows x64 | `mcpnet-gateway-<version>-win-x64.zip` |
| Linux x64 | `mcpnet-gateway-<version>-linux-x64.tar.gz` |
| Linux ARM64 | `mcpnet-gateway-<version>-linux-arm64.tar.gz` |
| macOS x64 (Intel) | `mcpnet-gateway-<version>-osx-x64.tar.gz` |
| macOS ARM64 (Apple Silicon) | `mcpnet-gateway-<version>-osx-arm64.tar.gz` |

The `mcpnet` CLI binary is published in separate archives under the same release (e.g. `mcpnet-<version>-linux-x64.tar.gz`).

```bash
# Linux/macOS example
tar -xzf mcpnet-gateway-1.0.0-linux-x64.tar.gz
chmod +x mcpnet-gateway
./mcpnet-gateway
```

### NuGet - embed in your own application

The gateway can be embedded in any .NET 8 application without ASP.NET Core:

```bash
dotnet add package McpNet.Gateway.Standalone
```

```csharp
using McpNet.Gateway.Standalone;

await McpGatewayBuilder.Create()
    .ListenOn(5050)
    .WithDataDirectory("mcp-data")
    .WithMode(GatewayMode.Dev)
    .Build()
    .StartAsync();
```

The core routing library (without the HttpListener host) is also available separately:

```bash
dotnet add package McpNet.Gateway
```

### Docker

```bash
docker compose up -d
```

See the [Docker](#docker) section for full details.

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
# Default - JSON file persistence, no database
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

In **Dev mode** (default) the `Authorization` header is optional - all requests are allowed. In **Enterprise mode** a valid client bearer token is required.

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
  },
  "AutoRestart": true,             // restart on unexpected exit (default: true)
  "CacheTtlSeconds": 0,            // cache tool call results; 0 = disabled (default)
  "ToolAliases": {                 // optional extra names for tools on this server
    "my_alias": "original_tool_name"
  }
}
```

`StdioEnvVars` lets you pass secrets to the child process without embedding them in `StdioArgs`.

The gateway probes the runtime automatically (`node --version` / `python --version` etc.) and reports this in the health endpoint response.

> **First-run note:** `npx` packages are downloaded on first connect. This can take 60–120 s. The gateway uses a 120 s per-server timeout and will succeed on the second background refresh (~60 s later) once the package is cached.

### Stdio Auto-Restart

When `AutoRestart: true` (the default for all stdio servers), the gateway automatically reconnects when the child process exits unexpectedly:

| Attempt | Delay before retry |
|---|---|
| 1 | 2 s |
| 2 | 4 s |
| 3 | 8 s |
| … | doubles each time (max 60 s) |
| 10 | stops retrying |

Once the process is back and responds, `IsConnected` is restored and tools are refreshed normally. Disable per-server with `"AutoRestart": false`.

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

Each upstream server is refreshed **concurrently** (`Task.WhenAll`). When a server responds, its tools are written to the live cache **immediately** - without waiting for other servers to finish. This means:

- Fast HTTP servers (typically 1–3 s) are available right away.
- A slow or unresponsive stdio server (e.g. 120 s npx download timeout) does not block fast servers.
- The `totalTools` counter in `/api/tools/status` increases incrementally as each server responds.

### Individual tool enable/disable

Any tool can be disabled without removing the upstream server. Disabled tools are hidden from the tool list and return an error if called directly.

---

### Auto Health Check & Auto-Disable

The gateway tracks consecutive failures per upstream server. After **3 consecutive failed refreshes**, the server is automatically marked as *auto-disabled*:

- Its tools are hidden from `tools/list` and return an error if called.
- The gateway **continues retrying** on every refresh cycle.
- When the server responds successfully again, the auto-disable flag is cleared **automatically** and the tools reappear - no manual intervention needed.

```
GET /api/tools/status   → "diagnostics" array shows which servers are failing
```

### Tool Argument Validation

Before forwarding any `tools/call` to an upstream, the gateway validates the supplied arguments against the tool's `inputSchema`. If a required field is missing or a type is wrong, the gateway returns immediately with error code `-32602 Invalid params` - saving a round-trip to the upstream.

Supported schema checks: `required` fields, `type` constraints (`string`, `number`, `integer`, `boolean`, `array`, `object`).

### Tool Aliases

A server can expose its tools under additional names without modifying the upstream:

```jsonc
{
  "Name": "my-server",
  "ToolAliases": {
    "my_short_name": "original_long_tool_name"
  }
}
```

This creates an extra entry `my-server__my_short_name` in the catalogue that routes to the same underlying tool. Useful for backwards compatibility when renaming tools.

---

### Gateway Chaining (Gateway-of-Gateways)

Because McpNet Gateway itself speaks the MCP protocol, one gateway can be registered as an **upstream server** of another gateway. This lets you build federated topologies without any special configuration.

**Example scenario:**

- **Gateway A** - Alice's machine, exposes tools `read_file`, `search_web`
- **Gateway B** - Bob's machine, exposes tools `generate_image`, `send_email`
- **Gateway C** - shared server; registers A as `"alice"` and B as `"bob"`

When an AI agent (Claude, Cursor, etc.) connects to **C**, it sees a single unified tool list:

```
alice__read_file
alice__search_web
bob__generate_image
bob__send_email
```

```
AI Agent (Claude)
      │  one MCP connection
      ▼
┌-----------┐
│   Gateway C          │  ← AI sees this only; tool names prefixed by C
└---─┬---┬---─┘
        │      │  upstream MCP calls (original tool names, no prefix)
        ▼      ▼
   Gateway A  Gateway B
```

**How it works end-to-end:**

1. C's `ToolAggregator` refreshes A and B as normal upstream MCP servers.
2. Each tool is stored with `FullName = "{serverName}__{localName}"` (e.g. `alice__read_file`) and `LocalName = "read_file"`.
3. When the agent calls `tools/list`, C returns the `FullName` values - the agent sees the prefix.
4. When the agent calls `tools/call` with `alice__read_file`, C strips the prefix and forwards `read_file` to Gateway A. A and B never see the prefix.

**Key points:**

- The prefix (`alice`, `bob`) is whatever name **C's operator** gives the upstream gateway when registering it - it is not fixed by A or B.
- Name collisions are impossible: even if A and B both expose a tool named `get_time`, C presents them as `alice__get_time` and `bob__get_time`.
- Auth, rate limiting, and ACLs on C apply to the aggregated view; A and B can independently require their own bearer tokens from C.
- Chaining depth is unlimited - a gateway can aggregate other aggregating gateways.

---

## Tool Response Cache

Tool call results can be cached in-memory to reduce upstream load for frequently called, idempotent tools. Configure per-server:

```jsonc
{
  "Name": "my-server",
  "CacheTtlSeconds": 300    // cache responses for 5 minutes; 0 = disabled (default)
}
```

Cache behaviour:
- Only **non-error** responses are cached.
- Cache key is a SHA-256 hash of `toolFullName + serialised arguments` - different argument combinations are cached separately.
- The entire server's cache is invalidated automatically after a tool refresh (`ToolRefreshBackgroundService`).
- `CacheTtlSeconds: 0` (default) disables caching for that server.

---

## Real-Time Notifications (SSE Push)

Clients connected via the SSE endpoint (`GET /mcp`) receive push notifications when the tool catalogue changes. This eliminates the need for polling `tools/list`:

| Notification | When sent |
|---|---|
| `notifications/tools/list_changed` | After any successful tool refresh that changes the catalogue |
| `notifications/prompts/list_changed` | After a prompt refresh |
| `notifications/resources/list_changed` | After a resource refresh |

To detect changes without an active SSE connection, poll:

```
GET /api/tools/version
→ { "version": 42, "lastRefreshedAt": "2026-06-22T10:00:00Z" }
```

The `version` counter increments each time the tool catalogue changes (content-based - identical refreshes do not bump it).

---

## Tool Groups

Tool groups let you bundle related tools and assign them to clients as a unit.

```
POST /api/groups          - create group
POST /api/groups/{id}/tools - add tool to group
DELETE /api/groups/{id}/tools/{toolName} - remove tool from group
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

## Security Quarantine

The Security Quarantine feature protects against **Tool Poisoning Attacks** — a class of prompt-injection attack where a malicious MCP server injects hidden instructions inside tool descriptions to hijack the AI agent's behavior.

### How it works

Every server registered via the REST API (`POST /api/servers`) is placed in **quarantine by default** and does not connect or expose tools until explicitly approved by an administrator:

```
POST /api/servers           →  server created, Quarantined = true
                                → tools NOT refreshed, NOT visible to agents

POST /api/servers/{id}/approve  → Quarantined = false, refresh triggered
                                    → tools now visible to agents
```

> **Bypass for trusted automation:** pass `"autoApprove": true` in the `POST /api/servers` body to approve immediately (e.g. your own CI pipeline or a local dev setup).

### Quarantine endpoints

| Method | Path | Description |
|---|---|---|
| `POST` | `/api/servers/{id}/approve` | Clear quarantine, trigger tool refresh |
| `POST` | `/api/servers/{id}/quarantine` | Place an active server back into quarantine |
| `GET` | `/api/servers/quarantine` | List all currently quarantined servers |

### Dashboard integration

- The **Overview** tab shows a prominent amber alert banner when quarantined servers are pending review.
- The **Servers** table has a new **Status** column: `Active` (green) or `Quarantined` (orange).
  - Active servers show a shield icon button to quarantine them.
  - Quarantined servers show a green **✓ Approve** button.
- The **Security** tab shows the full quarantine review queue with one-click approve/delete actions.

### Server registration flow in practice

```
External agent or CI pipeline:
  POST /api/servers { name: "untrusted-server", url: "..." }
  → Server registered, quarantined
  → Admin reviews description in Security tab
  → Admin clicks Approve  (or DELETE if malicious)
  → Tools become available to agents
```

---

## BM25 Semantic Tool Search

When many MCP servers are connected the agent's context window fills quickly because `tools/list` returns every tool schema. The BM25 Semantic Tool Search feature solves this by exposing a single `mcpnet__retrieve_tools` tool that the agent calls to retrieve only the relevant tool schemas for the current task.

### Token savings example

```
Without BM25:  tools/list → 50 schemas → ~7,500 tokens used
With BM25:     tools/list → 1 schema (retrieve_tools) → ~150 tokens
               retrieve_tools("read file") → top-5 results → ~750 tokens
               Savings: ~6,600 tokens per turn
```

### How it works

1. On every tool refresh, the gateway builds an in-memory **BM25 index** over all tool names and descriptions.
2. When the agent calls `mcpnet__retrieve_tools`, the gateway scores every tool with BM25 and returns the top-N matching schemas.
3. The `ToolSearchMetrics` singleton counts calls, results returned, and estimates cumulative token savings.

### BM25 index details

- **Zero dependencies** — pure C# implementation, no NuGet packages.
- **Tuning:** K1 = 1.5, B = 0.75 (standard BM25 parameters).
- **IDF formula:** Robertson-Sparck Jones variant (`log((N - df + 0.5)/(df + 0.5) + 1)`) — always ≥ 0, even for terms appearing in every document.
- **Tokenizer:** splits on camelCase boundaries, `__`, `-`, whitespace; lowercases all terms. `deepwiki__ask_question` → `["deepwiki", "ask", "question"]`.
- **Prefix expansion:** query terms of ≥ 3 characters are expanded to all vocabulary terms with that prefix, so `"file"` matches `"filesystem"`, `"deep"` matches `"deepwiki"`, etc.
- **Thread safety:** `Rebuild` and `Search` share a single `volatile IndexSnapshot` reference. The snapshot (docs + IDF table + average doc length) is replaced atomically — readers always see a consistent triple with no locks needed.
- **Lazy rebuild:** if a search is requested before the first background `RefreshAsync` completes, the index is built on-demand from the current tool cache.
- **Pre-built TF:** term-frequency dictionaries are computed once at index build time, not on every search, eliminating per-search `Dictionary` allocations.

### `mcpnet__retrieve_tools` tool

Automatically available when `EnableMetaTools: true`:

```json
{
  "name": "mcpnet__retrieve_tools",
  "description": "Search for relevant MCP tools by semantic query. Returns top-N tool schemas ranked by BM25 relevance. Call this instead of loading all tool schemas to save context tokens.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "query": { "type": "string", "description": "Natural-language description of the task" },
      "top":   { "type": "integer", "description": "Max results to return (default: 5)" }
    },
    "required": ["query"]
  }
}
```

Example response:
```json
[
  {
    "fullName": "filesystem__read_file",
    "serverName": "filesystem",
    "description": "Read the complete contents of a file from the file system.",
    "inputSchema": { ... },
    "score": 4.821
  }
]
```

### BM25 REST endpoints

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/tools/search?q={query}` | Live BM25 search; returns top-10 results + total tool count |
| `GET` | `/api/tools/search-metrics` | Cumulative token-savings metrics |

`search-metrics` response:
```jsonc
{
  "enabled": true,
  "totalCalls": 142,
  "totalResultsReturned": 710,
  "estimatedTokensSaved": 189750,   // (totalTools - 1 - resultsReturned) × 150 tokens/schema
  "averageResultsPerCall": 5.0,
  "averageToolsAtCall": 34.2,
  "tokensPerSchema": 150,
  "firstCallAt": "2026-06-22T08:00:00Z",
  "lastCallAt": "2026-06-22T14:22:00Z"
}
```

### Dashboard: Security tab

The **Security** tab in the dashboard surfaces both quarantine management and BM25 metrics:

- **Quarantine queue** — pending servers with Approve / Delete actions.
- **BM25 Token Savings** — live metric cards: total calls, estimated tokens saved, average results per call, average tools at call time.
- **Live Search Tester** — type any query and see real BM25 results with scores and a visual score bar.

---

## Web Dashboard

Access at **http://localhost:5050/dashboard** (or `/`).

### Tabs

| Tab | Description |
|---|---|
| **Dashboard** | Overview: server count, tool count, client count, quarantine alert, recent activity feed |
| **Servers** | Register, edit, enable/disable, quarantine/approve, delete upstream servers; Status column; inline health chips; Ping button |
| **Catalog** | Browse / search 61+ built-in MCP servers; add custom entries; one-click install |
| **Tools** | Browse all aggregated tools; enable/disable individual tools; refresh trigger with live progress |
| **Tool Groups** | Create groups, manage tool membership |
| **Clients** | Create clients, manage permissions (server ACL, group ACL, rate limits, per-server rate limits), regenerate tokens |
| **Activity** | Filterable audit log table |
| **Security** | Quarantine review queue (approve / delete); BM25 token savings metrics; live BM25 search tester |
| **Settings** | Import / export gateway config; gateway info |

### Background auto-refresh

The dashboard polls `/api/tools/status` every **60 seconds** in the background and automatically updates tool counts and health chips without requiring a manual refresh.

### Incremental tool loading

When a manual **Refresh Tools** is triggered, the dashboard polls for status every 2 s and loads tools incrementally as fast servers respond - the tool list updates in real-time without waiting for slow servers.

---

## Management REST API

All endpoints are under `/api` and require `X-Admin-Token: <token>` in Enterprise mode.

### Servers

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/servers` | List all registered servers |
| `GET` | `/api/servers/{id}` | Get server details |
| `POST` | `/api/servers` | Register a new server (quarantined by default; pass `autoApprove: true` to bypass) |
| `PUT` | `/api/servers/{id}` | Update server |
| `DELETE` | `/api/servers/{id}` | Remove server |
| `PATCH` | `/api/servers/{id}/toggle` | Enable / disable server |
| `POST` | `/api/servers/{id}/approve` | Clear quarantine flag and trigger tool refresh |
| `POST` | `/api/servers/{id}/quarantine` | Put an active server into quarantine |
| `GET` | `/api/servers/quarantine` | List all currently quarantined servers |
| `GET` | `/api/servers/{id}/tools` | List tools from a specific server |
| `GET` | `/api/servers/{id}/health` | Live health check (latency, tool count, runtime info) |
| `GET` | `/api/servers/{id}/stdio-probe` | Probe stdio command path and runtime |

### Tools

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/tools` | List all aggregated tools |
| `GET` | `/api/tools/version` | Tool catalogue version counter + last refresh time |
| `POST` | `/api/tools/refresh` | Trigger async refresh (returns `202 Accepted`) |
| `GET` | `/api/tools/status` | Refresh status (`refreshing`, `totalTools`, diagnostics) |
| `GET` | `/api/tools/diagnostics` | Last refresh diagnostics per server |
| `POST` | `/api/tools/call` | Call a tool directly from the dashboard |
| `PATCH` | `/api/servers/{id}/tools/{toolName}/toggle` | Enable / disable individual tool |
| `GET` | `/api/tools/search?q={query}` | BM25 semantic search over all tools (top-10) |
| `GET` | `/api/tools/search-metrics` | Cumulative BM25 token-savings statistics |

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
| `POST` | `/api/import/claude-desktop` | Import from Claude Desktop config format (`mcpServers`) |
| `GET` | `/health` | Process health check (status, version, mode) |
| `GET` | `/metrics` | Prometheus scraping endpoint (requires `Telemetry:Prometheus:Enabled=true`) |

---

## Management CLI - `mcpnet`

Install as a .NET global tool:

```bash
dotnet tool install --global McpNet.Cli
```

Or run from source:

```bash
dotnet run --project src/McpNet.Cli -- <command>
```

### First-time setup

Save your gateway URL and admin token once - no need to pass flags on every command:

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
# - Servers ---------------------------------
mcpnet servers
mcpnet register --name myserver --server-url https://mcp.example.com/mcp
mcpnet register --name fs --transport Stdio --command npx \
    --arg -y --arg @modelcontextprotocol/server-filesystem --arg ~/docs \
    --env SOME_API_KEY=secret          # --arg and --env can be repeated
mcpnet deregister myserver
mcpnet refresh

# - Tools ----------------------------------
mcpnet tools [--server <name>]
mcpnet enable  myserver__search
mcpnet disable myserver__search

# - Groups ---------------------------------─
mcpnet groups
mcpnet group create --name search-tools [--description ".."]
mcpnet group delete search-tools
mcpnet group add-tool    search-tools myserver__search
mcpnet group remove-tool search-tools myserver__search

# - Clients ---------------------------------
mcpnet clients
mcpnet client myagent                              # show detail
mcpnet client create --name myagent
mcpnet client delete myagent
mcpnet client regenerate myagent                   # new bearer token
mcpnet client update myagent --rate-limit 120
mcpnet client update myagent --enabled false

# - Audit & meta ------------------------------─
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
| `mcpnet__retrieve_tools` | **BM25 semantic search** — returns top-N matching tool schemas ranked by relevance. Agents call this instead of loading all schemas to save context tokens. |

> Disabled by default. Enable only in trusted environments.

> `mcpnet__retrieve_tools` is the primary mechanism for token savings. Instead of listing every tool schema (expensive), the agent calls `retrieve_tools` with a natural-language description of the task and receives only the relevant schemas.

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

Plaintext values (from existing files before encryption was enabled) are **automatically detected** (no `enc:` prefix) and loaded as-is - no manual migration needed. The value is re-encrypted the next time that record is saved.

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
  "OtlpEndpoint": "http://localhost:4317",   // omit for console exporter
  "Prometheus": {
    "Enabled": false   // set true to expose /metrics for Prometheus scraping
  }
}
```

**Traces:**
- `mcp.tool.call` - span per tool call (source: `McpNet.Gateway`)
- Tags: `mcp.tool.name`, `mcp.server.name`
- ASP.NET Core + HttpClient instrumentation included

**Metrics:**
- `mcpnet.tool_calls` - counter, tagged by `tool` and `server`
- `mcpnet.tool_call.duration` - histogram (milliseconds)
- All ASP.NET Core + HttpClient metrics included

**Exporters:**

| Config | Exporter |
|---|---|
| `Prometheus:Enabled=true` | `/metrics` endpoint (Prometheus pull) |
| `OtlpEndpoint` set | OTLP gRPC push to collector |
| Neither | Console (useful for local debugging) |

Prometheus scraping endpoint: `GET /metrics` (no authentication required by default - place behind a firewall or reverse proxy).

---

## Session Management

The gateway maintains lightweight MCP sessions (`Mcp-Session-Id` header) with a 24-hour TTL. Sessions are stored in-memory and purged automatically every 30 minutes. Each session tracks created/last-seen timestamps for connection lifecycle management.

---

## Docker

```bash
# Default - JSON persistence, port 5050
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

### Import from Claude Desktop

If you already have servers configured in Claude Desktop (`claude_desktop_config.json`), you can import them directly:

```bash
POST /api/import/claude-desktop
Content-Type: application/json

{
  "mcpServers": {
    "filesystem": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-filesystem", "~/Documents"],
      "env": { "SOME_KEY": "value" }
    },
    "brave-search": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-brave-search"],
      "env": { "BRAVE_API_KEY": "..." }
    }
  }
}
```

Each `mcpServers` entry is registered as a Stdio upstream server. Servers whose names already exist are skipped (no duplicates). The response reports how many were added and skipped:

```jsonc
{ "serversAdded": 2, "serversSkipped": 0 }
```

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
| `ClientSecret` (OAuth) | **Encrypted at rest** - `enc:` prefix in `servers.json` | Back up `mcp-data/dp-keys/`; see [Secrets at Rest](#secrets-at-rest) |
| `BearerToken` (upstream servers) | **Encrypted at rest** - `enc:` prefix in `servers.json` | Same as above |
| `BearerToken` (clients) | **Encrypted at rest** in JSON persistence (`enc:` prefix); stored plaintext with the EF/SQL backends. Token is high-entropy random | Treat `clients.json` / the DB as secret; never commit it (see below) |
| Admin token | Env var `MCPGATEWAY_ADMIN_TOKEN` takes precedence over `appsettings.json`; compared in constant time | Always set this in production; never commit a real token to `appsettings.json` |
| Dev mode | **No authentication** on `/api`, `/mcp` or `/dashboard`; binds on all interfaces | Default is Dev. Never expose it to a network - registering a stdio server runs local commands. Use `Mode=Enterprise` for any shared/exposed deployment |
| Client bearer tokens | 32-byte cryptographically random (URL-safe Base64) | Rotate via `POST /api/clients/{id}/regenerate` or `mcpnet client regenerate <name>` |
| GET `/mcp` SSE | Authenticated in Enterprise mode (same Bearer check as POST) | Enforce Enterprise mode in production |
| TLS | Not handled by the gateway | Place behind a reverse proxy (nginx, Caddy, YARP) for TLS termination in production |
| Runtime data (`mcp-data/`) | Contains tokens, OAuth secrets, audit logs **and** the `dp-keys/` that decrypt them | **Never commit `mcp-data/` to source control.** It is git-ignored by default; if it was committed previously, purge it from history and rotate all tokens |
| **Tool Poisoning Attacks** | New servers are **quarantined by default** — tools are not exposed until an admin approves the server | Review each new server's description in the **Security** tab before approving. Never use `autoApprove: true` for untrusted third-party servers. |
| BM25 search index | In-memory, rebuilt from the live tool cache on every refresh | No external data exposure; tool names and descriptions are local data already held in the gateway |

---

## Built With

- [.NET 8](https://dotnet.microsoft.com/) - runtime and framework
- [ASP.NET Core](https://learn.microsoft.com/aspnet/core) - HTTP server and minimal APIs
- [System.Text.Json](https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/overview) - all serialisation (no Newtonsoft)
- [Entity Framework Core](https://learn.microsoft.com/ef/core/) - optional SQLite / PostgreSQL persistence
- [OpenTelemetry .NET](https://opentelemetry.io/docs/languages/net/) - observability (opt-in)
- [Model Context Protocol](https://modelcontextprotocol.io/) - AI tool-call protocol

---

*Built on .NET 8 · System.Text.Json only · Nullable enabled · No implicit usings*
