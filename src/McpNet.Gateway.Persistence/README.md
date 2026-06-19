# McpNet.Gateway.Persistence

Database persistence for the MCP Gateway. Provides EF Core implementations of the repository interfaces defined in `McpNet.Gateway`, with support for **SQLite** and **PostgreSQL**.

Use this when you need durable storage backed by a relational database instead of JSON files.

---

## Backends

| Backend | Package | Use case |
|---|---|---|
| SQLite | `Microsoft.EntityFrameworkCore.Sqlite` | Single-machine, file-based, zero config |
| PostgreSQL | `Npgsql.EntityFrameworkCore.PostgreSQL` | Multi-instance, production clusters |

---

## Registration

```csharp
// SQLite
builder.Services.AddMcpGatewayDbContext("Data Source=mcp-gateway.db");
builder.Services.AddMcpEfRepositories();

// PostgreSQL
builder.Services.AddMcpGatewayDbContext(connectionString, usePostgres: true);
builder.Services.AddMcpEfRepositories();
```

`AddMcpEfRepositories()` registers EF Core implementations for:
- `IServerRepository`
- `IToolGroupRepository`
- `IClientRepository`
- `IAuditLogRepository`

---

## Migrations

```bash
# Add a new migration
dotnet ef migrations add InitialCreate \
  --project src/McpNet.Gateway.Persistence \
  --startup-project src/McpNet.Host

# Apply migrations
dotnet ef database update \
  --project src/McpNet.Gateway.Persistence \
  --startup-project src/McpNet.Host
```

---

## Choosing Between JSON and EF

| | JSON files (`AddMcpJsonPersistence`) | EF Core (`AddMcpEfRepositories`) |
|---|---|---|
| Setup | Zero config | Connection string required |
| Dependencies | None | EF Core + driver |
| Concurrency | Single process | Multi-process / multi-instance |
| Migration | None | `dotnet ef` |
| Best for | Local dev, single server | Production, Docker Swarm, K8s |

The `McpNet.Host` selects the backend at startup via `McpGateway:Persistence` config key:

```json
{
  "McpGateway": {
    "Persistence": "json",
    "ConnectionString": ""
  }
}
```

Set `"Persistence": "sqlite"` or `"postgres"` to switch to EF Core.

---

## Dependency Graph

```
McpNet.Gateway.Persistence  (net8.0)
  └── McpNet.Gateway
  └── Microsoft.EntityFrameworkCore.Sqlite 8.0.11
  └── Npgsql.EntityFrameworkCore.PostgreSQL 8.0.11
```
