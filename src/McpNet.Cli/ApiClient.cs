using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using McpNet.Core.Serialization;

namespace McpNet.Cli
{
    /// <summary>
    /// Thin HTTP client for the gateway management REST API (/api).
    /// </summary>
    internal sealed class ApiClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _baseUrl;

        public ApiClient(string baseUrl, string? adminToken)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            if (!string.IsNullOrEmpty(adminToken))
                _http.DefaultRequestHeaders.Add("X-Admin-Token", adminToken);
        }

        public async Task<string> GetAsync(string path)
        {
            var resp = await _http.GetAsync(_baseUrl + path).ConfigureAwait(false);
            return await ReadAsync(resp).ConfigureAwait(false);
        }

        public async Task<string> PostAsync(string path, object? body)
        {
            var content = body == null
                ? new StringContent("", Encoding.UTF8, "application/json")
                : new StringContent(McpJsonOptions.Serialize(body), Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(_baseUrl + path, content).ConfigureAwait(false);
            return await ReadAsync(resp).ConfigureAwait(false);
        }

        public async Task<string> PutAsync(string path, object? body)
        {
            var content = body == null
                ? new StringContent("", Encoding.UTF8, "application/json")
                : new StringContent(McpJsonOptions.Serialize(body), Encoding.UTF8, "application/json");
            var resp = await _http.PutAsync(_baseUrl + path, content).ConfigureAwait(false);
            return await ReadAsync(resp).ConfigureAwait(false);
        }

        public async Task<string> PatchAsync(string path, object? body = null)
        {
            var req = new HttpRequestMessage(new HttpMethod("PATCH"), _baseUrl + path);
            if (body != null)
                req.Content = new StringContent(McpJsonOptions.Serialize(body), Encoding.UTF8, "application/json");
            var resp = await _http.SendAsync(req).ConfigureAwait(false);
            return await ReadAsync(resp).ConfigureAwait(false);
        }

        public async Task<string> DeleteAsync(string path)
        {
            var resp = await _http.DeleteAsync(_baseUrl + path).ConfigureAwait(false);
            return await ReadAsync(resp).ConfigureAwait(false);
        }

        private static async Task<string> ReadAsync(HttpResponseMessage resp)
        {
            var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new CliException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {text}");
            return text;
        }

        public void Dispose() => _http.Dispose();
    }

    internal sealed class CliException : Exception
    {
        public CliException(string message) : base(message) { }
    }
}
