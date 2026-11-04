using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring.Alerting;
using TriggerCheck.Monitoring.Checks;
using TriggerCheck.Monitoring.Engine;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Tests.Monitoring;

/// <summary>FEAT-1: certificate info on the monitor and the one-time "certificate expiring" alert.</summary>
public class CertificateExpiryTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static MonitorEntity Https(int warnDays, DateTime? expires, string? error = null) => new()
    {
        Type = MonitorType.Http, Target = "https://example.com/", CertWarnDays = warnDays, CertExpiresAtUtc = expires, CertError = error
    };

    [Theory]
    [InlineData(14, 10, true)]   // 10 days left, warn at 14 => warn
    [InlineData(30, 10, true)]
    [InlineData(7, 10, false)]   // not yet
    [InlineData(0, 10, false)]   // off
    [InlineData(14, -1, false)]  // expired: the probe fails and the DOWN alert covers it
    [InlineData(14, 14, true)]   // exactly at the threshold
    [InlineData(14, 15, false)]
    public void Warns_within_the_configured_days(int warnDays, int daysLeft, bool expected) =>
        Assert.Equal(expected, MonitorStateMachine.TryMarkCertificateWarning(Https(warnDays, Now.AddDays(daysLeft)), Now));

    [Fact]
    public void Warns_once_per_certificate_and_again_after_renewal()
    {
        var m = Https(14, Now.AddDays(10));
        Assert.True(MonitorStateMachine.TryMarkCertificateWarning(m, Now));
        Assert.Equal(m.CertExpiresAtUtc, m.CertWarningSentForUtc);
        Assert.False(MonitorStateMachine.TryMarkCertificateWarning(m, Now.AddDays(1)));
        Assert.False(MonitorStateMachine.TryMarkCertificateWarning(m, Now.AddDays(5)));

        m.CertExpiresAtUtc = Now.AddDays(90); // renewed
        Assert.False(MonitorStateMachine.TryMarkCertificateWarning(m, Now.AddDays(5)));
        Assert.True(MonitorStateMachine.TryMarkCertificateWarning(m, Now.AddDays(80)));
    }

    [Fact]
    public void Invalid_certificates_and_non_http_monitors_do_not_warn()
    {
        Assert.False(MonitorStateMachine.TryMarkCertificateWarning(Https(14, Now.AddDays(3), "certificate name does not match the host"), Now));
        var tcp = Https(14, Now.AddDays(3));
        tcp.Type = MonitorType.Tcp;
        Assert.False(MonitorStateMachine.TryMarkCertificateWarning(tcp, Now));
        Assert.False(MonitorStateMachine.TryMarkCertificateWarning(Https(14, null), Now));
    }

    [Fact]
    public void RecordCertificate_keeps_the_last_known_certificate_on_reused_connections()
    {
        var m = Https(14, null);
        MonitorStateMachine.RecordCertificate(m, new CertificateInfo(Now.AddDays(20), "CN=a", "CN=ca", null), Now);
        Assert.Equal(Now.AddDays(20), m.CertExpiresAtUtc);
        MonitorStateMachine.RecordCertificate(m, null, Now.AddMinutes(1));
        Assert.Equal(Now.AddDays(20), m.CertExpiresAtUtc);
        Assert.Equal("CN=ca", m.CertIssuer);
        Assert.Equal(Now, m.CertCheckedAtUtc);
    }

    [Fact]
    public void Alert_message_describes_the_expiry()
    {
        var m = Https(14, Now.AddDays(10));
        m.Id = 7;
        m.Name = "Shop";
        var msg = AlertMessage.CertificateExpiring(m, Now);
        Assert.Equal(AlertKind.CertificateExpiring, msg.Kind);
        Assert.Equal("[TriggerCheck] CERTIFICATE EXPIRING: Shop", msg.Subject);
        Assert.Equal("certificate_expiring", msg.StatusText);
        Assert.Contains("2026-01-11 12:00:00 (10 days left)", msg.Body);
    }

    [Fact]
    public async Task Runner_stores_the_certificate_and_sends_one_expiring_alert()
    {
        var notifier = new RecordingNotifier(AlertChannelType.Email);
        var checker = new ScriptedChecker(MonitorType.Http);
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceCheckers(checker).ReplaceNotifiers(notifier));
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id, m => { m.Target = "https://shop.example/"; m.CertWarnDays = 14; });
        var expires = host.Now.AddDays(10);
        var withCert = CheckOutcome.Up(40, 200) with { Certificate = new CertificateInfo(expires, "CN=shop.example", "CN=Test CA", null) };
        checker.Then(withCert, CheckOutcome.Up(20, 200), withCert);

        await host.RunCheckAsync(monitor.Id);
        var m = await host.GetMonitorAsync(monitor.Id);
        Assert.Equal(expires, m.CertExpiresAtUtc);
        Assert.Equal("CN=shop.example", m.CertSubject);
        Assert.Equal("CN=Test CA", m.CertIssuer);
        Assert.Null(m.CertError);
        var (to, alert) = Assert.Single(notifier.Sent);
        Assert.Equal("ops@example.test", to);
        Assert.Equal(AlertKind.CertificateExpiring, alert.Kind);
        Assert.Equal(expires, alert.CertificateExpiresAtUtc);

        // Reused connection (no certificate) and a later handshake with the same certificate: no new alert.
        host.Time.Advance(TimeSpan.FromMinutes(1));
        await host.RunCheckAsync(monitor.Id);
        host.Time.Advance(TimeSpan.FromMinutes(1));
        await host.RunCheckAsync(monitor.Id);
        Assert.Single(notifier.Sent);
        Assert.Equal(expires, (await host.GetMonitorAsync(monitor.Id)).CertExpiresAtUtc);
        Assert.Equal(1, await host.WithDbAsync(db => db.Notifications.CountAsync(n => n.Subject.Contains("CERTIFICATE EXPIRING"))));
    }

    [Fact]
    public async Task Paused_monitor_records_the_certificate_but_does_not_alert()
    {
        var notifier = new RecordingNotifier(AlertChannelType.Email);
        var checker = new ScriptedChecker(MonitorType.Http).Then(CheckOutcome.Up(40, 200) with
        {
            Certificate = new CertificateInfo(MonitoringTestHost.Start.UtcDateTime.AddDays(3), "CN=x", "CN=ca", null)
        });
        await using var host = await MonitoringTestHost.CreateAsync(s => s.ReplaceCheckers(checker).ReplaceNotifiers(notifier));
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id, m => { m.Target = "https://x.example/"; m.Status = MonitorStatus.Paused; });

        await host.RunCheckAsync(monitor.Id);
        Assert.NotNull((await host.GetMonitorAsync(monitor.Id)).CertExpiresAtUtc);
        Assert.Empty(notifier.Sent);
    }
}
