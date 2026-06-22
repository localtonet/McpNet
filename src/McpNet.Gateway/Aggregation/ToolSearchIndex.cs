using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using McpNet.Gateway.Models;

namespace McpNet.Gateway.Aggregation
{
    /// <summary>
    /// In-memory BM25 full-text index over all aggregated tool names + descriptions.
    /// Allows an AI agent to load a single <c>mcpnet__retrieve_tools</c> function instead
    /// of every tool schema, dramatically reducing token usage when many servers are
    /// connected (similar to mcpproxy-go's <c>retrieve_tools</c> feature).
    /// </summary>
    public sealed class ToolSearchIndex
    {
        // BM25 tuning constants (standard values).
        private const double K1 = 1.5;
        private const double B  = 0.75;

        // Minimum query-term length for prefix expansion.
        // Prevents "a" from matching every term in the vocabulary.
        private const int MinPrefixLen = 3;

        private record DocEntry(AggregatedTool Tool, int Length,
            Dictionary<string, int> Tf);  // pre-built TF, no per-search alloc

        // Immutable snapshot - replaced atomically on every Rebuild.
        private sealed record IndexSnapshot(
            List<DocEntry> Docs,
            Dictionary<string, int> Df,
            double AvgDocLen);

        private static readonly IndexSnapshot _empty =
            new(new List<DocEntry>(), new Dictionary<string, int>(), 1);

        // Single volatile reference → readers always see a consistent pair.
        private volatile IndexSnapshot _snap = _empty;

        /// <summary>True if the index has not been built yet (no Rebuild call).</summary>
        public bool IsEmpty => _snap.Docs.Count == 0;

        /// <summary>Rebuilds the index from the current enabled tool list.</summary>
        public void Rebuild(IEnumerable<AggregatedTool> tools)
        {
            var docs = new List<DocEntry>();
            var df   = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var tool in tools)
            {
                var text  = $"{tool.FullName} {tool.Definition?.Description}";
                var terms = Tokenize(text);
                var tf    = BuildTf(terms);
                docs.Add(new DocEntry(tool, terms.Length, tf));

                foreach (var term in tf.Keys)
                {
                    df.TryGetValue(term, out var cnt);
                    df[term] = cnt + 1;
                }
            }

            var avgLen = docs.Count == 0 ? 1 : docs.Average(d => d.Length);
            // Atomic swap: readers always see docs + df + avgLen together.
            _snap = new IndexSnapshot(docs, df, avgLen);
        }

        /// <summary>
        /// Returns up to <paramref name="topN"/> tools ranked by BM25 relevance to the query.
        /// Returns an empty list if the index has not been built yet.
        /// </summary>
        public List<(AggregatedTool Tool, double Score)> Search(string query, int topN = 5)
        {
            var snap = _snap;   // capture snapshot - consistent docs + df + avgLen
            if (snap.Docs.Count == 0 || string.IsNullOrWhiteSpace(query))
                return new List<(AggregatedTool, double)>();

            var queryTerms = Tokenize(query);
            if (queryTerms.Length == 0) return new List<(AggregatedTool, double)>();

            int n = snap.Docs.Count;
            var scores = new List<(AggregatedTool Tool, double Score)>(snap.Docs.Count);

            // Expand each query term to vocabulary terms with that prefix.
            // Only expand if the term is long enough to avoid explosion (e.g. "a" → everything).
            var expandedTerms = queryTerms
                .SelectMany(q => q.Length >= MinPrefixLen
                    ? snap.Df.Keys.Where(k => k.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                    : Enumerable.Repeat(q, 1))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (expandedTerms.Count == 0) expandedTerms = queryTerms.ToList();

            foreach (var doc in snap.Docs)
            {
                double score = 0;

                foreach (var term in expandedTerms)
                {
                    snap.Df.TryGetValue(term, out int docFreq);
                    if (docFreq == 0) continue;

                    // IDF - Robertson-Sparck Jones variant, always ≥ 0
                    double idf = Math.Log((n - docFreq + 0.5) / (docFreq + 0.5) + 1);

                    doc.Tf.TryGetValue(term, out int termFreq);
                    double normTf = termFreq * (K1 + 1)
                        / (termFreq + K1 * (1 - B + B * doc.Length / snap.AvgDocLen));

                    score += idf * normTf;
                }

                if (score > 0)
                    scores.Add((doc.Tool, score));
            }

            return scores
                .OrderByDescending(x => x.Score)
                .Take(topN)
                .ToList();
        }

        // ── helpers ───────────────────────────────────────────────────────────

        private static readonly Regex _tokenRegex = new(@"[a-zA-Z0-9]+", RegexOptions.Compiled);

        private static string[] Tokenize(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
            // Split on camelCase/snake_case boundaries and whitespace.
            var expanded = Regex.Replace(text, @"([a-z])([A-Z])", "$1 $2")
                                .Replace('_', ' ').Replace('-', ' ');
            return _tokenRegex.Matches(expanded.ToLowerInvariant())
                              .Select(m => m.Value)
                              .ToArray();
        }

        private static Dictionary<string, int> BuildTf(string[] terms)
        {
            var tf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in terms)
            {
                tf.TryGetValue(t, out var c);
                tf[t] = c + 1;
            }
            return tf;
        }
    }
}
