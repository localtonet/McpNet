using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace McpNet.Gateway.Observability
{
    /// <summary>
    /// Lightweight OpenTelemetry instrumentation primitives for the gateway.
    /// These are always defined but carry near-zero cost when no listener/exporter is
    /// attached. OpenTelemetry export is opt-in at the host level (default off) — by
    /// default the gateway relies on the built-in audit log for observability.
    /// </summary>
    public static class McpTelemetry
    {
        public const string SourceName = "McpNet.Gateway";
        public const string Version = "1.0.0";

        public static readonly ActivitySource ActivitySource = new(SourceName, Version);

        public static readonly Meter Meter = new(SourceName, Version);

        public static readonly Counter<long> ToolCalls =
            Meter.CreateCounter<long>("mcpnet.tool_calls", unit: "{call}", description: "Number of upstream tool calls routed by the gateway.");

        public static readonly Histogram<double> ToolCallDuration =
            Meter.CreateHistogram<double>("mcpnet.tool_call.duration", unit: "ms", description: "Duration of upstream tool calls in milliseconds.");
    }
}
