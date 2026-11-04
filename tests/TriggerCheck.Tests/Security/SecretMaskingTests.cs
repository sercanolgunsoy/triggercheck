using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Domain.Security;
using TriggerCheck.Monitoring.Alerting;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Tests.Monitoring;

namespace TriggerCheck.Tests.Security;

public class SecretMaskerTests
{
    [Theory]
    [InlineData("https://hooks.slack.com/services/T000/B000/XXXXSECRET", "https://hooks.slack.com/…")]
    [InlineData("http://127.0.0.1:8770/hook?token=abc", "http://127.0.0.1:8770/…")]
    [InlineData("https://user:pw@example.com/a", "https://example.com/…")]
    [InlineData("  https://example.com  ", "https://example.com/…")]
    [InlineData("not a url /secret", "…")]
    [InlineData("", "…")]
    [InlineData(null, "…")]
    public void Webhook_urls_keep_only_scheme_and_host(string? url, string expected) =>
        Assert.Equal(expected, SecretMasker.MaskWebhookUrl(url));

    [Theory]
    [InlineData("https://example.com/status?token=abc&x=1", "https://example.com/status?…")]
    [InlineData("https://example.com/status#frag", "https://example.com/status?…")]
    [InlineData("https://user:pw@example.com:8443/a/b", "https://example.com:8443/a/b")]
    [InlineData("https://example.com/", "https://example.com/")]
    [InlineData("example.com/path?key=1", "example.com/path?…")]
    [InlineData("example.com", "example.com")]
    [InlineData(null, "")]
    public void Urls_lose_credentials_and_query(string? url, string expected) =>
        Assert.Equal(expected, SecretMasker.MaskUrl(url));
}

public class WebhookMaskingTests
{
    private const string Secret = "https://hooks.example.com/services/T0/B0/TOPSECRET";

    [Fact]
    public async Task Dispatcher_logs_do_not_contain_the_full_webhook_url()
    {
        var logs = new CapturingLoggerProvider();
        var failing = new RecordingNotifier(AlertChannelType.Webhook, _ => NotifyResult.Fail("Webhook returned HTTP 500"));
        await using var host = await MonitoringTestHost.CreateAsync(s =>
        {
            s.ReplaceNotifiers(failing);
            s.AddSingleton<ILoggerProvider>(logs);
        });
        var customer = await host.AddCustomerAsync();
        await host.WithDbAsync(async db =>
        {
            db.AlertChannels.Add(new AlertChannel { CustomerId = customer.Id, Name = "hook", Type = AlertChannelType.Webhook, Target = Secret });
            await db.SaveChangesAsync();
        });

        await host.InScopeAsync(sp => sp.GetRequiredService<IAlertDispatcher>()
            .DispatchAsync(new AlertMessage(1, customer.Id, "M", "https://example.org", AlertKind.Down, "x", host.Now)));

        Assert.Contains(logs.Messages, m => m.Contains("https://hooks.example.com/…"));
        Assert.DoesNotContain(logs.Messages, m => m.Contains("TOPSECRET"));
    }

    [Fact]
    public async Task Alert_list_masks_webhook_urls()
    {
        await using var f = new TestWebAppFactory();
        var cid = await f.DemoCustomerIdAsync();
        await f.WithDbAsync(async db =>
        {
            db.AlertChannels.Add(new AlertChannel { CustomerId = cid, Name = "Slack", Type = AlertChannelType.Webhook, Target = Secret });
            await db.SaveChangesAsync();
        });
        await f.AddUserAsync(cid, "masked-viewer@demo.local", "Viewer123!", TriggerCheck.Domain.Roles.CustomerViewer);

        foreach (var (email, pw) in new[] { (TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword), ("masked-viewer@demo.local", "Viewer123!") })
        {
            var html = await (await f.LoginAsync(email, pw)).GetHtmlAsync("/App/Alerts/Index");
            Assert.Contains("https://hooks.example.com/…", html);
            Assert.DoesNotContain("TOPSECRET", html);
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(Messages);
        public void Dispose() { }

        private sealed class Logger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }
}
