using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using McpNet.Core.Serialization;

namespace McpNet.Gateway.Dashboard
{
    /// <summary>
    /// Manages the merged server catalog: embedded curated entries (from
    /// <c>McpNet.Gateway.wwwroot.catalog.json</c>) plus user-saved custom entries
    /// stored in <c>{dataDirectory}/custom-catalog.json</c>.
    ///
    /// No HTTP / ASP.NET Core dependency - usable from any host.
    /// </summary>
    public sealed class GatewayCatalogService
    {
        private readonly string _customFilePath;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        public GatewayCatalogService(string dataDirectory)
        {
            Directory.CreateDirectory(dataDirectory);
            _customFilePath = Path.Combine(dataDirectory, "custom-catalog.json");
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>Returns the merged catalog (curated + custom entries).</summary>
        public async Task<List<object>> GetMergedAsync(CancellationToken ct = default)
        {
            var merged = new List<object>();

            // 1. Embedded curated catalog
            try
            {
                var text = await GatewayDashboardResources.GetTextAsync("catalog.json", ct).ConfigureAwait(false);
                if (text != null)
                {
                    var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.TryGetProperty("servers", out var svrs))
                        foreach (var s in svrs.EnumerateArray())
                        {
                            var raw = McpJsonOptions.Deserialize<object>(s.GetRawText());
                            if (raw != null) merged.Add(raw);
                        }
                }
            }
            catch { }

            // 2. User-saved custom entries
            foreach (var item in await LoadCustomAsync(ct).ConfigureAwait(false))
            {
                var raw = McpJsonOptions.Deserialize<object>(item.GetRawText());
                if (raw != null) merged.Add(raw);
            }

            return merged;
        }

        /// <summary>Appends a new custom entry to the custom catalog file.</summary>
        public async Task AddCustomAsync(object entry, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var existing = await ReadCustomRawAsync(ct).ConfigureAwait(false);
                existing.Add(entry);
                await File.WriteAllTextAsync(_customFilePath, McpJsonOptions.Serialize(existing), ct).ConfigureAwait(false);
            }
            finally { _lock.Release(); }
        }

        /// <summary>Removes the custom entry with the given <c>name</c> field.</summary>
        public async Task RemoveCustomAsync(string name, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var prev = await LoadCustomAsync(ct).ConfigureAwait(false);
                var kept = prev
                    .Where(e => !(e.TryGetProperty("name", out var n) && n.GetString() == name))
                    .Select(e => (object)McpJsonOptions.Deserialize<object>(e.GetRawText())!)
                    .Where(x => x != null)
                    .ToList();
                await File.WriteAllTextAsync(_customFilePath, McpJsonOptions.Serialize(kept), ct).ConfigureAwait(false);
            }
            finally { _lock.Release(); }
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private async Task<List<JsonElement>> LoadCustomAsync(CancellationToken ct)
        {
            if (!File.Exists(_customFilePath)) return new List<JsonElement>();
            try
            {
                var json = await File.ReadAllTextAsync(_customFilePath, ct).ConfigureAwait(false);
                var doc  = JsonDocument.Parse(json);
                return doc.RootElement.ValueKind == JsonValueKind.Array
                    ? doc.RootElement.EnumerateArray().ToList()
                    : new List<JsonElement>();
            }
            catch { return new List<JsonElement>(); }
        }

        private async Task<List<object>> ReadCustomRawAsync(CancellationToken ct)
        {
            var existing = new List<object>();
            if (!File.Exists(_customFilePath)) return existing;
            try
            {
                var prev = await File.ReadAllTextAsync(_customFilePath, ct).ConfigureAwait(false);
                var doc  = JsonDocument.Parse(prev);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    foreach (var e in doc.RootElement.EnumerateArray())
                    {
                        var raw = McpJsonOptions.Deserialize<object>(e.GetRawText());
                        if (raw != null) existing.Add(raw);
                    }
            }
            catch { }
            return existing;
        }
    }
}
