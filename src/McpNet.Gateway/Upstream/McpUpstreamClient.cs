using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using McpNet.Core.Capabilities;
using McpNet.Core.Protocol;
using McpNet.Core.Serialization;
using McpNet.Gateway.Models;
using McpNet.Gateway.Upstream.Rest;

namespace McpNet.Gateway.Upstream
{
    public class McpUpstreamClient : IDisposable
    {
        private readonly RegisteredServer _server;
        private readonly HttpClient _http;
        private readonly OAuthTokenProvider? _oauth;
        // REST/OpenAPI upstream: delegate all operations to this connector instead of using the MCP protocol.
        private RestUpstreamConnector? _restConnector;
        // Session ID returned by the server on initialize (Mcp-Session-Id header).
        // Required for stateful StreamableHttp MCP servers on all subsequent requests.
        private string? _sessionId;
        // Serializes all stdio reads/writes so requests don't interleave.
        private readonly SemaphoreSlim _stdioIoLock = new SemaphoreSlim(1, 1);
        // Serializes ConnectAsync so two concurrent callers don't double-initialize.
        private readonly SemaphoreSlim _connectLock = new SemaphoreSlim(1, 1);
        private Process? _stdioProcess;
        private Stream? _stdioIn;
        private Stream? _stdioOut;
        private readonly ConcurrentQueue<string> _stderrTail = new ConcurrentQueue<string>();
        private readonly ConcurrentDictionary<object, TaskCompletionSource<JsonRpcResponse>> _pending = new();
        private int _requestCounter;
        private bool _disposed;

        public RegisteredServer Server => _server;

        // For stdio servers: also verify the child process is still alive.
        // This ensures that if the npx process exits between refreshes,
        // IsConnected returns false and ToolAggregator re-calls ConnectAsync
        // (which re-initializes the new process) instead of sending tools/list
        // to an uninitialized process and hanging until timeout.
        private bool _isConnectedFlag;
        public bool IsConnected
        {
            get
            {
                if (_restConnector != null) return _restConnector.IsConnected;
                if (!_isConnectedFlag) return false;
                if (_server.TransportType == UpstreamTransportType.Stdio)
                    return _stdioProcess != null && !_stdioProcess.HasExited;
                return true;
            }
            private set => _isConnectedFlag = value;
        }

        public McpUpstreamClient(RegisteredServer server, HttpClient? http = null)
        {
            _server = server;
            _http = http ?? new HttpClient();
            _http.Timeout = TimeSpan.FromSeconds(30);

            if (server.TransportType == UpstreamTransportType.RestOpenApi)
            {
                // For REST upstreams we don't use MCP JSON-RPC at all.
                // Auth headers are set on the shared HttpClient that RestUpstreamConnector will use.
                if (!string.IsNullOrEmpty(server.BearerToken))
                    _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", server.BearerToken);
                foreach (var h in server.CustomHeaders)
                    _http.DefaultRequestHeaders.TryAddWithoutValidation(h.Key, h.Value);
                _restConnector = new RestUpstreamConnector(server, _http);
                return;
            }

            if (server.OAuth is { Enabled: true } oauth && !string.IsNullOrEmpty(oauth.TokenUrl))
            {
                _oauth = new OAuthTokenProvider(oauth);
            }
            else if (!string.IsNullOrEmpty(server.BearerToken))
            {
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", server.BearerToken);
            }

            foreach (var h in server.CustomHeaders)
                _http.DefaultRequestHeaders.TryAddWithoutValidation(h.Key, h.Value);
        }

        private async Task ApplyOAuthAsync(CancellationToken ct)
        {
            if (_oauth == null) return;
            var token = await _oauth.GetAccessTokenAsync(ct).ConfigureAwait(false);
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        public async Task<InitializeResult?> ConnectAsync(CancellationToken ct = default)
        {
            // REST upstream: delegate to the dedicated connector.
            if (_restConnector != null)
            {
                await _restConnector.ConnectAsync(ct).ConfigureAwait(false);
                return null;
            }

            // Fast path — already connected.
            if (IsConnected) return null;

            // Serialize: prevent two concurrent callers from both sending initialize
            // to the same stdio process (double-initialize corrupts message stream).
            await _connectLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Double-check inside the lock.
                if (IsConnected) return null;

                var initRequest = new JsonRpcRequest
                {
                    Id = NextId(),
                    Method = McpMethods.Initialize,
                    Params = new InitializeParams
                    {
                        ProtocolVersion = McpProtocolVersion.Current,
                        Capabilities = new ClientCapabilities(),
                        ClientInfo = new McpImplementation { Name = "McpNet.Gateway", Version = "1.0.0" }
                    }
                };

                JsonRpcResponse result;
                if (_server.TransportType == UpstreamTransportType.Stdio)
                    result = await SendRequestStdioAsync(initRequest, ct).ConfigureAwait(false);
                else
                    result = await SendRequestAsync(initRequest, ct).ConfigureAwait(false);

                if (result.Error != null)
                    return null;

                if (_server.TransportType == UpstreamTransportType.Stdio)
                    await SendNotificationStdioAsync(new JsonRpcNotification { Method = McpMethods.Initialized }, ct).ConfigureAwait(false);
                else
                    await SendNotificationAsync(new JsonRpcNotification { Method = McpMethods.Initialized }, ct).ConfigureAwait(false);

                IsConnected = true;
                return McpJsonOptions.Convert<InitializeResult>(result.Result);
            }
            finally
            {
                _connectLock.Release();
            }
        }

        public async Task<List<McpTool>> ListToolsAsync(CancellationToken ct = default)
        {
            if (_restConnector != null)
            {
                if (!_restConnector.IsConnected) await _restConnector.ConnectAsync(ct).ConfigureAwait(false);
                return _restConnector.ListTools();
            }
            var req = new JsonRpcRequest { Id = NextId(), Method = McpMethods.ToolsList };
            var resp = await SendRequestAsync(req, ct).ConfigureAwait(false);
            if (resp.Error != null) return new List<McpTool>();
            var json = JsonSerializer.Serialize(resp.Result, McpJsonOptions.Default);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tools", out var toolsEl)) return new List<McpTool>();
            return JsonSerializer.Deserialize<List<McpTool>>(toolsEl.GetRawText(), McpJsonOptions.Default) ?? new List<McpTool>();
        }

        public async Task<List<McpPrompt>> ListPromptsAsync(CancellationToken ct = default)
        {
            var req = new JsonRpcRequest { Id = NextId(), Method = McpMethods.PromptsList };
            var resp = await SendRequestAsync(req, ct).ConfigureAwait(false);
            if (resp.Error != null) return new List<McpPrompt>();
            var json = JsonSerializer.Serialize(resp.Result, McpJsonOptions.Default);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("prompts", out var el)) return new List<McpPrompt>();
            return JsonSerializer.Deserialize<List<McpPrompt>>(el.GetRawText(), McpJsonOptions.Default) ?? new List<McpPrompt>();
        }

        public async Task<List<McpResource>> ListResourcesAsync(CancellationToken ct = default)
        {
            var req = new JsonRpcRequest { Id = NextId(), Method = McpMethods.ResourcesList };
            var resp = await SendRequestAsync(req, ct).ConfigureAwait(false);
            if (resp.Error != null) return new List<McpResource>();
            var json = JsonSerializer.Serialize(resp.Result, McpJsonOptions.Default);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("resources", out var el)) return new List<McpResource>();
            return JsonSerializer.Deserialize<List<McpResource>>(el.GetRawText(), McpJsonOptions.Default) ?? new List<McpResource>();
        }

        public async Task<ToolCallResult> CallToolAsync(string toolName, Dictionary<string, object?>? arguments, CancellationToken ct = default)
        {
            if (_restConnector != null)
            {
                if (!_restConnector.IsConnected) await _restConnector.ConnectAsync(ct).ConfigureAwait(false);
                return await _restConnector.CallToolAsync(toolName, arguments, ct).ConfigureAwait(false);
            }
            var req = new JsonRpcRequest
            {
                Id = NextId(),
                Method = McpMethods.ToolsCall,
                Params = new ToolCallParams { Name = toolName, Arguments = arguments }
            };
            var resp = await SendRequestAsync(req, ct).ConfigureAwait(false);
            if (resp.Error != null)
                return new ToolCallResult { IsError = true, Content = new List<McpContent> { McpContent.FromText(resp.Error.Message) } };
            return McpJsonOptions.Convert<ToolCallResult>(resp.Result) ?? new ToolCallResult();
        }

        public async Task<PromptGetResult> GetPromptAsync(string promptName, Dictionary<string, string>? arguments, CancellationToken ct = default)
        {
            var req = new JsonRpcRequest
            {
                Id = NextId(),
                Method = McpMethods.PromptsGet,
                Params = new PromptGetParams { Name = promptName, Arguments = arguments }
            };
            var resp = await SendRequestAsync(req, ct).ConfigureAwait(false);
            if (resp.Error != null) return new PromptGetResult();
            return McpJsonOptions.Convert<PromptGetResult>(resp.Result) ?? new PromptGetResult();
        }

        public async Task<ResourceReadResult> ReadResourceAsync(string uri, CancellationToken ct = default)
        {
            var req = new JsonRpcRequest { Id = NextId(), Method = McpMethods.ResourcesRead, Params = new ResourceReadParams { Uri = uri } };
            var resp = await SendRequestAsync(req, ct).ConfigureAwait(false);
            if (resp.Error != null) return new ResourceReadResult();
            return McpJsonOptions.Convert<ResourceReadResult>(resp.Result) ?? new ResourceReadResult();
        }

        private async Task<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, CancellationToken ct)
        {
            if (_server.TransportType == UpstreamTransportType.Stdio)
                return await SendRequestStdioAsync(request, ct).ConfigureAwait(false);

            var response = await SendRequestOnceAsync(request, ct).ConfigureAwait(false);

            // If an OAuth-protected upstream rejected us with 401, the cached token may have
            // been revoked early; refresh it once and retry the request.
            if (_oauth != null && response.Error?.Code == 401)
            {
                _oauth.Invalidate();
                response = await SendRequestOnceAsync(request, ct).ConfigureAwait(false);
            }

            return response;
        }

        private async Task<JsonRpcResponse> SendRequestOnceAsync(JsonRpcRequest request, CancellationToken ct)
        {
            await ApplyOAuthAsync(ct).ConfigureAwait(false);

            var body = McpJsonOptions.Serialize(request);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _server.Url)
            {
                Content = content
            };
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            httpRequest.Headers.TryAddWithoutValidation(McpHeaders.ProtocolVersion, McpProtocolVersion.Current);
            // Include the session ID on all requests after the initial handshake.
            if (_sessionId != null)
                httpRequest.Headers.TryAddWithoutValidation(McpHeaders.SessionId, _sessionId);

            using var httpResponse = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

            // Capture the session ID returned by the server on the initialize response.
            if (_sessionId == null && httpResponse.Headers.TryGetValues(McpHeaders.SessionId, out var vals))
                _sessionId = System.Linq.Enumerable.FirstOrDefault(vals);

            if (!httpResponse.IsSuccessStatusCode)
                return ErrorResponse(request.Id, (int)httpResponse.StatusCode, $"HTTP {httpResponse.StatusCode}");

            var contentType = httpResponse.Content.Headers.ContentType?.MediaType ?? string.Empty;

            if (contentType.Contains("text/event-stream"))
            {
                return await ParseSseResponseAsync(request.Id, httpResponse, ct).ConfigureAwait(false);
            }

            var responseBody = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JsonSerializer.Deserialize<JsonRpcResponse>(responseBody, McpJsonOptions.Default)
                ?? ErrorResponse(request.Id, -32700, "Invalid response");
        }

        private async Task<JsonRpcResponse> ParseSseResponseAsync(object? requestId, HttpResponseMessage httpResponse, CancellationToken ct)
        {
            var stream = await httpResponse.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var reader = new System.IO.StreamReader(stream, Encoding.UTF8);
            var dataParts = new StringBuilder();
            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null) break;
                if (line.StartsWith("data: "))
                {
                    dataParts.Append(line.Substring(6));
                }
                else if (line == string.Empty && dataParts.Length > 0)
                {
                    var data = dataParts.ToString();
                    dataParts.Clear();
                    try
                    {
                        var msg = JsonSerializer.Deserialize<JsonRpcResponse>(data, McpJsonOptions.Default);
                        if (msg?.Id != null) return msg;
                    }
                    catch { }
                }
            }
            return ErrorResponse(requestId, -32603, "No response in SSE stream");
        }

        private async Task SendNotificationAsync(JsonRpcNotification notification, CancellationToken ct)
        {
            if (_server.TransportType == UpstreamTransportType.Stdio)
            {
                await SendNotificationStdioAsync(notification, ct).ConfigureAwait(false);
                return;
            }

            await ApplyOAuthAsync(ct).ConfigureAwait(false);

            var body = McpJsonOptions.Serialize(notification);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, _server.Url) { Content = content };
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Headers.TryAddWithoutValidation(McpHeaders.ProtocolVersion, McpProtocolVersion.Current);
            if (_sessionId != null)
                req.Headers.TryAddWithoutValidation(McpHeaders.SessionId, _sessionId);
            await _http.SendAsync(req, ct).ConfigureAwait(false);
        }

        private async Task<JsonRpcResponse> SendRequestStdioAsync(JsonRpcRequest request, CancellationToken ct)
        {
            await EnsureStdioProcessStartedAsync(ct).ConfigureAwait(false);

            // IMPORTANT: WaitAsync must be inside the try so the finally-Release only
            // executes when we actually acquired the lock. If WaitAsync were outside the
            // try and threw (e.g. ct already cancelled → TaskCanceledException), the
            // finally would Release a lock we never held, corrupting the SemaphoreSlim.
            bool acquired = false;
            try
            {
                await _stdioIoLock.WaitAsync(ct).ConfigureAwait(false);
                acquired = true;

                var body = McpJsonOptions.Serialize(request);
                await WriteStdioMessageAsync(body, ct).ConfigureAwait(false);

                while (!ct.IsCancellationRequested)
                {
                    var raw = await ReadStdioMessageAsync(ct).ConfigureAwait(false);
                    if (raw is null)
                        return ErrorResponse(request.Id, -32603, "Stdio process closed the stream." + BuildStderrTailSuffix());

                    JsonRpcResponse? msg = null;
                    try { msg = JsonSerializer.Deserialize<JsonRpcResponse>(raw, McpJsonOptions.Default); }
                    catch { }

                    if (msg?.Id != null && JsonRpcIdsEqual(msg.Id, request.Id))
                        return msg;
                }

                return ErrorResponse(request.Id, -32603, "Stdio request timed out.");
            }
            catch (OperationCanceledException)
            {
                return ErrorResponse(request.Id, -32603, "Stdio request timed out (server did not respond within the allotted time)." + BuildStderrTailSuffix());
            }
            catch (Exception ex)
            {
                return ErrorResponse(request.Id, -32603, "Stdio transport error: " + ex.Message + BuildStderrTailSuffix());
            }
            finally
            {
                if (acquired) _stdioIoLock.Release();
            }
        }

        private async Task SendNotificationStdioAsync(JsonRpcNotification notification, CancellationToken ct)
        {
            await EnsureStdioProcessStartedAsync(ct).ConfigureAwait(false);

            bool acquired = false;
            try
            {
                await _stdioIoLock.WaitAsync(ct).ConfigureAwait(false);
                acquired = true;

                var body = McpJsonOptions.Serialize(notification);
                await WriteStdioMessageAsync(body, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* notification is fire-and-forget; ignore timeout */ }
            finally
            {
                if (acquired) _stdioIoLock.Release();
            }
        }

        private async Task EnsureStdioProcessStartedAsync(CancellationToken ct)
        {
            // Fast path — process is already running.
            if (_stdioProcess != null && !_stdioProcess.HasExited && _stdioIn != null && _stdioOut != null)
                return;

            // ConnectAsync is already holding _connectLock when this is called, so
            // concurrent process startup is already serialized. No extra lock needed here.

            if (string.IsNullOrWhiteSpace(_server.StdioCommand))
                throw new InvalidOperationException("Stdio command is not configured.");

            var resolvedCommand = StdioCommandHelper.ResolveCommandPath(_server.StdioCommand);
            var commandExt = Path.GetExtension(resolvedCommand);
            var runViaCmd = OperatingSystem.IsWindows() &&
                            (string.Equals(commandExt, ".cmd", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(commandExt, ".bat", StringComparison.OrdinalIgnoreCase));

            var psi = new ProcessStartInfo
            {
                FileName = runViaCmd ? "cmd.exe" : resolvedCommand,
                Arguments = runViaCmd
                    ? BuildCmdWrapperArguments(resolvedCommand, _server.StdioArgs)
                    : BuildArguments(_server.StdioArgs),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardInputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            if (!string.IsNullOrWhiteSpace(_server.StdioWorkingDirectory))
                psi.WorkingDirectory = _server.StdioWorkingDirectory;

            // Inject server-specific environment variables (e.g. API keys).
            // These are merged on top of the current process environment so the child
            // inherits PATH, TEMP, etc., but gets the extra secrets it needs.
            if (_server.StdioEnvVars != null && _server.StdioEnvVars.Count > 0)
            {
                foreach (var kv in _server.StdioEnvVars)
                    psi.Environment[kv.Key] = kv.Value;
            }

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!proc.Start())
                throw new InvalidOperationException("Failed to start stdio process.");

            _stdioProcess = proc;
            _stdioIn = proc.StandardInput.BaseStream;
            _stdioOut = proc.StandardOutput.BaseStream;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!proc.HasExited)
                    {
                        var line = await proc.StandardError.ReadLineAsync().ConfigureAwait(false);
                        if (line == null) break;
                        _stderrTail.Enqueue(line);
                        while (_stderrTail.Count > 20 && _stderrTail.TryDequeue(out _)) { }
                    }
                }
                catch { }
            }, ct);
        }

        // MCP spec (2025-03-26): stdio transport uses newline-delimited JSON (NDJSON).
        // Each message is a single JSON line terminated with \n; no Content-Length headers.
        private async Task WriteStdioMessageAsync(string message, CancellationToken ct)
        {
            if (_stdioIn is null)
                throw new InvalidOperationException("Stdio input stream is not available.");

            var payload = Encoding.UTF8.GetBytes(message + "\n");
            await _stdioIn.WriteAsync(payload, 0, payload.Length, ct).ConfigureAwait(false);
            await _stdioIn.FlushAsync(ct).ConfigureAwait(false);
        }

        private async Task<string?> ReadStdioMessageAsync(CancellationToken ct)
        {
            if (_stdioOut is null)
                throw new InvalidOperationException("Stdio output stream is not available.");

            // Read one NDJSON line. Skip blank lines — the server may emit a bare \n
            // as a keep-alive or separator; treating them as EOF broke tool discovery.
            // Only return null on true EOF (read == 0) or cancellation.
            var bytes = new List<byte>(4096);
            var one = new byte[1];

            while (true)
            {
                bytes.Clear();

                // Read bytes until \n
                while (true)
                {
                    int read;
                    try { read = await _stdioOut.ReadAsync(one, 0, 1, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return null; }

                    if (read == 0) return null; // true EOF — stream closed

                    var b = one[0];
                    if (b == (byte)'\n') break;
                    bytes.Add(b);
                }

                // Strip trailing \r (Windows CRLF)
                if (bytes.Count > 0 && bytes[bytes.Count - 1] == (byte)'\r')
                    bytes.RemoveAt(bytes.Count - 1);

                if (bytes.Count == 0) continue; // blank line — skip and read next

                return Encoding.UTF8.GetString(bytes.ToArray());
            }
        }

        private static bool JsonRpcIdsEqual(object? left, object? right)
        {
            if (left == null || right == null) return false;
            return string.Equals(
                Convert.ToString(left, CultureInfo.InvariantCulture),
                Convert.ToString(right, CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
        }

        private static string BuildArguments(IEnumerable<string> args)
            => string.Join(" ", (args ?? Enumerable.Empty<string>()).Select(EscapeArgument));

        private static string EscapeArgument(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            if (!value.Any(ch => char.IsWhiteSpace(ch) || ch == '"')) return value;
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static string BuildCmdWrapperArguments(string commandPath, IEnumerable<string> args)
            => "/c " + EscapeArgument(commandPath) + (args != null && args.Any() ? " " + BuildArguments(args) : string.Empty);

        private static string ResolveCommandPath(string command)
            => StdioCommandHelper.ResolveCommandPath(command);

        private string BuildStderrTailSuffix()
        {
            if (_stderrTail.IsEmpty) return string.Empty;
            var lines = _stderrTail.ToArray();
            return " stderr: " + string.Join(" | ", lines);
        }

        private static JsonRpcResponse ErrorResponse(object? id, int code, string message) =>
            new JsonRpcResponse { Id = id, Error = new JsonRpcError { Code = code, Message = message } };

        private int NextId() => System.Threading.Interlocked.Increment(ref _requestCounter);

        public void Dispose()
        {
            if (_disposed) return;

            try { _stdioIn?.Dispose(); } catch { }
            try { _stdioOut?.Dispose(); } catch { }

            try
            {
                if (_stdioProcess != null)
                {
                    if (!_stdioProcess.HasExited)
                        _stdioProcess.Kill(entireProcessTree: true);
                    _stdioProcess.Dispose();
                }
            }
            catch { }

            _http.Dispose();
            _stdioIoLock.Dispose();
            _connectLock.Dispose();
            _disposed = true;
        }
    }
}
