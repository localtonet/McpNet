using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using McpNet.Core.Protocol;
using McpNet.Core.Serialization;
using McpNet.Gateway.Auth;
using McpNet.Gateway.Notifications;
using McpNet.Gateway.Routing;
using McpNet.Gateway.Sessions;

namespace McpNet.Transport.AspNetCore
{
    public static class McpEndpointRouteBuilderExtensions
    {
        public static IEndpointConventionBuilder MapMcp(
            this IEndpointRouteBuilder endpoints,
            string pattern = "/mcp")
        {
            var group = endpoints.MapGroup(pattern);

            group.MapPost("", async (HttpContext ctx) =>
            {
                var router = ctx.RequestServices.GetRequiredService<GatewayRequestRouter>();
                var auth = ctx.RequestServices.GetRequiredService<GatewayAuthenticator>();
                var sessions = ctx.RequestServices.GetRequiredService<GatewaySessionManager>();

                // Security: validate Origin
                if (!IsAllowedOrigin(ctx.Request.Headers["Origin"].ToString(), ctx.Request.Host.Host))
                {
                    ctx.Response.StatusCode = 403;
                    return;
                }

                var sessionId = ctx.Request.Headers[McpHeaders.SessionId].ToString();
                McpNet.Gateway.Models.McpClient? client = null;

                if (auth.RequiresMcpAuth)
                {
                    var authHeader = ctx.Request.Headers["Authorization"].ToString();
                    var token = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authHeader[7..] : null;
                    client = await auth.AuthenticateMcpClientAsync(token, ctx.RequestAborted);
                    if (client == null) { ctx.Response.StatusCode = 401; return; }
                }

                if (!string.IsNullOrEmpty(sessionId) && !sessions.SessionExists(sessionId))
                {
                    ctx.Response.StatusCode = 404;
                    return;
                }

                string body;
                using (var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8))
                    body = await reader.ReadToEndAsync(ctx.RequestAborted);

                JsonRpcRequest? request;
                try { request = McpJsonOptions.Deserialize<JsonRpcRequest>(body); }
                catch { ctx.Response.StatusCode = 400; return; }

                if (request == null) { ctx.Response.StatusCode = 400; return; }

                // Notifications → 202
                if (request.Id == null) { ctx.Response.StatusCode = 202; return; }

                var response = await router.HandleAsync(request, sessionId, client, ctx.RequestAborted);

                if (response.SessionId != null)
                    ctx.Response.Headers[McpHeaders.SessionId] = response.SessionId;

                var wantsSse = ctx.Request.Headers["Accept"].ToString().Contains("text/event-stream");
                if (wantsSse)
                {
                    ctx.Response.ContentType = "text/event-stream";
                    ctx.Response.Headers["Cache-Control"] = "no-cache";
                    ctx.Response.Headers["X-Accel-Buffering"] = "no";
                    var json = McpJsonOptions.Serialize(response);
                    var sse = $"data: {json}\n\n";
                    await ctx.Response.WriteAsync(sse, ctx.RequestAborted);
                }
                else
                {
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync(McpJsonOptions.Serialize(response), ctx.RequestAborted);
                }
            });

            group.MapGet("", async (HttpContext ctx) =>
            {
                ctx.Response.ContentType = "text/event-stream";
                ctx.Response.Headers["Cache-Control"] = "no-cache";
                ctx.Response.Headers["X-Accel-Buffering"] = "no";

                // Feature 2: register with SseConnectionManager so server-initiated notifications
                // (e.g. notifications/tools/list_changed) can be pushed to this client.
                var sseManager = ctx.RequestServices.GetService<SseConnectionManager>();
                Guid sseId = default;
                System.Threading.Channels.ChannelReader<string>? reader = null;
                if (sseManager != null)
                    (sseId, reader) = sseManager.Register();

                var keepAlive = Encoding.UTF8.GetBytes(":keep-alive\n\n");
                try
                {
                    while (!ctx.RequestAborted.IsCancellationRequested)
                    {
                        // Drain any pending notifications first.
                        if (reader != null)
                        {
                            while (reader.TryRead(out var msg))
                            {
                                await ctx.Response.WriteAsync(msg, ctx.RequestAborted);
                                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                            }
                        }

                        await ctx.Response.Body.WriteAsync(keepAlive, ctx.RequestAborted);
                        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                        await Task.Delay(15000, ctx.RequestAborted);
                    }
                }
                catch (OperationCanceledException) { }
                finally
                {
                    sseManager?.Deregister(sseId);
                }
            });

            group.MapDelete("", (HttpContext ctx) =>
            {
                var sessions = ctx.RequestServices.GetRequiredService<GatewaySessionManager>();
                var sessionId = ctx.Request.Headers[McpHeaders.SessionId].ToString();
                if (!string.IsNullOrEmpty(sessionId))
                    sessions.TerminateSession(sessionId);
                return Results.Ok();
            });

            return group;
        }

        private static bool IsAllowedOrigin(string origin, string host)
        {
            if (string.IsNullOrEmpty(origin)) return true;
            if (origin.Contains("localhost", StringComparison.OrdinalIgnoreCase)) return true;
            if (origin.Contains("127.0.0.1")) return true;
            if (origin.Contains(host, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    public static class McpServiceCollectionExtensions
    {
        public static IServiceCollection AddMcpGateway(
            this IServiceCollection services,
            Action<McpGatewayServiceOptions>? configure = null)
        {
            var opts = new McpGatewayServiceOptions();
            configure?.Invoke(opts);
            services.AddSingleton(opts);
            services.AddSingleton<GatewaySessionManager>();
            services.AddSingleton(opts.AuthOptions);
            services.AddSingleton<GatewayAuthenticator>(sp =>
                new GatewayAuthenticator(
                    sp.GetRequiredService<GatewayAuthOptions>(),
                    sp.GetService<McpNet.Gateway.Abstractions.IClientRepository>()));
            return services;
        }
    }

    public class McpGatewayServiceOptions
    {
        public GatewayAuthOptions AuthOptions { get; set; } = new GatewayAuthOptions();
    }
}
