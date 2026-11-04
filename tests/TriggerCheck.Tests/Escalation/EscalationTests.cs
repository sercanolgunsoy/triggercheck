using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring.Alerting;
using TriggerCheck.Monitoring.Checks;
using TriggerCheck.Monitoring.Escalation;
using TriggerCheck.Tests.Monitoring;

namespace TriggerCheck.Tests.Escalation;

/// <summary>FEAT-5: escalation steps driven by the fake clock, stopping on acknowledgement / resolution, restart safety.</summary>
public class EscalationTests
{
    private static readonly CheckOutcome Refused = CheckOutcome.Down("Connection refused", 3);

    private readonly ScriptedChecker _checker = new(MonitorType.Http);
    private readonly RecordingNotifier _email = new(AlertChannelType.Email);
    private readonly RecordingNotifier _webhook = new(AlertChannelType.Webhook);
    private readonly RecordingNotifier _slack = new(AlertChannelType.Slack);

    private sealed record Setup(MonitoringTestHost Host, int CustomerId, int MonitorId, int EmailId, int WebhookId, int SlackId);

    /// <summary>Customer with three channels; the policy (when <paramref name="steps"/> is given) uses channel names "email", "webhook", "slack".</summary>
    private async Task<Setup> CreateAsync(bool enabled = true, params (int Minutes, string[] Channels)[] steps)
    {
        var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceCheckers(_checker).ReplaceNotifiers(_email, _webhook, _slack),
            new() { ["Escalation:PublicBaseUrl"] = "https://monitor.example.test" });
        var customer = await host.AddCustomerAsync();
        var ids = await host.WithDbAsync(async db =>
        {
            var email = new AlertChannel { CustomerId = customer.Id, Name = "email", Type = AlertChannelType.Email, Target = "ops@example.test" };
            var webhook = new AlertChannel { CustomerId = customer.Id, Name = "webhook", Type = AlertChannelType.Webhook, Target = "https://hooks.example.test/a" };
            var slack = new AlertChannel { CustomerId = customer.Id, Name = "slack", Type = AlertChannelType.Slack, Target = "https://hooks.slack.test/b" };
            db.AlertChannels.AddRange(email, webhook, slack);
            if (steps.Length > 0)
            {
                var policy = new EscalationPolicy { CustomerId = customer.Id, IsEnabled = enabled };
                foreach (var (minutes, names) in steps)
                {
                    var step = new EscalationStep { DelayMinutes = minutes };
                    foreach (var n in names) step.Channels.Add(n switch { "email" => email, "webhook" => webhook, _ => slack });
                    policy.Steps.Add(step);
                }
                db.EscalationPolicies.Add(policy);
            }
            await db.SaveChangesAsync();
            return (email.Id, webhook.Id, slack.Id);
        });
        var monitor = await host.AddMonitorAsync(customer.Id, m => { m.FailureThreshold = 1; m.Status = MonitorStatus.Up; m.Name = "Shop"; });
        return new Setup(host, customer.Id, monitor.Id, ids.Item1, ids.Item2, ids.Item3);
    }

    private async Task<int> GoDownAsync(Setup s)
    {
        _checker.Then(Refused);
        await s.Host.RunCheckAsync(s.MonitorId);
        return await s.Host.WithDbAsync(db => db.Incidents.Where(i => i.MonitorId == s.MonitorId && i.ResolvedAtUtc == null).Select(i => i.Id).SingleAsync());
    }

    private static async Task<int> EscalateAsync(Setup s)
    {
        var n = await s.Host.InScopeAsync(sp => sp.GetRequiredService<EscalationRunner>().RunOnceAsync());
        await s.Host.AlertsIdleAsync();
        return n;
    }

    private static List<AlertMessage> Escalations(RecordingNotifier n) => n.Sent.Select(x => x.Message).Where(m => m.Kind == AlertKind.Escalation).ToList();

    [Fact]
    public async Task Steps_fire_after_their_delay_only_on_their_channels()
    {
        var s = await CreateAsync(true, (5, ["webhook"]), (15, ["slack", "email"]));
        await using var host = s.Host;
        var incidentId = await GoDownAsync(s);
        // Step 0: the DOWN alert went to every enabled channel.
        Assert.Single(_email.Sent);
        Assert.Single(_webhook.Sent);
        Assert.Single(_slack.Sent);

        host.Time.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        Assert.Equal(0, await EscalateAsync(s));

        host.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await EscalateAsync(s));
        var first = Assert.Single(Escalations(_webhook));
        Assert.Equal(1, first.EscalationLevel);
        Assert.Equal(incidentId, first.IncidentId);
        Assert.Empty(Escalations(_email));
        Assert.Empty(Escalations(_slack));
        Assert.Contains("still DOWN", first.Headline);

        Assert.Equal(0, await EscalateAsync(s)); // nothing new is due
        host.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(1, await EscalateAsync(s));
        Assert.Single(Escalations(_webhook));
        Assert.Equal(2, Assert.Single(Escalations(_slack)).EscalationLevel);
        Assert.Single(Escalations(_email));
        Assert.Equal(2, await host.WithDbAsync(db => db.Incidents.Where(i => i.Id == incidentId).Select(i => i.LastEscalationStep).SingleAsync()));

        host.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, await EscalateAsync(s)); // no more steps
    }

    [Fact]
    public async Task Acknowledged_incident_stops_escalating_and_ack_does_not_change_the_monitor()
    {
        var s = await CreateAsync(true, (5, ["webhook"]), (10, ["slack"]));
        await using var host = s.Host;
        var incidentId = await GoDownAsync(s);
        host.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(1, await EscalateAsync(s));

        var result = await host.InScopeAsync(sp => sp.GetRequiredService<IncidentAcknowledger>().AcknowledgeAsync(incidentId, s.CustomerId, "user-1", " on it "));
        Assert.Equal(AckResult.Acknowledged, result);
        var again = await host.InScopeAsync(sp => sp.GetRequiredService<IncidentAcknowledger>().AcknowledgeAsync(incidentId, s.CustomerId, "user-2", null));
        Assert.Equal(AckResult.AlreadyAcknowledged, again);

        host.Time.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(0, await EscalateAsync(s));
        Assert.Empty(Escalations(_slack));

        var incident = await host.WithDbAsync(db => db.Incidents.AsNoTracking().SingleAsync(i => i.Id == incidentId));
        Assert.Equal("user-1", incident.AcknowledgedByUserId);
        Assert.Equal("on it", incident.AcknowledgeNote);
        Assert.Equal(MonitoringTestHost.Start.UtcDateTime.AddMinutes(5), incident.AcknowledgedAtUtc);
        Assert.Null(incident.ResolvedAtUtc);
        Assert.Equal(MonitorStatus.Down, (await host.GetMonitorAsync(s.MonitorId)).Status);
    }

    [Fact]
    public async Task Resolved_incident_stops_escalating_and_cannot_be_acknowledged()
    {
        var s = await CreateAsync(true, (5, ["webhook"]));
        await using var host = s.Host;
        var incidentId = await GoDownAsync(s);
        _checker.Then(CheckOutcome.Up(10, 200));
        host.Time.Advance(TimeSpan.FromMinutes(1));
        await host.RunCheckAsync(s.MonitorId);

        host.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(0, await EscalateAsync(s));
        Assert.Empty(Escalations(_webhook));
        Assert.Equal(AckResult.Resolved,
            await host.InScopeAsync(sp => sp.GetRequiredService<IncidentAcknowledger>().AcknowledgeAsync(incidentId, s.CustomerId, "u", null)));
    }

    [Fact]
    public async Task Acknowledging_another_customers_incident_finds_nothing()
    {
        var s = await CreateAsync(true, (5, ["webhook"]));
        await using var host = s.Host;
        var incidentId = await GoDownAsync(s);
        var other = await host.AddCustomerAsync();
        Assert.Equal(AckResult.NotFound,
            await host.InScopeAsync(sp => sp.GetRequiredService<IncidentAcknowledger>().AcknowledgeAsync(incidentId, other.Id, "intruder", null)));
        Assert.Null(await host.WithDbAsync(db => db.Incidents.Where(i => i.Id == incidentId).Select(i => i.AcknowledgedAtUtc).SingleAsync()));
    }

    [Fact]
    public async Task Steps_are_not_repeated_after_a_restart_and_overdue_steps_are_combined()
    {
        var s = await CreateAsync(true, (5, ["webhook"]), (10, ["slack"]));
        await using var host = s.Host;
        var incidentId = await GoDownAsync(s);
        host.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(1, await EscalateAsync(s));

        // "Restart": a fresh runner (new scope, nothing in memory) sees the persisted step and does not repeat it.
        Assert.Equal(0, await EscalateAsync(s));
        Assert.Single(Escalations(_webhook));

        // A second incident while the job was down for 20 minutes: both steps are overdue -> one alert to both step channels.
        _checker.Then(CheckOutcome.Up(10, 200));
        await host.RunCheckAsync(s.MonitorId);
        var second = await GoDownAsync(s);
        Assert.NotEqual(incidentId, second);
        host.Time.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(1, await EscalateAsync(s));
        var combined = Escalations(_slack).Single();
        Assert.Equal(2, combined.EscalationLevel);
        Assert.Equal(second, combined.IncidentId);
        Assert.Equal(2, Escalations(_webhook).Count);
        Assert.Empty(Escalations(_email));
    }

    [Fact]
    public async Task Without_a_policy_or_with_a_disabled_one_only_the_down_alert_is_sent()
    {
        foreach (var enabled in new[] { true, false })
        {
            _email.Sent.Clear();
            var s = enabled ? await CreateAsync() : await CreateAsync(false, (1, ["email"]));
            await using var host = s.Host;
            await GoDownAsync(s);
            host.Time.Advance(TimeSpan.FromHours(2));
            Assert.Equal(0, await EscalateAsync(s));
            Assert.Equal([AlertKind.Down], _email.Sent.Select(x => x.Message.Kind));
        }
    }

    [Fact]
    public async Task Escalation_pauses_during_maintenance()
    {
        var s = await CreateAsync(true, (5, ["webhook"]));
        await using var host = s.Host;
        await GoDownAsync(s);
        await host.WithDbAsync(async db =>
        {
            db.MaintenanceWindows.Add(new MaintenanceWindow { CustomerId = s.CustomerId, Name = "M", StartsAtUtc = host.Now.AddMinutes(1), EndsAtUtc = host.Now.AddMinutes(30) });
            await db.SaveChangesAsync();
        });
        host.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(0, await EscalateAsync(s));
        host.Time.Advance(TimeSpan.FromMinutes(21)); // window over
        Assert.Equal(1, await EscalateAsync(s));
    }

    [Fact]
    public async Task Escalation_acknowledged_after_queueing_is_not_delivered()
    {
        var s = await CreateAsync(true, (5, ["webhook"]));
        await using var host = s.Host;
        var incidentId = await GoDownAsync(s);
        await host.InScopeAsync(sp => sp.GetRequiredService<IncidentAcknowledger>().AcknowledgeAsync(incidentId, s.CustomerId, "u", null));
        var monitor = await host.GetMonitorAsync(s.MonitorId);

        Assert.True(host.Alerts.TryEnqueue(AlertMessage.ForEscalation(monitor, incidentId, host.Now, 1, [s.WebhookId], host.Now)));
        await host.AlertsIdleAsync();

        Assert.Empty(Escalations(_webhook));
        Assert.Equal(0, await host.WithDbAsync(db => db.Notifications.CountAsync(n => n.Subject.Contains("ESCALATION"))));
    }

    [Fact]
    public async Task Down_alert_carries_a_signed_acknowledge_link()
    {
        var s = await CreateAsync();
        await using var host = s.Host;
        var incidentId = await GoDownAsync(s);
        var (_, down) = Assert.Single(_email.Sent);
        Assert.StartsWith("https://monitor.example.test/App/Incidents/Ack?token=", down.AckUrl);
        Assert.Contains("Acknowledge: https://monitor.example.test/App/Incidents/Ack?token=", down.Body);

        var url = down.AckUrl!;
        var token = Uri.UnescapeDataString(url[(url.IndexOf("token=", StringComparison.Ordinal) + 6)..]);
        var links = host.Services.GetRequiredService<IncidentAckLinks>();
        Assert.Equal(AckTokenStatus.Valid, links.Validate(token, out var parsed));
        Assert.Equal(incidentId, parsed!.IncidentId);
        Assert.Equal(s.CustomerId, parsed.CustomerId);

        // Slack and Teams render it as a link / button; UP alerts never carry one.
        Assert.Contains("Acknowledge this incident", SlackNotifier.BuildPayload(down).ToJsonString());
        Assert.Contains("Action.OpenUrl", TeamsNotifier.BuildPayload(down).ToJsonString());
        Assert.Null((down with { Kind = AlertKind.Up }).AckLink);
    }

    [Fact]
    public async Task Hosted_service_escalates_on_the_fake_clock()
    {
        var s = await CreateAsync(true, (2, ["webhook"]));
        await using var host = s.Host;
        await GoDownAsync(s);
        var service = ActivatorUtilities.CreateInstance<EscalationService>(host.Services,
            Microsoft.Extensions.Options.Options.Create(new TriggerCheck.Monitoring.MonitoringOptions { Enabled = true }));
        await service.StartAsync(CancellationToken.None);
        try
        {
            for (var i = 0; i < 10 && Escalations(_webhook).Count == 0; i++)
            {
                host.Time.Advance(TimeSpan.FromSeconds(30));
                await Task.Delay(50);
                await host.AlertsIdleAsync();
            }
            Assert.Single(Escalations(_webhook));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
