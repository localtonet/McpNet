using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using McpNet.Core.Serialization;

namespace McpNet.Gateway.Dashboard
{
    /// <summary>
    /// Loads the embedded dashboard static files (dashboard.html, dashboard.js,
    /// dashboard.css, catalog.json) from <c>McpNet.Gateway.dll</c>.
    ///
    /// Resource name prefix: <c>McpNet.Gateway.wwwroot.</c>
    ///
    /// Used by both the ASP.NET Core host (<c>McpDashboardEndpoints</c>) and the
    /// standalone HttpListener host so the files live in exactly one place.
    /// </summary>
    public static class GatewayDashboardResources
    {
        private static readonly ConcurrentDictionary<string, byte[]> _cache
            = new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        private static readonly Assembly _asm = typeof(GatewayDashboardResources).Assembly;

        /// <summary>
        /// Returns the raw bytes for <paramref name="fileName"/>
        /// (e.g. <c>"dashboard.html"</c>).
        /// Returns <c>null</c> if the file is not found.
        /// </summary>
        public static async Task<byte[]?> GetAsync(string fileName, CancellationToken ct = default)
        {
            if (_cache.TryGetValue(fileName, out var cached)) return cached;

            var resourceName = "McpNet.Gateway.wwwroot." + fileName.TrimStart('/').Replace('/', '.');
            using var stream = _asm.GetManifestResourceStream(resourceName);
            if (stream is null) return null;

            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
            var bytes = ms.ToArray();
            _cache[fileName] = bytes;
            return bytes;
        }

        /// <summary>Returns the UTF-8 text for <paramref name="fileName"/>.</summary>
        public static async Task<string?> GetTextAsync(string fileName, CancellationToken ct = default)
        {
            var bytes = await GetAsync(fileName, ct).ConfigureAwait(false);
            return bytes is null ? null : Encoding.UTF8.GetString(bytes);
        }

        /// <summary>Returns all embedded resource names under <c>wwwroot</c>.</summary>
        public static IEnumerable<string> ListFiles() =>
            _asm.GetManifestResourceNames()
                .Where(n => n.StartsWith("McpNet.Gateway.wwwroot.", StringComparison.Ordinal))
                .Select(n => n.Substring("McpNet.Gateway.wwwroot.".Length));
    }
}
