using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using McpNet.Core.Protocol;
using McpNet.Core.Serialization;
using McpNet.Gateway.Auth;
using McpNet.Gateway.Routing;
using McpNet.Gateway.Sessions;

namespace McpNet.Transport.Http
{
    public class HttpListenerMcpTransport : IDisposable
    {
        private readonly HttpListenerMcpOptions _options;
        private readonly GatewayRequestRouter _router;
        private readonly GatewayAuthenticator _auth;
        private readonly GatewaySessionManager _sessions;
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private bool _disposed;

        public HttpListenerMcpTransport(
            HttpListenerMcpOptions options,
            GatewayRequestRouter router,
            GatewayAuthenticator auth,
            GatewaySessionManager sessions)
        {
            _options = options;
            _router = router;
            _auth = auth;
            _sessions = sessions;
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{_options.Port}{_options.McpPath}/");
            _listener.Start();
            _ = AcceptLoopAsync(_cts.Token);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _cts?.Cancel();
            _listener?.Stop();
            return Task.CompletedTask;
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && (_listener?.IsListening == true))
            {
                try
                {
                    var context = await _listener.GetContextAsync().ConfigureAwait(false);
                    _ = HandleContextAsync(context, ct);
                }
                catch (HttpListenerException) when (ct.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) { break; }
            }
        }

        public async Task HandleContextAsync(HttpListenerContext context, CancellationToken ct)
        {
            var req = context.Request;
            var resp = context.Response;

            try
            {
                // Security: validate Origin header to prevent DNS rebinding
                var origin = req.Headers["Origin"];
                if (!string.IsNullOrEmpty(origin) && !IsAllowedOrigin(origin))
                {
                    resp.StatusCode = 403;
                    resp.Close();
                    return;
                }

                // Route based on method
                if (req.HttpMethod == "POST" && req.Url?.AbsolutePath.TrimEnd('/') == _options.McpPath.TrimEnd('/'))
                    await HandleMcpPostAsync(req, resp, ct).ConfigureAwait(false);
                else if (req.HttpMethod == "GET" && req.Url?.AbsolutePath.TrimEnd('/') == _options.McpPath.TrimEnd('/'))
                    await HandleMcpGetSseAsync(req, resp, ct).ConfigureAwait(false);
                else if (req.HttpMethod == "DELETE" && req.Url?.AbsolutePath.TrimEnd('/') == _options.McpPath.TrimEnd('/'))
                    HandleMcpDelete(req, resp);
                else
                {
                    resp.StatusCode = 404;
                    resp.Close();
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                try
                {
                    resp.StatusCode = 500;
                    await WriteJsonAsync(resp, new { error = ex.Message }, ct).ConfigureAwait(false);
                }
                catch { resp.Close(); }
            }
        }

        private async Task HandleMcpPostAsync(HttpListenerRequest req, HttpListenerResponse resp, CancellationToken ct)
        {
            // Validate MCP protocol version
            var protocolVersion = req.Headers[McpHeaders.ProtocolVersion];

            // Auth
            var sessionId = req.Headers[McpHeaders.SessionId];
            McpNet.Gateway.Models.McpClient? client = null;

            if (_auth.RequiresMcpAuth)
            {
                var authHeader = req.Headers["Authorization"];
                var token = authHeader?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
                    ? authHeader.Substring(7) : null;
                client = await _auth.AuthenticateMcpClientAsync(token, ct).ConfigureAwait(false);
                if (client == null)
                {
                    resp.StatusCode = 401;
                    resp.Close();
                    return;
                }
            }

            // Session check (for non-initialize requests)
            if (!string.IsNullOrEmpty(sessionId) && !_sessions.SessionExists(sessionId))
            {
                resp.StatusCode = 404;
                resp.Close();
                return;
            }

            string body;
            using (var reader = new StreamReader(req.InputStream, Encoding.UTF8))
                body = await reader.ReadToEndAsync().ConfigureAwait(false);

            JsonRpcRequest? rpcRequest;
            try { rpcRequest = McpJsonOptions.Deserialize<JsonRpcRequest>(body); }
            catch { resp.StatusCode = 400; resp.Close(); return; }

            if (rpcRequest == null) { resp.StatusCode = 400; resp.Close(); return; }

            // Notifications and responses → 202 Accepted
            if (rpcRequest.Id == null)
            {
                resp.StatusCode = 202;
                resp.Close();
                return;
            }

            var rpcResponse = await _router.HandleAsync(rpcRequest, sessionId, client, ct).ConfigureAwait(false);

            // Check if client accepts SSE
            var accept = req.AcceptTypes ?? Array.Empty<string>();
            var wantsSse = Array.Exists(accept, a => a.Contains("text/event-stream"));

            if (wantsSse)
            {
                resp.ContentType = "text/event-stream";
                resp.AddHeader("Cache-Control", "no-cache");
                resp.AddHeader("X-Accel-Buffering", "no");
                if (rpcResponse.SessionId != null)
                    resp.AddHeader(McpHeaders.SessionId, rpcResponse.SessionId);

                await WriteSseEventAsync(resp.OutputStream, rpcResponse, ct).ConfigureAwait(false);
                resp.OutputStream.Close();
            }
            else
            {
                resp.ContentType = "application/json";
                if (rpcResponse.SessionId != null)
                    resp.AddHeader(McpHeaders.SessionId, rpcResponse.SessionId);
                await WriteJsonAsync(resp, rpcResponse, ct).ConfigureAwait(false);
            }
        }

        private async Task HandleMcpGetSseAsync(HttpListenerRequest req, HttpListenerResponse resp, CancellationToken ct)
        {
            // GET /mcp opens a persistent SSE stream for server→client notifications
            var sessionId = req.Headers[McpHeaders.SessionId];

            // Enterprise auth — same check as POST
            if (_auth.RequiresMcpAuth)
            {
                var authHeader = req.Headers["Authorization"];
                var token = authHeader?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
                    ? authHeader.Substring(7) : null;
                var client = await _auth.AuthenticateMcpClientAsync(token, ct).ConfigureAwait(false);
                if (client == null)
                {
                    resp.StatusCode = 401;
                    resp.Close();
                    return;
                }
            }

            if (string.IsNullOrEmpty(sessionId))
            {
                resp.StatusCode = 400;
                resp.Close();
                return;
            }

            resp.ContentType = "text/event-stream";
            resp.AddHeader("Cache-Control", "no-cache");
            resp.AddHeader("X-Accel-Buffering", "no");

            // Keep alive until cancelled
            var keepAlive = ":keep-alive\n\n";
            var keepAliveBytes = Encoding.UTF8.GetBytes(keepAlive);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await resp.OutputStream.WriteAsync(keepAliveBytes, 0, keepAliveBytes.Length, ct).ConfigureAwait(false);
                    await resp.OutputStream.FlushAsync(ct).ConfigureAwait(false);
                    await Task.Delay(15000, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            finally { resp.OutputStream.Close(); }
        }

        private void HandleMcpDelete(HttpListenerRequest req, HttpListenerResponse resp)
        {
            var sessionId = req.Headers[McpHeaders.SessionId];
            if (!string.IsNullOrEmpty(sessionId))
                _sessions.TerminateSession(sessionId);
            resp.StatusCode = 200;
            resp.Close();
        }

        private bool IsAllowedOrigin(string origin)
        {
            // Allow all localhost / loopback variants (http and https)
            if (origin.StartsWith("http://localhost",  StringComparison.OrdinalIgnoreCase)) return true;
            if (origin.StartsWith("https://localhost", StringComparison.OrdinalIgnoreCase)) return true;
            if (origin.StartsWith("http://127.0.0.1",  StringComparison.OrdinalIgnoreCase)) return true;
            if (origin.StartsWith("https://127.0.0.1", StringComparison.OrdinalIgnoreCase)) return true;
            if (_options.AllowedOrigins != null)
                foreach (var allowed in _options.AllowedOrigins)
                    if (origin.Equals(allowed, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static async Task WriteJsonAsync(HttpListenerResponse resp, object value, CancellationToken ct)
        {
            resp.ContentType = "application/json";
            var json = McpJsonOptions.Serialize(value);
            var bytes = Encoding.UTF8.GetBytes(json);
            resp.ContentLength64 = bytes.Length;
            await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
            resp.OutputStream.Close();
        }

        private static async Task WriteSseEventAsync(Stream stream, object data, CancellationToken ct)
        {
            var json = McpJsonOptions.Serialize(data);
            var eventStr = $"data: {json}\n\n";
            var bytes = Encoding.UTF8.GetBytes(eventStr);
            await stream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _cts?.Cancel();
                _listener?.Stop();
                ((IDisposable?)_listener)?.Dispose();
                _cts?.Dispose();
                _disposed = true;
            }
        }
    }

    public class HttpListenerMcpOptions
    {
        public int Port { get; set; } = 5050;
        public string McpPath { get; set; } = "/mcp";
        public string[]? AllowedOrigins { get; set; }
    }
}
