using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Features.MonitorSetup;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Tests.Accessibility;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;
using static TriggerCheck.Tests.App.AppTestData;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Tests.Ai.Features;

/// <summary>Counts every monitor row any DbContext tries to insert.</summary>
public sealed class MonitorInsertSpy : SaveChangesInterceptor
{
    private int _inserts;
    public int Inserts => Volatile.Read(ref _inserts);

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Count(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Count(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    private void Count(DbContext? db)
    {
        if (db is null) return;
        Interlocked.Add(ref _inserts, db.ChangeTracker.Entries<MonitorEntity>().Count(e => e.State == EntityState.Added));
    }
}

/// <summary>AI on with the fake client, the SSRF guard on (production default) and a spy on monitor inserts.</summary>
public sealed class MonitorSetupWebAppFactory : TestWebAppFactory
{
    public MonitorInsertSpy Spy { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Ai:Enabled", "true");
        builder.UseSetting("Ai:Events:PollSeconds", "86400");
        builder.UseSetting("Monitoring:AllowPrivateTargets", "false");
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<IAiClient>();
            s.AddSingleton<IAiClient>(new FakeAiClient());
            s.ConfigureDbContext<AppDbContext>(o => o.AddInterceptors(Spy));
        });
    }

    public FakeAiClient Fake => (FakeAiClient)Services.GetRequiredService<IAiClient>();
}

/// <summary>AI-9 on the create-monitor page: pre-fill, never save, validation, gating, roles, Turkish and accessibility.</summary>
public class MonitorSetupWebTests(MonitorSetupWebAppFactory f) : IClassFixture<MonitorSetupWebAppFactory>
{
    private const string ViewerEmail = "setup-viewer@demo.local", ViewerPassword = "Viewer123!";
    private const string Box = "id=\"monitor-assistant\"";

    private Task<HttpClient> AdminAsync() => f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);

    private static string Answer(string type, string? target, int? interval, string? keyword = null, string explanation = "A keyword monitor checks the page.", string outcome = "form", string? clarification = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["outcome"] = outcome, ["clarification"] = clarification, ["explanation"] = explanation, ["type"] = type, ["name"] = null,
            ["target"] = target, ["port"] = null, ["keyword"] = keyword, ["http_method"] = null, ["expected_status_codes"] = null,
            ["interval_seconds"] = interval, ["timeout_seconds"] = null, ["failure_threshold"] = null, ["dns_record_type"] = null,
            ["json_path"] = null, ["assertion_operator"] = null, ["expected_value"] = null, ["grace_period_seconds"] = null,
        });

    private void Respond(string json) => f.Fake.Responder = _ => FakeAiClient.Success(json);

    private static Task<HttpResponseMessage> AssistAsync(HttpClient c, string request) =>
        c.PostFormAsync("/App/Monitors/Create?handler=Assist", new() { ["assistantRequest"] = request }, "/App/Monitors/Create");

    private async Task ArrangeAsync(bool aiEnabled = true, string language = "en")
    {
        var cid = await f.DemoCustomerIdAsync();
        await f.WithDbAsync(db => db.Customers.Where(c => c.Id == cid)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.AiEnabled, aiEnabled).SetProperty(c => c.AiLanguage, language)));
        f.Fake.IsAvailable = true;
    }

    [Fact]
    public async Task Assistant_prefills_the_form_and_never_saves()
    {
        await ArrangeAsync();
        Respond(Answer("keyword", "[U1]", 60, "[T1]"));
        var admin = await AdminAsync();
        var html = await admin.GetHtmlAsync("/App/Monitors/Create");
        Assert.Contains(Box, html);
        Assert.Contains("Describe what to monitor", html);

        var count = await f.WithDbAsync(db => db.Monitors.CountAsync());
        var inserts = f.Spy.Inserts;
        var decisions = await f.WithDbAsync(db => db.AiDecisions.CountAsync(d => d.Feature == AiFeatures.MonitorSetupAssistant));

        var r = await AssistAsync(admin, "Monitor https://shop.example.test/checkout/pay every minute, alert if 'Pay now' is missing");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode); // re-rendered, no redirect
        var page = await r.ReadHtmlAsync();
        Assert.Contains("data-assistant-filled=\"true\"", page);
        Assert.Contains("A keyword monitor checks the page.", page);
        Assert.Contains("value=\"https://shop.example.test/checkout/pay\"", page);
        Assert.Contains("value=\"Pay now\"", page);
        Assert.Contains("<option value=\"Keyword\" selected=\"selected\">", page);
        Assert.Contains("<option value=\"60\" selected=\"selected\">", page);
        Assert.Contains("Nothing has been saved yet.", page);
        Assert.Contains("action=\"/App/Monitors/Create\"", page); // the form posts to the normal create handler
        Assert.Equal(count, await f.WithDbAsync(db => db.Monitors.CountAsync()));
        Assert.Equal(inserts, f.Spy.Inserts);
        Assert.Equal(decisions + 1, await f.WithDbAsync(db => db.AiDecisions.CountAsync(d => d.Feature == AiFeatures.MonitorSetupAssistant && d.Status == AiDecisionStatus.Succeeded)));
        Assert.DoesNotContain("/checkout/pay", f.Fake.Requests.Last().UserContent);

        // The form draft is not an interpretation: the AI page's recent list does not show it as a failed summary.
        Assert.DoesNotContain("The AI summary could not be generated.", await admin.GetHtmlAsync("/App/Ai"));

        // Saving is the user's normal create post.
        var name = "Setup saved " + Guid.NewGuid().ToString("N")[..6];
        var save = await admin.PostFormAsync("/App/Monitors/Create",
            MonitorForm(name, ("Type", "Keyword"), ("Target", "https://shop.example.test/checkout/pay"), ("Keyword", "Pay now")));
        AssertRedirect(save, "/App/Monitors/Details/");
        Assert.Equal(inserts + 1, f.Spy.Inserts);
    }

    [Fact]
    public async Task Interval_below_the_plan_minimum_is_raised_with_an_explanation()
    {
        await ArrangeAsync();
        Respond(Answer("http", "[U1]", 15, explanation: "An HTTP monitor checks the site every 15 seconds."));
        var page = await (await AssistAsync(await AdminAsync(), "check https://www.example.test every 15 seconds")).ReadHtmlAsync();

        Assert.Contains("You asked for a check every 15 sec. Your plan (Standard) allows 1 min at the fastest, so 1 min was set.", page);
        Assert.Contains("<option value=\"60\" selected=\"selected\">", page);
        Assert.DoesNotContain("<option value=\"15\"", page); // not even offered on Standard
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/admin")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    public async Task Private_target_from_the_model_shows_the_normal_validation_error(string target)
    {
        await ArrangeAsync();
        Respond(Answer("http", target, 60, explanation: "An HTTP monitor."));
        // A distinct request per case: an identical one would reuse the earlier answer.
        var page = await (await AssistAsync(await AdminAsync(), "monitor the admin panel " + target.Length)).ReadHtmlAsync();

        Assert.Contains("This address points to a private or internal network and cannot be monitored.", page);
        Assert.Contains("Some suggested values need your attention; see the highlighted fields.", page);
        Assert.Contains("alert-warning", page);
    }

    [Theory]
    [InlineData("monitor http://10.0.0.5/x", "http://10.0.0.5/x", "Http")]
    [InlineData("check localhost:8080/internal/health every minute", "https://localhost:8080/internal/health", "Http")]
    [InlineData("ping 192.168.1.10:22/x", "192.168.1.10", "Ping")]
    public async Task Private_link_restored_from_the_users_own_text_is_rejected_by_validation(string request, string restored, string type)
    {
        await ArrangeAsync();
        Respond(Answer(type.ToLowerInvariant(), "[U1]", 60, explanation: "A monitor."));
        var page = await (await AssistAsync(await AdminAsync(), request)).ReadHtmlAsync();

        Assert.Contains($"value=\"{restored}\"", page); // the user's own link is back in the form ...
        Assert.Contains("This address points to a private or internal network and cannot be monitored.", page); // ... and rejected
        Assert.Contains("Some suggested values need your attention; see the highlighted fields.", page);
        Assert.DoesNotContain("/internal", f.Fake.Requests.Last().UserContent);
    }

    [Fact]
    public async Task Overlong_posts_are_capped_on_the_server()
    {
        await ArrangeAsync();
        Respond(Answer("http", "[U1]", 60, explanation: "An HTTP monitor."));
        var huge = "check https://www.example.test " + new string('z', 50_000);
        var page = await (await AssistAsync(await AdminAsync(), huge)).ReadHtmlAsync();

        Assert.DoesNotContain(new string('z', MonitorSetupRequest.RawCap(1000) + 1), page); // the echo is capped
        Assert.Contains("Your description was too long and was shortened.", page);
        Assert.True(f.Fake.Requests.Last().UserContent.Length < 3000);
    }

    [Fact]
    public async Task Ambiguous_request_asks_instead_of_filling()
    {
        await ArrangeAsync();
        Respond(Answer("http", null, null, explanation: "No server named.", outcome: "clarify", clarification: "Which server should be monitored?"));
        var page = await (await AssistAsync(await AdminAsync(), "monitor our servers")).ReadHtmlAsync();

        Assert.Contains("Which server should be monitored?", page);
        Assert.Contains("data-assistant-filled=\"false\"", page);
        Assert.DoesNotContain("Nothing has been saved yet.", page);
    }

    [Fact]
    public async Task Box_is_hidden_when_AI_is_off_or_opted_out_and_the_form_still_works()
    {
        await ArrangeAsync();
        var admin = await AdminAsync();
        try
        {
            f.Fake.IsAvailable = false;
            Assert.DoesNotContain(Box, await admin.GetHtmlAsync("/App/Monitors/Create"));
            var requests = f.Fake.Requests.Count;
            var r = await AssistAsync(admin, "check https://shop.example.test");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.DoesNotContain(Box, await r.ReadHtmlAsync());
            Assert.Equal(requests, f.Fake.Requests.Count);

            f.Fake.IsAvailable = true;
            await ArrangeAsync(aiEnabled: false);
            Assert.DoesNotContain(Box, await admin.GetHtmlAsync("/App/Monitors/Create"));
            await AssistAsync(admin, "check https://shop.example.test");
            Assert.Equal(requests, f.Fake.Requests.Count);

            var name = "No AI " + Guid.NewGuid().ToString("N")[..6];
            AssertRedirect(await admin.PostFormAsync("/App/Monitors/Create", MonitorForm(name)), "/App/Monitors/Details/");
        }
        finally
        {
            await ArrangeAsync();
        }
    }

    [Fact]
    public async Task Viewer_cannot_use_the_assistant()
    {
        await ArrangeAsync();
        var cid = await f.DemoCustomerIdAsync();
        if (!await f.WithDbAsync(db => db.Users.AnyAsync(u => u.Email == ViewerEmail)))
            await f.AddUserAsync(cid, ViewerEmail, ViewerPassword, Roles.CustomerViewer);
        var viewer = await f.LoginAsync(ViewerEmail, ViewerPassword);
        var requests = f.Fake.Requests.Count;

        AssertAccessDenied(await viewer.PostFormAsync("/App/Monitors/Create?handler=Assist", new() { ["assistantRequest"] = "check https://shop.example.test" }, "/App/Monitors"));
        AssertAccessDenied(await viewer.GetAsync("/App/Monitors/Create"));
        Assert.Equal(requests, f.Fake.Requests.Count);
    }

    [Fact]
    public async Task Turkish_ui_and_AI_language()
    {
        await ArrangeAsync(language: "tr");
        try
        {
            Respond(Answer("keyword", "[U1]", 15, "[T1]", explanation: "Ödeme sayfası dakikada bir kontrol edilir."));
            var admin = await AdminAsync();
            await admin.SetLanguageAsync("tr");
            Assert.Contains("Neyi izlemek istediğinizi yazın", await admin.GetHtmlAsync("/App/Monitors/Create"));

            var page = await (await AssistAsync(admin, "shop.example.test/odeme sayfasını 15 saniyede bir izle, 'Ödeme' yazısı yoksa alarm ver")).ReadHtmlAsync();
            Assert.Contains("Ödeme sayfası dakikada bir kontrol edilir.", page);
            Assert.Contains("Planınız (Standard) en sık", page);
            Assert.Contains("Henüz hiçbir şey kaydedilmedi.", page);
            Assert.Contains("value=\"https://shop.example.test/odeme\"", page);
            Assert.StartsWith("response_language: Turkish", f.Fake.Requests.Last().UserContent);
            await admin.SetLanguageAsync("en");
        }
        finally
        {
            await ArrangeAsync();
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("tr")]
    public async Task Create_page_with_the_assistant_passes_the_accessibility_audit(string lang)
    {
        await ArrangeAsync();
        Respond(Answer("http", "[U1]", 15, explanation: "An HTTP monitor."));
        var admin = await AdminAsync();
        await admin.SetLanguageAsync(lang);
        try
        {
            var empty = await (await admin.GetAsync("/App/Monitors/Create")).Content.ReadAsStringAsync();
            Assert.Empty(AccessibilityAudit.Check(empty, lang));
            var filled = await (await AssistAsync(admin, "check https://www.example.test every 15 seconds")).Content.ReadAsStringAsync();
            Assert.Contains(Box, filled);
            var problems = AccessibilityAudit.Check(filled, lang);
            Assert.True(problems.Count == 0, string.Join("\n", problems));
        }
        finally
        {
            await admin.SetLanguageAsync("en");
        }
    }
}
