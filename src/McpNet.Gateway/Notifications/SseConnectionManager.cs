using System;
using System.Collections.Concurrent;
using System.Threading.Channels;
using McpNet.Core.Protocol;
using McpNet.Core.Serialization;

namespace McpNet.Gateway.Notifications
{
    /// <summary>
    /// Tracks active SSE connections (GET /mcp) and broadcasts MCP server-initiated
    /// notifications (e.g. <c>notifications/tools/list_changed</c>) to every connected client.
    ///
    /// Usage:
    ///   • On SSE connection open: call <see cref="Register"/> → get a <see cref="ChannelReader{T}"/>.
    ///   • SSE loop reads from the channel and writes events to the HTTP response.
    ///   • On connection close: call <see cref="Deregister"/> with the returned ID.
    ///   • After a tool-list change: call <see cref="BroadcastToolsChanged"/>.
    /// </summary>
    public sealed class SseConnectionManager
    {
        private readonly ConcurrentDictionary<Guid, Channel<string>> _channels = new();

        /// <summary>
        /// Registers a new SSE connection.
        /// Returns a unique ID (for deregistration) and a reader the SSE loop should drain.
        /// </summary>
        public (Guid id, ChannelReader<string> reader) Register()
        {
            var id = Guid.NewGuid();
            var ch = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
            _channels[id] = ch;
            return (id, ch.Reader);
        }

        /// <summary>Deregisters the connection and completes its channel.</summary>
        public void Deregister(Guid id)
        {
            if (_channels.TryRemove(id, out var ch))
                ch.Writer.TryComplete();
        }

        /// <summary>Sends <c>notifications/tools/list_changed</c> to all connected clients.</summary>
        public void BroadcastToolsChanged() => Broadcast(McpMethods.ToolsListChanged);

        /// <summary>Sends <c>notifications/prompts/list_changed</c> to all connected clients.</summary>
        public void BroadcastPromptsChanged() => Broadcast(McpMethods.PromptsListChanged);

        /// <summary>Sends <c>notifications/resources/list_changed</c> to all connected clients.</summary>
        public void BroadcastResourcesChanged() => Broadcast(McpMethods.ResourcesListChanged);

        /// <summary>Broadcasts an arbitrary MCP notification to all active SSE connections.</summary>
        public void Broadcast(string method, object? @params = null)
        {
            if (_channels.IsEmpty) return;
            // JSON-RPC notification: no "id" field.
            var notification = McpJsonOptions.Serialize(new JsonRpcRequest { Method = method, Params = @params });
            var sse = $"data: {notification}\n\n";
            foreach (var ch in _channels.Values)
                ch.Writer.TryWrite(sse);
        }

        public int ConnectionCount => _channels.Count;
    }
}
