using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using System.Diagnostics.Metrics;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring.Alerting;
using TriggerCheck.Monitoring.Diagnostics;
using TriggerCheck.Tests.Monitoring;

namespace TriggerCheck.Tests.Alerting;

/// <summary>FEAT-1 on the alert pipeline: rendering per channel, routing (no voice calls), lanes and metrics.</summary>
public class CertificateAlertTests
{
    private static readonly DateTime At = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly AlertMessage Cert = new(7, 1, "Shop", "https://user:pw@shop.example.test/health?key=secret",
        AlertKind.CertificateExpiring, null, At, null, At.AddDays(10).AddHours(3));

    [Fact]
    public void Message_properties_cover_the_certificate_kind()
    {
        Assert.Equal("certificate_expiring", Cert.StatusText);
        Assert.Equal("CERTIFICATE EXPIRING", Cert.StatusLabel);
        Assert.Equal("[TriggerCheck] CERTIFICATE EXPIRING: Shop", Cert.Subject);
        Assert.Equal(10, Cert.CertificateDaysLeft);
        Assert.Equal("The TLS certificate of monitor \"Shop\" expires in 10 days.", Cert.Headline);
        Assert.Contains("Certificate expires (UTC): 2026-01-11 15:00:00", Cert.Body);
        Assert.Contains("Target: https://user:pw@shop.example.test/health?key=secret", Cert.Body); // e-mail: full target
        Assert.DoesNotContain("secret", Cert.MaskedBody);                                          // chat channels: masked
        Assert.DoesNotContain("pw@", Cert.MaskedTarget);
        Assert.Null(Cert.DowntimeText);
        Assert.Equal("The TLS certificate of monitor \"Shop\" expires in 1 day.", (Cert with { CertificateExpiresAtUtc = At.AddDays(1) }).Headline);
    }

    [Fact]
    public void Every_channel_renders_the_certificate_warning()
    {
        var slack = SlackNotifier.BuildPayload(Cert).ToJsonString();
        Assert.Contains(":warning:", slack);
        Assert.Contains("Certificate expires (UTC)", slack);
        Assert.DoesNotContain("key=secret", slack);

        var teams = TeamsNotifier.BuildPayload(Cert).ToJsonString();
        Assert.Contains("\"Warning\"", teams);
        Assert.Contains("CERTIFICATE EXPIRING: Shop", teams);
        Assert.Contains("Certificate expires (UTC)", teams);

        var telegram = TelegramNotifier.BuildPayload("-100123", Cert).ToJsonString();
        Assert.Contains("CERTIFICATE EXPIRING", telegram);
        Assert.Contains("2026-01-11 15:00:00", telegram);

        var sms = TwilioSmsNotifier.BuildBody(Cert);
        Assert.StartsWith("TriggerCheck CERTIFICATE: Shop (", sms);
        Assert.Contains("expires 2026-01-11 UTC (10 days)", sms);
        Assert.True(sms.Length <= TwilioSmsNotifier.MaxLength);

        Assert.Contains("expires in 10 days", TwilioVoiceNotifier.BuildTwiml(Cert));

        var webhook = JsonSerializer.Serialize(new WebhookPayload(Cert.MonitorId, Cert.MonitorName, Cert.Target, Cert.StatusText, Cert.Error,
            Cert.OccurredAtUtc, Cert.CertificateExpiresAtUtc), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"status\":\"certificate_expiring\"", webhook);
        Assert.Contains("\"certificateExpiresAtUtc\":\"2026-01-11T15:00:00Z\"", webhook);
    }

    private static Task AddChannelAsync(MonitoringTestHost host, int customerId, AlertChannelType type, string target) =>
        host.WithDbAsync(async db =>
        {
            db.AlertChannels.Add(new AlertChannel { CustomerId = customerId, Name = type.ToString(), Type = type, Target = target });
            await db.SaveChangesAsync();
        });

    [Fact]
    public async Task Certificate_warnings_skip_voice_calls_but_go_to_other_channels()
    {
        var webhook = new Recorder(AlertChannelType.Webhook);
        var voice = new Recorder(AlertChannelType.VoiceCall);
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceNotifiers(webhook, voice));
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id);
        await AddChannelAsync(host, customer.Id, AlertChannelType.Webhook, "https://hooks.example.test/x");
        await AddChannelAsync(host, customer.Id, AlertChannelType.VoiceCall, "+15005550006");

        var rows = await host.InScopeAsync(sp => sp.GetRequiredService<IAlertDispatcher>()
            .DispatchAsync(Cert with { MonitorId = monitor.Id, CustomerId = customer.Id }));

        Assert.Single(webhook.Calls);
        Assert.Empty(voice.Calls);
        var row = Assert.Single(rows);
        Assert.True(row.Success);
    }

    [Fact]
    public async Task Voice_only_customer_gets_certificate_warnings_by_contact_email()
    {
        var email = new Recorder(AlertChannelType.Email);
        var voice = new Recorder(AlertChannelType.VoiceCall);
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceNotifiers(email, voice));
        var customer = await host.AddCustomerAsync(contactEmail: "owner@example.test");
        var monitor = await host.AddMonitorAsync(customer.Id);
        await AddChannelAsync(host, customer.Id, AlertChannelType.VoiceCall, "+15005550006");

        await host.InScopeAsync(sp => sp.GetRequiredService<IAlertDispatcher>()
            .DispatchAsync(Cert with { MonitorId = monitor.Id, CustomerId = customer.Id }));

        Assert.Empty(voice.Calls);
        Assert.Equal("owner@example.test", Assert.Single(email.Calls).Target);
    }

    [Fact]
    public async Task Certificate_warning_neither_supersedes_nor_is_superseded_by_status_alerts()
    {
        // The DOWN delivery fails transiently (would retry); a certificate warning arrives meanwhile, then the DOWN is
        // still retried and delivered, and the warning is delivered in its own lane.
        var downAttempts = 0;
        var release = new TaskCompletionSource<NotifyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var webhook = new Recorder(AlertChannelType.Webhook, m =>
            m.Kind == AlertKind.Down
                ? (Interlocked.Increment(ref downAttempts) == 1 ? release.Task : Task.FromResult(NotifyResult.Ok))
                : Task.FromResult(NotifyResult.Ok));
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceNotifiers(webhook));
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id);
        await AddChannelAsync(host, customer.Id, AlertChannelType.Webhook, "https://hooks.example.test/x");
        var down = new AlertMessage(monitor.Id, customer.Id, "Shop", "https://shop.example.test", AlertKind.Down, "refused", MonitoringTestHost.Start.UtcDateTime);
        var cert = Cert with { MonitorId = monitor.Id, CustomerId = customer.Id };

        host.Alerts.TryEnqueue(down);
        await WaitUntilAsync(() => webhook.Calls.Count == 1);     // DOWN in delivery
        host.Alerts.TryEnqueue(cert);
        await WaitUntilAsync(() => webhook.Calls.Any(c => c.Kind == AlertKind.CertificateExpiring)); // not waiting behind the DOWN
        release.SetResult(NotifyResult.Transient("Webhook returned HTTP 503"));
        await host.AdvanceUntilAlertsIdleAsync();

        var rows = await host.WithDbAsync(db => db.Notifications.ToListAsync());
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(r.Success, r.Error));            // nothing superseded
        Assert.Equal(2, webhook.Calls.Count(c => c.Kind == AlertKind.Down)); // the DOWN retry was not cut short
    }

    [Fact]
    public async Task Each_final_channel_outcome_is_counted_in_metrics()
    {
        var ok = new Recorder(AlertChannelType.Webhook);
        var bad = new Recorder(AlertChannelType.Slack, _ => Task.FromResult(NotifyResult.Fail("Slack returned HTTP 404")));
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceNotifiers(ok, bad));
        using var alerts = new MetricCollector<long>(host.Services.GetRequiredService<IMeterFactory>(), MonitoringMetrics.Name, "triggercheck.monitoring.alerts");
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id);
        await AddChannelAsync(host, customer.Id, AlertChannelType.Webhook, "https://hooks.example.test/x");
        await AddChannelAsync(host, customer.Id, AlertChannelType.Slack, "https://hooks.slack.com/services/T/B/x");

        await host.InScopeAsync(sp => sp.GetRequiredService<IAlertDispatcher>().DispatchAsync(Cert with { MonitorId = monitor.Id, CustomerId = customer.Id }));

        var points = alerts.GetMeasurementSnapshot();
        Assert.Equal(2, points.Count);
        Assert.Contains(points, p => (string)p.Tags["channel.type"]! == "webhook" && (string)p.Tags["result"]! == "sent");
        Assert.Contains(points, p => (string)p.Tags["channel.type"]! == "slack" && (string)p.Tags["result"]! == "failed");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException();
            await Task.Delay(5);
        }
    }

    private sealed class Recorder(AlertChannelType type, Func<AlertMessage, Task<NotifyResult>>? behavior = null) : INotifier
    {
        public ConcurrentQueue<(string Target, AlertKind Kind)> Calls { get; } = new();
        public AlertChannelType Type => type;

        public Task<NotifyResult> SendAsync(NotifyTarget target, AlertMessage message, CancellationToken ct)
        {
            Calls.Enqueue((target.Address, message.Kind));
            return behavior?.Invoke(message) ?? Task.FromResult(NotifyResult.Ok);
        }
    }
}
