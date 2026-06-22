using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using McpNet.Gateway.Observability;

namespace McpNet.Host
{
    /// <summary>
    /// Optional OpenTelemetry wiring. Disabled by default - the gateway's built-in audit
    /// log is the default lightweight observability. Enable by setting
    /// <c>McpGateway:Telemetry:Enabled = true</c>.
    /// Set <c>McpGateway:Telemetry:Prometheus:Enabled = true</c> to expose /metrics.
    /// </summary>
    internal static class TelemetryExtensions
    {
        public static bool PrometheusEnabled { get; private set; }

        public static IServiceCollection AddMcpTelemetry(this IServiceCollection services, IConfiguration cfg)
        {
            var section = cfg.GetSection("McpGateway:Telemetry");
            if (!bool.TryParse(section["Enabled"], out var enabled) || !enabled)
                return services; // default: no telemetry export

            PrometheusEnabled = bool.TryParse(section["Prometheus:Enabled"], out var promOn) && promOn;

            // OTLP endpoint (e.g. http://localhost:4317). When empty, fall back to console.
            var otlpEndpoint = section["OtlpEndpoint"];
            var useConsole = string.IsNullOrWhiteSpace(otlpEndpoint) && !PrometheusEnabled;

            var resource = ResourceBuilder.CreateDefault()
                .AddService(serviceName: "mcpnet-gateway", serviceVersion: McpTelemetry.Version);

            services.AddOpenTelemetry()
                .WithTracing(t =>
                {
                    t.SetResourceBuilder(resource)
                     .AddSource(McpTelemetry.SourceName)
                     .AddAspNetCoreInstrumentation()
                     .AddHttpClientInstrumentation();
                    if (useConsole) t.AddConsoleExporter();
                    else if (!string.IsNullOrWhiteSpace(otlpEndpoint)) t.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint!));
                })
                .WithMetrics(mt =>
                {
                    mt.SetResourceBuilder(resource)
                      .AddMeter(McpTelemetry.SourceName)
                      .AddAspNetCoreInstrumentation()
                      .AddHttpClientInstrumentation();
                    if (PrometheusEnabled)
                        mt.AddPrometheusExporter();
                    else if (useConsole)
                        mt.AddConsoleExporter();
                    else if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                        mt.AddOtlpExporter(o => o.Endpoint = new Uri(otlpEndpoint!));
                });

            return services;
        }

        public static WebApplication MapPrometheusIfEnabled(this WebApplication app)
        {
            if (PrometheusEnabled)
                app.MapPrometheusScrapingEndpoint("/metrics");
            return app;
        }
    }
}
