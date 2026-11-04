using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Features;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;

namespace TriggerCheck.Tests.Ai.Features;

/// <summary>Web host with AI switched on and the fake client (no key, no network); the event watcher is effectively idle.</summary>
public sealed class AiEnabledWebAppFactory : TestWebAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Ai:Enabled", "true");
        builder.UseSetting("Ai:Events:PollSeconds", "86400");
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<IAiClient>();
            s.AddSingleton<IAiClient>(new FakeAiClient());
        });
    }
}

/// <summary>AI-2 / AI-3 rendering: incident details and list, monitor slowdown card, dashboard badge, unavailable states.</summary>
public class IncidentAiWebTests(AiEnabledWebAppFactory f) : IClassFixture<AiEnabledWebAppFactory>
{
    private Task<HttpClient> AdminAsync() => f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);

    private Task<int> AddIncidentAsync(int monitorId, DateTime started, DateTime? resolved = null) => f.WithDbAsync(async db =>
    {
        var i = new Incident { MonitorId = monitorId, StartedAtUtc = started, ResolvedAtUtc = resolved, Cause = "HTTP 503" };
        db.Incidents.Add(i);
        await db.SaveChangesAsync();
        return i.Id;
    });

    private Task AddDecisionAsync(int customerId, string feature, string subjectType, string subjectId, AiDecisionStatus status = AiDecisionStatus.Succeeded) =>
        f.WithDbAsync(async db =>
        {
            db.AiDecisions.Add(new AiDecision
            {
                CustomerId = customerId, Feature = feature, SubjectType = subjectType, SubjectId = subjectId, Model = "claude-opus-5-5", Effort = "low",
                Status = status, OutputJson = status == AiDecisionStatus.Succeeded ? FakeAiClient.CannedJson : null, CreatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        });

    private Task SetCustomerAsync(int customerId, bool aiEnabled) =>
        f.WithDbAsync(db => db.Customers.Where(c => c.Id == customerId).ExecuteUpdateAsync(s => s.SetProperty(c => c.AiEnabled, aiEnabled)));

    [Fact]
    public async Task Incident_details_and_list_show_the_AI_summary_with_feedback()
    {
        var cid = await f.DemoCustomerIdAsync();
        await SetCustomerAsync(cid, true);
        var mid = await f.AddMonitorAsync(cid, "AI incident monitor");
        var id = await AddIncidentAsync(mid, DateTime.UtcNow.AddHours(-2), DateTime.UtcNow.AddHours(-1));
        await AddDecisionAsync(cid, AiFeatures.IncidentInterpretation, AiSubjects.Incident, id.ToString());
        var admin = await AdminAsync();

        var html = await admin.GetHtmlAsync($"/App/Incidents/Details/{id}");
        Assert.Contains("data-ai-state=\"decision\"", html);
        Assert.Contains("AI-generated", html);
        Assert.Contains("The monitor failed because the target returned HTTP 503", html);
        Assert.Contains("Was this helpful?", html);
        Assert.Contains($"value=\"/App/Incidents/Details/{id}\"", html); // feedback returns to the incident

        var list = await admin.GetHtmlAsync("/App/Incidents");
        Assert.Contains("ai-list-summary", list);
        Assert.Contains("The monitor failed because the target returned HTTP 503", list);
    }

    [Fact]
    public async Task Incident_details_explain_why_there_is_no_summary()
    {
        var cid = await f.DemoCustomerIdAsync();
        var mid = await f.AddMonitorAsync(cid, "AI states monitor");
        var admin = await AdminAsync();

        // Fresh incident, nothing generated yet: being prepared.
        await SetCustomerAsync(cid, true);
        var fresh = await AddIncidentAsync(mid, DateTime.UtcNow.AddMinutes(-1));
        Assert.Contains("data-ai-state=\"pending\"", await admin.GetHtmlAsync($"/App/Incidents/Details/{fresh}"));

        // Old incident without a decision.
        var old = await AddIncidentAsync(mid, DateTime.UtcNow.AddDays(-3), DateTime.UtcNow.AddDays(-3).AddMinutes(5));
        Assert.Contains("No AI summary is available for this item.", await admin.GetHtmlAsync($"/App/Incidents/Details/{old}"));

        // Failed call.
        var failed = await AddIncidentAsync(mid, DateTime.UtcNow.AddDays(-2), DateTime.UtcNow.AddDays(-2).AddMinutes(5));
        await AddDecisionAsync(cid, AiFeatures.IncidentInterpretation, AiSubjects.Incident, failed.ToString(), AiDecisionStatus.Failed);
        Assert.Contains("The AI summary could not be generated.", await admin.GetHtmlAsync($"/App/Incidents/Details/{failed}"));

        // Opted out: a clear note with a link to the settings (admin), localized.
        await SetCustomerAsync(cid, false);
        var html = await admin.GetHtmlAsync($"/App/Incidents/Details/{fresh}");
        Assert.Contains("data-ai-state=\"opt-out\"", html);
        Assert.Contains("href=\"/App/Ai\"", html);
        await admin.SetLanguageAsync("tr");
        Assert.Contains("Yapay zekâ özetleri hesabınız için kapalı.", await admin.GetHtmlAsync($"/App/Incidents/Details/{fresh}"));
        await admin.SetLanguageAsync("en");
        await SetCustomerAsync(cid, true);
    }

    [Fact]
    public async Task Plan_without_the_feature_shows_not_in_plan_and_other_tenants_decisions_are_never_shown()
    {
        var other = await f.AddCustomerAsync("AI Basic Co", planName: "Basic");
        await SetCustomerAsync(other, true);
        await f.WithDbAsync(db => db.Plans.Where(p => p.Name == "Basic").ExecuteUpdateAsync(s => s.SetProperty(p => p.AiFeatures, "")));
        const string email = "ai-basic-admin@basic.local";
        await f.AddUserAsync(other, email, "Admin123!", Roles.CustomerAdmin);
        var otherAdmin = await f.LoginAsync(email, "Admin123!");
        var otherIncident = await AddIncidentAsync(await f.AddMonitorAsync(other, "Basic monitor"), DateTime.UtcNow.AddHours(-1));
        Assert.Contains("data-ai-state=\"not-in-plan\"", await otherAdmin.GetHtmlAsync($"/App/Incidents/Details/{otherIncident}"));
        await f.WithDbAsync(db => db.Plans.Where(p => p.Name == "Basic").ExecuteUpdateAsync(s => s.SetProperty(p => p.AiFeatures, AiFeatures.IncidentInterpretation)));

        // A decision of the demo customer stored under the other tenant's incident id is not shown to it.
        var cid = await f.DemoCustomerIdAsync();
        await AddDecisionAsync(cid, AiFeatures.IncidentInterpretation, AiSubjects.Incident, otherIncident.ToString());
        var html = await otherAdmin.GetHtmlAsync($"/App/Incidents/Details/{otherIncident}");
        Assert.DoesNotContain("data-ai-state=\"decision\"", html);
        Assert.DoesNotContain("returned HTTP 503", html);
    }

    [Fact]
    public async Task Monitor_details_show_slowdown_with_raw_text_and_AI3_explanation_and_dashboard_badge()
    {
        var cid = await f.DemoCustomerIdAsync();
        await SetCustomerAsync(cid, true);
        var mid = await f.AddMonitorAsync(cid, "AI slow monitor", m => m.Status = MonitorStatus.Up);
        var signalId = await f.WithDbAsync(async db =>
        {
            var s = new AnomalySignal
            {
                MonitorId = mid, CustomerId = cid, Metric = "ResponseTimeMs", StartedAtUtc = DateTime.UtcNow.AddMinutes(-20), Score = 7, UpdatedAtUtc = DateTime.UtcNow,
                DetailsJson = """{"points":[{"ratio":3.2},{"ratio":4.1}]}"""
            };
            db.AnomalySignals.Add(s);
            await db.SaveChangesAsync();
            return s.Id;
        });
        await AddDecisionAsync(cid, AiFeatures.SlowdownExplanation, AiSubjects.AnomalySignal, signalId.ToString());
        var admin = await AdminAsync();

        var html = await admin.GetHtmlAsync($"/App/Monitors/Details/{mid}");
        Assert.Contains("id=\"slowdown-card\"", html);
        Assert.Contains("up to 4.1× the baseline", html);
        Assert.Contains("No outage so far: the monitor is still up.", html);
        Assert.Contains("data-ai-state=\"decision\"", html);

        var dashboard = await admin.GetHtmlAsync("/App");
        Assert.Contains("slow-badge", dashboard);
    }

    [Fact]
    public async Task Admin_sets_the_AI_summary_language_and_enabling_AI_takes_the_UI_language()
    {
        var cid = await f.DemoCustomerIdAsync();
        var admin = await AdminAsync();
        var r = await admin.PostFormAsync("/App/Ai?handler=Language", new() { ["language"] = "tr" }, formPageUrl: "/App/Ai");
        AppTestData.AssertRedirect(r, "/App/Ai");
        Assert.Equal("tr", await f.WithDbAsync(db => db.Customers.Where(c => c.Id == cid).Select(c => c.AiLanguage).SingleAsync()));

        r = await admin.PostFormAsync("/App/Ai?handler=Language", new() { ["language"] = "de" }, formPageUrl: "/App/Ai");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, r.StatusCode);

        await admin.PostFormAsync("/App/Ai", new() { ["enabled"] = "true" }); // UI is English
        Assert.Equal("en", await f.WithDbAsync(db => db.Customers.Where(c => c.Id == cid).Select(c => c.AiLanguage).SingleAsync()));
    }
}

/// <summary>With AI switched off (default test host) the incident page says so and still renders fully.</summary>
public class IncidentAiOffWebTests(TestWebAppFactory f) : IClassFixture<TestWebAppFactory>
{
    [Fact]
    public async Task Incident_page_shows_AI_off_state()
    {
        var cid = await f.DemoCustomerIdAsync();
        var mid = await f.AddMonitorAsync(cid, "AI off monitor");
        var id = await f.WithDbAsync(async db =>
        {
            var i = new Incident { MonitorId = mid, StartedAtUtc = DateTime.UtcNow.AddMinutes(-3), Cause = "HTTP 500" };
            db.Incidents.Add(i);
            await db.SaveChangesAsync();
            return i.Id;
        });
        var admin = await f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);
        var html = await admin.GetHtmlAsync($"/App/Incidents/Details/{id}");
        Assert.Contains("data-ai-state=\"off\"", html);
        Assert.Contains("HTTP 500", html);
    }
}
