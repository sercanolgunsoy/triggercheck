using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TriggerCheck.Monitoring.Diagnostics;

namespace TriggerCheck.Web.Infrastructure;

/// <summary>
/// OpenTelemetry traces + metrics: ASP.NET Core, HttpClient (probes, webhooks), .NET runtime and the monitoring
/// engine's own Meter / ActivitySource (<see cref="MonitoringMetrics.Name"/>). Logs are exported too when an exporter
/// is configured. The OTLP exporter is enabled only when OTEL_EXPORTER_OTLP_ENDPOINT (standard OTel env var, honours
/// the other OTEL_* variables) or Telemetry:OtlpEndpoint (+ optional Telemetry:OtlpProtocol = grpc | http/protobuf)
/// is set; otherwise nothing is exported (instrumentation still runs, so in-process listeners keep working).
/// </summary>
public static class TelemetrySetup
{
    public const string ServiceName = "triggercheck";

    public static WebApplicationBuilder AddAppTelemetry(this WebApplicationBuilder builder)
    {
        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(ServiceName, serviceVersion: typeof(TelemetrySetup).Assembly.GetName().Version?.ToString()))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation(o =>
                    // Orchestrator health probes would drown the interesting traces.
                    o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                .AddSource(MonitoringMetrics.Name))
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(MonitoringMetrics.Name));

        var envEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var configEndpoint = builder.Configuration["Telemetry:OtlpEndpoint"];
        if (!string.IsNullOrWhiteSpace(envEndpoint))
        {
            otel.WithLogging().UseOtlpExporter(); // reads OTEL_EXPORTER_OTLP_* itself
        }
        else if (!string.IsNullOrWhiteSpace(configEndpoint))
        {
            var protocol = string.Equals(builder.Configuration["Telemetry:OtlpProtocol"], "http/protobuf", StringComparison.OrdinalIgnoreCase)
                ? OtlpExportProtocol.HttpProtobuf
                : OtlpExportProtocol.Grpc;
            otel.WithLogging().UseOtlpExporter(protocol, new Uri(configEndpoint));
        }
        return builder;
    }
}
