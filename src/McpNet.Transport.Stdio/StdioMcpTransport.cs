using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using McpNet.Core.Protocol;
using McpNet.Core.Serialization;
using McpNet.Gateway.Routing;
using McpNet.Gateway.Sessions;

namespace McpNet.Transport.Stdio
{
    public class StdioMcpTransport : IDisposable
    {
        private readonly GatewayRequestRouter _router;
        private readonly GatewaySessionManager _sessions;
        private bool _disposed;

        public StdioMcpTransport(GatewayRequestRouter router, GatewaySessionManager sessions)
        {
            _router = router;
            _sessions = sessions;
        }

        public async Task RunAsync(CancellationToken cancellationToken = default)
        {
            // Force stdin/stdout to UTF-8 without BOM
            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

            using var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
            using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
            stdout.AutoFlush = true;

            string? currentSessionId = null;

            while (!cancellationToken.IsCancellationRequested)
            {
                string? line;
                try { line = await stdin.ReadLineAsync().ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (IOException) { break; }

                if (line == null) break; // stdin closed
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonRpcRequest? request;
                try { request = McpJsonOptions.Deserialize<JsonRpcRequest>(line); }
                catch
                {
                    await WriteErrorAsync(stdout, null, JsonRpcErrorCodes.ParseError, "Parse error", cancellationToken);
                    continue;
                }

                if (request == null)
                {
                    await WriteErrorAsync(stdout, null, JsonRpcErrorCodes.InvalidRequest, "Invalid request", cancellationToken);
                    continue;
                }

                // Notifications (no id) → no response
                if (request.Id == null)
                {
                    if (request.Method == McpMethods.Initialized)
                        currentSessionId ??= _sessions.CreateSession();
                    continue;
                }

                try
                {
                    var response = await _router.HandleAsync(request, currentSessionId, null, cancellationToken).ConfigureAwait(false);
                    if (response.SessionId != null)
                        currentSessionId = response.SessionId;

                    var json = McpJsonOptions.Serialize(response);
                    await stdout.WriteLineAsync(json).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await WriteErrorAsync(stdout, request.Id, JsonRpcErrorCodes.InternalError, ex.Message, cancellationToken);
                }
            }
        }

        private static Task WriteErrorAsync(StreamWriter writer, object? id, int code, string message, CancellationToken ct)
        {
            var error = new JsonRpcResponse
            {
                Id = id,
                Error = new JsonRpcError { Code = code, Message = message }
            };
            return writer.WriteLineAsync(McpJsonOptions.Serialize(error));
        }

        public void Dispose()
        {
            if (!_disposed) { _disposed = true; }
        }
    }
}
