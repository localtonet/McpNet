using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using McpNet.Core.Capabilities;
using McpNet.Core.Serialization;
using McpNet.Gateway.Models;

namespace McpNet.Gateway.Upstream.Rest
{
    /// <summary>
    /// Virtualizes a REST API (described by an OpenAPI / Swagger document) as a set of MCP tools.
    /// Fetches and parses the document on <see cref="ConnectAsync"/>, exposes the generated tools
    /// via <see cref="ListTools"/>, and translates each <see cref="CallToolAsync"/> into the
    /// corresponding HTTP request.
    /// </summary>
    public sealed class RestUpstreamConnector
    {
        private readonly RegisteredServer _server;
        private readonly RestApiConfig _config;
        private readonly HttpClient _http;
        private readonly Func<CancellationToken, Task>? _applyAuth;

        private readonly Dictionary<string, RestOperation> _operations =
            new Dictionary<string, RestOperation>(StringComparer.OrdinalIgnoreCase);
        private List<McpTool> _tools = new List<McpTool>();
        private string? _baseUrl;

        public bool IsConnected { get; private set; }

        public RestUpstreamConnector(RegisteredServer server, HttpClient http, Func<CancellationToken, Task>? applyAuth = null)
        {
            _server = server ?? throw new ArgumentNullException(nameof(server));
            _config = server.Rest ?? throw new InvalidOperationException(
                $"Server '{server.Name}' is a REST upstream but has no Rest configuration.");
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _applyAuth = applyAuth;
        }

        /// <summary>
        /// Loads the OpenAPI document (from <see cref="RestApiConfig.SpecUrl"/> or
        /// <see cref="RestApiConfig.InlineSpec"/>), parses it, and builds the tool catalogue.
        /// </summary>
        public async Task ConnectAsync(CancellationToken ct = default)
        {
            string documentJson;
            Uri? specUri = null;

            if (!string.IsNullOrWhiteSpace(_config.InlineSpec))
            {
                documentJson = _config.InlineSpec!;
            }
            else if (!string.IsNullOrWhiteSpace(_config.SpecUrl))
            {
                if (!Uri.TryCreate(_config.SpecUrl, UriKind.Absolute, out specUri))
                    throw new InvalidOperationException($"Invalid OpenAPI SpecUrl: '{_config.SpecUrl}'.");

                await GuardAgainstSsrfAsync(specUri, ct).ConfigureAwait(false);
                if (_applyAuth != null) await _applyAuth(ct).ConfigureAwait(false);

                using var resp = await _http.GetAsync(specUri, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException(
                        $"Failed to fetch OpenAPI document ({(int)resp.StatusCode} {resp.StatusCode}) from {specUri}.");
                documentJson = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            else
            {
                throw new InvalidOperationException(
                    "REST upstream requires either Rest.SpecUrl or Rest.InlineSpec.");
            }

            var model = OpenApiSpecParser.Parse(documentJson, _config, specUri);

            _baseUrl = (model.BaseUrl ?? _config.BaseUrl)?.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(_baseUrl))
                throw new InvalidOperationException(
                    "Could not determine the API base URL. Set Rest.BaseUrl explicitly.");

            _operations.Clear();
            foreach (var op in model.Operations)
                _operations[op.ToolName] = op;

            _tools = RestToolBuilder.BuildTools(model.Operations);
            IsConnected = true;
        }

        public List<McpTool> ListTools() => _tools;

        /// <summary>The resolved API base URL once <see cref="ConnectAsync"/> has run.</summary>
        public string? BaseUrl => _baseUrl;

        /// <summary>The operations discovered (and kept after filtering) from the OpenAPI document.</summary>
        public IReadOnlyCollection<RestOperation> Operations => _operations.Values;

        /// <summary>
        /// Loads and parses an OpenAPI document for a given <see cref="RestApiConfig"/> without
        /// registering a server. Used by the dashboard to preview the operations a REST upstream
        /// would expose so the operator can choose which ones to include.
        /// </summary>
        public static async Task<RestUpstreamConnector> PreviewAsync(
            RestApiConfig config, HttpClient http, CancellationToken ct = default)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            var temp = new RegisteredServer { Name = "preview", TransportType = UpstreamTransportType.RestOpenApi, Rest = config };
            var connector = new RestUpstreamConnector(temp, http);
            await connector.ConnectAsync(ct).ConfigureAwait(false);
            return connector;
        }

        /// <summary>
        /// Executes the HTTP request corresponding to <paramref name="toolName"/> using the supplied
        /// arguments, and returns the response as an MCP tool result.
        /// </summary>
        public async Task<ToolCallResult> CallToolAsync(
            string toolName, Dictionary<string, object?>? arguments, CancellationToken ct = default)
        {
            if (!IsConnected) await ConnectAsync(ct).ConfigureAwait(false);

            if (!_operations.TryGetValue(toolName, out var op))
                return Error($"Unknown REST operation '{toolName}'.");

            var args = arguments ?? new Dictionary<string, object?>();

            HttpRequestMessage request;
            try
            {
                request = BuildRequest(op, args);
            }
            catch (Exception ex)
            {
                return Error($"Failed to build request: {ex.Message}");
            }

            try
            {
                await GuardAgainstSsrfAsync(request.RequestUri!, ct).ConfigureAwait(false);
                if (_applyAuth != null) await _applyAuth(ct).ConfigureAwait(false);

                using (request)
                using (var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
                {
                    var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    var text = FormatResponseBody(resp, body);

                    return new ToolCallResult
                    {
                        IsError = !resp.IsSuccessStatusCode,
                        Content = new List<McpContent> { McpContent.FromText(text) }
                    };
                }
            }
            catch (OperationCanceledException)
            {
                return Error("REST request timed out.");
            }
            catch (Exception ex)
            {
                return Error($"REST request failed: {ex.Message}");
            }
        }

        // ── Request construction ──────────────────────────────────────────────

        private HttpRequestMessage BuildRequest(RestOperation op, Dictionary<string, object?> args)
        {
            // 1. Path: substitute {name} placeholders with url-encoded path arguments.
            var path = op.PathTemplate;
            foreach (var p in op.Parameters.Where(p => p.Location == RestParameterLocation.Path))
            {
                if (!TryGetArg(args, p.Name, out var value) || value == null)
                {
                    if (p.Required) throw new InvalidOperationException($"Missing required path parameter '{p.Name}'.");
                    continue;
                }
                path = path.Replace(
                    "{" + p.Name + "}",
                    Uri.EscapeDataString(ToInvariantString(value)),
                    StringComparison.Ordinal);
            }

            // 2. Query string.
            var query = new List<string>();
            foreach (var p in op.Parameters.Where(p => p.Location == RestParameterLocation.Query))
            {
                if (!TryGetArg(args, p.Name, out var value) || value == null) continue;
                query.Add($"{Uri.EscapeDataString(p.Name)}={Uri.EscapeDataString(ToInvariantString(value))}");
            }

            var url = _baseUrl + path;
            if (query.Count > 0)
                url += (url.Contains('?') ? "&" : "?") + string.Join("&", query);

            var request = new HttpRequestMessage(new HttpMethod(op.Method), url);

            // 3. Header parameters.
            foreach (var p in op.Parameters.Where(p => p.Location == RestParameterLocation.Header))
            {
                if (!TryGetArg(args, p.Name, out var value) || value == null) continue;
                request.Headers.TryAddWithoutValidation(p.Name, ToInvariantString(value));
            }

            // 4. Request body.
            if (op.HasBody)
            {
                string json;
                if (op.RawBodyArgName != null && TryGetArg(args, op.RawBodyArgName, out var raw))
                {
                    json = ToJson(raw);
                }
                else
                {
                    var bodyObject = new Dictionary<string, object?>();
                    foreach (var p in op.Parameters.Where(p => p.Location == RestParameterLocation.Body))
                        if (TryGetArg(args, p.Name, out var value))
                            bodyObject[p.Name] = value;
                    json = JsonSerializer.Serialize(bodyObject, McpJsonOptions.Default);
                }

                var mediaType = string.IsNullOrWhiteSpace(op.BodyContentType) ? "application/json" : op.BodyContentType;
                request.Content = new StringContent(json, Encoding.UTF8, mediaType);
            }

            return request;
        }

        private static string FormatResponseBody(HttpResponseMessage resp, string body)
        {
            if (resp.IsSuccessStatusCode)
                return string.IsNullOrEmpty(body) ? $"HTTP {(int)resp.StatusCode} {resp.StatusCode} (empty body)" : body;

            var prefix = $"HTTP {(int)resp.StatusCode} {resp.StatusCode}";
            return string.IsNullOrEmpty(body) ? prefix : $"{prefix}: {body}";
        }

        // ── SSRF guard ────────────────────────────────────────────────────────

        private async Task GuardAgainstSsrfAsync(Uri uri, CancellationToken ct)
        {
            if (_config.AllowPrivateNetwork) return;

            IPAddress[] addresses;
            if (IPAddress.TryParse(uri.Host, out var literal))
            {
                addresses = new[] { literal };
            }
            else
            {
                try { addresses = await Dns.GetHostAddressesAsync(uri.Host, ct).ConfigureAwait(false); }
                catch { throw new InvalidOperationException($"Could not resolve host '{uri.Host}'."); }
            }

            if (addresses.Any(IsPrivateOrLoopback))
                throw new InvalidOperationException(
                    $"Refusing to call private/loopback address '{uri.Host}' (set Rest.AllowPrivateNetwork=true to allow).");
        }

        private static bool IsPrivateOrLoopback(IPAddress ip)
        {
            if (IPAddress.IsLoopback(ip)) return true;

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                return b[0] == 10                                   // 10.0.0.0/8
                    || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)    // 172.16.0.0/12
                    || (b[0] == 192 && b[1] == 168)                 // 192.168.0.0/16
                    || (b[0] == 169 && b[1] == 254)                 // 169.254.0.0/16 link-local
                    || b[0] == 127;                                 // 127.0.0.0/8
            }

            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal ||
                       ip.Equals(IPAddress.IPv6Loopback) ||
                       (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;     // fc00::/7 unique-local

            return false;
        }

        // ── Argument helpers ──────────────────────────────────────────────────

        private static bool TryGetArg(Dictionary<string, object?> args, string name, out object? value)
        {
            if (args.TryGetValue(name, out value)) return true;
            // Tolerate case-insensitive argument names from lenient clients.
            foreach (var kv in args)
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = kv.Value;
                    return true;
                }
            value = null;
            return false;
        }

        private static string ToInvariantString(object? value)
        {
            if (value == null) return string.Empty;
            if (value is JsonElement je)
            {
                return je.ValueKind switch
                {
                    JsonValueKind.String => je.GetString() ?? string.Empty,
                    JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => je.GetRawText()
                };
            }
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private static string ToJson(object? value)
        {
            if (value is JsonElement je) return je.GetRawText();
            return JsonSerializer.Serialize(value, McpJsonOptions.Default);
        }

        private static ToolCallResult Error(string message) => new ToolCallResult
        {
            IsError = true,
            Content = new List<McpContent> { McpContent.FromText(message) }
        };
    }
}
