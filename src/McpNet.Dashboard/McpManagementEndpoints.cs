using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using McpNet.Core.Serialization;
using McpNet.Gateway.Abstractions;
using McpNet.Gateway.Aggregation;
using McpNet.Gateway.Auth;
using McpNet.Gateway.Dashboard;
using McpNet.Gateway.Models;
using McpNet.Gateway.Registry;

namespace McpNet.Dashboard
{
    public static class McpManagementEndpoints
    {
        public static IEndpointConventionBuilder MapMcpManagement(
            this IEndpointRouteBuilder endpoints,
            string pattern = "/api")
        {
            // Public (no auth) — lets the dashboard show mode before authenticating.
            endpoints.MapGet(pattern + "/info", (GatewayAuthenticator auth) =>
                Results.Ok(new { mode = auth.IsDevMode ? "Dev" : "Enterprise", requiresAuth = !auth.IsDevMode }));

            var group = endpoints.MapGroup(pattern);
            group.AddEndpointFilter<AdminAuthFilter>();

            // ── Servers ──────────────────────────────────────────────────────
            group.MapGet("/servers", async (IServerRepository repo, CancellationToken ct) =>
            {
                var servers = await repo.GetAllAsync(ct);
                return Results.Ok(servers.Select(s => ServerDto(s)));
            });

            group.MapGet("/servers/{id:guid}", async (Guid id, IServerRepository repo, CancellationToken ct) =>
            {
                var s = await repo.GetByIdAsync(id, ct);
                return s is null ? Results.NotFound() : Results.Ok(ServerDetailDto(s));
            });

            group.MapPost("/servers", async (HttpContext ctx, IServerRepository repo, ServerRegistry registry, ToolAggregator aggregator, CancellationToken ct) =>
            {
                var body = await ReadJsonAsync<RegisterServerRequest>(ctx);
                if (body is null) return Results.BadRequest("Invalid body");
                var server = new RegisteredServer
                {
                    Name = body.Name,
                    Url = body.Url,
                    TransportType = Enum.TryParse<UpstreamTransportType>(body.TransportType, true, out var tt) ? tt : UpstreamTransportType.StreamableHttp,
                    BearerToken = body.BearerToken,
                    CustomHeaders = body.CustomHeaders ?? new System.Collections.Generic.Dictionary<string, string>(),
                    StdioCommand = body.StdioCommand,
                    StdioArgs = body.StdioArgs ?? new System.Collections.Generic.List<string>(),
                    StdioWorkingDirectory = body.StdioWorkingDirectory,
                    StdioEnvVars = body.StdioEnvVars ?? new System.Collections.Generic.Dictionary<string, string>(),
                    OAuth = body.OAuth
                };
                var saved = await registry.RegisterServerAsync(server, ct);
                // Fire-and-forget: stdio servers can take 60-120s to start on first run.
                _ = Task.Run(async () => { try { await aggregator.RefreshAsync(); } catch { } });
                return Results.Created($"/api/servers/{saved.Id}", ServerDto(saved));
            });

            group.MapPut("/servers/{id:guid}", async (Guid id, HttpContext ctx, IServerRepository repo, ServerRegistry registry, ToolAggregator aggregator, CancellationToken ct) =>
            {
                var existing = await repo.GetByIdAsync(id, ct);
                if (existing is null) return Results.NotFound();
                var body = await ReadJsonAsync<RegisterServerRequest>(ctx);
                if (body is null) return Results.BadRequest("Invalid body");
                existing.Name = body.Name;
                existing.Url = body.Url;
                if (Enum.TryParse<UpstreamTransportType>(body.TransportType, true, out var tt2))
                    existing.TransportType = tt2;
                existing.BearerToken = body.BearerToken;
                existing.CustomHeaders = body.CustomHeaders ?? existing.CustomHeaders;
                existing.StdioCommand = body.StdioCommand;
                existing.StdioArgs = body.StdioArgs ?? existing.StdioArgs;
                existing.StdioWorkingDirectory = body.StdioWorkingDirectory;
                // Preserve existing env vars when the client omits them (e.g. on UI edit).
                existing.StdioEnvVars = body.StdioEnvVars ?? existing.StdioEnvVars;
                // Preserve OAuth secret when the client leaves it blank on edit.
                if (body.OAuth != null && string.IsNullOrEmpty(body.OAuth.ClientSecret) && existing.OAuth != null)
                    body.OAuth.ClientSecret = existing.OAuth.ClientSecret;
                existing.OAuth = body.OAuth;
                var updated = await registry.UpdateServerAsync(existing, ct);
                // Fire-and-forget refresh so stdio servers don't block the HTTP response.
                _ = Task.Run(async () => { try { await aggregator.RefreshAsync(); } catch { } });
                return Results.Ok(ServerDto(updated));
            });

            group.MapDelete("/servers/{id:guid}", async (Guid id, ServerRegistry registry, ToolAggregator aggregator, CancellationToken ct) =>
            {
                await registry.DeleteServerAsync(id, ct);
                // Fire-and-forget: just clears the tool cache for the removed server.
                _ = Task.Run(async () => { try { await aggregator.RefreshAsync(); } catch { } });
                return Results.NoContent();
            });

            group.MapGet("/servers/{id:guid}/tools", async (Guid id, ToolAggregator aggregator, CancellationToken ct) =>
            {
                var tools = aggregator.GetAllTools().Where(t => t.ServerId == id).ToList();
                return Results.Ok(tools.Select(t => new { t.FullName, t.LocalName, t.Enabled }));
            });

            group.MapPatch("/servers/{id:guid}/tools/{toolName}/toggle", (Guid id, string toolName, bool enabled, ToolAggregator aggregator) =>
            {
                aggregator.SetToolEnabled(toolName, enabled);
                return Results.Ok(new { toolName, enabled });
            });

            // ── Tool Groups ───────────────────────────────────────────────────
            group.MapGet("/groups", async (IToolGroupRepository repo, CancellationToken ct) =>
                Results.Ok(await repo.GetAllAsync(ct)));

            group.MapPost("/groups", async (HttpContext ctx, IToolGroupRepository repo, CancellationToken ct) =>
            {
                var body = await ReadJsonAsync<CreateGroupRequest>(ctx);
                if (body is null) return Results.BadRequest();
                var group2 = new ToolGroup { Name = body.Name, Description = body.Description };
                var saved = await repo.AddAsync(group2, ct);
                return Results.Created($"/api/groups/{saved.Id}", saved);
            });

            group.MapPost("/groups/{id:guid}/tools", async (Guid id, HttpContext ctx, IToolGroupRepository repo, CancellationToken ct) =>
            {
                var body = await ReadJsonAsync<ToolNameRequest>(ctx);
                if (body is null) return Results.BadRequest();
                var g = await repo.GetByIdAsync(id, ct);
                if (g is null) return Results.NotFound();
                if (!g.ToolNames.Contains(body.ToolName)) g.ToolNames.Add(body.ToolName);
                return Results.Ok(await repo.UpdateAsync(g, ct));
            });

            group.MapDelete("/groups/{id:guid}/tools/{toolName}", async (Guid id, string toolName, IToolGroupRepository repo, CancellationToken ct) =>
            {
                var g = await repo.GetByIdAsync(id, ct);
                if (g is null) return Results.NotFound();
                g.ToolNames.Remove(toolName);
                return Results.Ok(await repo.UpdateAsync(g, ct));
            });

            group.MapDelete("/groups/{id:guid}", async (Guid id, IToolGroupRepository repo, CancellationToken ct) =>
            {
                await repo.DeleteAsync(id, ct);
                return Results.NoContent();
            });

            // ── Clients (Enterprise) ──────────────────────────────────────────
            group.MapGet("/clients", async (IClientRepository? repo, CancellationToken ct) =>
            {
                if (repo is null) return Results.Ok(new object[0]);
                return Results.Ok((await repo.GetAllAsync(ct)).Select(c => ClientDto(c)));
            });

            group.MapPost("/clients", async (HttpContext ctx, IClientRepository? repo, CancellationToken ct) =>
            {
                if (repo is null) return Results.StatusCode(501);
                var body = await ReadJsonAsync<CreateClientRequest>(ctx);
                if (body is null) return Results.BadRequest();
                var token = GenerateToken();
                var client = new McpClient { Name = body.Name, BearerToken = token };
                var saved = await repo.AddAsync(client, ct);
                return Results.Created($"/api/clients/{saved.Id}", new { saved.Id, saved.Name, BearerToken = token });
            });

            group.MapDelete("/clients/{id:guid}", async (Guid id, IClientRepository? repo, CancellationToken ct) =>
            {
                if (repo is null) return Results.StatusCode(501);
                await repo.DeleteAsync(id, ct);
                return Results.NoContent();
            });

            group.MapGet("/clients/{id:guid}", async (Guid id, IClientRepository? repo, CancellationToken ct) =>
            {
                if (repo is null) return Results.StatusCode(501);
                var c = await repo.GetByIdAsync(id, ct);
                return c is null ? Results.NotFound() : Results.Ok(ClientDetailDto(c));
            });

            group.MapPut("/clients/{id:guid}", async (Guid id, HttpContext ctx, IClientRepository? repo, CancellationToken ct) =>
            {
                if (repo is null) return Results.StatusCode(501);
                var c = await repo.GetByIdAsync(id, ct);
                if (c is null) return Results.NotFound();
                var body = await ReadJsonAsync<UpdateClientRequest>(ctx);
                if (body is null) return Results.BadRequest();
                c.Enabled = body.Enabled;
                c.AllowedServerIds = body.AllowedServerIds ?? c.AllowedServerIds;
                c.AllowedGroupIds = body.AllowedGroupIds ?? c.AllowedGroupIds;
                c.RateLimitPerMinute = body.RateLimitPerMinute;
                c.ServerRateLimits = body.ServerRateLimits ?? c.ServerRateLimits;
                return Results.Ok(ClientDetailDto(await repo.UpdateAsync(c, ct)));
            });

            group.MapPost("/clients/{id:guid}/regenerate", async (Guid id, IClientRepository? repo, CancellationToken ct) =>
            {
                if (repo is null) return Results.StatusCode(501);
                var c = await repo.GetByIdAsync(id, ct);
                if (c is null) return Results.NotFound();
                var token = GenerateToken();
                c.BearerToken = token;
                await repo.UpdateAsync(c, ct);
                return Results.Ok(new { c.Id, c.Name, BearerToken = token });
            });

            group.MapPatch("/servers/{id:guid}/toggle", async (Guid id, bool enabled, IServerRepository repo, CancellationToken ct) =>
            {
                var s = await repo.GetByIdAsync(id, ct);
                if (s is null) return Results.NotFound();
                s.Enabled = enabled;
                await repo.UpdateAsync(s, ct);
                return Results.Ok(new { id, enabled });
            });

            // ── Audit Logs ────────────────────────────────────────────────────
            group.MapGet("/audit", async (IAuditLogRepository? repo, CancellationToken ct) =>
            {
                if (repo is null) return Results.Ok(new object[0]);
                return Results.Ok(await repo.GetRecentAsync(200, ct));
            });

            // ── Tools (all) ───────────────────────────────────────────────────
            group.MapGet("/tools", (ToolAggregator aggregator) =>
                Results.Ok(aggregator.GetAllTools().Select(t => new { t.FullName, t.LocalName, t.ServerName, t.Enabled })));

            group.MapGet("/tools/status", (ToolAggregator aggregator) =>
                Results.Ok(new
                {
                    refreshing = aggregator.IsRefreshing,
                    lastRefreshedAt = aggregator.LastRefreshedAt == DateTime.MinValue ? (DateTime?)null : aggregator.LastRefreshedAt,
                    totalTools = aggregator.GetAllTools().Count,
                    diagnostics = aggregator.GetLastRefreshDiagnostics().Select(d => new
                    {
                        d.ServerId, d.ServerName, d.TransportType, d.Status, d.Success,
                        d.ToolCount, d.DurationMs, d.ErrorMessage, d.Timestamp
                    })
                }));

            group.MapGet("/tools/diagnostics", (ToolAggregator aggregator) =>
                Results.Ok(aggregator.GetLastRefreshDiagnostics().Select(d => new
                {
                    d.ServerId,
                    d.ServerName,
                    d.TransportType,
                    d.Status,
                    d.Success,
                    d.ToolCount,
                    d.DurationMs,
                    d.ErrorMessage,
                    d.Timestamp
                })));

            group.MapPost("/tools/refresh", async (ToolAggregator aggregator, IAuditLogRepository? auditRepo, IServiceProvider sp, CancellationToken ct) =>
            {
                // Fire-and-forget with a fresh token so the refresh survives HTTP connection close.
                // Stdio servers (npx) can take 30-60s on first run; this prevents a 502/forcible-close.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await aggregator.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
                        if (auditRepo != null)
                        {
                            var diags = aggregator.GetLastRefreshDiagnostics();
                            foreach (var f in diags.Where(d => !string.Equals(d.Status, "ok", StringComparison.OrdinalIgnoreCase)))
                            {
                                await auditRepo.AddAsync(new AuditLog
                                {
                                    ClientId = "dashboard",
                                    ClientName = "dashboard",
                                    Method = "tools.refresh",
                                    ToolName = "diagnostics",
                                    ServerName = f.ServerName,
                                    Success = false,
                                    ErrorMessage = f.ErrorMessage,
                                    DurationMs = f.DurationMs,
                                    Timestamp = DateTime.UtcNow
                                }, CancellationToken.None);
                            }
                        }
                    }
                    catch { /* logged per-server inside RefreshAsync */ }
                });

                // Return 202 immediately; client can poll /api/tools/diagnostics for results.
                return Results.Accepted("/api/tools/diagnostics", new { message = "Refresh started. Poll /api/tools/diagnostics for results." });
            });

            // ── Tool Inspector — call a tool from the dashboard ───────────────
            group.MapPost("/tools/call", async (HttpContext ctx, ToolAggregator aggregator, ServerRegistry registry, IServerRepository repo, CancellationToken ct) =>
            {
                var body = await ReadJsonAsync<CallToolRequest>(ctx);
                if (body is null || string.IsNullOrWhiteSpace(body.FullName)) return Results.BadRequest("fullName required");
                var tool = aggregator.GetTool(body.FullName);
                if (tool is null) return Results.NotFound(new { error = "Tool not found" });
                var server = await repo.GetByIdAsync(tool.ServerId, ct);
                if (server is null) return Results.NotFound(new { error = "Server not found" });
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var client = registry.GetOrCreateClient(server);
                    if (!client.IsConnected) await client.ConnectAsync(ct);
                    var result = await client.CallToolAsync(tool.LocalName, body.Arguments, ct);
                    sw.Stop();
                    return Results.Ok(new { success = !result.IsError, isError = result.IsError, durationMs = sw.ElapsedMilliseconds, content = result.Content });
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    return Results.Ok(new { success = false, isError = true, durationMs = sw.ElapsedMilliseconds, error = ex.Message });
                }
            });

            // ── Server health check ───────────────────────────────────────────
            group.MapGet("/servers/{id:guid}/health", async (Guid id, IServerRepository repo, ServerRegistry registry, CancellationToken ct) =>
            {
                var server = await repo.GetByIdAsync(id, ct);
                if (server is null) return Results.NotFound();

                if (server.TransportType == UpstreamTransportType.Stdio)
                {
                    // For stdio servers: resolve command, detect runtime, try real connect+list
                    var resolvedCmd  = McpNet.Gateway.Upstream.StdioCommandHelper.ResolveCommandPath(server.StdioCommand ?? "");
                    var runtimeInfo  = await McpNet.Gateway.Upstream.StdioCommandHelper.GetRuntimeInfoAsync(resolvedCmd, ct);

                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        var client = registry.GetOrCreateClient(server);
                        if (!client.IsConnected) await client.ConnectAsync(timeoutCts.Token);
                        var toolList = await client.ListToolsAsync(timeoutCts.Token);
                        sw.Stop();
                        var status = toolList.Count > 0 ? "healthy" : "warning";
                        var note   = toolList.Count == 0
                            ? "Connected but the server returned 0 tools. Check the path argument (e.g. edit the server and set the working-directory / args)."
                            : (string?)null;
                        return Results.Ok(new { id, status, latencyMs = sw.ElapsedMilliseconds, toolCount = toolList.Count,
                            note, command = server.StdioCommand, runtimeInfo });
                    }
                    catch (Exception ex)
                    {
                        sw.Stop();
                        return Results.Ok(new { id, status = "unhealthy", latencyMs = sw.ElapsedMilliseconds, toolCount = 0,
                            error = ex.Message, command = server.StdioCommand, runtimeInfo });
                    }
                }

                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var client = registry.GetOrCreateClient(server);
                    if (!client.IsConnected) await client.ConnectAsync(ct);
                    var tools = await client.ListToolsAsync(ct);
                    sw2.Stop();
                    return Results.Ok(new { id, status = "healthy", latencyMs = sw2.ElapsedMilliseconds, toolCount = tools.Count });
                }
                catch (Exception ex)
                {
                    sw2.Stop();
                    return Results.Ok(new { id, status = "unhealthy", latencyMs = sw2.ElapsedMilliseconds, error = ex.Message });
                }
            });

            // ── Stdio raw-probe (diagnostic) ──────────────────────────────────
            // Sends initialize + tools/list to a fresh process and returns raw responses.
            // Useful for diagnosing protocol issues without affecting the cached client.
            group.MapGet("/servers/{id:guid}/stdio-probe", async (Guid id, IServerRepository repo, CancellationToken ct) =>
            {
                var server = await repo.GetByIdAsync(id, ct);
                if (server is null) return Results.NotFound();
                if (server.TransportType != McpNet.Gateway.Models.UpstreamTransportType.Stdio)
                    return Results.BadRequest(new { error = "Not a stdio server." });

                var resolvedCmd = McpNet.Gateway.Upstream.StdioCommandHelper.ResolveCommandPath(server.StdioCommand ?? "");
                var cmdExt = System.IO.Path.GetExtension(resolvedCmd).ToLowerInvariant();
                var viaCmd = OperatingSystem.IsWindows() && (cmdExt == ".cmd" || cmdExt == ".bat");

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName               = viaCmd ? "cmd.exe" : resolvedCmd,
                    Arguments              = viaCmd
                        ? "/c " + (resolvedCmd.Contains(' ') ? "\"" + resolvedCmd + "\"" : resolvedCmd)
                          + (server.StdioArgs?.Count > 0 ? " " + string.Join(" ", server.StdioArgs) : "")
                        : string.Join(" ", server.StdioArgs ?? new System.Collections.Generic.List<string>()),
                    UseShellExecute        = false,
                    RedirectStandardInput  = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardInputEncoding  = System.Text.Encoding.UTF8,
                };

                var log = new System.Collections.Generic.List<object>();
                System.Diagnostics.Process? proc = null;
                try
                {
                    proc = System.Diagnostics.Process.Start(psi)!;
                    if (proc is null) return Results.Ok(new { error = "Failed to start process." });

                    using var cts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(15));

                    // NDJSON helper
                    async Task<string?> ReadLine()
                    {
                        var bytes = new System.Collections.Generic.List<byte>(2048);
                        var one = new byte[1];
                        while (true)
                        {
                            var n = await proc.StandardOutput.BaseStream.ReadAsync(one, 0, 1, cts.Token);
                            if (n == 0) return bytes.Count > 0 ? System.Text.Encoding.UTF8.GetString(bytes.ToArray()) : null;
                            if (one[0] == '\n') break;
                            bytes.Add(one[0]);
                        }
                        if (bytes.Count > 0 && bytes[^1] == '\r') bytes.RemoveAt(bytes.Count - 1);
                        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
                    }

                    async Task WriteMsg(string msg) {
                        var buf = System.Text.Encoding.UTF8.GetBytes(msg + "\n");
                        await proc.StandardInput.BaseStream.WriteAsync(buf, 0, buf.Length, cts.Token);
                        await proc.StandardInput.BaseStream.FlushAsync(cts.Token);
                    }

                    // Step 1: initialize
                    var initReq = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\",\"clientInfo\":{\"name\":\"probe\",\"version\":\"1.0\"},\"capabilities\":{}}}";
                    await WriteMsg(initReq);
                    log.Add(new { sent = initReq });

                    var initResp = await ReadLine();
                    log.Add(new { recv = initResp });

                    // Step 2: notifications/initialized
                    var notif = "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\",\"params\":{}}";
                    await WriteMsg(notif);
                    log.Add(new { sent = notif });

                    // Step 3: tools/list
                    var listReq = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}";
                    await WriteMsg(listReq);
                    log.Add(new { sent = listReq });

                    // Read until we get id=2 response or timeout
                    for (var i = 0; i < 20; i++)
                    {
                        var line = await ReadLine();
                        if (line is null) { log.Add(new { recv = "(null - stream closed)" }); break; }
                        log.Add(new { recv = line.Length > 2000 ? line[..2000] + "…(truncated)" : line });
                        if (line.Contains("\"id\":2")) break;
                    }

                    // Collect any stderr
                    await Task.Delay(200, cts.Token);
                    var stderr = new System.Collections.Generic.List<string>();
                    while (proc.StandardError.Peek() > 0)
                    {
                        var l = proc.StandardError.ReadLine();
                        if (l != null) stderr.Add(l);
                    }

                    return Results.Ok(new { command = psi.FileName + " " + psi.Arguments, log, stderr });
                }
                catch (Exception ex)
                {
                    // Capture stderr even on error
                    var stderrOnError = new System.Collections.Generic.List<string>();
                    try {
                        if (proc != null && !proc.HasExited)
                            while (proc.StandardError.Peek() > 0) { var l = proc.StandardError.ReadLine(); if (l != null) stderrOnError.Add(l); }
                        else if (proc != null)
                            stderrOnError.AddRange((await proc.StandardError.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    } catch { }
                    return Results.Ok(new { error = ex.Message, log, stderr = stderrOnError });
                }
                finally
                {
                    try { proc?.Kill(entireProcessTree: true); } catch { }
                }
            })
            .AddEndpointFilter<AdminAuthFilter>();

            // ── Config export / import ────────────────────────────────────────
            group.MapGet("/export", async (IServerRepository sRepo, IToolGroupRepository gRepo, IClientRepository? cRepo, CancellationToken ct) =>
            {
                var servers = await sRepo.GetAllAsync(ct);
                var groups = await gRepo.GetAllAsync(ct);
                var clients = cRepo is null ? new List<McpClient>() : (await cRepo.GetAllAsync(ct)).ToList();
                return Results.Ok(new { exportedAt = DateTime.UtcNow, servers, groups, clients });
            });

            group.MapPost("/import", async (HttpContext ctx, IServerRepository sRepo, IToolGroupRepository gRepo, IClientRepository? cRepo, ServerRegistry registry, ToolAggregator aggregator, CancellationToken ct) =>
            {
                var body = await ReadJsonAsync<ImportRequest>(ctx);
                if (body is null) return Results.BadRequest();
                int sAdded = 0, gAdded = 0, cAdded = 0;
                if (body.Servers != null)
                {
                    var existing = (await sRepo.GetAllAsync(ct)).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var s in body.Servers)
                    {
                        if (existing.Contains(s.Name)) continue;
                        s.Id = Guid.NewGuid();
                        await sRepo.AddAsync(s, ct);
                        sAdded++;
                    }
                }
                if (body.Groups != null)
                {
                    var existing = (await gRepo.GetAllAsync(ct)).Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var g in body.Groups)
                    {
                        if (existing.Contains(g.Name)) continue;
                        g.Id = Guid.NewGuid();
                        await gRepo.AddAsync(g, ct);
                        gAdded++;
                    }
                }
                if (body.Clients != null && cRepo != null)
                {
                    var existing = (await cRepo.GetAllAsync(ct)).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var c in body.Clients)
                    {
                        if (existing.Contains(c.Name)) continue;
                        c.Id = Guid.NewGuid();
                        if (string.IsNullOrEmpty(c.BearerToken)) c.BearerToken = GenerateToken();
                        await cRepo.AddAsync(c, ct);
                        cAdded++;
                    }
                }
                await aggregator.RefreshAsync(ct);
                return Results.Ok(new { serversAdded = sAdded, groupsAdded = gAdded, clientsAdded = cAdded });
            });

            // ── Catalog ──────────────────────────────────────────────────────
            group.MapGet("/catalog", async (IServiceProvider sp, CancellationToken ct) =>
            {
                var catalog = sp.GetRequiredService<GatewayCatalogService>();
                return Results.Ok(new { version = 4, updatedAt = DateTime.UtcNow, servers = await catalog.GetMergedAsync(ct) });
            });

            group.MapGet("/catalog/search", async (HttpContext ctx, IServiceProvider sp, CancellationToken ct) =>
            {
                var catalog = sp.GetRequiredService<GatewayCatalogService>();
                var q = ctx.Request.Query["q"].ToString().Trim().ToLowerInvariant();
                var all = await catalog.GetMergedAsync(ct);
                if (string.IsNullOrEmpty(q)) return Results.Ok(new { servers = all });
                var filtered = all.Where(c =>
                {
                    if (c is not System.Text.Json.JsonElement el) return false;
                    var title = el.TryGetProperty("title",       out var t) ? (t.GetString() ?? "").ToLowerInvariant() : "";
                    var desc  = el.TryGetProperty("description", out var d) ? (d.GetString() ?? "").ToLowerInvariant() : "";
                    var cat   = el.TryGetProperty("category",    out var g) ? (g.GetString() ?? "").ToLowerInvariant() : "";
                    var name  = el.TryGetProperty("name",        out var n) ? (n.GetString() ?? "").ToLowerInvariant() : "";
                    return title.Contains(q) || desc.Contains(q) || cat.Contains(q) || name.Contains(q);
                }).ToList();
                return Results.Ok(new { servers = filtered });
            });

            group.MapPost("/catalog/custom", async (HttpContext ctx, IServiceProvider sp, CancellationToken ct) =>
            {
                var catalog = sp.GetRequiredService<GatewayCatalogService>();
                var body = await ReadJsonAsync<CustomCatalogEntry>(ctx);
                if (body is null || string.IsNullOrWhiteSpace(body.Title) || string.IsNullOrWhiteSpace(body.Command))
                    return Results.BadRequest("title and command are required");
                body.Name = "custom-" + Guid.NewGuid().ToString("N")[..8];
                body.Source = "custom";
                await catalog.AddCustomAsync(body, ct);
                return Results.Ok(body);
            });

            group.MapDelete("/catalog/custom/{name}", async (string name, IServiceProvider sp, CancellationToken ct) =>
            {
                var catalog = sp.GetRequiredService<GatewayCatalogService>();
                await catalog.RemoveCustomAsync(name, ct);
                return Results.Ok();
            });

            return group;
        }

        private static async Task<T?> ReadJsonAsync<T>(HttpContext ctx)
        {
            try
            {
                using var reader = new System.IO.StreamReader(ctx.Request.Body, System.Text.Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                return McpJsonOptions.Deserialize<T>(body);
            }
            catch { return default; }
        }

        private static object ServerDto(RegisteredServer s) => new
        {
            s.Id, s.Name, s.Url, TransportType = s.TransportType.ToString(),
            s.StdioCommand,
            s.Enabled, s.CreatedAt, s.UpdatedAt,
            HasAuth = !string.IsNullOrEmpty(s.BearerToken) || s.CustomHeaders.Count > 0 || s.OAuth is { Enabled: true },
            OAuth = s.OAuth is { Enabled: true } ? "client_credentials" : null
        };

        // Richer DTO for the edit form (secret masked).
        private static object ServerDetailDto(RegisteredServer s) => new
        {
            s.Id, s.Name, s.Url, TransportType = s.TransportType.ToString(),
            s.Enabled, s.CreatedAt, s.UpdatedAt,
            s.StdioCommand, s.StdioArgs, s.StdioWorkingDirectory,
            // Return env var keys only (never values) so the dashboard can show which
            // keys are configured without leaking secrets to the browser.
            StdioEnvVarKeys = s.StdioEnvVars.Keys.ToList(),
            CustomHeaders = s.CustomHeaders,
            HasAuth = !string.IsNullOrEmpty(s.BearerToken) || s.CustomHeaders.Count > 0 || s.OAuth is { Enabled: true },
            OAuth = s.OAuth is { Enabled: true }
                ? new { s.OAuth.Enabled, s.OAuth.TokenUrl, s.OAuth.ClientId, s.OAuth.Scopes }
                : null
        };

        private static object ClientDto(McpClient c) => new
        {
            c.Id, c.Name, c.Enabled, c.CreatedAt,
            AllowedServers = c.AllowedServerIds.Count,
            AllowedGroups = c.AllowedGroupIds.Count
        };

        private static object ClientDetailDto(McpClient c) => new
        {
            c.Id, c.Name, c.Enabled, c.CreatedAt,
            c.AllowedServerIds,
            c.AllowedGroupIds,
            c.RateLimitPerMinute,
            c.ServerRateLimits,
            AllowedServers = c.AllowedServerIds.Count,
            AllowedGroups = c.AllowedGroupIds.Count
        };

        private static string GenerateToken()
        {
            var bytes = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").Replace("=", "");
        }

        private class RegisterServerRequest
        {
            public string Name { get; set; } = string.Empty;
            public string? Url { get; set; }
            public string TransportType { get; set; } = "StreamableHttp";
            public string? BearerToken { get; set; }
            public System.Collections.Generic.Dictionary<string, string>? CustomHeaders { get; set; }
            public string? StdioCommand { get; set; }
            public System.Collections.Generic.List<string>? StdioArgs { get; set; }
            public string? StdioWorkingDirectory { get; set; }
            public System.Collections.Generic.Dictionary<string, string>? StdioEnvVars { get; set; }
            public OAuthConfig? OAuth { get; set; }
        }

        private class CreateGroupRequest
        {
            public string Name { get; set; } = string.Empty;
            public string? Description { get; set; }
        }

        private class ToolNameRequest
        {
            public string ToolName { get; set; } = string.Empty;
        }

        private class CreateClientRequest
        {
            public string Name { get; set; } = string.Empty;
        }

        private class UpdateClientRequest
        {
            public bool Enabled { get; set; } = true;
            public List<Guid>? AllowedServerIds { get; set; }
            public List<Guid>? AllowedGroupIds { get; set; }
            public int RateLimitPerMinute { get; set; } = 0;
            public List<ServerRateLimit>? ServerRateLimits { get; set; }
        }

        private class CallToolRequest
        {
            public string FullName { get; set; } = string.Empty;
            public Dictionary<string, object?>? Arguments { get; set; }
        }

        private class ImportRequest
        {
            public List<RegisteredServer>? Servers { get; set; }
            public List<ToolGroup>? Groups { get; set; }
            public List<McpClient>? Clients { get; set; }
        }

        private class CustomCatalogEntry
        {
            public string Name { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public string Category { get; set; } = "Custom";
            public string Transport { get; set; } = "Stdio";
            // Stdio fields
            public string Command { get; set; } = string.Empty;
            public List<string> Args { get; set; } = new();
            // HTTP fields
            public string? Url { get; set; }
            public string Source { get; set; } = "custom";
        }
    }

    public class AdminAuthFilter : IEndpointFilter
    {
        private readonly GatewayAuthenticator _auth;
        public AdminAuthFilter(GatewayAuthenticator auth) { _auth = auth; }

        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            if (!_auth.IsDevMode)
            {
                var token = context.HttpContext.Request.Headers[McpNet.Core.Protocol.McpHeaders.AdminToken].ToString();
                if (!_auth.ValidateAdminToken(token))
                {
                    context.HttpContext.Response.StatusCode = 401;
                    await context.HttpContext.Response.WriteAsync("Unauthorized");
                    return null;
                }
            }
            return await next(context);
        }
    }
}

