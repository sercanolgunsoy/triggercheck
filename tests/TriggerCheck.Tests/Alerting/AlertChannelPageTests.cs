using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring.Alerting;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Tests.Monitoring;
using TriggerCheck.Tests.SuperAdmin;
using static TriggerCheck.Tests.App.AppTestData;

namespace TriggerCheck.Tests.Alerting;

/// <summary>App Alerts pages: new channel types (FEAT-2), test button, no-channel setting (ENG-6), SMS/voice gating (FEAT-7).</summary>
public class AlertChannelPageTests(TestWebAppFactory f) : IClassFixture<TestWebAppFactory>
{
    private const string BotToken = "123456789:AAH-secret_token_value_abcdefghijk";

    private Task<HttpClient> AdminAsync() => f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);

    private static Dictionary<string, string> Form(string name, string type, string target, string? secret = null) => new()
    {
        ["Input.Name"] = name, ["Input.Type"] = type, ["Input.Target"] = target, ["Input.Secret"] = secret ?? "", ["Input.IsEnabled"] = "true"
    };

    private Task<AlertChannel?> FindAsync(string name) =>
        f.WithDbAsync(db => db.AlertChannels.AsNoTracking().FirstOrDefaultAsync(c => c.Name == name));

    [Fact]
    public async Task Slack_teams_and_telegram_channels_are_validated_and_saved()
    {
        var c = await AdminAsync();
        AssertRedirect(await c.PostFormAsync("/App/Alerts/Create", Form("Slack ops", "Slack", "https://hooks.slack.com/services/T0/B0/secretpart")), "/App/Alerts");
        AssertRedirect(await c.PostFormAsync("/App/Alerts/Create", Form("Teams ops", "Teams", "https://prod-1.westeurope.logic.azure.com/workflows/abc?sig=secretsig")), "/App/Alerts");
        AssertRedirect(await c.PostFormAsync("/App/Alerts/Create", Form("TG ops", "Telegram", "-1001234567890", BotToken)), "/App/Alerts");

        var tg = (await FindAsync("TG ops"))!;
        Assert.Equal(AlertChannelType.Telegram, tg.Type);
        Assert.Equal("-1001234567890", tg.Target);
        Assert.Equal(BotToken, tg.Secret);
        Assert.Null((await FindAsync("Slack ops"))!.Secret);

        // Listing and edit form never show secrets (webhook paths, bot token).
        var index = await c.GetHtmlAsync("/App/Alerts/Index");
        Assert.Contains("https://hooks.slack.com/…", index);
        Assert.DoesNotContain("secretpart", index);
        Assert.DoesNotContain("secretsig", index);
        Assert.Contains("-1001234567890", index);
        Assert.DoesNotContain("AAH-secret", index);
        var edit = await c.GetHtmlAsync($"/App/Alerts/Edit/{tg.Id}");
        Assert.DoesNotContain("AAH-secret", edit);
        Assert.Contains("Leave empty to keep the stored token", edit);

        // Editing with an empty token keeps the stored one.
        AssertRedirect(await c.PostFormAsync($"/App/Alerts/Edit/{tg.Id}", Form("TG renamed", "Telegram", "@opschannel")), "/App/Alerts");
        tg = (await FindAsync("TG renamed"))!;
        Assert.Equal("@opschannel", tg.Target);
        Assert.Equal(BotToken, tg.Secret);

        // Switching away from Telegram drops the token.
        AssertRedirect(await c.PostFormAsync($"/App/Alerts/Edit/{tg.Id}", Form("TG renamed", "Email", "ops@example.com")), "/App/Alerts");
        Assert.Null((await FindAsync("TG renamed"))!.Secret);
    }

    [Theory]
    [InlineData("Slack", "http://hooks.slack.com/services/x", null)]         // not https
    [InlineData("Slack", "https://example.com/services/x", null)]            // not a Slack host
    [InlineData("Teams", "http://prod.logic.azure.com/workflows/x", null)]   // not https
    [InlineData("Teams", "not a url", null)]
    [InlineData("Telegram", "-1001234567890", null)]                         // token missing
    [InlineData("Telegram", "-1001234567890", "not-a-token")]
    [InlineData("Telegram", "my group", BotToken)]                           // bad chat id
    [InlineData("Sms", "+905551234567", null)]                               // Twilio not configured / plan
    [InlineData("VoiceCall", "+905551234567", null)]
    public async Task Invalid_new_channel_types_are_rejected(string type, string target, string? secret)
    {
        var name = $"Bad-{Guid.NewGuid():N}";
        var r = await (await AdminAsync()).PostFormAsync("/App/Alerts/Create", Form(name, type, target, secret));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("validation-error", await r.ReadHtmlAsync());
        Assert.Null(await FindAsync(name));
    }

    [Fact]
    public async Task Sms_and_voice_are_hidden_without_twilio()
    {
        var html = await (await AdminAsync()).GetHtmlAsync("/App/Alerts/Create");
        Assert.Contains("value=\"Slack\"", html);
        Assert.Contains("value=\"Telegram\"", html);
        Assert.DoesNotContain("value=\"Sms\"", html);
        Assert.DoesNotContain("value=\"VoiceCall\"", html);
        // The help/placeholder map is keyed by the option values (dictionary keys must not be camel-cased).
        Assert.Contains("\"Telegram\":", html);
        Assert.Contains("\"VoiceCall\":", html);
    }

    [Fact]
    public async Task Test_button_sends_shows_result_and_is_rate_limited()
    {
        var hits = new ConcurrentQueue<string>();
        await using var server = await LocalHttpServer.StartAsync(app =>
        {
            app.MapPost("/ok", async (HttpRequest req) => { using var r = new StreamReader(req.Body); hits.Enqueue(await r.ReadToEndAsync()); return Results.Ok(); });
            app.MapPost("/fail", () => Results.StatusCode(404));
        });
        var c = await AdminAsync();
        var cid = await f.DemoCustomerIdAsync();
        var ok = await AddChannelAsync(cid, "Test ok", server.Url("/ok"), enabled: false); // disabled channels can be tested too
        var bad = await AddChannelAsync(cid, "Test bad", server.Url("/fail"));

        var r = await c.PostFormAsync($"/App/Alerts/Index?handler=Test&id={ok}", new(), "/App/Alerts/Index");
        Assert.Contains("Test notification sent to \"Test ok\".", await c.FollowAsync(r));
        Assert.Contains("\"status\":\"test\"", Assert.Single(hits));

        r = await c.PostFormAsync($"/App/Alerts/Index?handler=Test&id={bad}", new(), "/App/Alerts/Index");
        Assert.Contains("Test notification to \"Test bad\" failed: Webhook returned HTTP 404", await c.FollowAsync(r));

        r = await c.PostFormAsync($"/App/Alerts/Index?handler=Test&id={ok}", new(), "/App/Alerts/Index");
        Assert.Contains("Too many test notifications. Try again in", await c.FollowAsync(r));
        Assert.Single(hits);

        // Nothing is recorded in the notification log for tests.
        Assert.Equal(0, await f.WithDbAsync(db => db.Notifications.CountAsync(n => n.Subject.Contains("TEST"))));
    }

    [Fact]
    public async Task Test_button_is_admin_only_and_tenant_scoped()
    {
        var cid = await f.DemoCustomerIdAsync();
        var other = await f.AddCustomerAsync($"Other-{Guid.NewGuid():N}");
        var foreign = await AddChannelAsync(other, "Foreign", "https://hooks.example.com/x");
        var mine = await AddChannelAsync(cid, "Mine for viewer", "https://hooks.example.com/y");

        var admin = await AdminAsync();
        var r = await admin.PostFormAsync($"/App/Alerts/Index?handler=Test&id={foreign}", new(), "/App/Alerts/Index");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);

        var viewerEmail = $"viewer-{Guid.NewGuid():N}@demo.local";
        await f.AddUserAsync(cid, viewerEmail, "Viewer123!", Roles.CustomerViewer);
        var viewer = await f.LoginAsync(viewerEmail, "Viewer123!");
        var html = await viewer.GetHtmlAsync("/App/Alerts/Index");
        Assert.DoesNotContain("handler=Test", html);
        r = await viewer.PostFormAsync($"/App/Alerts/Index?handler=Test&id={mine}", new(), "/App/Alerts/Index");
        Assert.True(r.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Redirect);
        if (r.StatusCode == HttpStatusCode.Redirect) AssertAccessDenied(r);
    }

    [Fact]
    public async Task No_channel_setting_is_explained_and_saved_by_admins_only()
    {
        var cid = await f.DemoCustomerIdAsync();
        var admin = await AdminAsync();
        var html = await admin.GetHtmlAsync("/App/Alerts/Index");
        Assert.Contains("When no channel is enabled", html);
        Assert.Contains("Send alerts to the account contact e-mail", html);
        Assert.Contains("Do not send alerts", html);

        var r = await admin.PostFormAsync("/App/Alerts/Index?handler=Fallback", new() { ["sendToContactEmail"] = "false" }, "/App/Alerts/Index");
        AssertRedirect(r, "/App/Alerts");
        Assert.False(await f.WithDbAsync(db => db.Customers.Where(c => c.Id == cid).Select(c => c.AlertContactEmailWhenNoChannel).SingleAsync()));

        var viewerEmail = $"viewer-{Guid.NewGuid():N}@demo.local";
        await f.AddUserAsync(cid, viewerEmail, "Viewer123!", Roles.CustomerViewer);
        var viewer = await f.LoginAsync(viewerEmail, "Viewer123!");
        r = await viewer.PostFormAsync("/App/Alerts/Index?handler=Fallback", new() { ["sendToContactEmail"] = "true" }, "/App/Alerts/Index");
        Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
        Assert.False(await f.WithDbAsync(db => db.Customers.Where(c => c.Id == cid).Select(c => c.AlertContactEmailWhenNoChannel).SingleAsync()));

        r = await admin.PostFormAsync("/App/Alerts/Index?handler=Fallback", new() { ["sendToContactEmail"] = "true" }, "/App/Alerts/Index");
        AssertRedirect(r, "/App/Alerts");
        Assert.True(await f.WithDbAsync(db => db.Customers.Where(c => c.Id == cid).Select(c => c.AlertContactEmailWhenNoChannel).SingleAsync()));
    }

    [Fact]
    public void Alert_sender_runs_with_the_scheduler_disabled()
    {
        // The factory sets Monitoring:Enabled=false: "check now" alerts must still be delivered.
        var sender = f.Services.GetServices<IHostedService>().OfType<AlertSenderService>().Single();
        Assert.NotNull(sender.ExecuteTask);
        Assert.False(sender.ExecuteTask!.IsCompleted);
    }

    private Task<int> AddChannelAsync(int customerId, string name, string url, bool enabled = true) =>
        f.WithDbAsync(async db =>
        {
            var ch = new AlertChannel { CustomerId = customerId, Name = name, Type = AlertChannelType.Webhook, Target = url, IsEnabled = enabled };
            db.AlertChannels.Add(ch);
            await db.SaveChangesAsync();
            return ch.Id;
        });
}

/// <summary>Web app with Twilio configured (operator-level settings).</summary>
public class TwilioWebAppFactory : TestWebAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Twilio:AccountSid", "AC0123456789abcdef");
        builder.UseSetting("Twilio:AuthToken", "test-token");
        builder.UseSetting("Twilio:FromNumber", "+15550001111");
    }
}

public class SmsVoiceChannelTests(TwilioWebAppFactory f) : IClassFixture<TwilioWebAppFactory>
{
    private static Dictionary<string, string> Form(string name, string type, string target) => new()
    {
        ["Input.Name"] = name, ["Input.Type"] = type, ["Input.Target"] = target, ["Input.IsEnabled"] = "true"
    };

    [Fact]
    public async Task Sms_and_voice_need_a_plan_that_allows_them()
    {
        var cid = await f.DemoCustomerIdAsync();
        var c = await f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);

        // Demo customer is on Standard: Twilio configured, but the plan does not include SMS / voice.
        Assert.DoesNotContain("value=\"Sms\"", await c.GetHtmlAsync("/App/Alerts/Create"));
        var r = await c.PostFormAsync("/App/Alerts/Create", Form("Pager", "Sms", "+905551234567"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("SMS and voice call alerts are not available for your plan.", await r.ReadHtmlAsync());

        var enterprise = await f.PlanIdAsync("Enterprise");
        Assert.True(await f.WithDbAsync(db => db.Plans.Where(p => p.Id == enterprise).Select(p => p.AllowsSmsVoice).SingleAsync()));
        await f.WithDbAsync(db => db.Customers.Where(x => x.Id == cid).ExecuteUpdateAsync(s => s.SetProperty(x => x.PlanId, enterprise)));
        try
        {
            var html = await c.GetHtmlAsync("/App/Alerts/Create");
            Assert.Contains("value=\"Sms\"", html);
            Assert.Contains("value=\"VoiceCall\"", html);

            r = await c.PostFormAsync("/App/Alerts/Create", Form("Pager", "Sms", "05551234567"));
            Assert.Contains("E.164", await r.ReadHtmlAsync());

            AssertRedirect(await c.PostFormAsync("/App/Alerts/Create", Form("Pager", "Sms", "+905551234567")), "/App/Alerts");
            AssertRedirect(await c.PostFormAsync("/App/Alerts/Create", Form("Caller", "VoiceCall", "+15551234567")), "/App/Alerts");
            var types = await f.WithDbAsync(db => db.AlertChannels.Where(x => x.CustomerId == cid).Select(x => x.Type).ToListAsync());
            Assert.Contains(AlertChannelType.Sms, types);
            Assert.Contains(AlertChannelType.VoiceCall, types);
        }
        finally
        {
            var standard = await f.PlanIdAsync("Standard");
            await f.WithDbAsync(db => db.Customers.Where(x => x.Id == cid).ExecuteUpdateAsync(s => s.SetProperty(x => x.PlanId, standard)));
        }
    }

    [Fact]
    public async Task Seeded_plans_allow_sms_only_on_enterprise_and_superadmin_can_toggle_it()
    {
        var flags = await f.WithDbAsync(db => db.Plans.ToDictionaryAsync(p => p.Name, p => p.AllowsSmsVoice));
        Assert.True(flags["Enterprise"]);
        Assert.False(flags["Basic"]);
        Assert.False(flags["Standard"]);
        Assert.False(flags["Pro"]);

        var sa = await f.LoginSuperAdminAsync();
        var pro = await f.PlanIdAsync("Pro");
        var edit = await sa.GetStringAsync($"/SuperAdmin/Plans/Edit/{pro}");
        Assert.Contains("Input.AllowsSmsVoice", edit);
        var plan = await f.WithDbAsync(db => db.Plans.AsNoTracking().SingleAsync(p => p.Id == pro));
        var r = await sa.PostFormAsync($"/SuperAdmin/Plans/Edit/{pro}", new()
        {
            ["Input.Name"] = plan.Name,
            ["Input.MinIntervalSeconds"] = plan.MinIntervalSeconds.ToString(),
            ["Input.MaxMonitors"] = plan.MaxMonitors.ToString(),
            ["Input.RetentionDays"] = plan.RetentionDays.ToString(),
            ["Input.MonthlyPrice"] = plan.MonthlyPrice.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Input.IsActive"] = "true",
            ["Input.AllowsSmsVoice"] = "true"
        });
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.True(await f.WithDbAsync(db => db.Plans.Where(p => p.Id == pro).Select(p => p.AllowsSmsVoice).SingleAsync()));
    }
}
