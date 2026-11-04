using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring;
using TriggerCheck.Monitoring.Checks;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Tests.Monitoring;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Tests.Ops;

/// <summary>App with an OTLP endpoint configured (unreachable) and an in-memory trace exporter next to it.</summary>
public sealed class TracingAppFactory : TestWebAppFactory
{
    public List<Activity> Spans { get; } = [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Telemetry:OtlpEndpoint", "http://127.0.0.1:9");
        builder.ConfigureTestServices(s => s.ConfigureOpenTelemetryTracerProvider(b => b.AddInMemoryExporter(Spans)));
    }
}

public class TelemetryTests
{
    private const string ProbeSecret = "probe-token-5f1c";
    private const string WebhookSecret = "hook-secret-9a7e";

    [Fact]
    public async Task Exported_spans_never_contain_probe_or_webhook_urls()
    {
        await using var target = await LocalHttpServer.StartAsync(app =>
        {
            app.MapGet($"/status/{ProbeSecret}", () => "ok");
            app.MapPost($"/hooks/{WebhookSecret}", () => Results.Ok());
        });
        await using var f = new TracingAppFactory();
        await f.CreateBrowser().GetAsync("/Account/Login"); // an incoming request: traced

        var monitorId = await f.WithDbAsync(async db =>
        {
            var customer = db.Customers.First();
            var m = new MonitorEntity
            {
                CustomerId = customer.Id, Name = "traced", Type = MonitorType.Http, Target = target.Url($"/status/{ProbeSecret}?key={ProbeSecret}"),
                IntervalSeconds = 60, FailureThreshold = 1
            };
            db.Monitors.Add(m);
            await db.SaveChangesAsync();
            return m.Id;
        });
        using (var scope = f.Services.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<IMonitorCheckRunner>().RunAsync(monitorId);
            Assert.True(result.IsUp, result.Error);
            var http = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(MonitoringHttpClients.Webhook);
            (await http.PostAsync(target.Url($"/hooks/{WebhookSecret}"), new StringContent("{}"))).EnsureSuccessStatusCode();
        }

        Assert.True(f.Services.GetRequiredService<TracerProvider>().ForceFlush(5000));
        // ActivitySources are process-wide: drop the target server's own request spans (that server is test-only and
        // runs in this process; in production it is the customer's server).
        var targetPort = new Uri(target.BaseUrl).Port;
        List<Activity> spans;
        lock (f.Spans) spans = f.Spans.Where(s => !(s.Source.Name == "Microsoft.AspNetCore" && Equals(s.GetTagItem("server.port"), targetPort))).ToList();

        Assert.Contains(spans, s => s.OperationName == "monitor.check" && Equals(s.GetTagItem("monitor.id"), monitorId));
        Assert.Contains(spans, s => s.Source.Name == "Microsoft.AspNetCore"); // tracing is on
        Assert.DoesNotContain(spans, s => s.Source.Name.StartsWith("System.Net.Http"));
        foreach (var span in spans)
        {
            var text = span.DisplayName + " " + string.Join(" ", span.TagObjects.Select(t => $"{t.Key}={t.Value}"));
            Assert.DoesNotContain(ProbeSecret, text);
            Assert.DoesNotContain(WebhookSecret, text);
        }
    }

    [Fact]
    public async Task Nothing_is_registered_without_an_exporter_endpoint()
    {
        await using var f = new TestWebAppFactory();
        _ = f.Services; // build the host
        Assert.Null(f.Services.GetService<TracerProvider>());
    }
}
