using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using McpNet.Core.Serialization;
using McpNet.Gateway.Abstractions;
using McpNet.Gateway.Models;
using McpNet.Gateway.Security;

namespace McpNet.Gateway.Persistence.Json
{
    // ─── Base ────────────────────────────────────────────────────────────────
    public abstract class JsonFileStore<T>
    {
        private readonly string _filePath;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        protected JsonFileStore(string filePath)
        {
            _filePath = filePath;
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        /// <summary>Called after loading each item from disk. Override to decrypt secrets.</summary>
        protected virtual T OnLoad(T item) => item;

        /// <summary>Called before saving each item to disk. Override to encrypt secrets.
        /// Must return a COPY — do not mutate the in-memory item.</summary>
        protected virtual T OnSave(T item) => item;

        protected async Task<List<T>> LoadAsync(CancellationToken ct)
        {
            if (!File.Exists(_filePath)) return new List<T>();
            var json = await File.ReadAllTextAsync(_filePath, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return new List<T>();
            var items = McpJsonOptions.Deserialize<List<T>>(json) ?? new List<T>();
            // Decrypt secrets on load
            for (int i = 0; i < items.Count; i++)
                items[i] = OnLoad(items[i]);
            return items;
        }

        protected async Task SaveAsync(List<T> items, CancellationToken ct)
        {
            // Encrypt secrets on save (clone — never mutate the live in-memory list)
            var toSave = new List<T>(items.Count);
            foreach (var item in items)
                toSave.Add(OnSave(item));

            var json = McpJsonOptions.Serialize(toSave);
            // Write to temp then rename for atomicity
            var tmp = _filePath + ".tmp";
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
            File.Move(tmp, _filePath, overwrite: true);
        }

        protected async Task<List<T>> MutateAsync(Func<List<T>, bool> mutation, CancellationToken ct)
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var items = await LoadAsync(ct).ConfigureAwait(false);
                if (mutation(items))
                    await SaveAsync(items, ct).ConfigureAwait(false);
                return items;
            }
            finally { _lock.Release(); }
        }
    }

    // ─── Server Repository ────────────────────────────────────────────────────
    public class JsonServerRepository : JsonFileStore<RegisteredServer>, IServerRepository
    {
        private readonly ISecretProtector _protector;

        public JsonServerRepository(string dataDirectory, ISecretProtector? protector = null)
            : base(Path.Combine(dataDirectory, "servers.json"))
        {
            _protector = protector ?? NullSecretProtector.Instance;
        }

        /// <summary>Decrypt BearerToken + OAuth.ClientSecret after loading from disk.</summary>
        protected override RegisteredServer OnLoad(RegisteredServer s)
        {
            var copy = CloneServer(s);
            copy.BearerToken = _protector.Unprotect(s.BearerToken) ?? string.Empty;
            if (copy.OAuth != null)
                copy.OAuth.ClientSecret = _protector.Unprotect(s.OAuth?.ClientSecret) ?? string.Empty;
            return copy;
        }

        protected override RegisteredServer OnSave(RegisteredServer s)
        {
            var copy = CloneServer(s);
            copy.BearerToken = _protector.Protect(s.BearerToken) ?? string.Empty;
            if (copy.OAuth != null)
                copy.OAuth.ClientSecret = _protector.Protect(s.OAuth?.ClientSecret) ?? string.Empty;
            return copy;
        }

        private static RegisteredServer CloneServer(RegisteredServer s) => new RegisteredServer
        {
            Id                   = s.Id,
            Name                 = s.Name,
            Url                  = s.Url,
            TransportType        = s.TransportType,
            Enabled              = s.Enabled,
            BearerToken          = s.BearerToken,
            CustomHeaders        = s.CustomHeaders,
            StdioCommand         = s.StdioCommand,
            StdioArgs            = s.StdioArgs,
            StdioWorkingDirectory = s.StdioWorkingDirectory,
            StdioEnvVars         = s.StdioEnvVars,
            OAuth                = s.OAuth == null ? null : new OAuthConfig
            {
                Enabled      = s.OAuth.Enabled,
                TokenUrl     = s.OAuth.TokenUrl,
                ClientId     = s.OAuth.ClientId,
                ClientSecret = s.OAuth.ClientSecret,
                Scopes       = s.OAuth.Scopes
            },
            CreatedAt            = s.CreatedAt,
            UpdatedAt            = s.UpdatedAt
        };

        public async Task<List<RegisteredServer>> GetAllAsync(CancellationToken ct = default)
        {
            await Task.CompletedTask;
            return await MutateAsync(items => false, ct);
        }

        public async Task<RegisteredServer?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => (await GetAllAsync(ct)).FirstOrDefault(s => s.Id == id);

        public async Task<RegisteredServer?> GetByNameAsync(string name, CancellationToken ct = default)
            => (await GetAllAsync(ct)).FirstOrDefault(s => s.Name == name);

        public async Task<RegisteredServer> AddAsync(RegisteredServer server, CancellationToken ct = default)
        {
            await MutateAsync(items => { items.Add(server); return true; }, ct);
            return server;
        }

        public async Task<RegisteredServer> UpdateAsync(RegisteredServer server, CancellationToken ct = default)
        {
            server.UpdatedAt = DateTime.UtcNow;
            await MutateAsync(items =>
            {
                var idx = items.FindIndex(s => s.Id == server.Id);
                if (idx >= 0) items[idx] = server;
                return idx >= 0;
            }, ct);
            return server;
        }

        public async Task DeleteAsync(Guid id, CancellationToken ct = default)
            => await MutateAsync(items => items.RemoveAll(s => s.Id == id) > 0, ct);
    }

    // ─── ToolGroup Repository ─────────────────────────────────────────────────
    public class JsonToolGroupRepository : JsonFileStore<ToolGroup>, IToolGroupRepository
    {
        public JsonToolGroupRepository(string dataDirectory)
            : base(Path.Combine(dataDirectory, "groups.json")) { }

        public async Task<List<ToolGroup>> GetAllAsync(CancellationToken ct = default)
            => await MutateAsync(items => false, ct);

        public async Task<ToolGroup?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => (await GetAllAsync(ct)).FirstOrDefault(g => g.Id == id);

        public async Task<ToolGroup?> GetByNameAsync(string name, CancellationToken ct = default)
            => (await GetAllAsync(ct)).FirstOrDefault(g => g.Name == name);

        public async Task<ToolGroup> AddAsync(ToolGroup group, CancellationToken ct = default)
        {
            await MutateAsync(items => { items.Add(group); return true; }, ct);
            return group;
        }

        public async Task<ToolGroup> UpdateAsync(ToolGroup group, CancellationToken ct = default)
        {
            await MutateAsync(items =>
            {
                var idx = items.FindIndex(g => g.Id == group.Id);
                if (idx >= 0) items[idx] = group;
                return idx >= 0;
            }, ct);
            return group;
        }

        public async Task DeleteAsync(Guid id, CancellationToken ct = default)
            => await MutateAsync(items => items.RemoveAll(g => g.Id == id) > 0, ct);
    }

    // ─── Client Repository ────────────────────────────────────────────────────
    public class JsonClientRepository : JsonFileStore<McpClient>, IClientRepository
    {
        private readonly ISecretProtector _protector;

        public JsonClientRepository(string dataDirectory, ISecretProtector? protector = null)
            : base(Path.Combine(dataDirectory, "clients.json"))
        {
            _protector = protector ?? NullSecretProtector.Instance;
        }

        /// <summary>Decrypt BearerToken after loading from disk.</summary>
        protected override McpClient OnLoad(McpClient c)
        {
            var copy = CloneClient(c);
            copy.BearerToken = _protector.Unprotect(c.BearerToken) ?? string.Empty;
            return copy;
        }

        /// <summary>Encrypt BearerToken before writing to disk.</summary>
        protected override McpClient OnSave(McpClient c)
        {
            var copy = CloneClient(c);
            copy.BearerToken = _protector.Protect(c.BearerToken) ?? string.Empty;
            return copy;
        }

        private static McpClient CloneClient(McpClient c) => new McpClient
        {
            Id                 = c.Id,
            Name               = c.Name,
            BearerToken        = c.BearerToken,
            AllowedServerIds   = c.AllowedServerIds,
            AllowedGroupIds    = c.AllowedGroupIds,
            Enabled            = c.Enabled,
            RateLimitPerMinute = c.RateLimitPerMinute,
            ServerRateLimits   = c.ServerRateLimits,
            CreatedAt          = c.CreatedAt
        };

        public async Task<List<McpClient>> GetAllAsync(CancellationToken ct = default)
            => await MutateAsync(items => false, ct);

        public async Task<McpClient?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => (await GetAllAsync(ct)).FirstOrDefault(c => c.Id == id);

        public async Task<McpClient?> GetByTokenAsync(string bearerToken, CancellationToken ct = default)
            => (await GetAllAsync(ct)).FirstOrDefault(c => c.BearerToken == bearerToken);

        public async Task<McpClient> AddAsync(McpClient client, CancellationToken ct = default)
        {
            await MutateAsync(items => { items.Add(client); return true; }, ct);
            return client;
        }

        public async Task<McpClient> UpdateAsync(McpClient client, CancellationToken ct = default)
        {
            await MutateAsync(items =>
            {
                var idx = items.FindIndex(c => c.Id == client.Id);
                if (idx >= 0) items[idx] = client;
                return idx >= 0;
            }, ct);
            return client;
        }

        public async Task DeleteAsync(Guid id, CancellationToken ct = default)
            => await MutateAsync(items => items.RemoveAll(c => c.Id == id) > 0, ct);
    }

    // ─── Audit Log Repository ─────────────────────────────────────────────────
    /// <summary>
    /// Appends audit entries as newline-delimited JSON (NDJSON) for efficient writes.
    /// Reads the last <paramref name="maxEntries"/> lines on query.
    /// </summary>
    public class JsonAuditLogRepository : IAuditLogRepository
    {
        private readonly string _filePath;
        private readonly int _maxEntries;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        public JsonAuditLogRepository(string dataDirectory, int maxEntries = 10_000)
        {
            _filePath = Path.Combine(dataDirectory, "audit.ndjson");
            _maxEntries = maxEntries;
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        public async Task AddAsync(AuditLog log, CancellationToken ct = default)
        {
            var line = McpJsonOptions.Serialize(log) + "\n";
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try { await File.AppendAllTextAsync(_filePath, line, ct).ConfigureAwait(false); }
            finally { _lock.Release(); }
        }

        public async Task<List<AuditLog>> GetRecentAsync(int count = 100, CancellationToken ct = default)
        {
            if (!File.Exists(_filePath)) return new List<AuditLog>();
            var lines = await File.ReadAllLinesAsync(_filePath, ct).ConfigureAwait(false);
            var result = new List<AuditLog>(Math.Min(count, lines.Length));
            // Read last `count` non-empty lines
            for (int i = lines.Length - 1; i >= 0 && result.Count < count; i--)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrEmpty(line)) continue;
                try
                {
                    var entry = McpJsonOptions.Deserialize<AuditLog>(line);
                    if (entry != null) result.Add(entry);
                }
                catch { /* skip corrupt lines */ }
            }
            result.Reverse();
            return result;
        }
    }

    // ─── Options & Extension ─────────────────────────────────────────────────
    public class JsonPersistenceOptions
    {
        /// <summary>Directory where JSON data files are stored. Default: "./mcp-data"</summary>
        public string DataDirectory { get; set; } = "mcp-data";

        /// <summary>Maximum audit log entries kept in memory on read. Default: 200</summary>
        public int AuditLogReadLimit { get; set; } = 200;
    }
}
