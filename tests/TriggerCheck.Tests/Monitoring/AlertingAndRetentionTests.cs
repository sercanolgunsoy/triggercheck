using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring.Alerting;
using TriggerCheck.Monitoring.Engine;
using TriggerCheck.Monitoring.Retention;
using TriggerCheck.Tests.Infrastructure;

namespace TriggerCheck.Tests.Monitoring;

public class AlertingAndRetentionTests
{
    private static AlertMessage Down(int monitorId = 7, int customerId = 1) =>
        new(monitorId, customerId, "Shop", "https://shop.example.test", AlertKind.Down, "Connection refused",
            new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task Webhook_posts_expected_json()
    {
        var received = new ConcurrentQueue<(string ContentType, string Body)>();
        await using var server = await LocalHttpServer.StartAsync(app =>
        {
            app.MapPost("/hook", async (HttpRequest req) =>
            {
                using var reader = new StreamReader(req.Body);
                received.Enqueue((req.ContentType ?? "", await reader.ReadToEndAsync()));
                return Results.Ok();
            });
            app.MapPost("/fail", () => Results.StatusCode(503));
        });
        await using var host = await MonitoringTestHost.CreateAsync();
        var webhook = host.Services.GetServices<INotifier>().Single(n => n.Type == AlertChannelType.Webhook);

        var ok = await webhook.SendAsync(server.Url("/hook"), Down(), CancellationToken.None);

        Assert.True(ok.Success, ok.Error);
        var (contentType, body) = Assert.Single(received);
        Assert.StartsWith("application/json", contentType);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal(7, root.GetProperty("monitorId").GetInt32());
        Assert.Equal("Shop", root.GetProperty("monitorName").GetString());
        Assert.Equal("https://shop.example.test", root.GetProperty("target").GetString());
        Assert.Equal("down", root.GetProperty("status").GetString());
        Assert.Equal("Connection refused", root.GetProperty("error").GetString());
        Assert.Equal("2026-01-01T12:00:00Z", root.GetProperty("occurredAtUtc").GetString());

        var fail = await webhook.SendAsync(server.Url("/fail"), Down(), CancellationToken.None);
        Assert.False(fail.Success);
        Assert.Equal("Webhook returned HTTP 503", fail.Error);

        var invalid = await webhook.SendAsync("not-a-url", Down(), CancellationToken.None);
        Assert.False(invalid.Success);
    }

    [Fact]
    public async Task Email_without_smtp_host_is_logged_and_recorded_as_success()
    {
        await using var host = await MonitoringTestHost.CreateAsync(settings: new() { ["Smtp:Host"] = "" });
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id);
        await host.WithDbAsync(async db =>
        {
            db.AlertChannels.Add(new AlertChannel { CustomerId = customer.Id, Name = "mail", Type = AlertChannelType.Email, Target = "ops@example.test" });
            db.AlertChannels.Add(new AlertChannel { CustomerId = customer.Id, Name = "bad", Type = AlertChannelType.Email, Target = "not an email" });
            await db.SaveChangesAsync();
        });

        var result = await host.InScopeAsync(sp => sp.GetRequiredService<IAlertDispatcher>().DispatchAsync(Down(monitor.Id, customer.Id)));

        Assert.Equal(2, result.Count);
        var stored = await host.WithDbAsync(db => db.Notifications.OrderBy(n => n.AlertChannelId).ToListAsync());
        Assert.Equal(2, stored.Count);
        Assert.True(stored[0].Success);
        Assert.Equal(EmailNotifier.NotConfiguredNote, stored[0].Error);
        Assert.Equal("[TriggerCheck] DOWN: Shop", stored[0].Subject);
        Assert.Equal(host.Now, stored[0].SentAtUtc);
        Assert.False(stored[1].Success);
        Assert.Contains("Invalid email", stored[1].Error);
    }

    [Fact]
    public void Subject_is_single_line()
    {
        var msg = Down() with { MonitorName = "evil\r\nBcc: x@y" };
        Assert.DoesNotContain('\n', msg.Subject);
        Assert.DoesNotContain('\r', msg.Subject);
    }

    [Fact]
    public async Task Retention_deletes_only_results_older_than_customer_plan_retention()
    {
        await using var host = await MonitoringTestHost.CreateAsync();
        var shortPlan = await host.AddCustomerAsync(retentionDays: 30);
        var longPlan = await host.AddCustomerAsync(retentionDays: 365);
        var m1 = await host.AddMonitorAsync(shortPlan.Id);
        var m2 = await host.AddMonitorAsync(longPlan.Id);
        var now = host.Now;

        await host.WithDbAsync(async db =>
        {
            foreach (var (monitorId, ageDays) in new[] { (m1.Id, 1), (m1.Id, 29), (m1.Id, 31), (m1.Id, 100), (m2.Id, 31), (m2.Id, 364), (m2.Id, 366) })
                db.CheckResults.Add(new CheckResult { MonitorId = monitorId, CheckedAtUtc = now.AddDays(-ageDays), IsUp = true });
            await db.SaveChangesAsync();
        });

        var deleted = await host.InScopeAsync(sp => sp.GetRequiredService<RetentionCleaner>().CleanupAsync());

        Assert.Equal(3, deleted);
        var remaining = await host.WithDbAsync(db => db.CheckResults.Select(c => new { c.MonitorId, c.CheckedAtUtc }).ToListAsync());
        Assert.Equal(2, remaining.Count(r => r.MonitorId == m1.Id));
        Assert.Equal(2, remaining.Count(r => r.MonitorId == m2.Id));
        Assert.All(remaining.Where(r => r.MonitorId == m1.Id), r => Assert.True(r.CheckedAtUtc >= now.AddDays(-30)));
    }

    [Fact]
    public async Task Retention_purges_old_resolved_incidents_and_notifications_but_never_open_incidents()
    {
        await using var host = await MonitoringTestHost.CreateAsync();
        var shortPlan = await host.AddCustomerAsync(retentionDays: 30);
        var forever = await host.AddCustomerAsync(retentionDays: 0);
        var m1 = await host.AddMonitorAsync(shortPlan.Id);
        var m2 = await host.AddMonitorAsync(forever.Id);
        var now = host.Now;

        await host.WithDbAsync(async db =>
        {
            db.Incidents.AddRange(
                new Incident { MonitorId = m1.Id, StartedAtUtc = now.AddDays(-400), ResolvedAtUtc = now.AddDays(-399), Cause = "old" },
                new Incident { MonitorId = m1.Id, StartedAtUtc = now.AddDays(-40), ResolvedAtUtc = now.AddDays(-10), Cause = "recent resolution" },
                new Incident { MonitorId = m1.Id, StartedAtUtc = now.AddDays(-400), Cause = "still open" },
                new Incident { MonitorId = m2.Id, StartedAtUtc = now.AddDays(-400), ResolvedAtUtc = now.AddDays(-399), Cause = "keep forever" });
            db.Notifications.AddRange(
                new Notification { MonitorId = m1.Id, SentAtUtc = now.AddDays(-31), Subject = "old" },
                new Notification { MonitorId = m1.Id, SentAtUtc = now.AddDays(-1), Subject = "new" },
                new Notification { MonitorId = m2.Id, SentAtUtc = now.AddDays(-400), Subject = "keep forever" });
            await db.SaveChangesAsync();
        });

        var deleted = await host.InScopeAsync(sp => sp.GetRequiredService<RetentionCleaner>().CleanupAsync());

        Assert.Equal(2, deleted);
        var incidents = await host.WithDbAsync(db => db.Incidents.Select(i => i.Cause).ToListAsync());
        Assert.Equal(new[] { "keep forever", "recent resolution", "still open" }, incidents.Order());
        var notifications = await host.WithDbAsync(db => db.Notifications.Select(n => n.Subject).ToListAsync());
        Assert.Equal(new[] { "keep forever", "new" }, notifications.Order());
    }

    [Fact]
    public async Task Run_gate_drops_semaphores_when_idle()
    {
        var gate = new MonitorRunGate();
        var a = await gate.AcquireAsync(1, CancellationToken.None);
        var waiting = gate.AcquireAsync(1, CancellationToken.None);
        var b = await gate.AcquireAsync(2, CancellationToken.None);
        Assert.Equal(2, gate.Count);
        Assert.False(waiting.IsCompleted);

        a.Dispose();
        var second = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, gate.Count);
        second.Dispose();
        b.Dispose();
        b.Dispose(); // double dispose is harmless
        Assert.Equal(0, gate.Count);

        // A cancelled waiter does not leak its entry either.
        using var held = await gate.AcquireAsync(3, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var cancelled = gate.AcquireAsync(3, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        held.Dispose();
        Assert.Equal(0, gate.Count);
    }

    [Fact]
    public async Task Run_gate_does_not_grow_with_checked_and_deleted_monitors()
    {
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceCheckers(new ScriptedChecker(MonitorType.Http)));
        var c = await host.AddCustomerAsync();
        for (var i = 0; i < 20; i++)
        {
            var m = await host.AddMonitorAsync(c.Id);
            await host.RunCheckAsync(m.Id);
            await host.WithDbAsync(db => db.Monitors.Where(x => x.Id == m.Id).ExecuteDeleteAsync());
        }
        Assert.Equal(0, host.Services.GetRequiredService<MonitorRunGate>().Count);
    }

    [SqliteFact]
    public async Task Sqlite_database_uses_wal_journal_mode()
    {
        await using var host = await MonitoringTestHost.CreateAsync();
        var mode = await host.WithDbAsync(async db =>
        {
            await db.Database.OpenConnectionAsync();
            await using var cmd = db.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode";
            return (string?)await cmd.ExecuteScalarAsync();
        });
        Assert.Equal("wal", mode);
    }
}
