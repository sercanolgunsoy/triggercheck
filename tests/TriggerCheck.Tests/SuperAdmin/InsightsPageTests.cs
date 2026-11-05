using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Ai.Client;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.Ai.Features;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Tests.SuperAdmin;

internal static class InsightsSeed
{
    /// <summary>A customer whose only monitor has been DOWN for 30 days (abandoned signal) and one with twelve incidents (noise).</summary>
    public static async Task<(int Abandoned, int Noisy)> SeedAsync(TestWebAppFactory f, string suffix)
    {
        return await f.WithDbAsync(async db =>
        {
            var plan = await db.Plans.FirstAsync(p => p.Name == "Standard");
            var abandoned = new Customer { Name = "Ghost Town " + suffix, ContactEmail = "ghost@example.test", PlanId = plan.Id };
            var noisy = new Customer { Name = "Flappy Ltd " + suffix, ContactEmail = "flappy@example.test", PlanId = plan.Id };
            db.Customers.AddRange(abandoned, noisy);
            await db.SaveChangesAsync();
            db.Monitors.Add(new MonitorEntity
            {
                CustomerId = abandoned.Id, Name = "Old landing page", Target = "https://ghost.example.test", IntervalSeconds = 300,
                Status = MonitorStatus.Down, StatusChangedAtUtc = DateTime.UtcNow.AddDays(-30)
            });
            var m = new MonitorEntity { CustomerId = noisy.Id, Name = "Flaky API", Target = "https://flappy.example.test/api", IntervalSeconds = 60, Status = MonitorStatus.Up };
            db.Monitors.Add(m);
            await db.SaveChangesAsync();
            for (var i = 0; i < 12; i++)
                db.Incidents.Add(new Incident { MonitorId = m.Id, StartedAtUtc = DateTime.UtcNow.AddHours(-1 - i), ResolvedAtUtc = DateTime.UtcNow.AddHours(-1 - i).AddMinutes(2) });
            await db.SaveChangesAsync();
            return (abandoned.Id, noisy.Id);
        });
    }
}

/// <summary>AI-12 SuperAdmin insights page without AI (the default test host: Ai:Enabled is false) and its authorization.</summary>
public class InsightsPageTests(TestWebAppFactory f) : IClassFixture<TestWebAppFactory>
{
    private const string ViewerEmail = "insights-viewer@demo.local";
    private const string ViewerPassword = "Viewer123!";

    [Fact]
    public async Task Page_lists_the_signals_without_AI()
    {
        var (abandoned, noisy) = await InsightsSeed.SeedAsync(f, "A");
        var admin = await f.LoginSuperAdminAsync();

        var html = await admin.GetHtmlAsync("/SuperAdmin/Insights");

        Assert.Contains("id=\"insights-ai-off\"", html);
        Assert.DoesNotContain("id=\"insights-summarize\"", html);
        Assert.Contains("id=\"insights-noise\"", html);
        Assert.Contains($"data-customer-id=\"{noisy}\"", html);
        Assert.Contains("Flappy Ltd A", html);
        Assert.Contains("Flaky API", html);
        Assert.Contains("id=\"insights-abandoned\"", html);
        Assert.Contains("Ghost Town A", html);
        Assert.Contains("all monitors DOWN", html);
        Assert.Contains($"href=\"/SuperAdmin/Customers/Details/{abandoned}\"", html);
        Assert.Contains("href=\"/SuperAdmin/Insights\"", html); // sidebar
    }

    [Fact]
    public async Task Page_is_translated()
    {
        await InsightsSeed.SeedAsync(f, "T");
        var admin = await f.LoginSuperAdminAsync();
        await admin.SetLanguageAsync("tr");
        var html = await admin.GetHtmlAsync("/SuperAdmin/Insights");
        Assert.Contains("Terk edilmiş olabilecek hesaplar", html);
        Assert.Contains("İçgörüler", html);
    }

    [Fact]
    public async Task Customer_users_and_anonymous_cannot_open_or_post()
    {
        var cid = await f.DemoCustomerIdAsync();
        if (!await f.WithDbAsync(db => db.Users.AnyAsync(u => u.Email == ViewerEmail)))
            await f.AddUserAsync(cid, ViewerEmail, ViewerPassword, Roles.CustomerViewer);

        foreach (var client in new[]
                 {
                     await f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword),
                     await f.LoginAsync(ViewerEmail, ViewerPassword),
                 })
        {
            var r = await client.GetAsync("/SuperAdmin/Insights");
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Contains("/Account/AccessDenied", r.Headers.Location!.ToString());
            var post = await client.PostFormAsync("/SuperAdmin/Insights?handler=Summarize", [], "/App");
            Assert.True(post.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest, ((int)post.StatusCode).ToString());
            if (post.StatusCode == HttpStatusCode.Redirect) Assert.Contains("/Account/AccessDenied", post.Headers.Location!.ToString());
        }
        var anonymous = await f.CreateBrowser().GetAsync("/SuperAdmin/Insights");
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.Contains("/Account/Login", anonymous.Headers.Location!.ToString());
        Assert.Equal(0, await f.WithDbAsync(db => db.AiDecisions.CountAsync(d => d.Feature == AiFeatures.PlatformInsights)));
    }

    [Fact]
    public async Task Summarize_with_AI_off_explains_and_stores_nothing()
    {
        var admin = await f.LoginSuperAdminAsync();
        var r = await admin.PostFormAsync("/SuperAdmin/Insights?handler=Summarize", [], "/SuperAdmin/Insights");
        var html = await admin.FollowAsync(r);
        Assert.Contains("AI is switched off; the signals below are computed without it.", html);
        Assert.Equal(0, await f.WithDbAsync(db => db.AiDecisions.CountAsync(d => d.Feature == AiFeatures.PlatformInsights)));
    }
}

/// <summary>AI-12 page with AI switched on (fake client): the summary is generated on request and rendered with names resolved server-side.</summary>
public class InsightsSummaryPageTests(AiEnabledWebAppFactory f) : IClassFixture<AiEnabledWebAppFactory>
{
    private FakeAiClient Fake => (FakeAiClient)f.Services.GetRequiredService<IAiClient>();

    [Fact]
    public async Task Summary_is_generated_on_request_and_rendered_with_server_values_and_links()
    {
        var (abandoned, _) = await InsightsSeed.SeedAsync(f, "S");
        var admin = await f.LoginSuperAdminAsync();
        Fake.Responder = req => req.Feature == AiFeatures.PlatformInsights
            ? FakeAiClient.Success("""
                {"headline":"[[C1]] needs a look.","points":["[[C1]] had {{C1.noise.incidents}} incidents.","For [[C2]], {{C2.abandoned.all_down}} for {{C2.abandoned.longest_down_days}}."]}
                """)
            : FakeAiClient.Success(FakeAiClient.CannedJson);

        var before = await admin.GetHtmlAsync("/SuperAdmin/Insights");
        Assert.Contains("id=\"insights-summarize\"", before);

        var r = await admin.PostFormAsync("/SuperAdmin/Insights?handler=Summarize", [], "/SuperAdmin/Insights");
        var html = await admin.FollowAsync(r);

        Assert.Contains("The AI summary was generated.", html);
        Assert.Contains("id=\"insights-headline\"", html);
        Assert.Contains(">Flappy Ltd S</a> had 12 incidents.", html);
        Assert.Contains($"href=\"/SuperAdmin/Customers/Details/{abandoned}\">Ghost Town S</a>, every enabled monitor is DOWN for 30 days.", html);
        Assert.DoesNotContain("{{", html);
        Assert.DoesNotContain("[[C", html);
        var request = Fake.Requests.Last(q => q.Feature == AiFeatures.PlatformInsights);
        Assert.DoesNotContain("Flappy", request.UserContent);
        Assert.DoesNotContain("Ghost", request.UserContent);

        var d = await f.WithDbAsync(db => db.AiDecisions.AsNoTracking().SingleAsync(x => x.Feature == AiFeatures.PlatformInsights));
        Assert.Null(d.CustomerId);
        Assert.Equal(AiDecisionStatus.Succeeded, d.Status);

        // The SuperAdmin AI usage page shows the platform budget separately.
        var usage = await admin.GetHtmlAsync("/SuperAdmin/Ai");
        Assert.Contains("id=\"ai-platform-insights\"", usage);
    }
}
