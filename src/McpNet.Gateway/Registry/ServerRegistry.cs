using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using McpNet.Core.Capabilities;
using McpNet.Gateway.Abstractions;
using McpNet.Gateway.Models;
using McpNet.Gateway.Upstream;

namespace McpNet.Gateway.Registry
{
    public class ServerRegistry : IDisposable
    {
        private readonly IServerRepository _repo;
        private readonly ConcurrentDictionary<Guid, McpUpstreamClient> _clients = new();
        private bool _disposed;

        public ServerRegistry(IServerRepository repo)
        {
            _repo = repo;
        }

        public async Task<List<RegisteredServer>> GetAllServersAsync(CancellationToken ct = default)
            => await _repo.GetAllAsync(ct).ConfigureAwait(false);

        public async Task<RegisteredServer> RegisterServerAsync(RegisteredServer server, CancellationToken ct = default)
        {
            var saved = await _repo.AddAsync(server, ct).ConfigureAwait(false);
            return saved;
        }

        public async Task<RegisteredServer> UpdateServerAsync(RegisteredServer server, CancellationToken ct = default)
        {
            server.UpdatedAt = DateTime.UtcNow;
            if (_clients.TryRemove(server.Id, out var old)) old.Dispose();
            return await _repo.UpdateAsync(server, ct).ConfigureAwait(false);
        }

        public async Task DeleteServerAsync(Guid id, CancellationToken ct = default)
        {
            if (_clients.TryRemove(id, out var old)) old.Dispose();
            await _repo.DeleteAsync(id, ct).ConfigureAwait(false);
        }

        public McpUpstreamClient GetOrCreateClient(RegisteredServer server)
        {
            return _clients.GetOrAdd(server.Id, _ => new McpUpstreamClient(server));
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                foreach (var c in _clients.Values) c.Dispose();
                _clients.Clear();
                _disposed = true;
            }
        }
    }
}
