using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring.Checks;
using TriggerCheck.Monitoring.Diagnostics;
using TriggerCheck.Monitoring.Scheduling;
using TriggerCheck.Tests.Monitoring;

namespace TriggerCheck.Tests.Ops;

public class MonitoringMetricsTests
{
    private static MetricCollector<T> Collect<T>(MonitoringTestHost host, string instrument) where T : struct =>
        new(host.Services.GetRequiredService<IMeterFactory>(), MonitoringMetrics.Name, instrument);

    [Fact]
    public async Task Each_probe_is_counted_and_timed_by_type_and_result()
    {
        var checker = new ScriptedChecker(MonitorType.Http).Then(CheckOutcome.Up(5, 200), CheckOutcome.Down("refused"));
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceCheckers(checker));
        using var checks = Collect<long>(host, "triggercheck.monitoring.checks");
        using var duration = Collect<double>(host, "triggercheck.monitoring.check.duration");
        var c = await host.AddCustomerAsync();
        var m = await host.AddMonitorAsync(c.Id);

        await host.RunCheckAsync(m.Id);
        await host.RunCheckAsync(m.Id);

        var points = checks.GetMeasurementSnapshot();
        Assert.Equal(2, points.Count);
        Assert.All(points, p => Assert.Equal("http", p.Tags["monitor.type"]));
        Assert.Equal(new[] { "up", "down" }, points.Select(p => (string)p.Tags["result"]!));
        Assert.Equal(2, duration.GetMeasurementSnapshot().Count);
    }

    [Fact]
    public async Task Throwing_checker_is_counted_as_error()
    {
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceCheckers(new ThrowingChecker()));
        using var checks = Collect<long>(host, "triggercheck.monitoring.checks");
        var c = await host.AddCustomerAsync();
        var m = await host.AddMonitorAsync(c.Id);

        var result = await host.RunCheckAsync(m.Id);

        Assert.False(result.IsUp);
        Assert.Equal("error", checks.LastMeasurement!.Tags["result"]);
    }

    [Fact]
    public async Task Each_probe_produces_a_span_without_the_target()
    {
        var checker = new ScriptedChecker(MonitorType.Tcp);
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceCheckers(checker));
        var c = await host.AddCustomerAsync();
        var m = await host.AddMonitorAsync(c.Id, x => { x.Type = MonitorType.Tcp; x.Target = "secret.example.test:443"; });

        // Listeners are process-wide and other test classes run checks in parallel (monitor ids repeat across their
        // databases): keep only spans in this test's own trace.
        using var parent = new Activity("test").SetIdFormat(ActivityIdFormat.W3C).Start();
        var traceId = parent.TraceId;
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == MonitoringMetrics.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { if (a.TraceId == traceId) lock (spans) spans.Add(a); }
        };
        ActivitySource.AddActivityListener(listener);

        await host.RunCheckAsync(m.Id);
        parent.Stop();

        var span = Assert.Single(spans);
        Assert.Equal("monitor.check", span.OperationName);
        Assert.Equal("tcp", span.GetTagItem("monitor.type"));
        Assert.Equal("up", span.GetTagItem("check.result"));
        Assert.DoesNotContain(span.TagObjects, t => t.Value?.ToString()?.Contains("secret.example.test") == true);
    }

    [Fact]
    public async Task Scheduler_tick_records_heartbeat_backlog_and_lag()
    {
        var checker = new ScriptedChecker(MonitorType.Http);
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceCheckers(checker),
            new() { ["Monitoring:SchedulerTickMilliseconds"] = "1000" });
        using var lag = Collect<double>(host, "triggercheck.monitoring.scheduler.tick_lag");
        using var due = Collect<int>(host, "triggercheck.monitoring.scheduler.due_monitors");
        using var heartbeat = Collect<long>(host, "triggercheck.monitoring.scheduler.heartbeat");
        var metrics = host.Services.GetRequiredService<MonitoringMetrics>();
        var scheduler = host.Services.GetRequiredService<MonitorScheduler>();
        var c = await host.AddCustomerAsync();
        await host.AddMonitorAsync(c.Id);
        await host.AddMonitorAsync(c.Id);

        Assert.Null(metrics.LastSchedulerHeartbeat);
        await scheduler.DispatchDueAsync(CancellationToken.None);
        await scheduler.WhenIdleAsync();
        Assert.Equal(host.Time.GetUtcNow(), metrics.LastSchedulerHeartbeat);
        due.RecordObservableInstruments();
        Assert.Equal(2, due.LastMeasurement!.Value);
        heartbeat.RecordObservableInstruments();
        Assert.Equal(host.Time.GetUtcNow().ToUnixTimeSeconds(), heartbeat.LastMeasurement!.Value);
        Assert.Empty(lag.GetMeasurementSnapshot()); // first tick has nothing to compare with

        // Next tick 6 s after the previous one with a 1 s interval: 5 s late. Both monitors are no longer due.
        host.Time.Advance(TimeSpan.FromSeconds(6));
        await scheduler.DispatchDueAsync(CancellationToken.None);
        Assert.Equal(5, lag.LastMeasurement!.Value, precision: 3);
        due.RecordObservableInstruments();
        Assert.Equal(0, due.LastMeasurement!.Value);
        Assert.Equal(host.Time.GetUtcNow(), metrics.LastSchedulerHeartbeat);
    }

    [Fact]
    public async Task Alert_hook_counts_deliveries_by_channel_type_and_result()
    {
        await using var host = await MonitoringTestHost.CreateAsync();
        using var alerts = Collect<long>(host, "triggercheck.monitoring.alerts");
        var metrics = host.Services.GetRequiredService<MonitoringMetrics>();

        metrics.RecordAlert(AlertChannelType.Email, success: true);
        metrics.RecordAlert(AlertChannelType.Webhook, success: false);

        var points = alerts.GetMeasurementSnapshot();
        Assert.Equal(new[] { ("email", "sent"), ("webhook", "failed") },
            points.Select(p => ((string)p.Tags["channel.type"]!, (string)p.Tags["result"]!)));
    }

    private sealed class ThrowingChecker : IMonitorChecker
    {
        public MonitorType Type => MonitorType.Http;
        public Task<CheckOutcome> CheckAsync(Domain.Entities.Monitor monitor, CancellationToken ct) => throw new InvalidOperationException("boom");
    }
}
