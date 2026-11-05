using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using TriggerCheck.Ai;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Features.Correlation;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Alerting;
using TriggerCheck.Monitoring.Checks;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Tests.Monitoring;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Tests.Ai.Features;

/// <summary>
/// AI-4 at A3 on the real alert path (check runner → queue → sender → dispatcher), with the autonomy raised for the
/// test only and no background services running (the scanner is driven explicitly). Invariants: no incident is lost or
/// closed, DOWN alerts never wait for the model or a busy database, folded DOWNs always reach the customer (undo, fold
/// cap, switch-off of any kind, full / stopped queue, restart), monitors left out get their own alerts, escalation and
/// maintenance are unchanged.
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

    /// <summary>E-mail notifier recording every delivered message; <see cref="Behavior"/> decides per attempt.</summary>
    private sealed class Mailbox : INotifier
    {
        public ConcurrentQueue<AlertMessage> Sent { get; } = new();
        public Func<AlertMessage, NotifyResult>? Behavior { get; set; }
        public AlertChannelType Type => AlertChannelType.Email;
        /// <summary>Deliveries of this monitor wait (asynchronously) for <see cref="Release"/>; <see cref="Entered"/> fires on the first.</summary>
        public int? HoldMonitorId { get; set; }
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<NotifyResult> SendAsync(NotifyTarget target, AlertMessage message, CancellationToken ct)
        {
            if (message.MonitorId == HoldMonitorId)
            {
                Entered.TrySetResult();
                await Release.Task;
            }
            var r = Behavior?.Invoke(message) ?? NotifyResult.Ok;
            if (r.Success) Sent.Enqueue(message);
            return r;
        }
    }

    /// <summary>The alert queue as everyone sees it, but able to refuse (full / shut down).</summary>
    private sealed class GateQueue(AlertQueue inner) : IAlertQueue
    {
        public volatile bool Refuse;
        /// <summary>Runs right before each enqueue (sees the database as the caller left it at that moment).</summary>
        public Action<AlertMessage>? OnEnqueue;
        public bool TryEnqueue(AlertMessage message)
        {
            OnEnqueue?.Invoke(message);
            return !Refuse && inner.TryEnqueue(message);
        }
    }


    private sealed record Setup(MonitoringTestHost Host, MapChecker Checker, Mailbox Mail, FakeAiClient Ai, GateQueue Queue, Customer Customer) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Host.DisposeAsync();

        public List<AlertMessage> Sent => Mail.Sent.ToList();
        public List<string> Names(AlertKind kind) => Sent.Where(m => m.Kind == kind).Select(m => m.MonitorName).ToList();

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

        /// <summary>One watcher poll (the scanner as the lease holder runs it), then waits for the queued alerts.</summary>
        public async Task<CorrelationScanner.Result> ScanAsync()
        {
            var r = await Host.InScopeAsync(sp => sp.GetRequiredService<CorrelationScanner>().ScanAsync());
            await Host.AlertsIdleAsync();
            return r;
        }

        public Task<T> GroupsServiceAsync<T>(Func<IncidentCorrelationService, Task<T>> f) =>
            Host.InScopeAsync(sp => f(sp.GetRequiredService<IncidentCorrelationService>()));

        public Task<List<IncidentGroup>> GroupsAsync() =>
            Host.WithDbAsync(db => db.IncidentGroups.AsNoTracking().Include(g => g.Members).ToListAsync());

        public Task<List<Notification>> NotificationsAsync(int monitorId) =>
            Host.WithDbAsync(db => db.Notifications.AsNoTracking().Where(n => n.MonitorId == monitorId).OrderBy(n => n.Id).ToListAsync());

        public Task<Incident> IncidentAsync(int monitorId) =>
            Host.WithDbAsync(db => db.Incidents.AsNoTracking().Include(i => i.Monitor).OrderByDescending(i => i.Id).FirstAsync(i => i.MonitorId == monitorId));
    }

    private static async Task<Setup> CreateAsync(AiAutonomy level = AiAutonomy.A3, bool grouping = true, int callLimit = 100,
        Dictionary<string, string?>? aiSettings = null)
    {
        var checker = new MapChecker();
        var mail = new Mailbox();
        var ai = new FakeAiClient();
        var ai0 = new Dictionary<string, string?> { ["Ai:Enabled"] = "true", ["Ai:ApiKey"] = "" };
        foreach (var kv in aiSettings ?? new()) ai0[kv.Key] = kv.Value;
        var aiConfig = new ConfigurationBuilder().AddInMemoryCollection(ai0).Build();
        var host = await MonitoringTestHost.CreateAsync(s =>
        {
            s.ReplaceCheckers(checker).ReplaceNotifiers(mail);
            s.AddAi(aiConfig);
            s.RemoveAll<IHostedService>(); // no watcher / AI worker in the background: the test drives the scanner
            s.RemoveAll<IAiClient>();
            s.AddSingleton<IAiClient>(ai);
            s.AddSingleton(sp => new GateQueue(sp.GetRequiredService<AlertQueue>()));
            s.AddSingleton<IAlertQueue>(sp => sp.GetRequiredService<GateQueue>());
            FixedCorrelationAutonomy.Register(s, level);
        });
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
        return new Setup(host, checker, mail, ai, host.Services.GetRequiredService<GateQueue>(), customer);
    }

    /// <summary>A (primary) delivered, B folded into it.</summary>
    private static async Task<(MonitorEntity A, MonitorEntity B)> FoldOneAsync(Setup t)
    {
        var a = await t.MonitorAsync("https://api.example.test/a", "Orders");
        var b = await t.MonitorAsync("https://api.example.test/b", "Users");
        await t.DownAsync(a);
        await t.DownAsync(b);
        Assert.Equal(["Orders"], t.Names(AlertKind.Down));
        Assert.Equal(IncidentGroupStatus.Auto, Assert.Single(await t.GroupsAsync()).Status);
        return (a, b);
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
        Assert.Equal(["Elsewhere", "Orders"], t.Names(AlertKind.Down).Order());
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

        // The primary recovers while B is still down: the UP goes out, then the group is undone and B gets its own DOWN.
        await t.UpAsync(a);
        Assert.Equal(IncidentGroupStatus.Dissolved, Assert.Single(await t.GroupsAsync()).Status);
        Assert.Equal(["Orders"], t.Names(AlertKind.Up));
        Assert.Equal(["Elsewhere", "Orders", "Users"], t.Names(AlertKind.Down).Order());
        var bDown = t.Sent.Single(m => m.Kind == AlertKind.Down && m.MonitorName == "Users");
        Assert.Equal((await t.IncidentAsync(b.Id)).Id, bDown.IncidentId);
        Assert.True(bDown.Deferred);
        Assert.NotNull((await t.GroupsAsync()).Single().Members.Single(m => m.Role == IncidentGroupRole.Rule).NotifiedAtUtc);

        // From now on B is on its own; a later scan does not send its DOWN again.
        await t.UpAsync(b);
        Assert.Equal(["Orders", "Users"], t.Names(AlertKind.Up).Order());
        await t.ScanAsync();
        Assert.Single(t.Names(AlertKind.Down), "Users");
    }

    [Fact]
    public async Task A_follower_that_recovers_unreported_is_folded_on_both_alerts()
    {
        await using var t = await CreateAsync();
        var (a, b) = await FoldOneAsync(t);
        await t.UpAsync(b);   // primary still down and nobody was told about B yet: covered by the group

        Assert.Empty(t.Names(AlertKind.Up));
        var rows = await t.NotificationsAsync(b.Id);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.StartsWith("Grouped with incident #", r.Error));

        await t.UpAsync(a);
        Assert.Equal(["Orders"], t.Names(AlertKind.Down)); // B was resolved: nothing to re-send
        Assert.Equal(["Orders"], t.Names(AlertKind.Up));
        Assert.Equal(IncidentGroupStatus.Dissolved, Assert.Single(await t.GroupsAsync()).Status);
    }

    [Fact]
    public async Task One_debounced_follow_up_lists_the_folded_monitors_and_later_folds_get_another()
    {
        await using var t = await CreateAsync();
        var a = await t.MonitorAsync("https://api.example.test/a", "Orders");
        var b = await t.MonitorAsync("https://api.example.test/b", "Users");
        var c = await t.MonitorAsync("https://api.example.test/c", "Billing");
        var d = await t.MonitorAsync("https://api.example.test/d", "Search");
        await t.DownAsync(a);
        await t.DownAsync(b);
        await t.DownAsync(c);
        Assert.Equal(0, (await t.ScanAsync()).FollowUps); // debounce: not yet

        t.Host.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(1, (await t.ScanAsync()).FollowUps);
        var first = Assert.Single(t.Sent, m => m.Kind == AlertKind.GroupUpdate);
        Assert.Equal("Orders", first.MonitorName);
        Assert.Equal((await t.IncidentAsync(a.Id)).Id, first.IncidentId);
        Assert.Equal(["Users", "Billing"], first.AlsoAffected!.Select(m => m.Name));
        Assert.Equal([b.Id, c.Id], first.AlsoAffected!.Select(m => m.MonitorId));
        Assert.Contains("Also affected (likely same cause as incident #", first.Headline);
        Assert.Equal(0, (await t.ScanAsync()).FollowUps); // reported once

        await t.DownAsync(d);                                // folded after the follow-up
        Assert.Equal(0, (await t.ScanAsync()).FollowUps);
        t.Host.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(1, (await t.ScanAsync()).FollowUps);
        Assert.Equal(["Search"], t.Sent.Where(m => m.Kind == AlertKind.GroupUpdate).Last().AlsoAffected!.Select(m => m.Name));

        // A reported member's recovery is not folded any more: the customer was told it is down.
        await t.UpAsync(b);
        Assert.Equal(["Users"], t.Names(AlertKind.Up));
    }

    [Fact]
    public async Task A_follower_still_down_after_the_fold_cap_gets_its_own_down()
    {
        await using var t = await CreateAsync(aiSettings: new() { ["Ai:Correlation:MaxFoldMinutes"] = "15" });
        var (_, b) = await FoldOneAsync(t);
        t.Host.Time.Advance(TimeSpan.FromMinutes(14));
        await t.ScanAsync();
        Assert.DoesNotContain("Users", t.Names(AlertKind.Down));

        t.Host.Time.Advance(TimeSpan.FromMinutes(2));
        await t.ScanAsync();
        var own = Assert.Single(t.Sent, m => m.Kind == AlertKind.Down && m.MonitorName == "Users");
        Assert.True(own.Deferred);
        Assert.Equal(IncidentGroupStatus.Auto, Assert.Single(await t.GroupsAsync()).Status); // the primary is still down

        await t.UpAsync(b); // notified on its own: its recovery is too
        Assert.Equal(["Users"], t.Names(AlertKind.Up));
    }

    [Fact]
    public async Task Undo_with_a_refusing_queue_is_retried_by_the_watcher_and_never_lost()
    {
        await using var t = await CreateAsync();
        var (a, _) = await FoldOneAsync(t);

        t.Queue.Refuse = true;  // queue full / shutting down while the primary's recovery undoes the group
        t.Checker.Outcomes[a.Id] = CheckOutcome.Up(5, 200);
        await t.Host.InScopeAsync(sp => sp.GetRequiredService<TriggerCheck.Monitoring.IMonitorCheckRunner>().RunAsync(a.Id));
        // The UP itself was refused too: deliver it by hand, the way the queue would have.
        Assert.Empty(t.Names(AlertKind.Up));
        await t.Host.InScopeAsync(sp => sp.GetRequiredService<IAlertDispatcher>().DispatchAsync(
            AlertMessage.For(a, AlertKind.Up, t.Host.Now, TimeSpan.FromMinutes(1))));
        var g = Assert.Single(await t.GroupsAsync());
        Assert.Equal(IncidentGroupStatus.Dissolved, g.Status);
        Assert.Null(g.Members.Single(m => m.Role == IncidentGroupRole.Rule).NotifiedAtUtc); // not marked: nothing was queued
        Assert.DoesNotContain("Users", t.Names(AlertKind.Down));
        Assert.Equal(0, (await t.ScanAsync()).Notified); // still refusing: kept for later

        t.Queue.Refuse = false;
        Assert.Equal(1, (await t.ScanAsync()).Notified);
        Assert.Contains("Users", t.Names(AlertKind.Down));
        Assert.Equal(0, (await t.ScanAsync()).Notified); // exactly once
    }

    [Fact]
    public async Task After_a_restart_held_back_downs_of_undone_groups_are_sent()
    {
        // State a crashed process left behind: group undone, the member's own DOWN never queued.
        await using var t = await CreateAsync();
        var a = await t.MonitorAsync("https://api.example.test/a", "Orders");
        var b = await t.MonitorAsync("https://api.example.test/b", "Users");
        await t.Host.WithDbAsync(async db =>
        {
            var ia = new Incident { MonitorId = a.Id, StartedAtUtc = t.Host.Now.AddMinutes(-3), ResolvedAtUtc = t.Host.Now.AddMinutes(-1), Cause = Timeout };
            var ib = new Incident { MonitorId = b.Id, StartedAtUtc = t.Host.Now.AddMinutes(-2), Cause = Timeout };
            db.Incidents.AddRange(ia, ib);
            await db.SaveChangesAsync();
            var g = new IncidentGroup { CustomerId = t.Customer.Id, PrimaryIncidentId = ia.Id, Status = IncidentGroupStatus.Dissolved, Mode = AiAutonomy.A3, CreatedAtUtc = t.Host.Now.AddMinutes(-2) };
            g.Members.Add(new IncidentGroupMember { IncidentId = ia.Id, CustomerId = t.Customer.Id, Role = IncidentGroupRole.Primary, Included = true });
            g.Members.Add(new IncidentGroupMember { IncidentId = ib.Id, CustomerId = t.Customer.Id, Role = IncidentGroupRole.Rule, Included = true, FoldedAtUtc = t.Host.Now.AddMinutes(-2) });
            db.IncidentGroups.Add(g);
            await db.SaveChangesAsync();
        });

        Assert.Equal(1, (await t.ScanAsync()).Notified);
        Assert.Equal(["Users"], t.Names(AlertKind.Down));
    }

    [Theory]
    [InlineData("opt-out")]
    [InlineData("ai-off")]
    [InlineData("autonomy-lowered")]
    [InlineData("plan")]
    [InlineData("budget")]
    [InlineData("switched-off")]
    public async Task Every_way_of_turning_grouping_off_unfolds_within_one_poll(string how)
    {
        await using var t = await CreateAsync();
        await FoldOneAsync(t);
        var cid = t.Customer.Id;
        switch (how)
        {
            case "opt-out":
                await t.Host.WithDbAsync(db => db.Customers.Where(c => c.Id == cid).ExecuteUpdateAsync(u => u.SetProperty(c => c.AiEnabled, false)));
                break;
            case "ai-off": // Ai:Enabled=false (applied at restart in production)
                t.Host.Services.GetRequiredService<IOptions<AiOptions>>().Value.Enabled = false;
                break;
            case "autonomy-lowered":
                t.Host.Services.GetRequiredService<IOptions<AiCorrelationOptions>>().Value.MaxAutonomy = AiAutonomy.A1;
                break;
            case "plan":
                await t.Host.WithDbAsync(db => db.Plans.Where(p => db.Customers.Any(c => c.Id == cid && c.PlanId == p.Id))
                    .ExecuteUpdateAsync(u => u.SetProperty(p => p.AiFeatures, AiFeatures.IncidentInterpretation)));
                break;
            case "budget":
                await t.Host.WithDbAsync(async db =>
                {
                    await db.Plans.Where(p => db.Customers.Any(c => c.Id == cid && c.PlanId == p.Id)).ExecuteUpdateAsync(u => u.SetProperty(p => p.AiMonthlyCallLimit, 1));
                    db.AiDecisions.Add(new AiDecision
                    {
                        CustomerId = cid, Feature = AiFeatures.IncidentInterpretation, SubjectType = "Incident", SubjectId = "1",
                        Status = AiDecisionStatus.Succeeded, InputTokens = 10, OutputTokens = 10, CreatedAtUtc = t.Host.Now
                    });
                    await db.SaveChangesAsync();
                });
                break;
            case "switched-off":
                await t.Host.WithDbAsync(db => db.Customers.Where(c => c.Id == cid).ExecuteUpdateAsync(u => u.SetProperty(c => c.AlertGroupingEnabled, false)));
                break;
        }

        var r = await t.ScanAsync();

        Assert.Equal(1, r.Settled);
        Assert.Equal(IncidentGroupStatus.Dissolved, Assert.Single(await t.GroupsAsync()).Status);
        Assert.Equal(["Orders", "Users"], t.Names(AlertKind.Down).Order());
    }

    [Theory]
    [InlineData("autonomy-a1")]
    [InlineData("switched-off")]
    [InlineData("budget")]
    [InlineData("opt-out")]
    public async Task Every_down_is_sent_on_its_own_when_automatic_grouping_is_not_active(string reason)
    {
        await using var t = await CreateAsync(
            level: reason == "autonomy-a1" ? AiAutonomy.A1 : AiAutonomy.A3,
            grouping: reason != "switched-off",
            callLimit: reason == "budget" ? 1 : 100);
        if (reason == "budget")
            await t.Host.WithDbAsync(async db =>
            {
                db.AiDecisions.Add(new AiDecision
                {
                    CustomerId = t.Customer.Id, Feature = AiFeatures.IncidentInterpretation, SubjectType = "Incident", SubjectId = "1",
                    Status = AiDecisionStatus.Succeeded, InputTokens = 10, OutputTokens = 10, CreatedAtUtc = t.Host.Now
                });
                await db.SaveChangesAsync();
            });
        if (reason == "opt-out")
            await t.Host.WithDbAsync(db => db.Customers.Where(c => c.Id == t.Customer.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.AiEnabled, false)));
        var a = await t.MonitorAsync("https://api.example.test/a", "Orders");
        var b = await t.MonitorAsync("https://api.example.test/b", "Users");

        await t.DownAsync(a);
        await t.DownAsync(b);

        Assert.Equal(["Orders", "Users"], t.Names(AlertKind.Down).Order());
        Assert.Empty(await t.GroupsAsync());
    }

    [Fact]
    public async Task Follower_is_sent_when_the_primary_down_was_not_delivered()
    {
        // The first DOWN fails on every channel: nobody knows about the outage, so nothing may be folded into it.
        await using var t = await CreateAsync();
        var first = true;
        t.Mail.Behavior = _ =>
        {
            if (!first) return NotifyResult.Ok;
            first = false;
            return NotifyResult.Fail("mailbox full");
        };
        var a = await t.MonitorAsync("https://api.example.test/a", "Orders");
        var b = await t.MonitorAsync("https://api.example.test/b", "Users");
        await t.DownAsync(a);
        await t.DownAsync(b);

        Assert.Equal(["Users"], t.Names(AlertKind.Down));
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

        Assert.Equal(["Late", "Orders", "Users"], t.Names(AlertKind.Down).Order());
    }

    [Fact]
    public async Task Down_alerts_never_wait_for_the_model()
    {
        await using var t = await CreateAsync();
        // A model that always fails: nothing on the alert path may depend on it (no AI worker runs in this host).
        t.Ai.Responder = _ => throw new InvalidOperationException("the alert path must not call the model");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await FoldOneAsync(t);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
        Assert.Empty(t.Ai.Requests);
    }

    [Fact]
    public async Task Fold_gives_up_quickly_and_sends_the_alert_when_the_database_is_locked()
    {
        await using var t = await CreateAsync(aiSettings: new() { ["Ai:Correlation:AlertPathLockTimeoutSeconds"] = "1" });
        var a = await t.MonitorAsync("https://api.example.test/a", "Orders");
        var b = await t.MonitorAsync("https://api.example.test/b", "Users");
        await t.DownAsync(a);
        var ib = await t.Host.WithDbAsync(async db =>
        {
            var i = new Incident { MonitorId = b.Id, StartedAtUtc = t.Host.Now, Cause = Timeout };
            db.Incidents.Add(i);
            await db.SaveChangesAsync();
            return i;
        });
        var cs = await t.Host.WithDbAsync(db => Task.FromResult(db.Database.GetConnectionString()!));

        // Another writer holds the write lock (Postgres: the transaction lock of PostgresWriteLockInterceptor).
        await using DbConnection other = TestDatabase.UsePostgres ? new NpgsqlConnection(cs) : new SqliteConnection(cs);
        await other.OpenAsync();
        await using (var cmd = other.CreateCommand())
        {
            cmd.CommandText = TestDatabase.UsePostgres
                ? $"BEGIN; SELECT pg_advisory_xact_lock({PostgresWriteLockInterceptor.LockKey});"
                : "BEGIN IMMEDIATE;";
            await cmd.ExecuteNonQueryAsync();
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var reason = await t.GroupsServiceAsync(s => s.TryFoldAsync(new AlertMessage(b.Id, t.Customer.Id, "Users", b.Target, AlertKind.Down, Timeout, t.Host.Now, IncidentId: ib.Id)));
        sw.Stop();
        await using (var cmd = other.CreateCommand())
        {
            cmd.CommandText = "ROLLBACK;";
            await cmd.ExecuteNonQueryAsync();
        }

        Assert.Null(reason); // fail open: the DOWN is sent on its own
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"waited {sw.Elapsed}");
        Assert.Empty(await t.GroupsAsync());
    }

    private sealed class CountingInterceptor : DbCommandInterceptor
    {
        public int Commands;

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        { Interlocked.Increment(ref Commands); return result; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { Interlocked.Increment(ref Commands); return ValueTask.FromResult(result); }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        { Interlocked.Increment(ref Commands); return ValueTask.FromResult(result); }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken ct = default)
        { Interlocked.Increment(ref Commands); return ValueTask.FromResult(result); }
    }

    [Fact]
    public async Task At_A1_the_alert_hook_runs_no_database_command_for_down_or_up()
    {
        await using var t = await CreateAsync(level: AiAutonomy.A1);
        var a = await t.MonitorAsync("https://api.example.test/a", "Orders");
        await t.DownAsync(a);
        await t.GroupsServiceAsync(async s => { await s.RefreshRuntimeStateAsync(); return 0; }); // no automatic group exists

        var counter = new CountingInterceptor();
        var cs = await t.Host.WithDbAsync(db => Task.FromResult(db.Database.GetConnectionString()!));
        var countedOptions = new DbContextOptionsBuilder<AppDbContext>();
        DatabaseOptions.Configure(countedOptions, TestDatabase.UsePostgres ? DatabaseProvider.Postgres : DatabaseProvider.Sqlite, cs);
        await using var counted = new AppDbContext(countedOptions.AddInterceptors(counter).Options);
        var incident = await t.IncidentAsync(a.Id);
        counter.Commands = 0;

        await t.Host.InScopeAsync(async sp =>
        {
            var service = ActivatorUtilities.CreateInstance<IncidentCorrelationService>(sp, counted);
            IAlertGrouping hook = new CorrelationAlertGrouping(service);
            var down = new AlertMessage(a.Id, t.Customer.Id, "Orders", a.Target, AlertKind.Down, Timeout, t.Host.Now, IncidentId: incident.Id);
            var up = AlertMessage.For(a, AlertKind.Up, t.Host.Now, TimeSpan.FromMinutes(1));
            Assert.Null(await hook.TryFoldAsync(down, default));
            Assert.Null(await hook.TryFoldAsync(up, default));
            await hook.AfterDeliveredAsync(down, default);
            await hook.AfterDeliveredAsync(up, default);
            return 0;
        });

        Assert.Equal(0, counter.Commands);
        // The counter works: a query on the counted context is seen.
        await counted.Monitors.CountAsync();
        Assert.Equal(1, counter.Commands);
    }

    [Fact]
    public async Task Escalation_of_a_folded_incident_is_unchanged()
    {
        await using var t = await CreateAsync();
        var (_, b) = await FoldOneAsync(t);

        var incident = await t.IncidentAsync(b.Id);
        var channel = await t.Host.WithDbAsync(db => db.AlertChannels.Select(c => c.Id).SingleAsync());
        Assert.True(t.Host.Alerts.TryEnqueue(AlertMessage.ForEscalation(incident.Monitor, incident.Id, incident.StartedAtUtc, 1, [channel], t.Host.Now)));
        await t.Host.AlertsIdleAsync();

        Assert.Equal(["Users"], t.Names(AlertKind.Escalation));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_deferred_down_does_not_supersede_a_retrying_escalation(bool deferred)
    {
        await using var t = await CreateAsync();
        var (_, b) = await FoldOneAsync(t);
        var incident = await t.IncidentAsync(b.Id);
        var channel = await t.Host.WithDbAsync(db => db.AlertChannels.Select(c => c.Id).SingleAsync());
        var escalationAttempts = 0;
        t.Mail.Behavior = m => m.Kind == AlertKind.Escalation && Interlocked.Increment(ref escalationAttempts) == 1
            ? NotifyResult.Transient("SMTP 451") : NotifyResult.Ok;

        Assert.True(t.Host.Alerts.TryEnqueue(AlertMessage.ForEscalation(incident.Monitor, incident.Id, incident.StartedAtUtc, 1, [channel], t.Host.Now)));
        for (var i = 0; i < 200 && Volatile.Read(ref escalationAttempts) == 0; i++) await Task.Delay(10); // first attempt failed, retry pending
        var down = AlertMessage.For(incident.Monitor, AlertKind.Down, incident.StartedAtUtc) with { IncidentId = incident.Id, Deferred = deferred };
        Assert.True(t.Host.Alerts.TryEnqueue(down));
        await t.Host.AdvanceUntilAlertsIdleAsync();

        var escalationRows = (await t.NotificationsAsync(b.Id)).Where(n => n.Subject.Contains("ESCALATION")).ToList();
        if (deferred)
        {
            Assert.Equal(["Users"], t.Names(AlertKind.Escalation));
            Assert.True(Assert.Single(escalationRows).Success);
        }
        else
        {
            // Control: an ordinary DOWN does make the waiting escalation stale (FEAT-5 behaviour, unchanged).
            Assert.Empty(t.Names(AlertKind.Escalation));
            Assert.Contains(AlertDispatcher.SupersededError, Assert.Single(escalationRows).Error);
        }
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

        Assert.Equal(["Orders"], t.Names(AlertKind.Down));  // B in maintenance: no DOWN, as before
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
        Assert.Equal(["Orders"], t.Names(AlertKind.Down));
        var g = Assert.Single(await t.GroupsAsync());
        Assert.Equal(3, g.Members.Count);

        // Another tenant cannot undo it.
        Assert.Equal(0, await t.GroupsServiceAsync(s => s.DissolveAsync(t.Customer.Id + 1000, g.Id, "x")));

        await t.GroupsServiceAsync(async s => { await s.SetEnabledAsync(t.Customer.Id, false, "admin"); return 0; });
        await t.Host.AlertsIdleAsync();

        Assert.Equal(IncidentGroupStatus.Dissolved, Assert.Single(await t.GroupsAsync()).Status);
        Assert.Equal(["Billing", "Orders", "Users"], t.Names(AlertKind.Down).Order());
        // Switched off: the next related DOWN is not folded.
        var d = await t.MonitorAsync("https://api.example.test/d", "Search");
        await t.DownAsync(d);
        Assert.Contains("Search", t.Names(AlertKind.Down));
    }

    [Fact]
    public async Task A_held_back_down_arriving_after_the_up_is_dropped()
    {
        await using var t = await CreateAsync();
        var (_, b) = await FoldOneAsync(t);
        var incident = await t.IncidentAsync(b.Id);
        await t.Host.WithDbAsync(db => db.Incidents.Where(i => i.Id == incident.Id).ExecuteUpdateAsync(u => u.SetProperty(i => i.ResolvedAtUtc, t.Host.Now)));

        // The held-back DOWN reaches the dispatcher after the incident recovered: stale, not sent.
        Assert.True(t.Host.Alerts.TryEnqueue(AlertMessage.For(incident.Monitor, AlertKind.Down, incident.StartedAtUtc) with { IncidentId = incident.Id, Deferred = true }));
        await t.Host.AlertsIdleAsync();
        Assert.DoesNotContain("Users", t.Names(AlertKind.Down));
        // NotifyPending does not even queue it for a recovered incident.
        await t.GroupsServiceAsync(async s => { await s.SetEnabledAsync(t.Customer.Id, false, "admin"); return 0; });
        await t.Host.AlertsIdleAsync();
        Assert.DoesNotContain("Users", t.Names(AlertKind.Down));
    }

    [Fact]
    public async Task A_held_back_down_never_replaces_a_waiting_up_or_cancels_the_delivery_in_progress()
    {
        await using var t = await CreateAsync();
        var (_, b) = await FoldOneAsync(t);
        var incident = await t.IncidentAsync(b.Id);
        // Group undone with B's own DOWN marked as queued, so its UPs are not folded and its incident stays open: only
        // the sender's lane rule can stop the stale DOWN here.
        await t.Host.WithDbAsync(async db =>
        {
            await db.IncidentGroups.ExecuteUpdateAsync(u => u.SetProperty(g => g.Status, IncidentGroupStatus.Dissolved));
            await db.IncidentGroupMembers.Where(m => m.IncidentId == incident.Id).ExecuteUpdateAsync(u => u.SetProperty(m => m.NotifiedAtUtc, t.Host.Now));
        });
        t.Mail.HoldMonitorId = b.Id;

        // A status alert of B is in delivery (held), its UP waits behind it, then the held-back DOWN arrives.
        Assert.True(t.Host.Alerts.TryEnqueue(new AlertMessage(b.Id, t.Customer.Id, "Users", b.Target, AlertKind.Up, null, t.Host.Now, TimeSpan.FromMinutes(1))));
        await t.Mail.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(t.Host.Alerts.TryEnqueue(new AlertMessage(b.Id, t.Customer.Id, "Users", b.Target, AlertKind.Up, null, t.Host.Now, TimeSpan.FromMinutes(1))));
        Assert.True(t.Host.Alerts.TryEnqueue(AlertMessage.For(incident.Monitor, AlertKind.Down, incident.StartedAtUtc) with { IncidentId = incident.Id, Deferred = true }));
        // Wait until the sender has read both: one of the three is then done (dropped, or superseded without the fix).
        for (var i = 0; i < 500 && t.Host.Alerts.Pending > 2; i++) await Task.Delay(10);
        Assert.Equal(2, t.Host.Alerts.Pending);
        t.Mail.Release.SetResult();
        await t.Host.AlertsIdleAsync();

        // Both UPs were delivered (none superseded by the stale DOWN); the DOWN was dropped.
        Assert.Equal(2, t.Names(AlertKind.Up).Count(n => n == "Users"));
        Assert.DoesNotContain("Users", t.Names(AlertKind.Down));
        Assert.DoesNotContain(await t.NotificationsAsync(b.Id), n => n.Error?.StartsWith(AlertDispatcher.SupersededError) == true);
    }

    [Fact]
    public async Task Overlapping_notify_calls_send_the_held_back_down_once()
    {
        await using var t = await CreateAsync();
        var (_, b) = await FoldOneAsync(t);
        await t.Host.WithDbAsync(db => db.IncidentGroups.ExecuteUpdateAsync(u => u.SetProperty(g => g.Status, IncidentGroupStatus.Dissolved)));

        var counts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => t.GroupsServiceAsync(s => s.NotifyPendingAsync())));
        await t.Host.AlertsIdleAsync();

        Assert.Equal(1, counts.Sum());
        Assert.Equal(["Users"], t.Names(AlertKind.Down).Where(n => n == "Users"));
    }

    [Fact]
    public async Task Marks_are_claimed_before_the_enqueue_and_released_when_it_fails()
    {
        await using var t = await CreateAsync();
        var a = await t.MonitorAsync("https://api.example.test/a", "Orders");
        var b = await t.MonitorAsync("https://api.example.test/b", "Users");
        await t.DownAsync(a);
        await t.DownAsync(b);
        var up = new AlertMessage(b.Id, t.Customer.Id, "Users", b.Target, AlertKind.Up, null, t.Host.Now, TimeSpan.FromMinutes(1));

        // At the moment the follow-up is queued, B is already marked as reported: an UP arriving then is not folded.
        string? foldAtEnqueue = "not called";
        DateTime? reportedAtEnqueue = null;
        t.Queue.OnEnqueue = m =>
        {
            if (m.Kind != AlertKind.GroupUpdate) return;
            reportedAtEnqueue = t.Host.WithDbAsync(db => db.IncidentGroupMembers.Where(x => x.Incident.MonitorId == b.Id).Select(x => x.ReportedAtUtc).SingleAsync()).GetAwaiter().GetResult();
            foldAtEnqueue = t.GroupsServiceAsync(s => s.TryFoldAsync(up)).GetAwaiter().GetResult();
        };
        t.Queue.Refuse = true;
        t.Host.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(0, (await t.ScanAsync()).FollowUps);
        Assert.NotNull(reportedAtEnqueue);
        Assert.Null(foldAtEnqueue);
        // The queue refused: the claim is released and the follow-up is sent by the next scan.
        Assert.Null(await t.Host.WithDbAsync(db => db.IncidentGroupMembers.Where(x => x.Incident.MonitorId == b.Id).Select(x => x.ReportedAtUtc).SingleAsync()));
        t.Queue.OnEnqueue = null;
        t.Queue.Refuse = false;
        Assert.Equal(1, (await t.ScanAsync()).FollowUps);

        // Same for the held-back DOWN: claimed (NotifiedAtUtc) before the enqueue, released when it fails.
        DateTime? notifiedAtEnqueue = null;
        t.Queue.OnEnqueue = m =>
        {
            if (m is { Kind: AlertKind.Down, Deferred: true })
                notifiedAtEnqueue = t.Host.WithDbAsync(db => db.IncidentGroupMembers.Where(x => x.Incident.MonitorId == b.Id).Select(x => x.NotifiedAtUtc).SingleAsync()).GetAwaiter().GetResult();
        };
        t.Queue.Refuse = true;
        await t.Host.WithDbAsync(db => db.IncidentGroups.ExecuteUpdateAsync(u => u.SetProperty(g => g.Status, IncidentGroupStatus.Dissolved)));
        Assert.Equal(0, await t.GroupsServiceAsync(s => s.NotifyPendingAsync()));
        Assert.NotNull(notifiedAtEnqueue);
        Assert.Null(await t.Host.WithDbAsync(db => db.IncidentGroupMembers.Where(x => x.Incident.MonitorId == b.Id).Select(x => x.NotifiedAtUtc).SingleAsync()));
    }

    [Fact]
    public async Task A_member_joining_an_automatic_group_gets_it_explained_again()
    {
        await using var t = await CreateAsync();
        await FoldOneAsync(t);
        await t.Host.WithDbAsync(db => db.IncidentGroups.ExecuteUpdateAsync(u => u.SetProperty(g => g.ExplanationQueuedAtUtc, t.Host.Now)));
        var c = await t.MonitorAsync("https://api.example.test/c", "Billing");
        await t.DownAsync(c);

        var g = Assert.Single(await t.GroupsAsync());
        Assert.Equal(3, g.Members.Count);
        Assert.Null(g.ExplanationQueuedAtUtc);
    }

    [Fact]
    public void Group_update_webhook_carries_the_primary_incident_and_the_affected_monitors()
    {
        var m = AlertMessage.ForGroupUpdate(new MonitorEntity { Id = 1, CustomerId = 2, Name = "Orders", Target = "https://api.example.test/a" }, 42,
            [new AffectedMonitor(5, "Users"), new AffectedMonitor(6, "Users")], DateTime.UtcNow);
        Assert.Equal("also_affected", m.StatusText);
        Assert.Contains("incident #42", m.Headline);
        var json = System.Text.Json.JsonSerializer.Serialize(new WebhookPayload(m.MonitorId, m.MonitorName, m.Target, m.StatusText, m.Error, m.OccurredAtUtc,
            IncidentId: m.Kind == AlertKind.GroupUpdate ? m.IncidentId : null, AlsoAffected: m.AlsoAffected), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("\"incidentId\":42", json);
        Assert.Contains("\"monitorId\":6", json);
    }

    [Fact]
    public async Task Watcher_undoes_a_group_whose_primary_closed_without_an_up_alert()
    {
        await using var t = await CreateAsync();
        var (a, _) = await FoldOneAsync(t);
        // e.g. the primary's monitor was paused: its incident is closed without an UP alert.
        await t.Host.WithDbAsync(db => db.Incidents.Where(i => i.MonitorId == a.Id).ExecuteUpdateAsync(u => u.SetProperty(i => i.ResolvedAtUtc, t.Host.Now)));

        var result = await t.ScanAsync();

        Assert.Equal(1, result.Settled);
        Assert.Equal(["Orders", "Users"], t.Names(AlertKind.Down).Order());
    }
}
