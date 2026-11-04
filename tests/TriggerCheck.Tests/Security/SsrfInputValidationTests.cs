using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Tests.Infrastructure;
using static TriggerCheck.Tests.App.AppTestData;

namespace TriggerCheck.Tests.Security;

/// <summary>Web host with the SSRF guard on (production default).</summary>
public sealed class GuardedWebAppFactory : TestWebAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Monitoring:AllowPrivateTargets", "false");
    }
}

public class SsrfInputValidationTests(GuardedWebAppFactory f) : IClassFixture<GuardedWebAppFactory>
{
    private const string MonitorError = "This address points to a private or internal network and cannot be monitored.";
    private const string WebhookError = "The webhook address points to a private or internal network and is not allowed.";

    private Task<HttpClient> AdminAsync() => f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);

    [Theory]
    [InlineData("Http", "http://127.0.0.1:8765/")]
    [InlineData("Http", "http://localhost/")]
    [InlineData("Http", "http://169.254.169.254/latest/meta-data/")]
    [InlineData("Http", "http://[::1]/")]
    [InlineData("Http", "http://[::ffff:127.0.0.1]/")]
    [InlineData("Http", "http://10.1.2.3/")]
    [InlineData("Keyword", "https://192.168.1.10/status")]
    [InlineData("Tcp", "172.16.0.1")]
    [InlineData("Ping", "100.64.0.1")]
    [InlineData("Ping", "localhost")]
    public async Task Private_monitor_target_is_rejected_with_a_form_error(string type, string target)
    {
        var name = $"Ssrf-{Guid.NewGuid():N}";
        var r = await (await AdminAsync()).PostFormAsync("/App/Monitors/Create",
            MonitorForm(name, ("Type", type), ("Target", target), ("Port", "22"), ("Keyword", "ok"), ("IntervalSeconds", "300")));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains(MonitorError, await r.ReadHtmlAsync());
        Assert.False(await f.WithDbAsync(db => db.Monitors.AnyAsync(m => m.Name == name)));
    }

    [Fact]
    public async Task Public_monitor_target_passes_validation()
    {
        var name = $"Public-{Guid.NewGuid():N}";
        var r = await (await AdminAsync()).PostFormAsync("/App/Monitors/Create",
            MonitorForm(name, ("Target", "https://example.org/health"), ("IntervalSeconds", "300")));
        AssertRedirect(r, "/App/Monitors");
        Assert.True(await f.WithDbAsync(db => db.Monitors.AnyAsync(m => m.Name == name)));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8770/hook")]
    [InlineData("http://localhost/hook")]
    [InlineData("http://169.254.169.254/")]
    public async Task Private_webhook_is_rejected(string url)
    {
        var name = $"Hook-{Guid.NewGuid():N}";
        var r = await (await AdminAsync()).PostFormAsync("/App/Alerts/Create",
            new() { ["Input.Name"] = name, ["Input.Type"] = "Webhook", ["Input.Target"] = url, ["Input.IsEnabled"] = "true" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains(WebhookError, await r.ReadHtmlAsync());
        Assert.False(await f.WithDbAsync(db => db.AlertChannels.AnyAsync(c => c.Name == name)));
    }

    [Fact]
    public async Task Public_webhook_passes_validation()
    {
        var name = $"Hook-{Guid.NewGuid():N}";
        var r = await (await AdminAsync()).PostFormAsync("/App/Alerts/Create",
            new() { ["Input.Name"] = name, ["Input.Type"] = "Webhook", ["Input.Target"] = "https://hooks.example.com/services/T0/B0/secret", ["Input.IsEnabled"] = "true" });
        AssertRedirect(r, "/App/Alerts");
    }

    [Fact]
    public async Task Error_is_localized_in_Turkish()
    {
        var c = await AdminAsync();
        await c.SetLanguageAsync("tr");
        var r = await c.PostFormAsync("/App/Monitors/Create",
            MonitorForm($"Tr-{Guid.NewGuid():N}", ("Target", "http://127.0.0.1/"), ("IntervalSeconds", "300")));
        Assert.Contains("Bu adres özel veya dahili bir ağı gösteriyor ve izlenemez.", await r.ReadHtmlAsync());
    }
}
