using System;
using System.Threading;
using System.Threading.Tasks;
using McpNet.Core.Protocol;

namespace McpNet.Core.Transport
{
    public interface IMcpTransport : IDisposable
    {
        Task StartAsync(CancellationToken cancellationToken = default);
        Task StopAsync(CancellationToken cancellationToken = default);
        event EventHandler<JsonRpcRequest>? RequestReceived;
        Task SendResponseAsync(JsonRpcResponse response, string? sessionId = null, CancellationToken cancellationToken = default);
        Task SendNotificationAsync(JsonRpcNotification notification, string? sessionId = null, CancellationToken cancellationToken = default);
    }

    public interface IMcpUpstreamTransport : IDisposable
    {
        Task ConnectAsync(CancellationToken cancellationToken = default);
        Task DisconnectAsync(CancellationToken cancellationToken = default);
        bool IsConnected { get; }
        Task<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken = default);
        Task SendNotificationAsync(JsonRpcNotification notification, CancellationToken cancellationToken = default);
        event EventHandler<JsonRpcNotification>? NotificationReceived;
    }
}
