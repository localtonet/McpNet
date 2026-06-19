using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace McpNet.Gateway.Routing
{
    /// <summary>
    /// In-memory sliding-window rate limiter that replaces the full audit-log scan that was
    /// previously used to count recent calls. O(calls-in-window) per check instead of O(log-size).
    /// </summary>
    public sealed class GatewayRateLimiter
    {
        // Key: "{clientId}:{serverId}" for per-server limits, "{clientId}:global" for global limits.
        private readonly ConcurrentDictionary<string, Queue<long>> _windows = new();
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Records a call attempt and returns <c>true</c> if allowed, <c>false</c> if the
        /// limit is already reached for the current one-minute sliding window.
        /// </summary>
        public bool TryRecord(Guid clientId, Guid? serverId, int limitPerMinute)
        {
            if (limitPerMinute <= 0) return true;

            var key = serverId.HasValue
                ? $"{clientId}:{serverId.Value}"
                : $"{clientId}:global";

            var queue = _windows.GetOrAdd(key, _ => new Queue<long>());
            var now    = DateTime.UtcNow.Ticks;
            var cutoff = (DateTime.UtcNow - Window).Ticks;

            lock (queue)
            {
                // Evict timestamps that have fallen outside the window.
                while (queue.Count > 0 && queue.Peek() < cutoff)
                    queue.Dequeue();

                if (queue.Count >= limitPerMinute)
                    return false;

                queue.Enqueue(now);
                return true;
            }
        }
    }
}
