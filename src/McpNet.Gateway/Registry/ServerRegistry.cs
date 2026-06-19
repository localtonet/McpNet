using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
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
        /// <summary>
        /// Optional factory that creates a managed <see cref="HttpClient"/> for each new upstream
        /// client. When null, <see cref="McpUpstreamClient"/> creates its own <c>new HttpClient()</c>.
        /// Supply via <see cref="System.Net.Http.IHttpClientFactory.CreateClient"/> in the host.
        /// </summary>
        private readonly Func<HttpClient>? _httpFactory;
        private readonly ConcurrentDictionary<Guid, McpUpstreamClient> _clients = new();
        private readonly object _createLock = new();
        private bool _disposed;

        public ServerRegistry(IServerRepository repo, Func<HttpClient>? httpFactory = null)
        {
            _repo = repo;
            _httpFactory = httpFactory;
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
            // Fast path — already exists.
            if (_clients.TryGetValue(server.Id, out var existing))
                return existing;

            // Slow path — serialize creation to prevent duplicate stdio processes.
            // ConcurrentDictionary.GetOrAdd can invoke the factory on multiple threads
            // simultaneously; for stdio servers this would start two child processes where
            // only one would ever be used or disposed.
            lock (_createLock)
            {
                if (_clients.TryGetValue(server.Id, out existing))
                    return existing;

                var client = new McpUpstreamClient(server, _httpFactory?.Invoke());
                _clients[server.Id] = client;
                return client;
            }
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
