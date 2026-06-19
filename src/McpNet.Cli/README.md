# McpNet.Cli

Command-line interface for managing a running MCP Gateway instance.

Publishes as a **self-contained single file** (`mcpnet`) for Windows, Linux, and macOS.

---

## Installation

```bash
# Download and place on PATH
curl -Lo mcpnet https://github.com/your-org/mcpnet/releases/latest/download/mcpnet-linux-x64
chmod +x mcpnet
```

---

## Configuration

```bash
# Save gateway URL and admin token for all future commands
mcpnet configure --url http://localhost:5050 --token your-secret-token
```

Saves to `~/.mcpnet/config.json`. Can be overridden per-command with `--url` and `--token` flags, or via environment variables:

```bash
export MCPNET_URL=http://localhost:5050
export MCPNET_ADMIN_TOKEN=your-secret-token
```

---

## Commands

### Servers

```bash
mcpnet servers                          # list all registered servers
mcpnet register --name my-server \
  --url http://my-mcp-server:3000       # register HTTP server
mcpnet register --name local-tool \
  --stdio npx --args my-mcp-package     # register stdio server
mcpnet deregister <server-id>           # remove a server
```

### Tools

```bash
mcpnet tools                            # list all aggregated tools
mcpnet tools --server <id>              # filter by server
mcpnet refresh                          # trigger tool refresh
mcpnet enable <tool-name>               # enable a tool
mcpnet disable <tool-name>              # disable a tool
```

### Groups

```bash
mcpnet groups                           # list tool groups
mcpnet group create --name "dev-tools"  # create a group
mcpnet group add <group-id> <tool-name> # add tool to group
mcpnet group remove <group-id> <tool>   # remove tool from group
mcpnet group delete <group-id>          # delete group
```

### Clients (Enterprise)

```bash
mcpnet clients                          # list clients
mcpnet client create --name my-app      # create client (prints bearer token)
mcpnet client regenerate <client-id>    # regenerate bearer token
mcpnet client delete <client-id>        # delete client
```

### Audit & Info

```bash
mcpnet audit                            # show last 200 audit log entries
mcpnet whoami                           # show current gateway URL and auth status
mcpnet version                          # print CLI version
```

---

## Global Flags

| Flag | Description |
|---|---|
| `--url <url>` | Gateway base URL (overrides config and env var) |
| `--token <token>` | Admin token (overrides config and env var) |
| `--help` / `-h` | Show help |

---

## Publishing

```bash
# Windows x64
dotnet publish src/McpNet.Cli -c Release -r win-x64

# Linux x64
dotnet publish src/McpNet.Cli -c Release -r linux-x64

# macOS ARM (Apple Silicon)
dotnet publish src/McpNet.Cli -c Release -r osx-arm64
```

Output: `bin/Release/net8.0/{rid}/publish/mcpnet[.exe]`

---

## Dependency Graph

```
McpNet.Cli  (net8.0, Exe)
  └── McpNet.Core
```

No gateway libraries - communicates with the gateway purely over HTTP (`/api/*`).
