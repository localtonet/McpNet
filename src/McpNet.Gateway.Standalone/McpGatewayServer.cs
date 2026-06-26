using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using McpNet.Gateway.Aggregation;
using McpNet.Gateway.Standalone.Dashboard;
using McpNet.Gateway.Standalone.Management;
using McpNet.Core.Serialization;
using McpNet.Transport.Http;

namespace McpNet.Gateway.Standalone
{
    /// <summary>
    /// Self-contained MCP gateway using <see cref="HttpListener"/>.
    /// Dispatches to:
    ///   /mcp*       → <see cref="HttpListenerMcpTransport"/> (McpNet.Transport.Http)
    ///   /api*       → <see cref="ManagementHandler"/>
    ///   /dashboard* → <see cref="DashboardHandler"/>
    ///   /health     → inline JSON
    /// </summary>
    public sealed class McpGatewayServer : IAsyncDisposable
    {
        private readonly IServiceProvider        _sp;
        private readonly McpGatewayOptions       _opts;
        private readonly ManagementHandler       _mgmt;
        private readonly DashboardHandler        _dash;
        private readonly HttpListenerMcpTransport _mcpTransport;

        private HttpListener?            _listener;
        private CancellationTokenSource? _cts;
        private Task?                    _acceptLoop;
        private Task?                    _refreshLoop;

        internal McpGatewayServer(IServiceProvider sp, McpGatewayOptions opts)
        {
            _sp           = sp;
            _opts         = opts;
            _mgmt         = new ManagementHandler(sp);
            _dash         = new DashboardHandler();
            _mcpTransport = sp.GetRequiredService<HttpListenerMcpTransport>();
        }

        public Task StartAsync(CancellationToken ct = default)
        {
            _cts      = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _listener = new HttpListener();
            var prefix = _opts.ListenPrefix ?? $"http://localhost:{_opts.Port}/";
            _listener.Prefixes.Add(prefix);
            _listener.Start();
            _acceptLoop  = AcceptLoopAsync(_cts.Token);
            _refreshLoop = RefreshLoopAsync(_cts.Token);
            return Task.CompletedTask;
        }

        public async Task RunAsync(CancellationToken ct = default)
        {
            if (_listener is null) await StartAsync(ct).ConfigureAwait(false);
            try { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            await StopAsync().ConfigureAwait(false);
        }

        public async Task StopAsync()
        {
            _cts?.Cancel();
            _listener?.Stop();
            if (_acceptLoop  is not null) try { await _acceptLoop.ConfigureAwait(false); }  catch { }
            if (_refreshLoop is not null) try { await _refreshLoop.ConfigureAwait(false); } catch { }
        }

        public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _listener?.IsListening == true)
            {
                try { _ = DispatchAsync(await _listener.GetContextAsync().ConfigureAwait(false), ct); }
                catch (HttpListenerException) when (ct.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) { break; }
            }
        }

        private async Task RefreshLoopAsync(CancellationToken ct)
        {
            var agg = _sp.GetRequiredService<ToolAggregator>();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                while (!ct.IsCancellationRequested)
                {
                    try { await agg.RefreshAsync(ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    catch { }
                    await Task.Delay(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        }

        private async Task DispatchAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            var path = ctx.Request.Url?.AbsolutePath?.TrimEnd('/') ?? "";
            try
            {
                // CORS
                var origin = ctx.Request.Headers["Origin"];
                if (!string.IsNullOrEmpty(origin) && IsAllowedOrigin(origin))
                    ctx.Response.AddHeader("Access-Control-Allow-Origin", origin);

                if (ctx.Request.HttpMethod == "OPTIONS")
                {
                    ctx.Response.AddHeader("Access-Control-Allow-Methods", "GET,POST,PUT,DELETE,PATCH,OPTIONS");
                    ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type,Authorization,X-Admin-Token,Mcp-Session-Id,MCP-Protocol-Version");
                    ctx.Response.StatusCode = 204;
                    ctx.Response.Close();
                    return;
                }

                if (path == "" || path == "/dashboard")
                    { await _dash.ServeAsync(ctx, "dashboard.html", ct); return; }
                if (path.EndsWith("/dashboard.js",  StringComparison.OrdinalIgnoreCase))
                    { await _dash.ServeAsync(ctx, "dashboard.js",   ct); return; }
                if (path.EndsWith("/dashboard.css", StringComparison.OrdinalIgnoreCase))
                    { await _dash.ServeAsync(ctx, "dashboard.css",  ct); return; }
                if (path.EndsWith("/catalog.json",  StringComparison.OrdinalIgnoreCase))
                    { await _dash.ServeAsync(ctx, "catalog.json",   ct); return; }
                if (path == "/health")
                    { await HandleHealthAsync(ctx, ct); return; }
                if (path.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase))
                    { await _mcpTransport.HandleContextAsync(ctx, ct); return; }
                if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
                    { await _mgmt.HandleAsync(ctx, ct); return; }

                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                try
                {
                    ctx.Response.StatusCode = 500;
                    var msg = Encoding.UTF8.GetBytes($"{{\"error\":\"{ex.Message}\"}}");
                    ctx.Response.ContentType     = "application/json";
                    ctx.Response.ContentLength64 = msg.Length;
                    await ctx.Response.OutputStream.WriteAsync(msg, 0, msg.Length, ct).ConfigureAwait(false);
                }
                catch { }
                finally { try { ctx.Response.Close(); } catch { } }
            }
        }

        private async Task HandleHealthAsync(HttpListenerContext ctx, CancellationToken ct)
        {
            var json  = McpJsonOptions.Serialize(new { status = "ok", mode = _opts.Mode.ToString() });
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentType     = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
            ctx.Response.OutputStream.Close();
        }

        private static bool IsAllowedOrigin(string origin) =>
            origin.StartsWith("http://localhost",  StringComparison.OrdinalIgnoreCase) ||
            origin.StartsWith("https://localhost", StringComparison.OrdinalIgnoreCase) ||
            origin.StartsWith("http://127.0.0.1",  StringComparison.OrdinalIgnoreCase) ||
            origin.StartsWith("https://127.0.0.1", StringComparison.OrdinalIgnoreCase);
    }
}
