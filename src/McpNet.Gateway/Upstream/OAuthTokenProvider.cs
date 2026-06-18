using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using McpNet.Gateway.Models;

namespace McpNet.Gateway.Upstream
{
    /// <summary>
    /// Acquires and caches OAuth 2.0 access tokens using the client-credentials grant,
    /// refreshing automatically before expiry. One instance per upstream server.
    /// </summary>
    public sealed class OAuthTokenProvider
    {
        private readonly OAuthConfig _config;
        private readonly HttpClient _http;
        private readonly SemaphoreSlim _lock = new(1, 1);

        private string? _accessToken;
        private DateTime _expiresAtUtc = DateTime.MinValue;

        // Refresh slightly early to avoid races against the expiry boundary.
        private static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(30);

        public OAuthTokenProvider(OAuthConfig config, HttpClient? http = null)
        {
            _config = config;
            _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
        {
            if (_accessToken != null && DateTime.UtcNow + RefreshSkew < _expiresAtUtc)
                return _accessToken;

            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_accessToken != null && DateTime.UtcNow + RefreshSkew < _expiresAtUtc)
                    return _accessToken;

                return await FetchTokenAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>Force the next request to acquire a new token (e.g. after a 401).</summary>
        public void Invalidate() => _expiresAtUtc = DateTime.MinValue;

        private async Task<string> FetchTokenAsync(CancellationToken ct)
        {
            var form = new List<KeyValuePair<string, string>>
            {
                new("grant_type", "client_credentials"),
                new("client_id", _config.ClientId),
                new("client_secret", _config.ClientSecret)
            };
            if (_config.Scopes.Count > 0)
                form.Add(new("scope", string.Join(' ', _config.Scopes)));

            using var content = new FormUrlEncodedContent(form);
            using var resp = await _http.PostAsync(_config.TokenUrl, content, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"OAuth token request failed: HTTP {(int)resp.StatusCode} {body}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("access_token", out var tokenEl) || tokenEl.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("OAuth token response did not contain an access_token");

            _accessToken = tokenEl.GetString();

            var expiresIn = root.TryGetProperty("expires_in", out var expEl) && expEl.TryGetInt32(out var sec)
                ? sec
                : 3600;
            _expiresAtUtc = DateTime.UtcNow.AddSeconds(expiresIn);

            return _accessToken!;
        }
    }
}
