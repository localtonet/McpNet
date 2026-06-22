using System;
using System.Threading;

namespace McpNet.Gateway.Aggregation
{
    /// <summary>
    /// In-memory counters for the <c>mcpnet__retrieve_tools</c> BM25 search feature.
    /// Tracks call volume and estimates token savings (assuming ~150 tokens per full
    /// tool schema - name + description + input schema).
    /// </summary>
    public sealed class ToolSearchMetrics
    {
        /// <summary>Assumed average tokens per full tool schema (name + desc + inputSchema).</summary>
        public const int TokensPerSchema = 150;

        private long _totalCalls;
        private long _totalToolsAtCallSum;   // sum of totalTools at each call
        private long _totalResultsReturned;  // sum of results returned at each call
        private long _estimatedTokensSaved;  // cumulative

        public long TotalCalls            => Interlocked.Read(ref _totalCalls);
        public long TotalResultsReturned  => Interlocked.Read(ref _totalResultsReturned);
        public long EstimatedTokensSaved  => Interlocked.Read(ref _estimatedTokensSaved);

        public double AverageResultsPerCall =>
            _totalCalls == 0 ? 0 : (double)_totalResultsReturned / _totalCalls;

        /// <summary>Average total-tools count across all calls.</summary>
        public double AverageToolsAtCall =>
            _totalCalls == 0 ? 0 : (double)_totalToolsAtCallSum / _totalCalls;

        public DateTime FirstCallAt { get; private set; } = DateTime.MinValue;
        public DateTime LastCallAt  { get; private set; } = DateTime.MinValue;

        /// <summary>
        /// Records one <c>retrieve_tools</c> call.
        /// </summary>
        /// <param name="totalTools">Total enabled tools in the gateway at call time.</param>
        /// <param name="resultsReturned">How many tools were returned to the agent.</param>
        public void Record(int totalTools, int resultsReturned)
        {
            var now = DateTime.UtcNow;
            if (FirstCallAt == DateTime.MinValue) FirstCallAt = now;
            LastCallAt = now;

            Interlocked.Increment(ref _totalCalls);
            Interlocked.Add(ref _totalToolsAtCallSum, totalTools);
            Interlocked.Add(ref _totalResultsReturned, resultsReturned);

            // Tokens saved = schemas NOT sent to agent × avg tokens per schema
            // The agent loaded 1 retrieve_tools schema instead of totalTools schemas,
            // then received resultsReturned schemas back. Net saving:
            //   (totalTools - 1 - resultsReturned) * TokensPerSchema  (clamped to 0)
            var saved = Math.Max(0, (totalTools - 1 - resultsReturned)) * TokensPerSchema;
            Interlocked.Add(ref _estimatedTokensSaved, saved);
        }
    }
}
