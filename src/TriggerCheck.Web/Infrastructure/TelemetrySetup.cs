using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TriggerCheck.Monitoring.Diagnostics;

namespace TriggerCheck.Web.Infrastructure;

/// <summary>
/// OpenTelemetry, active only when an OTLP endpoint is configured: the standard OTEL_EXPORTER_OTLP_ENDPOINT or a
/// signal-specific OTEL_EXPORTER_OTLP_{TRACES,METRICS,LOGS}_ENDPOINT (the other OTEL_* variables apply), or
/// Telemetry:OtlpEndpoint (+ optional Telemetry:OtlpProtocol = grpc | http/protobuf). Without one nothing is
/// registered or exported; in-process listeners of the monitoring Meter / ActivitySource keep working.
/// <list type="bullet">
/// <item>Traces: ASP.NET Core requests (without /health) and the monitoring engine's "monitor.check" spans.
/// Outgoing HttpClient calls are deliberately NOT traced: their URLs carry secrets (webhook paths, tokens in probe
/// targets), and the probe span already covers them.</item>
/// <item>Metrics: ASP.NET Core, .NET runtime and <see cref="MonitoringMetrics.Name"/>. System.Net.Http metrics are not
/// collected: their server.address tag has unbounded cardinality (one value per customer target).</item>
/// <item>Logs: only with Telemetry:ExportLogs=true. Log messages contain personal data (alert recipients, monitor
/// names, error texts), so shipping them to a collector is an explicit decision.</item>
/// </list>
/// </summary>
public static class TelemetrySetup
{
    public const string ServiceName = "triggercheck";

    private static readonly string[] OtlpEndpointVariables =
    [
        "OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT",
        "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT", "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"
    ];

    public static WebApplicationBuilder AddAppTelemetry(this WebApplicationBuilder builder)
    {
        var config = builder.Configuration;
        var fromEnvironment = OtlpEndpointVariables.Any(v => !string.IsNullOrWhiteSpace(config[v]));
        var configEndpoint = config["Telemetry:OtlpEndpoint"];
        if (!fromEnvironment && string.IsNullOrWhiteSpace(configEndpoint))
            return builder; // no exporter: no tracing / metric pipelines at all

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(ServiceName, serviceVersion: typeof(TelemetrySetup).Assembly.GetName().Version?.ToString()))
            .WithTracing(t => t
                // Orchestrator health probes would drown the interesting traces.
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
                .AddSource(MonitoringMetrics.Name))
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(MonitoringMetrics.Name));
        if (config.GetValue("Telemetry:ExportLogs", false))
            otel.WithLogging();

        if (fromEnvironment)
        {
            otel.UseOtlpExporter(); // reads OTEL_EXPORTER_OTLP_* (incl. per-signal endpoints) itself
        }
        else
        {
            var protocol = string.Equals(config["Telemetry:OtlpProtocol"], "http/protobuf", StringComparison.OrdinalIgnoreCase)
                ? OtlpExportProtocol.HttpProtobuf
                : OtlpExportProtocol.Grpc;
            otel.UseOtlpExporter(protocol, new Uri(configEndpoint!));
        }
        return builder;
    }
}
