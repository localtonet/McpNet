using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using McpNet.Core.Protocol;
using McpNet.Core.Serialization;
using McpNet.Gateway.Abstractions;
using McpNet.Gateway.Aggregation;
using McpNet.Gateway.Auth;
using McpNet.Gateway.Dashboard;
using McpNet.Gateway.Models;
using McpNet.Gateway.Registry;

namespace McpNet.Gateway.Standalone.Management
{
    internal sealed class ManagementHandler
    {
        private readonly IServiceProvider _sp;

        public ManagementHandler(IServiceProvider sp) => _sp = sp;

        // ── Entry point ───────────────────────────────────────────────────────

        public async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            var method = ctx.Request.HttpMethod.ToUpperInvariant();
            var path   = ctx.Request.Url?.AbsolutePath?.Trim('/') ?? "";
            var segs   = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

            // GET /api/info - no auth
            if (method == "GET" && segs.Length == 2 && Seg(segs, 1) == "info")
            { await HandleInfoAsync(ctx, ct); return; }

            // All other /api/* - require admin auth
            var auth = _sp.GetRequiredService<GatewayAuthenticator>();
            if (!auth.IsDevMode)
            {
                if (!auth.ValidateAdminToken(ctx.Request.Headers[McpHeaders.AdminToken]))
                { await WriteErrorAsync(ctx, 401, "Unauthorized", ct); return; }
            }

            try
            {
                switch (Seg(segs, 1))
                {
                    case "servers":  await HandleServersAsync(ctx, segs, method, ct);  return;
                    case "groups":   await HandleGroupsAsync(ctx, segs, method, ct);   return;
                    case "clients":  await HandleClientsAsync(ctx, segs, method, ct);  return;
                    case "audit":    await HandleAuditAsync(ctx, ct);                  return;
                    case "tools":    await HandleToolsAsync(ctx, segs, method, ct);    return;
                    case "export":   await HandleExportAsync(ctx, ct);                 return;
                    case "import":   await HandleImportAsync(ctx, ct);                 return;
                    case "catalog":  await HandleCatalogAsync(ctx, segs, method, ct);  return;
                }
                await WriteErrorAsync(ctx, 404, "Not found", ct);
            }
            catch (Exception ex) { await WriteErrorAsync(ctx, 500, ex.Message, ct); }
        }

        // ── /api/info ─────────────────────────────────────────────────────────

        private async Task HandleInfoAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            var auth = _sp.GetRequiredService<GatewayAuthenticator>();
            await WriteJsonAsync(ctx, 200, new { mode = auth.IsDevMode ? "Dev" : "Enterprise", requiresAuth = !auth.IsDevMode }, ct);
        }

        // ── /api/servers ──────────────────────────────────────────────────────

        private async Task HandleServersAsync(HttpListenerContext ctx, string[] segs, string method, CancellationToken ct)
        {
            var repo       = _sp.GetRequiredService<IServerRepository>();
            var registry   = _sp.GetRequiredService<ServerRegistry>();
            var aggregator = _sp.GetRequiredService<ToolAggregator>();

            if (segs.Length == 2)
            {
                if (method == "GET")
                { await WriteJsonAsync(ctx, 200, (await repo.GetAllAsync(ct)).Select(ServerDto), ct); return; }
                if (method == "POST")
                {
                    var body = await ReadJsonAsync<RegisterServerRequest>(ctx, ct);
                    if (body is null) { await WriteErrorAsync(ctx, 400, "Invalid body", ct); return; }
                    var saved = await registry.RegisterServerAsync(BuildServer(body), ct);
                    FireAndForget(() => aggregator.RefreshAsync());
                    await WriteJsonAsync(ctx, 201, ServerDto(saved), ct);
                    return;
                }
            }

            // GET /api/servers/quarantine - list quarantined servers
            if (segs.Length == 3 && Seg(segs, 2) == "quarantine" && method == "GET")
            {
                var all = await repo.GetAllAsync(ct);
                await WriteJsonAsync(ctx, 200, all.Where(s => s.Quarantined).Select(ServerDto), ct); return;
            }

            if (!TryGuid(segs, 2, out var id)) { await WriteErrorAsync(ctx, 404, "Not found", ct); return; }
            var s3 = Seg(segs, 3); var s4 = Seg(segs, 4); var s5 = Seg(segs, 5);

            if (segs.Length == 3)
            {
                if (method == "GET")
                {
                    var s = await repo.GetByIdAsync(id, ct);
                    if (s is null) { await WriteErrorAsync(ctx, 404, "Not found", ct); return; }
                    await WriteJsonAsync(ctx, 200, ServerDetailDto(s), ct); return;
                }
                if (method == "PUT")
                {
                    var existing = await repo.GetByIdAsync(id, ct);
                    if (existing is null) { await WriteErrorAsync(ctx, 404, "Not found", ct); return; }
                    var body = await ReadJsonAsync<RegisterServerRequest>(ctx, ct);
                    if (body is null) { await WriteErrorAsync(ctx, 400, "Invalid body", ct); return; }
                    ApplyServerUpdate(existing, body);
                    var updated = await registry.UpdateServerAsync(existing, ct);
                    FireAndForget(() => aggregator.RefreshAsync());
                    await WriteJsonAsync(ctx, 200, ServerDto(updated), ct); return;
                }
                if (method == "DELETE")
                {
                    await registry.DeleteServerAsync(id, ct);
                    FireAndForget(() => aggregator.RefreshAsync());
                    ctx.Response.StatusCode = 204; ctx.Response.Close(); return;
                }
            }
            if (segs.Length == 4 && s3 == "toggle" && method == "PATCH")
            {
                var s = await repo.GetByIdAsync(id, ct);
                if (s is null) { await WriteErrorAsync(ctx, 404, "Not found", ct); return; }
                if (bool.TryParse(ctx.Request.QueryString["enabled"], out var en)) s.Enabled = en;
                await repo.UpdateAsync(s, ct);
                await WriteJsonAsync(ctx, 200, new { id, s.Enabled }, ct); return;
            }
            if (segs.Length == 4 && s3 == "tools" && method == "GET")
            {
                await WriteJsonAsync(ctx, 200, aggregator.GetAllTools().Where(t => t.ServerId == id)
                    .Select(t => new { t.FullName, t.LocalName, t.Enabled }), ct); return;
            }
            if (segs.Length == 6 && s3 == "tools" && s5 == "toggle" && method == "PATCH")
            {
                if (bool.TryParse(ctx.Request.QueryString["enabled"], out var en))
                    aggregator.SetToolEnabled(s4, en);
                await WriteJsonAsync(ctx, 200, new { toolName = s4, enabled = en }, ct); return;
            }
            if (segs.Length == 4 && s3 == "health"      && method == "GET") { await HandleServerHealthAsync(ctx, id, repo, registry, ct); return; }
            if (segs.Length == 4 && s3 == "stdio-probe" && method == "GET") { await HandleStdioProbeAsync(ctx, id, repo, ct); return; }
            if (segs.Length == 4 && s3 == "approve" && method == "POST")
            {
                var s = await repo.GetByIdAsync(id, ct);
                if (s is null) { await WriteErrorAsync(ctx, 404, "Not found", ct); return; }
                s.Quarantined = false;
                await registry.UpdateServerAsync(s, ct);
                FireAndForget(() => aggregator.RefreshAsync());
                await WriteJsonAsync(ctx, 200, ServerDto(s), ct); return;
            }
            if (segs.Length == 4 && s3 == "quarantine" && method == "POST")
            {
                var s = await repo.GetByIdAsync(id, ct);
                if (s is null) { await WriteErrorAsync(ctx, 404, "Not found", ct); return; }
                s.Quarantined = true;
                await registry.UpdateServerAsync(s, ct);
                FireAndForget(() => aggregator.RefreshAsync());
                await WriteJsonAsync(ctx, 200, ServerDto(s), ct); return;
            }

            await WriteErrorAsync(ctx, 404, "Not found", ct);
        }

        // ── /api/groups ───────────────────────────────────────────────────────

        private async Task HandleGroupsAsync(HttpListenerContext ctx, string[] segs, string method, CancellationToken ct)
        {
            var repo = _sp.GetRequiredService<IToolGroupRepository>();
            if (segs.Length == 2)
            {
                if (method == "GET")  { await WriteJsonAsync(ctx, 200, await repo.GetAllAsync(ct), ct); return; }
                if (method == "POST")
                {
                    var body = await ReadJsonAsync<CreateGroupRequest>(ctx, ct);
                    if (body is null) { await WriteErrorAsync(ctx, 400, "Invalid body", ct); return; }
                    await WriteJsonAsync(ctx, 201, await repo.AddAsync(new ToolGroup { Name = body.Name, Description = body.Description }, ct), ct);
                    return;
                }
            }
            if (!TryGuid(segs, 2, out var id)) { await WriteErrorAsync(ctx, 404, "Not found", ct); return; }
            var s3 = Seg(segs, 3); var s4 = Seg(segs, 4);
            if (segs.Length == 3 && method == "DELETE") { await repo.DeleteAsync(id, ct); ctx.Response.StatusCode = 204; ctx.Response.Close(); return; }
            if (segs.Length == 4 && s3 == "tools" && method == "POST")
            {
                var body = await ReadJsonAsync<ToolNameRequest>(ctx, ct);
                if (body is null) { await WriteErrorAsync(ctx, 400, "Invalid body", ct); return; }
                var g = await repo.GetByIdAsync(id, ct);
                if (g is null) { await WriteErrorAsync(ctx, 404, "Not found", ct); return; }
                if (!g.ToolNames.Contains(body.ToolName)) g.ToolNames.Add(body.ToolName);
                await WriteJsonAsync(ctx, 200, await repo.UpdateAsync(g, ct), ct); return;
            }
            if (segs.Length == 5 && s3 == "tools" && method == "DELETE")
            {
                var g = await repo.GetByIdAsync(id, ct);
                if (g is null) { await WriteErrorAsync(ctx, 404, "Not found", ct); return; }
                g.ToolNames.Remove(s4);
                await WriteJsonAsync(ctx, 200, await repo.UpdateAsync(g, ct), ct); return;
            }
            await WriteErrorAsync(ctx, 404, "Not found", ct);
        }

        // ── /api/clients ──────────────────────────────────────────────────────

        private async Task HandleClientsAsync(HttpListenerContext ctx, string[] segs, string method, CancellationToken ct)
        {
            var repo = _sp.GetService<IClientRepository>();
            if (segs.Length == 2)
            {
                if (method == "GET")
                {
                    if (repo is null) { await WriteJsonAsync(ctx, 200, Array.Empty<object>(), ct); return; }
                    await WriteJsonAsync(ctx, 200, (await repo.GetAllAsync(ct)).Select(ClientDto), ct); return;
                }
                if (method == "POST")
                {
                    if (repo is null) { await WriteErrorAsync(ctx, 501, "Not implemented", ct); return; }
                    var body = await ReadJsonAsync<CreateClientRequest>(ctx, ct);
                    if (body is null) { await WriteErrorAsync(ctx, 400, "Invalid body", ct); return; }
                    var token  = GenerateToken();
                    var saved  = await repo.AddAsync(new McpClient { Name = body.Name, BearerToken = token }, ct);
                    await WriteJsonAsync(ctx, 201, new { saved.Id, saved.Name, BearerToken = token }, ct); return;
                }
            }
            if (!TryGuid(segs, 2, out var id)) { await WriteErrorAsync(ctx, 404, "Not found", ct); return; }
            var s3 = Seg(segs, 3);
            if (repo is null) { await WriteErrorAsync(ctx, 501, "Not implemented", ct); return; }
            if (segs.Length == 3)
            {
                if (method == "GET")  { var c = await repo.GetByIdAsync(id, ct); if (c is null) { await WriteErrorAsync(ctx, 404, "Not found", ct); return; } await WriteJsonAsync(ctx, 200, ClientDetailDto(c), ct); return; }
                if (method == "PUT")
                {
                    var c = await repo.GetByIdAsync(id, ct);
                    if (c is null) { await WriteErrorAsync(ctx, 404, "Not found", ct); return; }
                    var body = await ReadJsonAsync<UpdateClientRequest>(ctx, ct);
                    if (body is null) { await WriteErrorAsync(ctx, 400, "Invalid body", ct); return; }
                    c.Enabled = body.Enabled; c.AllowedServerIds = body.AllowedServerIds ?? c.AllowedServerIds;
                    c.AllowedGroupIds = body.AllowedGroupIds ?? c.AllowedGroupIds;
                    c.RateLimitPerMinute = body.RateLimitPerMinute; c.ServerRateLimits = body.ServerRateLimits ?? c.ServerRateLimits;
                    await WriteJsonAsync(ctx, 200, ClientDetailDto(await repo.UpdateAsync(c, ct)), ct); return;
                }
                if (method == "DELETE") { await repo.DeleteAsync(id, ct); ctx.Response.StatusCode = 204; ctx.Response.Close(); return; }
            }
            if (segs.Length == 4 && s3 == "regenerate" && method == "POST")
            {
                var c = await repo.GetByIdAsync(id, ct);
                if (c is null) { await WriteErrorAsync(ctx, 404, "Not found", ct); return; }
                c.BearerToken = GenerateToken();
                await repo.UpdateAsync(c, ct);
                await WriteJsonAsync(ctx, 200, new { c.Id, c.Name, BearerToken = c.BearerToken }, ct); return;
            }
            await WriteErrorAsync(ctx, 404, "Not found", ct);
        }

        // ── /api/audit ────────────────────────────────────────────────────────

        private async Task HandleAuditAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            var repo = _sp.GetService<IAuditLogRepository>();
            await WriteJsonAsync(ctx, 200, repo is null ? (object)Array.Empty<object>() : await repo.GetRecentAsync(200, ct), ct);
        }

        // ── /api/tools ────────────────────────────────────────────────────────

        private async Task HandleToolsAsync(HttpListenerContext ctx, string[] segs, string method, CancellationToken ct)
        {
            var agg = _sp.GetRequiredService<ToolAggregator>();
            var s2  = Seg(segs, 2);

            if (segs.Length == 2 && method == "GET")
            { await WriteJsonAsync(ctx, 200, agg.GetAllTools().Select(t => new { t.FullName, t.LocalName, t.ServerName, t.Enabled }), ct); return; }

            if (s2 == "refresh" && method == "POST")
            {
                var audit = _sp.GetService<IAuditLogRepository>();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await agg.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
                        if (audit != null)
                            foreach (var f in agg.GetLastRefreshDiagnostics().Where(d => !string.Equals(d.Status, "ok", StringComparison.OrdinalIgnoreCase)))
                                await audit.AddAsync(new AuditLog { ClientId = "dashboard", ClientName = "dashboard", Method = "tools.refresh",
                                    ServerName = f.ServerName, Success = false, ErrorMessage = f.ErrorMessage, DurationMs = f.DurationMs, Timestamp = DateTime.UtcNow }, CancellationToken.None);
                    }
                    catch { }
                });
                await WriteJsonAsync(ctx, 202, new { message = "Refresh started. Poll /api/tools/diagnostics for results." }, ct); return;
            }
            if (s2 == "status" && method == "GET")
            {
                await WriteJsonAsync(ctx, 200, new
                {
                    refreshing = agg.IsRefreshing,
                    lastRefreshedAt = agg.LastRefreshedAt == DateTime.MinValue ? (DateTime?)null : agg.LastRefreshedAt,
                    totalTools = agg.GetAllTools().Count,
                    diagnostics = agg.GetLastRefreshDiagnostics().Select(d => new { d.ServerId, d.ServerName, d.TransportType, d.Status, d.Success, d.ToolCount, d.DurationMs, d.ErrorMessage, d.Timestamp })
                }, ct); return;
            }
            if (s2 == "diagnostics" && method == "GET")
            { await WriteJsonAsync(ctx, 200, agg.GetLastRefreshDiagnostics().Select(d => new { d.ServerId, d.ServerName, d.TransportType, d.Status, d.Success, d.ToolCount, d.DurationMs, d.ErrorMessage, d.Timestamp }), ct); return; }

            if (s2 == "version" && method == "GET")
            {
                await WriteJsonAsync(ctx, 200, new
                {
                    version = agg.ToolsVersion,
                    lastRefreshedAt = agg.LastRefreshedAt == DateTime.MinValue ? (DateTime?)null : agg.LastRefreshedAt
                }, ct); return;
            }
            if (s2 == "search-metrics" && method == "GET")
            {
                var m = _sp.GetService<McpNet.Gateway.Aggregation.ToolSearchMetrics>();
                if (m is null) { await WriteJsonAsync(ctx, 200, new { enabled = false }, ct); return; }
                await WriteJsonAsync(ctx, 200, new
                {
                    enabled = true,
                    totalCalls = m.TotalCalls,
                    totalResultsReturned = m.TotalResultsReturned,
                    estimatedTokensSaved = m.EstimatedTokensSaved,
                    averageResultsPerCall = Math.Round(m.AverageResultsPerCall, 1),
                    averageToolsAtCall = Math.Round(m.AverageToolsAtCall, 1),
                    tokensPerSchema = McpNet.Gateway.Aggregation.ToolSearchMetrics.TokensPerSchema,
                    firstCallAt = m.FirstCallAt == DateTime.MinValue ? (DateTime?)null : m.FirstCallAt,
                    lastCallAt  = m.LastCallAt  == DateTime.MinValue ? (DateTime?)null : m.LastCallAt
                }, ct); return;
            }
            if (s2 == "search" && method == "GET")
            {
                var q = ctx.Request.QueryString["q"] ?? "";
                if (string.IsNullOrWhiteSpace(q)) { await WriteErrorAsync(ctx, 400, "q is required", ct); return; }
                var results = agg.SearchTools(q.Trim(), 10);
                await WriteJsonAsync(ctx, 200, new
                {
                    query = q,
                    totalTools = agg.GetEnabledTools().Count,
                    results = results.Select(r => new
                    {
                        r.Tool.FullName,
                        r.Tool.ServerName,
                        Description = r.Tool.Definition?.Description,
                        Score = Math.Round(r.Score, 3)
                    })
                }, ct); return;
            }

            if (s2 == "call" && method == "POST")
            {
                var body = await ReadJsonAsync<CallToolRequest>(ctx, ct);
                if (body is null || string.IsNullOrWhiteSpace(body.FullName)) { await WriteErrorAsync(ctx, 400, "fullName required", ct); return; }
                var tool = agg.GetTool(body.FullName);
                if (tool is null) { await WriteJsonAsync(ctx, 404, new { error = "Tool not found" }, ct); return; }
                var repo     = _sp.GetRequiredService<IServerRepository>();
                var registry = _sp.GetRequiredService<ServerRegistry>();
                var server   = await repo.GetByIdAsync(tool.ServerId, ct);
                if (server is null) { await WriteJsonAsync(ctx, 404, new { error = "Server not found" }, ct); return; }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var client = registry.GetOrCreateClient(server);
                    if (!client.IsConnected) await client.ConnectAsync(ct);
                    var result = await client.CallToolAsync(tool.LocalName, body.Arguments, ct);
                    sw.Stop();
                    await WriteJsonAsync(ctx, 200, new { success = !result.IsError, isError = result.IsError, durationMs = sw.ElapsedMilliseconds, content = result.Content }, ct);
                }
                catch (Exception ex) { sw.Stop(); await WriteJsonAsync(ctx, 200, new { success = false, isError = true, durationMs = sw.ElapsedMilliseconds, error = ex.Message }, ct); }
                return;
            }
            await WriteErrorAsync(ctx, 404, "Not found", ct);
        }

        // ── /api/export ───────────────────────────────────────────────────────

        private async Task HandleExportAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            var sRepo = _sp.GetRequiredService<IServerRepository>();
            var gRepo = _sp.GetRequiredService<IToolGroupRepository>();
            var cRepo = _sp.GetService<IClientRepository>();
            await WriteJsonAsync(ctx, 200, new
            {
                exportedAt = DateTime.UtcNow,
                servers    = await sRepo.GetAllAsync(ct),
                groups     = await gRepo.GetAllAsync(ct),
                clients    = cRepo is null ? new List<McpClient>() : (await cRepo.GetAllAsync(ct)).ToList()
            }, ct);
        }

        // ── /api/import ───────────────────────────────────────────────────────

        private async Task HandleImportAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            var body = await ReadJsonAsync<ImportRequest>(ctx, ct);
            if (body is null) { await WriteErrorAsync(ctx, 400, "Invalid body", ct); return; }
            var sRepo = _sp.GetRequiredService<IServerRepository>();
            var gRepo = _sp.GetRequiredService<IToolGroupRepository>();
            var cRepo = _sp.GetService<IClientRepository>();
            var agg   = _sp.GetRequiredService<ToolAggregator>();
            int sAdded = 0, gAdded = 0, cAdded = 0;
            if (body.Servers != null)
            {
                var ex = (await sRepo.GetAllAsync(ct)).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var s in body.Servers) { if (ex.Contains(s.Name)) continue; s.Id = Guid.NewGuid(); await sRepo.AddAsync(s, ct); sAdded++; }
            }
            if (body.Groups != null)
            {
                var ex = (await gRepo.GetAllAsync(ct)).Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var g in body.Groups) { if (ex.Contains(g.Name)) continue; g.Id = Guid.NewGuid(); await gRepo.AddAsync(g, ct); gAdded++; }
            }
            if (body.Clients != null && cRepo != null)
            {
                var ex = (await cRepo.GetAllAsync(ct)).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var c in body.Clients) { if (ex.Contains(c.Name)) continue; c.Id = Guid.NewGuid(); if (string.IsNullOrEmpty(c.BearerToken)) c.BearerToken = GenerateToken(); await cRepo.AddAsync(c, ct); cAdded++; }
            }
            await agg.RefreshAsync(ct);
            await WriteJsonAsync(ctx, 200, new { serversAdded = sAdded, groupsAdded = gAdded, clientsAdded = cAdded }, ct);
        }

        // ── /api/catalog ──────────────────────────────────────────────────────

        private async Task HandleCatalogAsync(HttpListenerContext ctx, string[] segs, string method, CancellationToken ct)
        {
            var catalog = _sp.GetRequiredService<GatewayCatalogService>();
            var s2 = Seg(segs, 2);

            if (segs.Length == 2 && method == "GET")
            { await WriteJsonAsync(ctx, 200, new { version = 4, updatedAt = DateTime.UtcNow, servers = await catalog.GetMergedAsync(ct) }, ct); return; }

            if (s2 == "search" && method == "GET")
            {
                var q   = ctx.Request.QueryString["q"]?.Trim().ToLowerInvariant() ?? "";
                var all = await catalog.GetMergedAsync(ct);
                var filtered = string.IsNullOrEmpty(q) ? all : all.Where(c =>
                {
                    if (c is not System.Text.Json.JsonElement el) return false;
                    var title = el.TryGetProperty("title",       out var t) ? (t.GetString() ?? "").ToLowerInvariant() : "";
                    var desc  = el.TryGetProperty("description", out var d) ? (d.GetString() ?? "").ToLowerInvariant() : "";
                    var cat   = el.TryGetProperty("category",    out var g) ? (g.GetString() ?? "").ToLowerInvariant() : "";
                    var name  = el.TryGetProperty("name",        out var n) ? (n.GetString() ?? "").ToLowerInvariant() : "";
                    return title.Contains(q) || desc.Contains(q) || cat.Contains(q) || name.Contains(q);
                }).ToList();
                await WriteJsonAsync(ctx, 200, new { servers = filtered }, ct); return;
            }
            if (s2 == "custom" && method == "POST")
            {
                var body = await ReadJsonAsync<CustomCatalogEntry>(ctx, ct);
                if (body is null || string.IsNullOrWhiteSpace(body.Title) || string.IsNullOrWhiteSpace(body.Command))
                { await WriteErrorAsync(ctx, 400, "title and command are required", ct); return; }
                body.Name = "custom-" + Guid.NewGuid().ToString("N")[..8]; body.Source = "custom";
                await catalog.AddCustomAsync(body, ct);
                await WriteJsonAsync(ctx, 200, body, ct); return;
            }
            if (segs.Length == 4 && s2 == "custom" && method == "DELETE")
            { await catalog.RemoveCustomAsync(Seg(segs, 3), ct); ctx.Response.StatusCode = 200; ctx.Response.Close(); return; }

            await WriteErrorAsync(ctx, 404, "Not found", ct);
        }

        // ── Server health / stdio-probe (unchanged from original) ─────────────

        private async Task HandleServerHealthAsync(HttpListenerContext ctx, Guid id, IServerRepository repo, ServerRegistry registry, CancellationToken ct)
        {
            var server = await repo.GetByIdAsync(id, ct);
            if (server is null) { await WriteErrorAsync(ctx, 404, "Not found", ct); return; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var client = registry.GetOrCreateClient(server);
                if (!client.IsConnected) await client.ConnectAsync(ct);
                var tools = await client.ListToolsAsync(ct);
                sw.Stop();
                await WriteJsonAsync(ctx, 200, new { id, status = "healthy", latencyMs = sw.ElapsedMilliseconds, toolCount = tools.Count }, ct);
            }
            catch (Exception ex) { sw.Stop(); await WriteJsonAsync(ctx, 200, new { id, status = "unhealthy", latencyMs = sw.ElapsedMilliseconds, error = ex.Message }, ct); }
        }

        private async Task HandleStdioProbeAsync(HttpListenerContext ctx, Guid id, IServerRepository repo, CancellationToken ct)
        {
            var server = await repo.GetByIdAsync(id, ct);
            if (server is null) { await WriteErrorAsync(ctx, 404, "Not found", ct); return; }
            if (server.TransportType != UpstreamTransportType.Stdio)
            { await WriteJsonAsync(ctx, 400, new { error = "Not a stdio server." }, ct); return; }

            var resolvedCmd = McpNet.Gateway.Upstream.StdioCommandHelper.ResolveCommandPath(server.StdioCommand ?? "");
            var cmdExt      = System.IO.Path.GetExtension(resolvedCmd).ToLowerInvariant();
            var viaCmd      = OperatingSystem.IsWindows() && (cmdExt == ".cmd" || cmdExt == ".bat");

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = viaCmd ? "cmd.exe" : resolvedCmd,
                Arguments = viaCmd
                    ? "/c " + (resolvedCmd.Contains(' ') ? $"\"{resolvedCmd}\"" : resolvedCmd)
                      + (server.StdioArgs?.Count > 0 ? " " + string.Join(" ", server.StdioArgs) : "")
                    : string.Join(" ", server.StdioArgs ?? new List<string>()),
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8, StandardInputEncoding = Encoding.UTF8,
            };

            var log = new List<object>();
            System.Diagnostics.Process? proc = null;
            try
            {
                proc = System.Diagnostics.Process.Start(psi)!;
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(15));

                async Task<string?> ReadLine() { var bytes = new List<byte>(); var one = new byte[1]; while (true) { var n = await proc.StandardOutput.BaseStream.ReadAsync(one, 0, 1, cts.Token); if (n == 0) return bytes.Count > 0 ? Encoding.UTF8.GetString(bytes.ToArray()) : null; if (one[0] == '\n') break; bytes.Add(one[0]); } if (bytes.Count > 0 && bytes[^1] == '\r') bytes.RemoveAt(bytes.Count - 1); return Encoding.UTF8.GetString(bytes.ToArray()); }
                async Task WriteMsg(string m) { var b = Encoding.UTF8.GetBytes(m + "\n"); await proc.StandardInput.BaseStream.WriteAsync(b, 0, b.Length, cts.Token); await proc.StandardInput.BaseStream.FlushAsync(cts.Token); }

                var init = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\",\"clientInfo\":{\"name\":\"probe\",\"version\":\"1.0\"},\"capabilities\":{}}}";
                await WriteMsg(init); log.Add(new { sent = init });
                var r = await ReadLine(); log.Add(new { recv = r });
                var notif = "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\",\"params\":{}}";
                await WriteMsg(notif); log.Add(new { sent = notif });
                var list = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}";
                await WriteMsg(list); log.Add(new { sent = list });
                for (var i = 0; i < 20; i++) { var line = await ReadLine(); if (line is null) { log.Add(new { recv = "(stream closed)" }); break; } log.Add(new { recv = line.Length > 2000 ? line[..2000] + "…" : line }); if (line.Contains("\"id\":2")) break; }
                await Task.Delay(200, cts.Token);
                var stderr = new List<string>(); while (proc.StandardError.Peek() > 0) { var l = proc.StandardError.ReadLine(); if (l != null) stderr.Add(l); }
                await WriteJsonAsync(ctx, 200, new { command = psi.FileName + " " + psi.Arguments, log, stderr }, ct);
            }
            catch (Exception ex)
            {
                var se = new List<string>(); try { if (proc != null) se.AddRange((await proc.StandardError.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); } catch { }
                await WriteJsonAsync(ctx, 200, new { error = ex.Message, log, stderr = se }, ct);
            }
            finally { try { proc?.Kill(entireProcessTree: true); } catch { } }
        }

        // ── HTTP helpers ──────────────────────────────────────────────────────

        private static async Task WriteJsonAsync(HttpListenerContext ctx, int status, object data, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(McpJsonOptions.Serialize(data));
            ctx.Response.StatusCode = status; ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
            ctx.Response.OutputStream.Close();
        }

        private static Task WriteErrorAsync(HttpListenerContext ctx, int status, string message, CancellationToken ct)
            => WriteJsonAsync(ctx, status, new { error = message }, ct);

        private static async Task<T?> ReadJsonAsync<T>(HttpListenerContext ctx, CancellationToken ct)
        {
            try { using var r = new System.IO.StreamReader(ctx.Request.InputStream, Encoding.UTF8); return McpJsonOptions.Deserialize<T>(await r.ReadToEndAsync().ConfigureAwait(false)); }
            catch { return default; }
        }

        private static string Seg(string[] s, int i) => i < s.Length ? s[i] : "";
        private static bool TryGuid(string[] s, int i, out Guid id) => Guid.TryParse(Seg(s, i), out id);
        private static void FireAndForget(Func<Task> fn) => _ = Task.Run(async () => { try { await fn().ConfigureAwait(false); } catch { } });
        private static string GenerateToken() { var b = new byte[32]; RandomNumberGenerator.Fill(b); return Convert.ToBase64String(b).Replace("+", "-").Replace("/", "_").Replace("=", ""); }

        // ── DTO helpers ───────────────────────────────────────────────────────

        private static object ServerDto(RegisteredServer s) => new { s.Id, s.Name, s.Url, TransportType = s.TransportType.ToString(), s.StdioCommand, s.Enabled, s.Quarantined, s.CreatedAt, s.UpdatedAt, HasAuth = !string.IsNullOrEmpty(s.BearerToken) || s.CustomHeaders.Count > 0 || s.OAuth is { Enabled: true }, OAuth = s.OAuth is { Enabled: true } ? "client_credentials" : null };
        private static object ServerDetailDto(RegisteredServer s) => new { s.Id, s.Name, s.Url, TransportType = s.TransportType.ToString(), s.Enabled, s.CreatedAt, s.UpdatedAt, s.StdioCommand, s.StdioArgs, s.StdioWorkingDirectory, StdioEnvVarKeys = s.StdioEnvVars.Keys.ToList(), CustomHeaders = s.CustomHeaders, HasAuth = !string.IsNullOrEmpty(s.BearerToken) || s.CustomHeaders.Count > 0 || s.OAuth is { Enabled: true }, OAuth = s.OAuth is { Enabled: true } ? new { s.OAuth.Enabled, s.OAuth.TokenUrl, s.OAuth.ClientId, s.OAuth.Scopes } : null };
        private static object ClientDto(McpClient c) => new { c.Id, c.Name, c.Enabled, c.CreatedAt, AllowedServers = c.AllowedServerIds.Count, AllowedGroups = c.AllowedGroupIds.Count };
        private static object ClientDetailDto(McpClient c) => new { c.Id, c.Name, c.Enabled, c.CreatedAt, c.AllowedServerIds, c.AllowedGroupIds, c.RateLimitPerMinute, c.ServerRateLimits, AllowedServers = c.AllowedServerIds.Count, AllowedGroups = c.AllowedGroupIds.Count };

        // ── Server builder helpers ────────────────────────────────────────────

        private static RegisteredServer BuildServer(RegisterServerRequest b) => new RegisteredServer
        {
            Name = b.Name, Url = b.Url,
            TransportType = Enum.TryParse<UpstreamTransportType>(b.TransportType, true, out var tt) ? tt : UpstreamTransportType.StreamableHttp,
            BearerToken = b.BearerToken, CustomHeaders = b.CustomHeaders ?? new Dictionary<string, string>(),
            StdioCommand = b.StdioCommand, StdioArgs = b.StdioArgs ?? new List<string>(),
            StdioWorkingDirectory = b.StdioWorkingDirectory, StdioEnvVars = b.StdioEnvVars ?? new Dictionary<string, string>(), OAuth = b.OAuth
        };

        private static void ApplyServerUpdate(RegisteredServer existing, RegisterServerRequest b)
        {
            existing.Name = b.Name; existing.Url = b.Url;
            if (Enum.TryParse<UpstreamTransportType>(b.TransportType, true, out var tt)) existing.TransportType = tt;
            existing.BearerToken = b.BearerToken; existing.CustomHeaders = b.CustomHeaders ?? existing.CustomHeaders;
            existing.StdioCommand = b.StdioCommand; existing.StdioArgs = b.StdioArgs ?? existing.StdioArgs;
            existing.StdioWorkingDirectory = b.StdioWorkingDirectory; existing.StdioEnvVars = b.StdioEnvVars ?? existing.StdioEnvVars;
            if (b.OAuth != null && string.IsNullOrEmpty(b.OAuth.ClientSecret) && existing.OAuth != null) b.OAuth.ClientSecret = existing.OAuth.ClientSecret;
            existing.OAuth = b.OAuth;
        }

        // ── Request models ────────────────────────────────────────────────────

        private class RegisterServerRequest { public string Name { get; set; } = ""; public string? Url { get; set; } public string TransportType { get; set; } = "StreamableHttp"; public string? BearerToken { get; set; } public Dictionary<string, string>? CustomHeaders { get; set; } public string? StdioCommand { get; set; } public List<string>? StdioArgs { get; set; } public string? StdioWorkingDirectory { get; set; } public Dictionary<string, string>? StdioEnvVars { get; set; } public OAuthConfig? OAuth { get; set; } }
        private class CreateGroupRequest   { public string Name { get; set; } = ""; public string? Description { get; set; } }
        private class ToolNameRequest      { public string ToolName { get; set; } = ""; }
        private class CreateClientRequest  { public string Name { get; set; } = ""; }
        private class UpdateClientRequest  { public bool Enabled { get; set; } = true; public List<Guid>? AllowedServerIds { get; set; } public List<Guid>? AllowedGroupIds { get; set; } public int RateLimitPerMinute { get; set; } public List<ServerRateLimit>? ServerRateLimits { get; set; } }
        private class CallToolRequest      { public string FullName { get; set; } = ""; public Dictionary<string, object?>? Arguments { get; set; } }
        private class ImportRequest        { public List<RegisteredServer>? Servers { get; set; } public List<ToolGroup>? Groups { get; set; } public List<McpClient>? Clients { get; set; } }
        private class CustomCatalogEntry   { public string Name { get; set; } = ""; public string Title { get; set; } = ""; public string Description { get; set; } = ""; public string Category { get; set; } = "Custom"; public string Transport { get; set; } = "Stdio"; public string Command { get; set; } = ""; public List<string> Args { get; set; } = new(); public string? Url { get; set; } public string Source { get; set; } = "custom"; }
    }
}
