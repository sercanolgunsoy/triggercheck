using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TriggerCheck.Ai;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Features.Correlation;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring.Alerting;
using TriggerCheck.Monitoring.Checks;
using TriggerCheck.Tests.Monitoring;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Tests.Ai.Features;

/// <summary>
/// AI-4 at A3 on the real alert path (check runner → queue → sender → dispatcher), with the autonomy raised for the
/// test only. Invariants: no incident is lost or closed, DOWN alerts never wait for the model, monitors left out get
/// their own alerts, folded members are notified when the group is undone, escalation and maintenance are unchanged.
/// </summary>
public class CorrelationAlertTests
{
    private const string Timeout = "Timeout after 10s";

    /// <summary>Probe outcome per monitor, changeable during the test.</summary>
    private sealed class MapChecker : IMonitorChecker
    {
        public ConcurrentDictionary<int, CheckOutcome> Outcomes { get; } = new();
        public MonitorType Type => MonitorType.Http;
        public Task<CheckOutcome> CheckAsync(MonitorEntity monitor, CancellationToken ct) =>
            Task.FromResult(Outcomes.TryGetValue(monitor.Id, out var o) ? o : CheckOutcome.Up(5, 200));
    }

    private sealed record Setup(MonitoringTestHost Host, MapChecker Checker, RecordingNotifier Email, FakeAiClient Ai, Customer Customer) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Host.DisposeAsync();

        public List<AlertMessage> Sent => Email.Sent.Select(s => s.Message).ToList();

        public async Task<MonitorEntity> MonitorAsync(string target, string name) =>
            await Host.AddMonitorAsync(Customer.Id, m => { m.Target = target; m.Name = name; m.FailureThreshold = 1; });

        public async Task DownAsync(MonitorEntity m, string error = Timeout)
        {
            Checker.Outcomes[m.Id] = CheckOutcome.Down(error, 5);
            await Host.RunCheckAsync(m.Id);
            Host.Time.Advance(TimeSpan.FromSeconds(20));
        }

        public async Task UpAsync(MonitorEntity m)
        {
            Checker.Outcomes[m.Id] = CheckOutcome.Up(5, 200);
            await Host.RunCheckAsync(m.Id);
            await Host.AlertsIdleAsync();
            Host.Time.Advance(TimeSpan.FromSeconds(20));
        }

        public Task<List<IncidentGroup>> GroupsAsync() =>
            Host.WithDbAsync(db => db.IncidentGroups.AsNoTracking().Include(g => g.Members).ToListAsync());

        public Task<List<Notification>> NotificationsAsync(int monitorId) =>
            Host.WithDbAsync(db => db.Notifications.AsNoTracking().Where(n => n.MonitorId == monitorId).OrderBy(n => n.Id).ToListAsync());
    }

    private static async Task<Setup> CreateAsync(AiAutonomy level = AiAutonomy.A3, bool grouping = true, int callLimit = 100,
        Func<string, NotifyResult>? email = null, Dictionary<string, string?>? settings = null)
    {
        var checker = new MapChecker();
        var notifier = new RecordingNotifier(AlertChannelType.Email, email);
        var ai = new FakeAiClient();
        var aiConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Ai:Enabled"] = "true", ["Ai:ApiKey"] = "" }).Build();
        var host = await MonitoringTestHost.CreateAsync(s =>
        {
            s.ReplaceCheckers(checker).ReplaceNotifiers(notifier);
            s.AddAi(aiConfig);
            s.RemoveAll<IAiClient>();
            s.AddSingleton<IAiClient>(ai);
            FixedCorrelationAutonomy.Register(s, level);
        }, settings);
        var customer = await host.WithDbAsync(async db =>
        {
            var plan = new Plan
            {
                Name = "Pro-" + Guid.NewGuid().ToString("N")[..6], MinIntervalSeconds = 15, MaxMonitors = 100,
                AiFeatures = AiFeatures.Format(AiFeatures.All.Select(f => f.Name)), AiMonthlyCallLimit = callLimit
            };
            var c = new Customer { Name = "C", ContactEmail = "ops@example.test", Plan = plan, AiEnabled = true, AlertGroupingEnabled = grouping };
            db.Customers.Add(c);
            db.AlertChannels.Add(new AlertChannel { Customer = c, Name = "Ops", Type = AlertChannelType.Email, Target = "ops@example.test", IsEnabled = true });
            await db.SaveChangesAsync();
            return c;
        });
        return new Setup(host, checker, notifier, ai, customer);
    }

    [Fact]
    public async Task Follower_down_is_folded_into_the_primary_and_notified_when_the_primary_recovers()
    {
        await using var t = await CreateAsync();
        var a = await t.MonitorAsync("https://api.example.test/a", "Orders");
        var b = await t.MonitorAsync("https://api.example.test/b", "Users");
        var c = await t.MonitorAsync("https://elsewhere.test/", "Elsewhere"); // same error, other host: ambiguous → own alert

        await t.DownAsync(a);
        await t.DownAsync(b);
        await t.DownAsync(c);

        // A's DOWN went out; B's was folded (recorded, not sent); C was left out and got its own.
        Assert.Equal(["Orders", "Elsewhere"], t.Sent.Select(m => m.MonitorName));
        var folded = Assert.Single(await t.NotificationsAsync(b.Id));
        Assert.False(folded.Success);
        Assert.Equal(0, folded.Attempts);
        Assert.StartsWith("Grouped with incident #", folded.Error);
        var g = Assert.Single(await t.GroupsAsync());
        Assert.Equal(IncidentGroupStatus.Auto, g.Status);
        Assert.Equal(AiAutonomy.A3, g.Mode);
        Assert.Equal(2, g.Members.Count);
        Assert.NotNull(g.Members.Single(m => m.Role == IncidentGroupRole.Rule).FoldedAtUtc);
        // Grouping never closes or loses an incident, and never asked the model on the alert path.
        Assert.Equal(3, await t.Host.WithDbAsync(db => db.Incidents.CountAsync(i => i.ResolvedAtUtc == null)));
        Assert.Empty(t.Ai.Requests);

        // The primary recovers while B is still down: the group is undone and B gets its own DOWN now.
        await t.UpAsync(a);
        Assert.Equal(IncidentGroupStatus.Dissolved, Assert.Single(await t.GroupsAsync()).Status);
        Assert.Equal(["Orders", "Elsewhere", "Orders", "Users"], t.Sent.Select(m => m.MonitorName));
        Assert.Equal([AlertKind.Down, AlertKind.Down, AlertKind.Up, AlertKind.Down], t.Sent.Select(m => m.Kind));
        var bIncident = await t.Host.WithDbAsync(db => db.Incidents.SingleAsync(i => i.MonitorId == b.Id));
        Assert.Equal(bIncident.Id, t.Sent[^1].IncidentId);

        // From now on B is on its own.
        await t.UpAsync(b);
        Assert.Equal(AlertKind.Up, t.Sent[^1].Kind);
        Assert.Equal("Users", t.Sent[^1].MonitorName);
    }

    [Fact]
    public async Task A_follower_that_recovers_first_is_folded_on_both_alerts()
    {
        await using var t = await CreateAsync();
        var a = await t.MonitorAsync("https://api.example.test/a", "Orders");
        var b = await t.MonitorAsync("https://api.example.test/b", "Users");
        await t.DownAsync(a);
        await t.DownAsync(b);
        await t.UpAsync(b);   // primary still down: covered by the group

        Assert.Equal(["Orders"], t.Sent.Select(m => m.MonitorName));
        var rows = await t.NotificationsAsync(b.Id);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.StartsWith("Grouped with incident #", r.Error));

        await t.UpAsync(a);
        Assert.Equal([AlertKind.Down, AlertKind.Up], t.Sent.Select(m => m.Kind)); // B was resolved: nothing to re-send
        Assert.Equal(IncidentGroupStatus.Dissolved, Assert.Single(await t.GroupsAsync()).Status);
    }

    [Theory]
    [InlineData("autonomy-a1")]
    [InlineData("switched-off")]
    [InlineData("budget")]
    [InlineData("ai-off")]
    public async Task Every_down_is_sent_on_its_own_when_automatic_grouping_is_not_active(string reason)
    {
        await using var t = await CreateAsync(
            level: reason == "autonomy-a1" ? AiAutonomy.A1 : AiAutonomy.A3,
            grouping: reason != "switched-off",
            callLimit: reason == "budget" ? 1 : 100);
        if (reason == "budget")
            await t.Host.WithDbAsync(async db =>
            {
                db.AiDecisions.Add(new AiDecision { CustomerId = t.Customer.Id, Feature = AiFeatures.IncidentInterpretation, SubjectType = "Incident", SubjectId = "1",
                    Status = AiDecisionStatus.Succeeded, InputTokens = 10, OutputTokens = 10, CreatedAtUtc = t.Host.Now });
                await db.SaveChangesAsync();
            });
        if (reason == "ai-off")
            await t.Host.WithDbAsync(db => db.Customers.Where(c => c.Id == t.Customer.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.AiEnabled, false)));
        var a = await t.MonitorAsync("https://api.example.test/a", "Orders");
        var b = await t.MonitorAsync("https://api.example.test/b", "Users");

        await t.DownAsync(a);
        await t.DownAsync(b);

        Assert.Equal(["Orders", "Users"], t.Sent.Select(m => m.MonitorName));
        Assert.Empty(await t.GroupsAsync());
    }

    [Fact]
    public async Task Follower_is_sent_when_the_primary_down_was_not_delivered()
    {
        // The first DOWN fails on every channel: nobody knows about the outage, so nothing may be folded into it.
        var calls = 0;
        await using var t = await CreateAsync(email: _ => Interlocked.Increment(ref calls) == 1 ? NotifyResult.Fail("mailbox full") : NotifyResult.Ok);
        var a = await t.MonitorAsync("https://api.example.test/a", "Orders");
        var b = await t.MonitorAsync("https://api.example.test/b", "Users");
        await t.DownAsync(a);
        await t.DownAsync(b);

        Assert.Equal(["Orders", "Users"], t.Sent.Select(m => m.MonitorName));
        Assert.True((await t.NotificationsAsync(b.Id)).Single().Success);
        Assert.Empty(await t.GroupsAsync());
    }

    [Fact]
    public async Task Incidents_outside_the_window_or_with_unrelated_errors_are_not_folded()
    {
        await using var t = await CreateAsync();
        var a = await t.MonitorAsync("https://api.example.test/a", "Orders");
        var b = await t.MonitorAsync("https://api.example.test/b", "Users");
        var late = await t.MonitorAsync("https://api.example.test/c", "Late");
        await t.DownAsync(a);
        await t.DownAsync(b, "Unexpected status code 404 (expected 200-299)"); // same host, unrelated error: ambiguous
        t.Host.Time.Advance(TimeSpan.FromMinutes(6));
        await t.DownAsync(late);                                                 // after the window

        Assert.Equal(["Orders", "Users", "Late"], t.Sent.Select(m => m.MonitorName));
    }

    [Fact]
    public async Task Down_alerts_never_wait_for_the_model()
    {
        await using var t = await CreateAsync();
        // A model that never answers: nothing on the alert path may depend on it.
        t.Ai.Responder = _ => throw new InvalidOperationException("the alert path must not call the model");
        var a = await t.MonitorAsync("https://api.example.test/a", "Orders");
        var b = await t.MonitorAsync("https://api.example.test/b", "Users");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await t.DownAsync(a);
        await t.DownAsync(b);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
        Assert.Single(t.Sent);
        Assert.Empty(t.Ai.Requests);
    }

    [Fact]
    public async Task Escalation_of_a_folded_incident_is_unchanged()
    {
        await using var t = await CreateAsync();
        var a = await t.MonitorAsync("https://api.example.test/a", "Orders");
        var b = await t.MonitorAsync("https://api.example.test/b", "Users");
        await t.DownAsync(a);
        await t.DownAsync(b);
        Assert.Single(t.Sent);

        var (monitor, incident) = await t.Host.WithDbAsync(async db =>
            (await db.Monitors.AsNoTracking().SingleAsync(m => m.Id == b.Id), await db.Incidents.AsNoTracking().SingleAsync(i => i.MonitorId == b.Id)));
        var channel = await t.Host.WithDbAsync(db => db.AlertChannels.Select(c => c.Id).SingleAsync());
        Assert.True(t.Host.Alerts.TryEnqueue(AlertMessage.ForEscalation(monitor, incident.Id, incident.StartedAtUtc, 1, [channel], t.Host.Now)));
        await t.Host.AlertsIdleAsync();

        Assert.Equal(AlertKind.Escalation, t.Sent[^1].Kind);
        Assert.Equal("Users", t.Sent[^1].MonitorName);
    }

    [Fact]
    public async Task Maintenance_suppression_is_unchanged_and_creates_no_group()
    {
        await using var t = await CreateAsync();
        var a = await t.MonitorAsync("https://api.example.test/a", "Orders");
        var b = await t.MonitorAsync("https://api.example.test/b", "Users");
        await t.Host.WithDbAsync(async db =>
        {
            var w = new MaintenanceWindow { CustomerId = t.Customer.Id, Name = "Deploy", StartsAtUtc = t.Host.Now.AddMinutes(-1), EndsAtUtc = t.Host.Now.AddHours(1), AppliesToAllMonitors = false };
            w.Monitors.Add(await db.Monitors.SingleAsync(m => m.Id == b.Id));
            db.MaintenanceWindows.Add(w);
            await db.SaveChangesAsync();
        });
        await t.DownAsync(a);
        await t.DownAsync(b);

        Assert.Equal(["Orders"], t.Sent.Select(m => m.MonitorName));  // B in maintenance: no DOWN, as before
        Assert.Empty(await t.NotificationsAsync(b.Id));
        Assert.Empty(await t.GroupsAsync());
    }

    [Fact]
    public async Task Admin_ungroup_and_switching_off_notify_the_folded_members()
    {
        await using var t = await CreateAsync();
        var a = await t.MonitorAsync("https://api.example.test/a", "Orders");
        var b = await t.MonitorAsync("https://api.example.test/b", "Users");
        var c = await t.MonitorAsync("https://api.example.test/c", "Billing");
        await t.DownAsync(a);
        await t.DownAsync(b);
        await t.DownAsync(c);
        Assert.Single(t.Sent);
        var g = Assert.Single(await t.GroupsAsync());
        Assert.Equal(3, g.Members.Count);

        // Another tenant cannot undo it.
        Assert.Equal(0, await t.Host.InScopeAsync(sp => sp.GetRequiredService<IncidentCorrelationService>().DissolveAsync(t.Customer.Id + 1000, g.Id, "x")));

        await t.Host.InScopeAsync(async sp => { await sp.GetRequiredService<IncidentCorrelationService>().SetEnabledAsync(t.Customer.Id, false, "admin"); return 0; });
        await t.Host.AlertsIdleAsync();

        Assert.Equal(IncidentGroupStatus.Dissolved, Assert.Single(await t.GroupsAsync()).Status);
        Assert.Equal(["Orders", "Users", "Billing"], t.Sent.Select(m => m.MonitorName).Take(1).Concat(t.Sent.Skip(1).Select(m => m.MonitorName).Order(StringComparer.Ordinal).Reverse()));
        Assert.All(t.Sent, m => Assert.Equal(AlertKind.Down, m.Kind));
    }

    [Fact]
    public async Task Watcher_undoes_a_group_whose_primary_closed_without_an_up_alert()
    {
        await using var t = await CreateAsync();
        var a = await t.MonitorAsync("https://api.example.test/a", "Orders");
        var b = await t.MonitorAsync("https://api.example.test/b", "Users");
        await t.DownAsync(a);
        await t.DownAsync(b);
        // e.g. the primary's monitor was paused: its incident is closed without an UP alert.
        await t.Host.WithDbAsync(db => db.Incidents.Where(i => i.MonitorId == a.Id).ExecuteUpdateAsync(u => u.SetProperty(i => i.ResolvedAtUtc, t.Host.Now)));

        var result = await t.Host.InScopeAsync(sp => sp.GetRequiredService<CorrelationScanner>().ScanAsync());
        await t.Host.AlertsIdleAsync();

        Assert.Equal(1, result.Settled);
        Assert.Equal(["Orders", "Users"], t.Sent.Select(m => m.MonitorName));
    }
}
