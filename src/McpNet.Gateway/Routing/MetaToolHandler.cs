using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using McpNet.Core.Capabilities;
using McpNet.Core.Serialization;
using McpNet.Gateway.Abstractions;
using McpNet.Gateway.Aggregation;
using McpNet.Gateway.Models;
using McpNet.Gateway.Registry;

namespace McpNet.Gateway.Routing
{
    /// <summary>
    /// Exposes gateway-management operations as built-in MCP tools (prefixed with
    /// <c>mcpnet__</c>) so an MCP-capable AI client can administer the gateway over the
    /// protocol itself. Disabled by default; enable via <see cref="MetaToolOptions"/>.
    /// </summary>
    public sealed class MetaToolHandler
    {
        public const string Prefix = "mcpnet__";

        private readonly ServerRegistry _registry;
        private readonly ToolAggregator _aggregator;
        private readonly IServerRepository _serverRepo;
        private readonly IToolGroupRepository? _groupRepo;
        private readonly ToolSearchMetrics? _searchMetrics;

        public MetaToolHandler(
            ServerRegistry registry,
            ToolAggregator aggregator,
            IServerRepository serverRepo,
            IToolGroupRepository? groupRepo = null,
            ToolSearchMetrics? searchMetrics = null)
        {
            _registry = registry;
            _aggregator = aggregator;
            _serverRepo = serverRepo;
            _groupRepo = groupRepo;
            _searchMetrics = searchMetrics;
        }

        public bool IsMetaTool(string name) => name.StartsWith(Prefix, StringComparison.Ordinal);

        public IReadOnlyList<McpTool> GetToolDefinitions()
        {
            var tools = new List<McpTool>
            {
                Tool("list_servers", "List all registered upstream MCP servers."),
                Tool("register_server", "Register a new upstream MCP server.",
                    Prop("name", "Unique server name", required: true),
                    Prop("url", "Upstream MCP endpoint URL (for http/sse transports)"),
                    Prop("transport", "Transport type: StreamableHttp, Sse, or Stdio (default StreamableHttp)"),
                    Prop("bearerToken", "Optional bearer token for the upstream server"),
                    Prop("command", "Executable command (for Stdio transport)")),
                Tool("deregister_server", "Remove a registered upstream server by name.",
                    Prop("name", "Name of the server to remove", required: true)),
                Tool("list_tools", "List all aggregated tools across upstream servers.",
                    Prop("server", "Optional server name filter")),
                Tool("enable_tool", "Enable an aggregated tool.",
                    Prop("name", "Full tool name (server__tool)", required: true)),
                Tool("disable_tool", "Disable an aggregated tool.",
                    Prop("name", "Full tool name (server__tool)", required: true)),
                Tool("refresh", "Refresh the aggregated tool cache from all upstream servers."),
                // BM25 semantic search - load only this tool and call it to find what you need.
                // Reduces token usage by ~99% compared to loading all tool schemas.
                Tool("retrieve_tools",
                    "Search for tools by natural language query using BM25 ranking. " +
                    "Returns the top matching tools with their full schemas. " +
                    "Use this instead of loading all tools to save token usage.",
                    Prop("query", "Natural language description of what you want to do", required: true),
                    Prop("top", "Number of tools to return (default 5, max 20)")),
            };

            if (_groupRepo != null)
            {
                tools.Add(Tool("create_group", "Create a tool group.",
                    Prop("name", "Group name", required: true),
                    Prop("description", "Optional group description")));
                tools.Add(Tool("list_groups", "List all tool groups."));
            }

            return tools;
        }

        public async Task<ToolCallResult> HandleAsync(string fullName, Dictionary<string, object?>? args, CancellationToken ct)
        {
            var op = fullName.Substring(Prefix.Length);
            args ??= new Dictionary<string, object?>();

            try
            {
                return op switch
                {
                    "list_servers"      => await ListServers(ct),
                    "register_server"   => await RegisterServer(args, ct),
                    "deregister_server" => await DeregisterServer(args, ct),
                    "list_tools"        => ListTools(args),
                    "enable_tool"       => ToggleTool(args, true),
                    "disable_tool"      => ToggleTool(args, false),
                    "refresh"           => await Refresh(ct),
                    "retrieve_tools"    => RetrieveTools(args),
                    "create_group"      => await CreateGroup(args, ct),
                    "list_groups"       => await ListGroups(ct),
                    _ => Error($"Unknown meta-tool '{fullName}'")
                };
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }
        }

        // ── operations ─────────────────────────────────────────────────────────
        private async Task<ToolCallResult> ListServers(CancellationToken ct)
        {
            var servers = await _serverRepo.GetAllAsync(ct).ConfigureAwait(false);
            var view = servers.Select(s => new
            {
                s.Name,
                Transport = s.TransportType.ToString(),
                s.Url,
                s.Enabled
            });
            return Json(view);
        }

        private async Task<ToolCallResult> RegisterServer(Dictionary<string, object?> args, CancellationToken ct)
        {
            var name = Str(args, "name");
            if (string.IsNullOrWhiteSpace(name)) return Error("'name' is required");

            var transport = Str(args, "transport") ?? "StreamableHttp";
            var server = new RegisteredServer
            {
                Name = name!,
                Url = Str(args, "url"),
                TransportType = Enum.TryParse<UpstreamTransportType>(transport, true, out var tt) ? tt : UpstreamTransportType.StreamableHttp,
                BearerToken = Str(args, "bearerToken"),
                StdioCommand = Str(args, "command")
            };
            var saved = await _registry.RegisterServerAsync(server, ct).ConfigureAwait(false);
            await _aggregator.RefreshAsync(ct).ConfigureAwait(false);
            return Text($"Registered server '{saved.Name}' (id {saved.Id}).");
        }

        private async Task<ToolCallResult> DeregisterServer(Dictionary<string, object?> args, CancellationToken ct)
        {
            var name = Str(args, "name");
            if (string.IsNullOrWhiteSpace(name)) return Error("'name' is required");

            var server = (await _serverRepo.GetAllAsync(ct).ConfigureAwait(false))
                .FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (server == null) return Error($"Server '{name}' not found");

            await _registry.DeleteServerAsync(server.Id, ct).ConfigureAwait(false);
            await _aggregator.RefreshAsync(ct).ConfigureAwait(false);
            return Text($"Deregistered server '{name}'.");
        }

        private ToolCallResult RetrieveTools(Dictionary<string, object?> args)
        {
            var query = Str(args, "query");
            if (string.IsNullOrWhiteSpace(query)) return Error("'query' is required");
            var topRaw = Str(args, "top");
            var topN = int.TryParse(topRaw, out var n) ? Math.Clamp(n, 1, 20) : 5;

            var results = _aggregator.SearchTools(query!, topN);

            // Record metrics
            _searchMetrics?.Record(_aggregator.GetEnabledTools().Count, results.Count);

            if (results.Count == 0)
                return Text("No tools matched your query. Try a different description or call mcpnet__list_tools to see all available tools.");

            var view = results.Select(r => new
            {
                r.Tool.FullName,
                r.Tool.ServerName,
                Description = r.Tool.Definition?.Description,
                InputSchema = r.Tool.Definition?.InputSchema,
                Score = Math.Round(r.Score, 3)
            });
            return Json(view);
        }

        private ToolCallResult ListTools(Dictionary<string, object?> args)
        {
            var filter = Str(args, "server");
            var tools = _aggregator.GetAllTools()
                .Where(t => filter == null || t.ServerName.Equals(filter, StringComparison.OrdinalIgnoreCase))
                .Select(t => new { t.FullName, t.ServerName, t.Enabled });
            return Json(tools);
        }

        private ToolCallResult ToggleTool(Dictionary<string, object?> args, bool enable)
        {
            var name = Str(args, "name");
            if (string.IsNullOrWhiteSpace(name)) return Error("'name' is required");
            _aggregator.SetToolEnabled(name!, enable);
            return Text($"{(enable ? "Enabled" : "Disabled")} tool '{name}'.");
        }

        private async Task<ToolCallResult> Refresh(CancellationToken ct)
        {
            await _aggregator.RefreshAsync(ct).ConfigureAwait(false);
            return Text($"Tool cache refreshed. {_aggregator.GetAllTools().Count} tools available.");
        }

        private async Task<ToolCallResult> CreateGroup(Dictionary<string, object?> args, CancellationToken ct)
        {
            if (_groupRepo == null) return Error("Tool groups are not available");
            var name = Str(args, "name");
            if (string.IsNullOrWhiteSpace(name)) return Error("'name' is required");
            var group = new ToolGroup { Name = name!, Description = Str(args, "description") };
            var saved = await _groupRepo.AddAsync(group, ct).ConfigureAwait(false);
            return Text($"Created group '{saved.Name}' (id {saved.Id}).");
        }

        private async Task<ToolCallResult> ListGroups(CancellationToken ct)
        {
            if (_groupRepo == null) return Error("Tool groups are not available");
            var groups = (await _groupRepo.GetAllAsync(ct).ConfigureAwait(false))
                .Select(g => new { g.Name, g.Description, ToolCount = g.ToolNames.Count });
            return Json(groups);
        }

        // ── helpers ──────────────────────────────────────────────────────────
        private static string? Str(Dictionary<string, object?> args, string key)
        {
            if (!args.TryGetValue(key, out var v) || v is null) return null;
            if (v is JsonElement je)
                return je.ValueKind == JsonValueKind.String ? je.GetString() : je.GetRawText();
            return v.ToString();
        }

        private static McpTool Tool(string op, string description, params (string Name, McpSchemaProperty Prop, bool Required)[] props)
        {
            var schema = new McpToolInputSchema();
            if (props.Length > 0)
            {
                schema.Properties = props.ToDictionary(p => p.Name, p => p.Prop);
                var required = props.Where(p => p.Required).Select(p => p.Name).ToList();
                if (required.Count > 0) schema.Required = required;
            }
            return new McpTool { Name = Prefix + op, Description = description, InputSchema = schema };
        }

        private static (string, McpSchemaProperty, bool) Prop(string name, string description, bool required = false)
            => (name, new McpSchemaProperty { Type = "string", Description = description }, required);

        private static ToolCallResult Text(string message)
            => new ToolCallResult { Content = new List<McpContent> { McpContent.FromText(message) } };

        private static ToolCallResult Json(object value)
            => Text(JsonSerializer.Serialize(value, McpJsonOptions.Default));

        private static ToolCallResult Error(string message)
            => new ToolCallResult { IsError = true, Content = new List<McpContent> { McpContent.FromText("Error: " + message) } };
    }

    /// <summary>Options controlling the built-in gateway meta-tools.</summary>
    public sealed class MetaToolOptions
    {
        /// <summary>When true, <c>mcpnet__*</c> management tools are advertised and callable over MCP. Default false.</summary>
        public bool Enabled { get; set; }
    }
}
