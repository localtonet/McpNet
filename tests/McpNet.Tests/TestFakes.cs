using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using McpNet.Gateway.Abstractions;
using McpNet.Gateway.Models;

namespace McpNet.Tests
{
    // ─── In-memory repositories for unit tests ───────────────────────────────
    internal sealed class InMemoryServerRepository : IServerRepository
    {
        private readonly List<RegisteredServer> _items = new();

        public Task<List<RegisteredServer>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult(_items.ToList());

        public Task<RegisteredServer?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(_items.FirstOrDefault(s => s.Id == id));

        public Task<RegisteredServer?> GetByNameAsync(string name, CancellationToken ct = default)
            => Task.FromResult(_items.FirstOrDefault(s => s.Name == name));

        public Task<RegisteredServer> AddAsync(RegisteredServer server, CancellationToken ct = default)
        {
            _items.Add(server);
            return Task.FromResult(server);
        }

        public Task<RegisteredServer> UpdateAsync(RegisteredServer server, CancellationToken ct = default)
        {
            var idx = _items.FindIndex(s => s.Id == server.Id);
            if (idx >= 0) _items[idx] = server;
            return Task.FromResult(server);
        }

        public Task DeleteAsync(Guid id, CancellationToken ct = default)
        {
            _items.RemoveAll(s => s.Id == id);
            return Task.CompletedTask;
        }
    }

    internal sealed class InMemoryToolGroupRepository : IToolGroupRepository
    {
        private readonly List<ToolGroup> _items = new();

        public Task<List<ToolGroup>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult(_items.ToList());

        public Task<ToolGroup?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(_items.FirstOrDefault(g => g.Id == id));

        public Task<ToolGroup?> GetByNameAsync(string name, CancellationToken ct = default)
            => Task.FromResult(_items.FirstOrDefault(g => g.Name == name));

        public Task<ToolGroup> AddAsync(ToolGroup group, CancellationToken ct = default)
        {
            _items.Add(group);
            return Task.FromResult(group);
        }

        public Task<ToolGroup> UpdateAsync(ToolGroup group, CancellationToken ct = default)
        {
            var idx = _items.FindIndex(g => g.Id == group.Id);
            if (idx >= 0) _items[idx] = group;
            return Task.FromResult(group);
        }

        public Task DeleteAsync(Guid id, CancellationToken ct = default)
        {
            _items.RemoveAll(g => g.Id == id);
            return Task.CompletedTask;
        }
    }

    // ─── Stub HttpMessageHandler — returns scripted responses ─────────────────
    internal sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public int CallCount { get; private set; }

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_responder(request));
        }
    }
}
