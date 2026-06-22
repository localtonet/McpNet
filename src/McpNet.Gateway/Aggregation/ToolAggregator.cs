using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using McpNet.Core.Capabilities;
using McpNet.Gateway.Abstractions;
using McpNet.Gateway.Models;
using McpNet.Gateway.Registry;

namespace McpNet.Gateway.Aggregation
{
    public class ToolAggregator
    {
        // ── Feature 1: auto health-check / auto-disable ──────────────────────
        /// <summary>Number of consecutive refresh failures before a server is auto-disabled.</summary>
        public const int AutoDisableThreshold = 3;
        private readonly ConcurrentDictionary<Guid, int> _consecutiveFailures = new();
        private readonly ConcurrentDictionary<Guid, byte> _autoDisabled = new();

        // ── Feature 2: change detection for notifications/tools/list_changed ─
        private long _toolsVersion;
        private string _lastToolsHash = string.Empty;

        // ── Core fields ──────────────────────────────────────────────────────
        private readonly ServerRegistry _registry;
        private readonly IServerRepository _serverRepo;
        private readonly IToolStateStore? _stateStore;
        private readonly ConcurrentDictionary<string, AggregatedTool> _toolCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly ToolSearchIndex _searchIndex = new();
        private readonly object _diagnosticsLock = new object();
        private readonly object _stateLock = new object();
        private List<ToolRefreshDiagnostic> _lastRefreshDiagnostics = new List<ToolRefreshDiagnostic>();
        private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);
        private volatile bool _isRefreshing;
        private DateTime _lastRefreshedAt = DateTime.MinValue;
        private Dictionary<string, bool> _persistedState = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private bool _stateLoaded;

        public bool IsRefreshing => _isRefreshing;
        public DateTime LastRefreshedAt => _lastRefreshedAt;
        /// <summary>Increments each time the aggregated tool list changes. Poll for push-notification decisions.</summary>
        public long ToolsVersion => Interlocked.Read(ref _toolsVersion);

        public ToolAggregator(ServerRegistry registry, IServerRepository serverRepo, IToolStateStore? stateStore = null)
        {
            _registry = registry;
            _serverRepo = serverRepo;
            _stateStore = stateStore;
        }

        public async Task RefreshAsync(CancellationToken ct = default)
        {
            // Only one refresh runs at a time; queued calls wait at most 30s then give up.
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            waitCts.CancelAfter(TimeSpan.FromSeconds(30));
            if (!await _refreshLock.WaitAsync(0).ConfigureAwait(false))
            {
                // Another refresh is already running; wait briefly then return -
                // the caller will pick up the freshly-populated cache when it reads tools.
                try { await _refreshLock.WaitAsync(waitCts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                _refreshLock.Release();
                return;
            }

            _isRefreshing = true;
            try
            {
                var servers = await _serverRepo.GetAllAsync(ct).ConfigureAwait(false);
                // Skip quarantined servers - they are pending approval and must not connect.
                var enabledServers = servers.Where(s => s.Enabled && !s.Quarantined).ToList();

                // Track which keys this refresh produces (for stale-entry removal at the end).
                var allNewKeys  = new System.Collections.Concurrent.ConcurrentBag<string>();
                var allDiags    = new System.Collections.Concurrent.ConcurrentBag<ToolRefreshDiagnostic>();

                // Each server task writes its tools into the shared cache AS SOON AS it finishes -
                // a slow or unresponsive server therefore never blocks the others from being visible.
                var serverTasks = enabledServers.Select(async server =>
                {
                    var startedAt = DateTime.UtcNow;
                    using var serverCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    serverCts.CancelAfter(TimeSpan.FromSeconds(120));
                    var serverCt = serverCts.Token;
                    try
                    {
                        var client = _registry.GetOrCreateClient(server);
                        if (!client.IsConnected)
                            await client.ConnectAsync(serverCt).ConfigureAwait(false);

                        if (!client.IsConnected)
                        {
                            // Feature 1: count as a failure.
                            RecordFailure(server.Id);
                            allDiags.Add(new ToolRefreshDiagnostic
                            {
                                ServerId = server.Id, ServerName = server.Name,
                                TransportType = server.TransportType.ToString(),
                                Status = "error", Success = false, ToolCount = 0,
                                DurationMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds,
                                ErrorMessage = "Connection failed. The server may still be starting (npx package download can take 60-120 s on first run). Try refreshing again in a moment.",
                                Timestamp = startedAt
                            });
                            return;
                        }

                        var tools = await client.ListToolsAsync(serverCt).ConfigureAwait(false);

                        // Write this server's tools into the live cache immediately.
                        foreach (var tool in tools)
                        {
                            var fullName = $"{server.Name}__{tool.Name}";
                            allNewKeys.Add(fullName);
                            var entry = new AggregatedTool
                            {
                                FullName = fullName,
                                LocalName = tool.Name,
                                ServerName = server.Name,
                                ServerId = server.Id,
                                Enabled = true,
                                Definition = new McpTool
                                {
                                    Name = fullName,
                                    Description = tool.Description,
                                    InputSchema = tool.InputSchema
                                }
                            };
                            if (_toolCache.TryGetValue(fullName, out var existing))
                                entry.Enabled = existing.Enabled;
                            _toolCache[fullName] = entry;
                        }

                        // Feature 7: register tool aliases defined on the server.
                        if (server.ToolAliases != null && server.ToolAliases.Count > 0)
                        {
                            var toolLookup = tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
                            foreach (var alias in server.ToolAliases)
                            {
                                if (!toolLookup.TryGetValue(alias.Value, out var srcTool)) continue;
                                var aliasFullName = $"{server.Name}__{alias.Key}";
                                allNewKeys.Add(aliasFullName);
                                var aliasEntry = new AggregatedTool
                                {
                                    FullName = aliasFullName,
                                    LocalName = alias.Value,   // route to original tool name
                                    ServerName = server.Name,
                                    ServerId = server.Id,
                                    Enabled = true,
                                    Definition = new McpTool
                                    {
                                        Name = aliasFullName,
                                        Description = $"(alias for {alias.Value}) {srcTool.Description}",
                                        InputSchema = srcTool.InputSchema
                                    }
                                };
                                if (_toolCache.TryGetValue(aliasFullName, out var existingAlias))
                                    aliasEntry.Enabled = existingAlias.Enabled;
                                _toolCache[aliasFullName] = aliasEntry;
                            }
                        }

                        // Feature 1: success → clear failure counter and auto-disabled flag.
                        _consecutiveFailures.TryRemove(server.Id, out _);
                        _autoDisabled.TryRemove(server.Id, out _);

                        allDiags.Add(new ToolRefreshDiagnostic
                        {
                            ServerId = server.Id, ServerName = server.Name,
                            TransportType = server.TransportType.ToString(),
                            Status = tools.Count == 0 ? "warning" : "ok",
                            Success = true, ToolCount = tools.Count,
                            DurationMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds,
                            ErrorMessage = tools.Count == 0 ? "Connected successfully but server returned 0 tools." : null,
                            Timestamp = startedAt
                        });
                    }
                    catch (Exception ex)
                    {
                        // Feature 1: count consecutive failures and auto-disable after threshold.
                        RecordFailure(server.Id);
                        allDiags.Add(new ToolRefreshDiagnostic
                        {
                            ServerId = server.Id, ServerName = server.Name,
                            TransportType = server.TransportType.ToString(),
                            Status = "error", Success = false, ToolCount = 0,
                            DurationMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds,
                            ErrorMessage = ex.Message,
                            Timestamp = startedAt
                        });
                    }
                }).ToList();

                // Wait for all servers - fast ones have already updated the cache by now.
                await Task.WhenAll(serverTasks).ConfigureAwait(false);

                // Remove stale entries (tools from servers that no longer exist / returned nothing).
                var newKeySet = new HashSet<string>(allNewKeys, StringComparer.OrdinalIgnoreCase);
                foreach (var key in _toolCache.Keys.ToList())
                    if (!newKeySet.Contains(key))
                        _toolCache.TryRemove(key, out _);

                lock (_diagnosticsLock)
                    _lastRefreshDiagnostics = allDiags.ToList();

                // Load persisted tool-enabled state on the first refresh (lazy, inside lock).
                if (!_stateLoaded && _stateStore != null)
                {
                    var loaded = await _stateStore.LoadAsync(ct).ConfigureAwait(false);
                    lock (_stateLock)
                    {
                        if (!_stateLoaded)
                        {
                            _persistedState = loaded;
                            _stateLoaded = true;
                        }
                    }
                }
                // Re-apply persisted enabled/disabled overrides to the freshly built cache.
                lock (_stateLock)
                {
                    foreach (var kv in _persistedState)
                        if (_toolCache.TryGetValue(kv.Key, out var t))
                            t.Enabled = kv.Value;
                }

                // Feature 2: compute a hash of the current tool set; increment version on change.
                var toolHash = ComputeToolsHash();
                if (toolHash != _lastToolsHash)
                {
                    _lastToolsHash = toolHash;
                    Interlocked.Increment(ref _toolsVersion);
                }

                // Rebuild BM25 search index from current enabled tools.
                _searchIndex.Rebuild(GetEnabledTools());

                _lastRefreshedAt = DateTime.UtcNow;
            }
            finally
            {
                _isRefreshing = false;
                _refreshLock.Release();
            }
        }

        // ── Feature 1: helper ────────────────────────────────────────────────
        private void RecordFailure(Guid serverId)
        {
            var count = _consecutiveFailures.AddOrUpdate(serverId, 1, (_, c) => c + 1);
            if (count >= AutoDisableThreshold)
                _autoDisabled[serverId] = 0;
        }

        /// <summary>Returns server IDs that were auto-disabled due to repeated refresh failures.</summary>
        public IReadOnlyCollection<Guid> GetAutoDisabledServerIds() => _autoDisabled.Keys.ToList();

        // ── Feature 2: hash helper ───────────────────────────────────────────
        private string ComputeToolsHash()
        {
            var names = string.Join("|", _toolCache.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(names));
            return Convert.ToHexString(hash);
        }
        public List<AggregatedTool> GetAllTools() => _toolCache.Values.ToList();

        /// <summary>BM25 full-text search over all enabled tool names and descriptions.</summary>
        public List<(AggregatedTool Tool, double Score)> SearchTools(string query, int topN = 5)
        {
            // Lazy rebuild: if the index is empty but tools are already in the cache
            // (e.g. searched before the first background RefreshAsync completes its Rebuild),
            // build it on demand so the first search isn't always empty.
            if (_searchIndex.IsEmpty && _toolCache.Count > 0)
                _searchIndex.Rebuild(GetEnabledTools());
            return _searchIndex.Search(query, topN);
        }

        /// <summary>
        /// Returns tools that are both user-enabled and not auto-disabled (Feature 1).
        /// This is the list sent to MCP clients via tools/list.
        /// </summary>
        public List<AggregatedTool> GetEnabledTools()
            => _toolCache.Values
                .Where(t => t.Enabled && !_autoDisabled.ContainsKey(t.ServerId))
                .ToList();

        public AggregatedTool? GetTool(string fullName)
        {
            _toolCache.TryGetValue(fullName, out var tool);
            return tool;
        }

        public void SetToolEnabled(string fullName, bool enabled)
        {
            if (!_toolCache.TryGetValue(fullName, out var tool)) return;
            tool.Enabled = enabled;
            Dictionary<string, bool>? snapshot = null;
            lock (_stateLock)
            {
                _persistedState[fullName] = enabled;
                snapshot = new Dictionary<string, bool>(_persistedState, StringComparer.OrdinalIgnoreCase);
            }
            // Best-effort async persist; fire-and-forget is acceptable for durability (state is in memory).
            if (_stateStore != null)
                _ = _stateStore.SaveAsync(snapshot, CancellationToken.None);
        }

        public List<ToolRefreshDiagnostic> GetLastRefreshDiagnostics()
        {
            lock (_diagnosticsLock)
            {
                return _lastRefreshDiagnostics
                    .Select(d => new ToolRefreshDiagnostic
                    {
                        ServerId = d.ServerId,
                        ServerName = d.ServerName,
                        TransportType = d.TransportType,
                        Status = d.Status,
                        Success = d.Success,
                        ToolCount = d.ToolCount,
                        DurationMs = d.DurationMs,
                        ErrorMessage = d.ErrorMessage,
                        Timestamp = d.Timestamp
                    })
                    .ToList();
            }
        }
    }

    public class ToolRefreshDiagnostic
    {
        public Guid ServerId { get; set; }
        public string ServerName { get; set; } = string.Empty;
        public string TransportType { get; set; } = string.Empty;
        public string Status { get; set; } = "ok";
        public bool Success { get; set; }
        public int ToolCount { get; set; }
        public long DurationMs { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}
