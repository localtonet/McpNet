using System;
using System.Threading;
using System.Threading.Tasks;
using McpNet.Gateway.Aggregation;
using McpNet.Gateway.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpNet.Host
{
    internal sealed class ToolRefreshBackgroundService : BackgroundService
    {
        private readonly ToolAggregator _aggregator;
        private readonly ILogger<ToolRefreshBackgroundService> _logger;
        private readonly SseConnectionManager? _sseManager;
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

        public ToolRefreshBackgroundService(
            ToolAggregator aggregator,
            ILogger<ToolRefreshBackgroundService> logger,
            SseConnectionManager? sseManager = null)
        {
            _aggregator = aggregator;
            _logger = logger;
            _sseManager = sseManager;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Refresh immediately on startup so tools are available as soon as possible,
            // then repeat every 60 s.
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("Periodic tool refresh starting.");
                    var versionBefore = _aggregator.ToolsVersion;
                    await _aggregator.RefreshAsync(stoppingToken).ConfigureAwait(false);
                    var count = _aggregator.GetAllTools().Count;
                    _logger.LogInformation("Periodic tool refresh complete. {Count} tools loaded.", count);

                    // Feature 2: if the tool list changed, push notifications to SSE clients.
                    if (_aggregator.ToolsVersion != versionBefore && _sseManager != null)
                    {
                        _sseManager.BroadcastToolsChanged();
                        _logger.LogDebug("tools/list_changed notification broadcast to {N} SSE clients.",
                            _sseManager.ConnectionCount);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Periodic tool refresh encountered an error.");
                }

                try { await Task.Delay(Interval, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }
}
