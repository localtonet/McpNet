using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using McpNet.Gateway.Abstractions;
using McpNet.Gateway.Models;

namespace McpNet.Gateway.Persistence.Repositories
{
    public class ServerRepository : IServerRepository
    {
        private readonly GatewayDbContext _db;
        public ServerRepository(GatewayDbContext db) { _db = db; }

        public Task<List<RegisteredServer>> GetAllAsync(CancellationToken ct) =>
            _db.Servers.AsNoTracking().ToListAsync(ct);

        public Task<RegisteredServer?> GetByIdAsync(Guid id, CancellationToken ct) =>
            _db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);

        public Task<RegisteredServer?> GetByNameAsync(string name, CancellationToken ct) =>
            _db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.Name == name, ct);

        public async Task<RegisteredServer> AddAsync(RegisteredServer server, CancellationToken ct)
        {
            _db.Servers.Add(server);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return server;
        }

        public async Task<RegisteredServer> UpdateAsync(RegisteredServer server, CancellationToken ct)
        {
            _db.Servers.Update(server);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return server;
        }

        public async Task DeleteAsync(Guid id, CancellationToken ct)
        {
            var server = await _db.Servers.FindAsync(new object[] { id }, ct).ConfigureAwait(false);
            if (server != null) { _db.Servers.Remove(server); await _db.SaveChangesAsync(ct).ConfigureAwait(false); }
        }
    }

    public class ToolGroupRepository : IToolGroupRepository
    {
        private readonly GatewayDbContext _db;
        public ToolGroupRepository(GatewayDbContext db) { _db = db; }

        public Task<List<ToolGroup>> GetAllAsync(CancellationToken ct) =>
            _db.ToolGroups.AsNoTracking().ToListAsync(ct);

        public Task<ToolGroup?> GetByIdAsync(Guid id, CancellationToken ct) =>
            _db.ToolGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == id, ct);

        public Task<ToolGroup?> GetByNameAsync(string name, CancellationToken ct) =>
            _db.ToolGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Name == name, ct);

        public async Task<ToolGroup> AddAsync(ToolGroup group, CancellationToken ct)
        {
            _db.ToolGroups.Add(group);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return group;
        }

        public async Task<ToolGroup> UpdateAsync(ToolGroup group, CancellationToken ct)
        {
            _db.ToolGroups.Update(group);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return group;
        }

        public async Task DeleteAsync(Guid id, CancellationToken ct)
        {
            var group = await _db.ToolGroups.FindAsync(new object[] { id }, ct).ConfigureAwait(false);
            if (group != null) { _db.ToolGroups.Remove(group); await _db.SaveChangesAsync(ct).ConfigureAwait(false); }
        }
    }

    public class ClientRepository : IClientRepository
    {
        private readonly GatewayDbContext _db;
        public ClientRepository(GatewayDbContext db) { _db = db; }

        public Task<List<McpClient>> GetAllAsync(CancellationToken ct) =>
            _db.Clients.AsNoTracking().ToListAsync(ct);

        public Task<McpClient?> GetByIdAsync(Guid id, CancellationToken ct) =>
            _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);

        public Task<McpClient?> GetByTokenAsync(string bearerToken, CancellationToken ct) =>
            _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.BearerToken == bearerToken && c.Enabled, ct);

        public async Task<McpClient> AddAsync(McpClient client, CancellationToken ct)
        {
            _db.Clients.Add(client);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return client;
        }

        public async Task<McpClient> UpdateAsync(McpClient client, CancellationToken ct)
        {
            _db.Clients.Update(client);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return client;
        }

        public async Task DeleteAsync(Guid id, CancellationToken ct)
        {
            var client = await _db.Clients.FindAsync(new object[] { id }, ct).ConfigureAwait(false);
            if (client != null) { _db.Clients.Remove(client); await _db.SaveChangesAsync(ct).ConfigureAwait(false); }
        }
    }

    public class AuditLogRepository : IAuditLogRepository
    {
        private readonly GatewayDbContext _db;
        public AuditLogRepository(GatewayDbContext db) { _db = db; }

        public async Task AddAsync(AuditLog log, CancellationToken ct)
        {
            _db.AuditLogs.Add(log);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        public Task<List<AuditLog>> GetRecentAsync(int count, CancellationToken ct) =>
            _db.AuditLogs.AsNoTracking().OrderByDescending(l => l.Timestamp).Take(count).ToListAsync(ct);
    }
}
