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
    /// The exporter configuration for one signal, or null when no endpoint applies to it. With an OTEL_* endpoint
    /// the exporter reads the environment itself; with Telemetry:OtlpEndpoint the endpoint and protocol are set
    /// here (http/protobuf needs the full per-signal path).
    /// </summary>
    private static Action<OtlpExporterOptions>? ExporterFor(IConfiguration config, string signal, string httpPath)
    {
        if (!string.IsNullOrWhiteSpace(config["OTEL_EXPORTER_OTLP_ENDPOINT"]) ||
            !string.IsNullOrWhiteSpace(config[$"OTEL_EXPORTER_OTLP_{signal}_ENDPOINT"]))
            return _ => { };

        var endpoint = config["Telemetry:OtlpEndpoint"];
        if (string.IsNullOrWhiteSpace(endpoint)) return null;

        var httpProtobuf = string.Equals(config["Telemetry:OtlpProtocol"], "http/protobuf", StringComparison.OrdinalIgnoreCase);
        var baseUri = new Uri(endpoint.EndsWith('/') ? endpoint : endpoint + "/");
        return o =>
        {
            o.Protocol = httpProtobuf ? OtlpExportProtocol.HttpProtobuf : OtlpExportProtocol.Grpc;
            o.Endpoint = httpProtobuf ? new Uri(baseUri, httpPath) : new Uri(endpoint);
        };
    }
}
