using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using McpNet.Core.Capabilities;
using McpNet.Core.Protocol;
using McpNet.Core.Serialization;
using McpNet.Gateway.Abstractions;
using McpNet.Gateway.Aggregation;
using McpNet.Gateway.Auth;
using McpNet.Gateway.Groups;
using McpNet.Gateway.Models;
using McpNet.Gateway.Registry;
using McpNet.Gateway.Sessions;

namespace McpNet.Gateway.Routing
{
    public class GatewayRequestRouter
    {
        private readonly ToolAggregator _aggregator;
        private readonly ServerRegistry _registry;
        private readonly IServerRepository _serverRepo;
        private readonly IClientRepository? _clientRepo;
        private readonly ToolGroupManager _groupManager;
        private readonly IAuditLogRepository? _auditLog;
        private readonly GatewaySessionManager _sessions;
        private readonly MetaToolHandler? _metaTools;

        public GatewayRequestRouter(
            ToolAggregator aggregator,
            ServerRegistry registry,
            IServerRepository serverRepo,
            ToolGroupManager groupManager,
            GatewaySessionManager sessions,
            IClientRepository? clientRepo = null,
            IAuditLogRepository? auditLog = null,
            MetaToolHandler? metaTools = null)
        {
            _aggregator = aggregator;
            _registry = registry;
            _serverRepo = serverRepo;
            _groupManager = groupManager;
            _sessions = sessions;
            _clientRepo = clientRepo;
            _auditLog = auditLog;
            _metaTools = metaTools;
        }

        public async Task<JsonRpcResponse> HandleAsync(JsonRpcRequest request, string? sessionId, McpClient? client, CancellationToken ct = default)
        {
            try
            {
                return request.Method switch
                {
                    McpMethods.Initialize => await HandleInitializeAsync(request, sessionId, ct),
                    McpMethods.Ping => new JsonRpcResponse { Id = request.Id, Result = new { } },
                    McpMethods.ToolsList => await HandleToolsListAsync(request, client, ct),
                    McpMethods.ToolsCall => await HandleToolsCallAsync(request, client, ct),
                    McpMethods.PromptsList => await HandlePromptsListAsync(request, client, ct),
                    McpMethods.PromptsGet => await HandlePromptsGetAsync(request, ct),
                    McpMethods.ResourcesList => await HandleResourcesListAsync(request, client, ct),
                    McpMethods.ResourcesRead => await HandleResourcesReadAsync(request, ct),
                    _ => MethodNotFound(request.Id, request.Method)
                };
            }
            catch (Exception ex)
            {
                return new JsonRpcResponse
                {
                    Id = request.Id,
                    Error = new JsonRpcError { Code = JsonRpcErrorCodes.InternalError, Message = ex.Message }
                };
            }
        }

        private Task<JsonRpcResponse> HandleInitializeAsync(JsonRpcRequest request, string? sessionId, CancellationToken ct)
        {
            var newSession = _sessions.CreateSession();
            var result = new InitializeResult
            {
                ServerInfo = new McpImplementation { Name = "McpNet.Gateway", Version = "1.0.0" },
                Capabilities = new ServerCapabilities
                {
                    Tools = new ToolsCapability { ListChanged = true },
                    Prompts = new PromptsCapability { ListChanged = true },
                    Resources = new ResourcesCapability { ListChanged = true }
                }
            };
            var response = new JsonRpcResponse { Id = request.Id, Result = result };
            response.SessionId = newSession;
            return Task.FromResult(response);
        }

        private async Task<JsonRpcResponse> HandleToolsListAsync(JsonRpcRequest request, McpClient? client, CancellationToken ct)
        {
            var tools = _aggregator.GetEnabledTools();

            if (client != null && client.AllowedGroupIds.Count > 0)
            {
                var allowed = await _groupManager.GetToolsForClientAsync(client, ct).ConfigureAwait(false);
                if (allowed != null)
                    tools = tools.Where(t => allowed.Contains(t.FullName)).ToList();
            }

            if (client != null && client.AllowedServerIds.Count > 0)
                tools = tools.Where(t => client.AllowedServerIds.Contains(t.ServerId)).ToList();

            var definitions = tools.Select(t => t.Definition).ToList();
            if (_metaTools != null)
                definitions.AddRange(_metaTools.GetToolDefinitions());

            return new JsonRpcResponse
            {
                Id = request.Id,
                Result = new { tools = definitions }
            };
        }

        private async Task<JsonRpcResponse> HandleToolsCallAsync(JsonRpcRequest request, McpClient? client, CancellationToken ct)
        {
            var p = McpJsonOptions.Convert<ToolCallParams>(request.Params)
                ?? throw new InvalidOperationException("Invalid tool call params");

            if (_metaTools != null && _metaTools.IsMetaTool(p.Name))
            {
                var metaResult = await _metaTools.HandleAsync(p.Name, p.Arguments, ct).ConfigureAwait(false);
                return new JsonRpcResponse { Id = request.Id, Result = metaResult };
            }

            var tool = _aggregator.GetTool(p.Name)
                ?? throw new KeyNotFoundException($"Tool '{p.Name}' not found");

            if (!tool.Enabled)
                return new JsonRpcResponse { Id = request.Id, Error = new JsonRpcError { Code = -32601, Message = $"Tool '{p.Name}' is disabled" } };

            var server = (await _serverRepo.GetAllAsync(ct).ConfigureAwait(false))
                .FirstOrDefault(s => s.Id == tool.ServerId)
                ?? throw new InvalidOperationException($"Server for tool '{p.Name}' not found");

            // Rate limiting: per-server override takes precedence over global limit.
            if (client != null && _auditLog != null)
            {
                var serverOverride = client.ServerRateLimits.FirstOrDefault(r => r.ServerId == server.Id);
                int effectiveLimit = serverOverride != null ? serverOverride.LimitPerMinute : client.RateLimitPerMinute;
                if (effectiveLimit > 0)
                {
                    var recent = await _auditLog.GetRecentAsync(effectiveLimit + 1, ct).ConfigureAwait(false);
                    var callsLastMinute = recent.Count(a =>
                        a.ClientId == client.Id.ToString() &&
                        a.ServerName == server.Name &&
                        a.Timestamp >= DateTime.UtcNow.AddMinutes(-1));
                    // For global limit: count all servers, not just this one.
                    if (serverOverride == null)
                        callsLastMinute = recent.Count(a =>
                            a.ClientId == client.Id.ToString() &&
                            a.Timestamp >= DateTime.UtcNow.AddMinutes(-1));
                    if (callsLastMinute >= effectiveLimit)
                        return new JsonRpcResponse
                        {
                            Id = request.Id,
                            Error = new JsonRpcError
                            {
                                Code = -32029,
                                Message = serverOverride != null
                                    ? $"Rate limit exceeded for server '{server.Name}' ({effectiveLimit} calls/min)."
                                    : $"Rate limit exceeded ({effectiveLimit} calls/min)."
                            }
                        };
                }
            }

            var upstreamClient = _registry.GetOrCreateClient(server);
            if (!upstreamClient.IsConnected)
                await upstreamClient.ConnectAsync(ct).ConfigureAwait(false);

            using var activity = Observability.McpTelemetry.ActivitySource.StartActivity("mcp.tool.call", System.Diagnostics.ActivityKind.Client);
            activity?.SetTag("mcp.tool.name", p.Name);
            activity?.SetTag("mcp.server.name", server.Name);

            var sw = Stopwatch.StartNew();
            bool success = false;
            string? errorMsg = null;
            ToolCallResult callResult;
            try
            {
                callResult = await upstreamClient.CallToolAsync(tool.LocalName, p.Arguments, ct).ConfigureAwait(false);
                success = !callResult.IsError;
                if (callResult.IsError)
                    errorMsg = callResult.Content?.FirstOrDefault()?.Text;
            }
            catch (Exception ex)
            {
                sw.Stop();
                errorMsg = ex.Message;
                if (_auditLog != null)
                    await _auditLog.AddAsync(new AuditLog
                    {
                        ClientId = client?.Id.ToString(),
                        ClientName = client?.Name,
                        Method = McpMethods.ToolsCall,
                        ToolName = p.Name,
                        ServerName = server.Name,
                        Success = false,
                        ErrorMessage = errorMsg,
                        DurationMs = sw.ElapsedMilliseconds
                    }, ct).ConfigureAwait(false);
                throw;
            }
            finally
            {
                sw.Stop();
                var tags = new System.Diagnostics.TagList
                {
                    { "tool", p.Name },
                    { "server", server.Name }
                };
                Observability.McpTelemetry.ToolCalls.Add(1, tags);
                Observability.McpTelemetry.ToolCallDuration.Record(sw.Elapsed.TotalMilliseconds, tags);
            }

            if (_auditLog != null)
                await _auditLog.AddAsync(new AuditLog
                {
                    ClientId = client?.Id.ToString(),
                    ClientName = client?.Name,
                    Method = McpMethods.ToolsCall,
                    ToolName = p.Name,
                    ServerName = server.Name,
                    Success = success,
                    ErrorMessage = errorMsg,
                    DurationMs = sw.ElapsedMilliseconds
                }, ct).ConfigureAwait(false);

            return new JsonRpcResponse { Id = request.Id, Result = callResult };
        }

        private async Task<JsonRpcResponse> HandlePromptsListAsync(JsonRpcRequest request, McpClient? client, CancellationToken ct)
        {
            var servers = await _serverRepo.GetAllAsync(ct).ConfigureAwait(false);
            var allPrompts = new List<McpPrompt>();
            foreach (var server in servers.Where(s => s.Enabled))
            {
                try
                {
                    var upClient = _registry.GetOrCreateClient(server);
                    if (!upClient.IsConnected) await upClient.ConnectAsync(ct).ConfigureAwait(false);
                    var prompts = await upClient.ListPromptsAsync(ct).ConfigureAwait(false);
                    foreach (var p in prompts)
                    {
                        p.Name = $"{server.Name}__{p.Name}";
                        allPrompts.Add(p);
                    }
                }
                catch { }
            }
            return new JsonRpcResponse { Id = request.Id, Result = new { prompts = allPrompts } };
        }

        private async Task<JsonRpcResponse> HandlePromptsGetAsync(JsonRpcRequest request, CancellationToken ct)
        {
            var p = McpJsonOptions.Convert<PromptGetParams>(request.Params)!;
            var parts = p.Name.Split(new[] { "__" }, 2, StringSplitOptions.None);
            if (parts.Length != 2)
                return MethodNotFound(request.Id, p.Name);

            var server = (await _serverRepo.GetAllAsync(ct).ConfigureAwait(false)).FirstOrDefault(s => s.Name == parts[0]);
            if (server == null) return MethodNotFound(request.Id, p.Name);

            var upClient = _registry.GetOrCreateClient(server);
            if (!upClient.IsConnected) await upClient.ConnectAsync(ct).ConfigureAwait(false);
            var result = await upClient.GetPromptAsync(parts[1], p.Arguments, ct).ConfigureAwait(false);
            return new JsonRpcResponse { Id = request.Id, Result = result };
        }

        private async Task<JsonRpcResponse> HandleResourcesListAsync(JsonRpcRequest request, McpClient? client, CancellationToken ct)
        {
            var servers = await _serverRepo.GetAllAsync(ct).ConfigureAwait(false);
            var allResources = new List<McpResource>();
            foreach (var server in servers.Where(s => s.Enabled))
            {
                try
                {
                    var upClient = _registry.GetOrCreateClient(server);
                    if (!upClient.IsConnected) await upClient.ConnectAsync(ct).ConfigureAwait(false);
                    var resources = await upClient.ListResourcesAsync(ct).ConfigureAwait(false);
                    allResources.AddRange(resources);
                }
                catch { }
            }
            return new JsonRpcResponse { Id = request.Id, Result = new { resources = allResources } };
        }

        private async Task<JsonRpcResponse> HandleResourcesReadAsync(JsonRpcRequest request, CancellationToken ct)
        {
            var p = McpJsonOptions.Convert<ResourceReadParams>(request.Params)!;
            var servers = await _serverRepo.GetAllAsync(ct).ConfigureAwait(false);
            foreach (var server in servers.Where(s => s.Enabled))
            {
                try
                {
                    var upClient = _registry.GetOrCreateClient(server);
                    if (!upClient.IsConnected) await upClient.ConnectAsync(ct).ConfigureAwait(false);
                    var result = await upClient.ReadResourceAsync(p.Uri, ct).ConfigureAwait(false);
                    if (result.Contents.Count > 0)
                        return new JsonRpcResponse { Id = request.Id, Result = result };
                }
                catch { }
            }
            return MethodNotFound(request.Id, p.Uri);
        }

        private static JsonRpcResponse MethodNotFound(object? id, string method) =>
            new JsonRpcResponse
            {
                Id = id,
                Error = new JsonRpcError { Code = JsonRpcErrorCodes.MethodNotFound, Message = $"Method '{method}' not found" }
            };
    }
}
