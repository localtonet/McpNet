using System;
using System.Threading;
using System.Threading.Tasks;
using McpNet.Gateway.Aggregation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpNet.Host
{
    internal sealed class ToolRefreshBackgroundService : BackgroundService
    {
        private readonly ToolAggregator _aggregator;
        private readonly ILogger<ToolRefreshBackgroundService> _logger;
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(15);

        public ToolRefreshBackgroundService(ToolAggregator aggregator, ILogger<ToolRefreshBackgroundService> logger)
        {
            _aggregator = aggregator;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(InitialDelay, stoppingToken); } catch (OperationCanceledException) { return; }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("Periodic tool refresh starting.");
                    await _aggregator.RefreshAsync(stoppingToken).ConfigureAwait(false);
                    _logger.LogInformation("Periodic tool refresh complete. {Count} tools loaded.", _aggregator.GetAllTools().Count);
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
