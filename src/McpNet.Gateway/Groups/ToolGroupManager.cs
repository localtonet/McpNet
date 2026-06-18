using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using McpNet.Gateway.Abstractions;
using McpNet.Gateway.Models;

namespace McpNet.Gateway.Groups
{
    public class ToolGroupManager
    {
        private readonly IToolGroupRepository _repo;

        public ToolGroupManager(IToolGroupRepository repo)
        {
            _repo = repo;
        }

        public Task<List<ToolGroup>> GetAllGroupsAsync(CancellationToken ct = default)
            => _repo.GetAllAsync(ct);

        public Task<ToolGroup?> GetGroupByIdAsync(Guid id, CancellationToken ct = default)
            => _repo.GetByIdAsync(id, ct);

        public Task<ToolGroup> CreateGroupAsync(string name, string? description = null, CancellationToken ct = default)
            => _repo.AddAsync(new ToolGroup { Name = name, Description = description }, ct);

        public async Task<ToolGroup> AddToolToGroupAsync(Guid groupId, string toolName, CancellationToken ct = default)
        {
            var group = await _repo.GetByIdAsync(groupId, ct).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Group {groupId} not found");
            if (!group.ToolNames.Contains(toolName))
                group.ToolNames.Add(toolName);
            return await _repo.UpdateAsync(group, ct).ConfigureAwait(false);
        }

        public async Task<ToolGroup> RemoveToolFromGroupAsync(Guid groupId, string toolName, CancellationToken ct = default)
        {
            var group = await _repo.GetByIdAsync(groupId, ct).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Group {groupId} not found");
            group.ToolNames.Remove(toolName);
            return await _repo.UpdateAsync(group, ct).ConfigureAwait(false);
        }

        public Task DeleteGroupAsync(Guid id, CancellationToken ct = default)
            => _repo.DeleteAsync(id, ct);

        public async Task<List<string>> GetToolsForClientAsync(McpClient client, CancellationToken ct = default)
        {
            if (client.AllowedGroupIds.Count == 0) return null!;
            var result = new List<string>();
            foreach (var groupId in client.AllowedGroupIds)
            {
                var group = await _repo.GetByIdAsync(groupId, ct).ConfigureAwait(false);
                if (group != null) result.AddRange(group.ToolNames);
            }
            return result.Distinct().ToList();
        }
    }
}
