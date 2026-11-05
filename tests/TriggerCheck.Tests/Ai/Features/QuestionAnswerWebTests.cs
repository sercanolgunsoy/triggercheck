using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Qa;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.Accessibility;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;

namespace TriggerCheck.Tests.Ai.Features;

/// <summary>Web host with AI switched on and the scripted tool-use client (no key, no network).</summary>
public sealed class QaWebAppFactory : TestWebAppFactory
{
    public FakeAiToolClient Fake { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Ai:Enabled", "true");
        builder.UseSetting("Ai:ApiKey", ""); // never a real key: the other AI features stay unavailable
        builder.UseSetting("Ai:Qa:PerUserPerMinute", "100");
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<IAiToolClient>();
            s.AddSingleton<IAiToolClient>(Fake);
        });
    }
}

/// <summary>AI-7 page /App/Ask: roles, gating, link rendering (tenant-checked), feedback, Turkish and accessibility.</summary>
public class QuestionAnswerWebTests(QaWebAppFactory f) : IClassFixture<QaWebAppFactory>
{
    private const string Password = "Secret123!";

    private async Task<(int Cid, HttpClient Client)> TenantAsync(string role = Roles.CustomerViewer, string plan = "Enterprise", bool aiEnabled = true)
    {
        var cid = await f.AddCustomerAsync("QA " + Guid.NewGuid().ToString("N")[..6], plan);
        await f.WithDbAsync(db => db.Customers.Where(c => c.Id == cid).ExecuteUpdateAsync(s => s.SetProperty(c => c.AiEnabled, aiEnabled)));
        var email = $"qa-{Guid.NewGuid():N}@demo.local";
        await f.AddUserAsync(cid, email, Password, role);
        return (cid, await f.LoginAsync(email, Password));
    }

    private static async Task<string> AskAsync(HttpClient c, string question, Dictionary<string, string>? extra = null)
    {
        var fields = new Dictionary<string, string>(extra ?? []) { ["Question"] = question };
        var r = await c.PostFormAsync("/App/Ask", fields);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await r.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task Viewer_asks_and_the_answer_links_own_records_only()
    {
        var (cid, viewer) = await TenantAsync();
        var monitor = await f.AddMonitorAsync(cid, "Payments API");
        var incident = await f.WithDbAsync(async db =>
        {
            var i = new Incident { MonitorId = monitor, StartedAtUtc = DateTime.UtcNow.AddHours(-20), ResolvedAtUtc = DateTime.UtcNow.AddHours(-19), Cause = "HTTP 503" };
            db.Incidents.Add(i);
            await db.SaveChangesAsync();
            return i.Id;
        });
        var otherCid = await f.AddCustomerAsync("QA other tenant", "Enterprise");
        var foreign = await f.AddMonitorAsync(otherCid, "Foreign secret monitor");

        f.Fake.Script.Enqueue(FakeAiToolClient.Answer(
            $"{QaAnswerText.MonitorRef(monitor)} went down in {QaAnswerText.IncidentRef(incident)}. <script>alert(1)</script> Also {QaAnswerText.MonitorRef(foreign)}."));
        var html = await AskAsync(viewer, "Why did the payments API go down yesterday?");

        Assert.Contains($"href=\"/App/Monitors/Details/{monitor}\"", html);
        Assert.Contains(">Payments API</a>", html);
        Assert.Contains($"href=\"/App/Incidents/Details/{incident}\"", html);
        Assert.Contains("Sources", html);
        Assert.DoesNotContain($"/App/Monitors/Details/{foreign}", html);
        Assert.DoesNotContain("Foreign secret monitor", html);
        Assert.Contains("(unknown record)", html);
        Assert.DoesNotContain("<script>alert(1)</script>", html); // model text is HTML-encoded
        Assert.Contains("AI-generated", html);
        Assert.Contains("Why did the payments API go down yesterday?", html);
        Assert.Contains("name=\"History[0].Answer\"", html); // the conversation stays in the page

        var d = await f.WithDbAsync(db => db.AiDecisions.AsNoTracking().Where(x => x.CustomerId == cid).SingleAsync());
        Assert.Equal(AiFeatures.QuestionAnswer, d.Feature);
        Assert.Equal(AiDecisionStatus.Succeeded, d.Status);
        Assert.Null(d.OutputJson);

        // A follow-up sends the earlier exchange; a forged history entry with another tenant's id still renders unlinked.
        f.Fake.Script.Enqueue(FakeAiToolClient.Answer("No other incidents."));
        html = await AskAsync(viewer, "Any other incidents?", new()
        {
            ["History[0].Question"] = "Earlier", ["History[0].Answer"] = QaAnswerText.MonitorRef(foreign), ["History[0].DecisionId"] = "0",
        });
        Assert.DoesNotContain("Foreign secret monitor", html);
        Assert.Contains("No other incidents.", html);
        Assert.Equal("question: Earlier", f.Fake.Requests.Last().Messages[0].Text);
    }

    [Fact]
    public async Task Feedback_keeps_the_conversation()
    {
        var (cid, viewer) = await TenantAsync();
        f.Fake.Script.Enqueue(FakeAiToolClient.Answer("All monitors are up."));
        await AskAsync(viewer, "Is everything up?");
        var id = await f.WithDbAsync(db => db.AiDecisions.Where(x => x.CustomerId == cid).Select(x => x.Id).SingleAsync());

        var r = await viewer.PostFormAsync("/App/Ask?handler=Feedback", new()
        {
            ["vote"] = $"{id}:1", ["History[0].Question"] = "Is everything up?", ["History[0].Answer"] = "All monitors are up.", ["History[0].DecisionId"] = id.ToString(),
        });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var html = await r.Content.ReadAsStringAsync();
        Assert.Contains("All monitors are up.", html);
        Assert.Contains("aria-pressed=\"true\"", html);
        Assert.Equal(1, await f.WithDbAsync(db => db.AiDecisions.Where(x => x.Id == id).Select(x => x.UserFeedback).SingleAsync()));

        // Another tenant's decision cannot be voted on.
        var (_, other) = await TenantAsync();
        r = await other.PostFormAsync("/App/Ask?handler=Feedback", new() { ["vote"] = $"{id}:-1" }, formPageUrl: "/App/Ask");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Theory]
    [InlineData("Pro", true, "not included in your plan")]
    [InlineData("Enterprise", false, "AI features are disabled for your account")]
    public async Task Page_explains_why_questions_are_unavailable(string plan, bool aiEnabled, string text)
    {
        var (_, admin) = await TenantAsync(Roles.CustomerAdmin, plan, aiEnabled);
        var html = await admin.GetHtmlAsync("/App/Ask");
        Assert.Contains("id=\"ask-unavailable\"", html);
        Assert.Contains(text, html);
        Assert.DoesNotContain("id=\"ask-question\"", html);

        var before = f.Fake.Requests.Count;
        var r = await admin.PostFormAsync("/App/Ask", new() { ["Question"] = "Hi?" });
        Assert.Contains("id=\"ask-error\"", await r.Content.ReadAsStringAsync());
        Assert.Equal(before, f.Fake.Requests.Count);
    }

    [Fact]
    public async Task Page_requires_a_customer_login_and_shows_in_the_menu()
    {
        var anon = f.CreateBrowser();
        var r = await anon.GetAsync("/App/Ask");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);

        var (_, viewer) = await TenantAsync();
        var html = await viewer.GetHtmlAsync("/App/Ask");
        Assert.Contains("href=\"/App/Ask\"", html);
        Assert.Contains("id=\"ask-question\"", html);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("tr")]
    public async Task Page_with_an_answer_passes_the_accessibility_audit(string lang)
    {
        var (cid, viewer) = await TenantAsync();
        if (lang != "en") await viewer.SetLanguageAsync(lang);
        var monitor = await f.AddMonitorAsync(cid, "A11y QA");
        Assert.Empty(AccessibilityAudit.Check(await viewer.GetHtmlAsync("/App/Ask"), lang));

        f.Fake.Script.Enqueue(FakeAiToolClient.Answer($"{QaAnswerText.MonitorRef(monitor)} is up."));
        var html = await AskAsync(viewer, "Is it up?");
        var problems = AccessibilityAudit.Check(html, lang);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        if (lang == "tr")
        {
            Assert.Contains("Yapay zekâya sor", html);
            Assert.Contains("Kaynaklar", html);
        }
    }
}
