using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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
        private readonly ServerRegistry _registry;
        private readonly IServerRepository _serverRepo;
        private readonly ConcurrentDictionary<string, AggregatedTool> _toolCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _diagnosticsLock = new object();
        private List<ToolRefreshDiagnostic> _lastRefreshDiagnostics = new List<ToolRefreshDiagnostic>();
        private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);
        private volatile bool _isRefreshing;
        private DateTime _lastRefreshedAt = DateTime.MinValue;

        public bool IsRefreshing => _isRefreshing;
        public DateTime LastRefreshedAt => _lastRefreshedAt;

        public ToolAggregator(ServerRegistry registry, IServerRepository serverRepo)
        {
            _registry = registry;
            _serverRepo = serverRepo;
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
                var enabledServers = servers.Where(s => s.Enabled).ToList();

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
                _lastRefreshedAt = DateTime.UtcNow;
            }
            finally
            {
                _isRefreshing = false;
                _refreshLock.Release();
            }
        }
        public List<AggregatedTool> GetAllTools() => _toolCache.Values.ToList();

        public List<AggregatedTool> GetEnabledTools() => _toolCache.Values.Where(t => t.Enabled).ToList();

        public AggregatedTool? GetTool(string fullName)
        {
            _toolCache.TryGetValue(fullName, out var tool);
            return tool;
        }

        public void SetToolEnabled(string fullName, bool enabled)
        {
            if (_toolCache.TryGetValue(fullName, out var tool))
                tool.Enabled = enabled;
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
