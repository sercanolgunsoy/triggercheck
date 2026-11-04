using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TriggerCheck.Monitoring.Diagnostics;

namespace TriggerCheck.Web.Infrastructure;

/// <summary>
/// OpenTelemetry with OTLP export, configured per signal. A signal is exported only when an endpoint applies to it:
/// the standard OTEL_EXPORTER_OTLP_ENDPOINT or that signal's OTEL_EXPORTER_OTLP_{TRACES,METRICS,LOGS}_ENDPOINT (the
/// other OTEL_* variables apply), else Telemetry:OtlpEndpoint (+ optional Telemetry:OtlpProtocol = grpc |
/// http/protobuf). A signal without an endpoint is not registered at all; in-process listeners of the monitoring
/// Meter / ActivitySource keep working regardless.
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

    public static WebApplicationBuilder AddAppTelemetry(this WebApplicationBuilder builder)
    {
        var config = builder.Configuration;
        var traces = ExporterFor(config, "TRACES", "v1/traces");
        var metrics = ExporterFor(config, "METRICS", "v1/metrics");
        var logs = config.GetValue("Telemetry:ExportLogs", false) ? ExporterFor(config, "LOGS", "v1/logs") : null;
        if (traces is null && metrics is null && logs is null)
            return builder;

        var serviceVersion = typeof(TelemetrySetup).Assembly.GetName().Version?.ToString();
        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(ServiceName, serviceVersion: serviceVersion));

        if (traces is not null)
            otel.WithTracing(t => t
                // Orchestrator health probes would drown the interesting traces.
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
                .AddSource(MonitoringMetrics.Name)
                .AddOtlpExporter(traces));

        if (metrics is not null)
            otel.WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(MonitoringMetrics.Name)
                .AddOtlpExporter(metrics));

        if (logs is not null)
            builder.Logging.AddOpenTelemetry(o =>
            {
                o.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(ServiceName, serviceVersion: serviceVersion));
                o.AddOtlpExporter(logs);
            });

        return builder;
    }

    /// <summary>
    /// The exporter configuration for one signal, or null when no endpoint applies to it. Precedence: the signal's
    /// OTEL_EXPORTER_OTLP_{SIGNAL}_ENDPOINT, then OTEL_EXPORTER_OTLP_ENDPOINT, then Telemetry:OtlpEndpoint (for
    /// http/protobuf the per-signal /v1/* path is appended).
    /// </summary>
    private static Action<OtlpExporterOptions>? ExporterFor(IConfiguration config, string signal, string httpPath)
    {
        // Per-signal endpoint (OTel spec: used as-is, no path appended). Set explicitly: the exporter does not read the
        // per-signal variables itself and would otherwise send to localhost.
        var signalEndpoint = config[$"OTEL_EXPORTER_OTLP_{signal}_ENDPOINT"];
        if (!string.IsNullOrWhiteSpace(signalEndpoint))
        {
            var protocol = config[$"OTEL_EXPORTER_OTLP_{signal}_PROTOCOL"] is { Length: > 0 } p ? p : config["OTEL_EXPORTER_OTLP_PROTOCOL"];
            return o =>
            {
                o.Endpoint = new Uri(signalEndpoint);
                o.Protocol = IsHttpProtobuf(protocol) ? OtlpExportProtocol.HttpProtobuf : OtlpExportProtocol.Grpc;
            };
        }

        // Generic endpoint: the exporter reads OTEL_EXPORTER_OTLP_* (endpoint, protocol, headers) itself.
        if (!string.IsNullOrWhiteSpace(config["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            return _ => { };

        var endpoint = config["Telemetry:OtlpEndpoint"];
        if (string.IsNullOrWhiteSpace(endpoint)) return null;

        var httpProtobuf = IsHttpProtobuf(config["Telemetry:OtlpProtocol"]);
        var baseUri = new Uri(endpoint.EndsWith('/') ? endpoint : endpoint + "/");
        return o =>
        {
            o.Protocol = httpProtobuf ? OtlpExportProtocol.HttpProtobuf : OtlpExportProtocol.Grpc;
            o.Endpoint = httpProtobuf ? new Uri(baseUri, httpPath) : new Uri(endpoint);
        };
    }

    private static bool IsHttpProtobuf(string? protocol) => string.Equals(protocol, "http/protobuf", StringComparison.OrdinalIgnoreCase);
}
