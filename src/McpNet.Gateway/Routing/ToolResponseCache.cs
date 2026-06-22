using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using McpNet.Core.Capabilities;

namespace McpNet.Gateway.Routing
{
    /// <summary>
    /// In-memory cache for tool call responses. Entries are keyed by a SHA-256 hash of
    /// the tool's full name and its serialised arguments, and expire after a per-server TTL.
    /// Only non-error responses are cached. The cache can be invalidated per-server (e.g.
    /// after a refresh) via <see cref="InvalidateServer"/>.
    /// </summary>
    public sealed class ToolResponseCache
    {
        private sealed record CacheEntry(ToolCallResult Result, DateTime ExpiresAt);

        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

        /// <summary>
        /// Attempts to retrieve a cached result. Returns false (and null) on cache miss or expiry.
        /// </summary>
        public bool TryGet(string toolFullName, Dictionary<string, object?>? args, out ToolCallResult? result)
        {
            var key = BuildKey(toolFullName, args);
            if (_cache.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTime.UtcNow)
            {
                result = entry.Result;
                return true;
            }
            _cache.TryRemove(key, out _);
            result = null;
            return false;
        }

        /// <summary>Stores a result. No-op when <paramref name="ttlSeconds"/> is 0.</summary>
        public void Set(string toolFullName, Dictionary<string, object?>? args, ToolCallResult result, int ttlSeconds)
        {
            if (ttlSeconds <= 0 || result.IsError) return;
            var key = BuildKey(toolFullName, args);
            _cache[key] = new CacheEntry(result, DateTime.UtcNow.AddSeconds(ttlSeconds));
        }

        /// <summary>
        /// Removes all cached entries whose key belongs to <paramref name="serverName"/>.
        /// Call after a server refresh to prevent stale results.
        /// </summary>
        public void InvalidateServer(string serverName)
        {
            var prefix = serverName + "__";
            foreach (var key in _cache.Keys)
            {
                // Keys are SHA-256 hashes, so we store the full name separately.
                // We maintain a secondary prefix index for server invalidation.
            }
            // Since keys are hashed, we track server prefix separately in _serverKeys.
            if (_serverKeys.TryRemove(serverName, out var keys))
                foreach (var k in keys)
                    _cache.TryRemove(k, out _);
        }

        // Server name → set of cache keys belonging to it (for invalidation).
        private readonly ConcurrentDictionary<string, ConcurrentBag<string>> _serverKeys = new();

        public void Set(string serverName, string toolFullName, Dictionary<string, object?>? args, ToolCallResult result, int ttlSeconds)
        {
            if (ttlSeconds <= 0 || result.IsError) return;
            var key = BuildKey(toolFullName, args);
            _cache[key] = new CacheEntry(result, DateTime.UtcNow.AddSeconds(ttlSeconds));
            _serverKeys.GetOrAdd(serverName, _ => new ConcurrentBag<string>()).Add(key);
        }

        public int Count => _cache.Count;

        private static string BuildKey(string toolFullName, Dictionary<string, object?>? args)
        {
            var argsJson = args == null || args.Count == 0 ? "{}" : JsonSerializer.Serialize(args);
            var raw = toolFullName + "\x00" + argsJson;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(hash);
        }
    }
}
