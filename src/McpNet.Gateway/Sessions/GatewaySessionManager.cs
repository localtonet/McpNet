using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading;

namespace McpNet.Gateway.Sessions
{
    public class GatewaySessionManager : IDisposable
    {
        private readonly ConcurrentDictionary<string, GatewaySession> _sessions = new(StringComparer.Ordinal);
        private readonly TimeSpan _sessionTtl;
        private readonly Timer _cleanupTimer;

        public GatewaySessionManager(TimeSpan? sessionTtl = null)
        {
            _sessionTtl = sessionTtl ?? TimeSpan.FromHours(24);
            // Purge expired sessions every 30 minutes to prevent unbounded memory growth.
            _cleanupTimer = new Timer(_ => PurgeExpired(), null,
                dueTime: TimeSpan.FromMinutes(30),
                period: TimeSpan.FromMinutes(30));
        }

        public string CreateSession()
        {
            var id = GenerateSecureId();
            _sessions[id] = new GatewaySession { Id = id, CreatedAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow };
            return id;
        }

        public bool TryGetSession(string id, out GatewaySession? session)
        {
            if (_sessions.TryGetValue(id, out session))
            {
                if (DateTime.UtcNow - session.LastSeenAt > _sessionTtl)
                {
                    _sessions.TryRemove(id, out _);
                    session = null;
                    return false;
                }
                session.LastSeenAt = DateTime.UtcNow;
                return true;
            }
            session = null;
            return false;
        }

        public bool SessionExists(string id) => TryGetSession(id, out _);

        public void TerminateSession(string id) => _sessions.TryRemove(id, out _);

        public int ActiveSessionCount => _sessions.Count;

        private void PurgeExpired()
        {
            var cutoff = DateTime.UtcNow - _sessionTtl;
            foreach (var kv in _sessions)
            {
                if (kv.Value.LastSeenAt < cutoff)
                    _sessions.TryRemove(kv.Key, out _);
            }
        }

        public void Dispose() => _cleanupTimer.Dispose();

        private static string GenerateSecureId()
        {
            var bytes = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").Replace("=", "");
        }
    }

    public class GatewaySession
    {
        public string Id { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime LastSeenAt { get; set; }
    }
}
