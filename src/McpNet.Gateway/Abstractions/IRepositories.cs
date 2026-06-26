using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using McpNet.Gateway.Models;

namespace McpNet.Gateway.Abstractions
{
    public interface IServerRepository
    {
        Task<List<RegisteredServer>> GetAllAsync(CancellationToken ct = default);
        Task<RegisteredServer?> GetByIdAsync(Guid id, CancellationToken ct = default);
        Task<RegisteredServer?> GetByNameAsync(string name, CancellationToken ct = default);
        Task<RegisteredServer> AddAsync(RegisteredServer server, CancellationToken ct = default);
        Task<RegisteredServer> UpdateAsync(RegisteredServer server, CancellationToken ct = default);
        Task DeleteAsync(Guid id, CancellationToken ct = default);
    }

    public interface IToolGroupRepository
    {
        Task<List<ToolGroup>> GetAllAsync(CancellationToken ct = default);
        Task<ToolGroup?> GetByIdAsync(Guid id, CancellationToken ct = default);
        Task<ToolGroup?> GetByNameAsync(string name, CancellationToken ct = default);
        Task<ToolGroup> AddAsync(ToolGroup group, CancellationToken ct = default);
        Task<ToolGroup> UpdateAsync(ToolGroup group, CancellationToken ct = default);
        Task DeleteAsync(Guid id, CancellationToken ct = default);
    }

    public interface IClientRepository
    {
        Task<List<McpClient>> GetAllAsync(CancellationToken ct = default);
        Task<McpClient?> GetByIdAsync(Guid id, CancellationToken ct = default);
        Task<McpClient?> GetByTokenAsync(string bearerToken, CancellationToken ct = default);
        Task<McpClient> AddAsync(McpClient client, CancellationToken ct = default);
        Task<McpClient> UpdateAsync(McpClient client, CancellationToken ct = default);
        Task DeleteAsync(Guid id, CancellationToken ct = default);
    }

    public interface IAuditLogRepository
    {
        Task AddAsync(AuditLog log, CancellationToken ct = default);
        Task<List<AuditLog>> GetRecentAsync(int count = 100, CancellationToken ct = default);
    }
}
