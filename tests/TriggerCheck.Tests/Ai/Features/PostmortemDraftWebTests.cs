using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TriggerCheck.Ai.Client;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;

namespace TriggerCheck.Tests.Ai.Features;

/// <summary>Web host with AI on and the fake client; the event watcher is effectively idle.</summary>
public sealed class PostmortemWebAppFactory : TestWebAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Ai:Enabled", "true");
        builder.UseSetting("Ai:Events:PollSeconds", "86400");
        builder.UseSetting("Ai:RetryCooldownMinutes", "0");
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<IAiClient>();
            s.AddSingleton<IAiClient>(new FakeAiClient());
        });
    }

    public FakeAiClient Fake => (FakeAiClient)Services.GetRequiredService<IAiClient>();
}

/// <summary>
/// AI-8 in the panel: the approval workflow (generate → edit → approve publishes exactly the edited text, once), nothing
/// public before approval, manual drafts without AI, roles (viewer read-only) and tenant isolation.
/// </summary>
public class PostmortemDraftWebTests(PostmortemWebAppFactory f) : IClassFixture<PostmortemWebAppFactory>
{
    private const string ViewerEmail = "postmortem-viewer@demo.local", ViewerPassword = "Viewer123!";

    private Task<HttpClient> AdminAsync() => f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);

    private static string Url(int incident, string? handler = null) => $"/App/Incidents/Postmortem/{incident}" + (handler is null ? "" : "?handler=" + handler);

    /// <summary>Demo customer with AI on and every AI feature in its plan; a resolved incident on a monitor shown on a new status page.</summary>
    private async Task<(int Customer, int Monitor, int Incident, int Page, string Slug)> ArrangeAsync(string tag, bool aiEnabled = true)
    {
        var cid = await f.DemoCustomerIdAsync();
        await f.WithDbAsync(async db =>
        {
            var planId = await db.Customers.Where(c => c.Id == cid).Select(c => c.PlanId).FirstAsync();
            await db.Plans.Where(p => p.Id == planId).ExecuteUpdateAsync(s => s
                .SetProperty(p => p.AiFeatures, AiFeatures.Format(AiFeatures.All.Select(x => x.Name)))
                .SetProperty(p => p.AiMonthlyCallLimit, 1000)
                .SetProperty(p => p.MaxStatusPages, (int?)null));
            await db.Customers.Where(c => c.Id == cid).ExecuteUpdateAsync(s => s.SetProperty(c => c.AiEnabled, aiEnabled).SetProperty(c => c.AiLanguage, "en"));
        });
        var mid = await f.AddMonitorAsync(cid, "Internal " + tag, m => m.Target = "https://shop.example.test/" + tag);
        var slug = "pm-" + tag;
        var (incident, page) = await f.WithDbAsync(async db =>
        {
            var started = DateTime.UtcNow.AddHours(-3);
            var i = new Incident { MonitorId = mid, StartedAtUtc = started, ResolvedAtUtc = started.AddMinutes(75), Cause = "HTTP 503" };
            db.Incidents.Add(i);
            db.CheckResults.Add(new CheckResult { MonitorId = mid, CheckedAtUtc = started, IsUp = false, StatusCode = 503, ResponseTimeMs = 300, Error = "HTTP 503" });
            var p = new StatusPage { CustomerId = cid, Title = "Status " + tag, Slug = slug, Monitors = [new StatusPageMonitor { MonitorId = mid, DisplayName = "Shop " + tag }] };
            db.StatusPages.Add(p);
            await db.SaveChangesAsync();
            return (i.Id, p.Id);
        });
        f.Fake.IsAvailable = true;
        f.Fake.Responder = _ => FakeAiClient.Success(PostmortemDraftTests.Answer(PostmortemDraftTests.GoodMessage));
        return (cid, mid, incident, page, slug);
    }

    private Task<int> NotesAsync(int incident) => f.WithDbAsync(db => db.StatusPageNotes.CountAsync(n => n.IncidentId == incident));

    [Fact]
    public async Task Admin_generates_edits_and_approves_and_only_the_edited_text_is_published_once()
    {
        var (_, _, incident, page, slug) = await ArrangeAsync("flow");
        var admin = await AdminAsync();

        var details = await admin.GetHtmlAsync($"/App/Incidents/Details/{incident}");
        Assert.Contains($"href=\"/App/Incidents/Postmortem/{incident}\"", details);
        var html = await admin.GetHtmlAsync(Url(incident));
        Assert.Contains("id=\"postmortem-timeline\"", html);
        Assert.Contains("data-ai-state=\"available\"", html);
        Assert.Contains("Draft with AI", html);

        var r = await admin.PostFormAsync(Url(incident, "Generate"), new(), Url(incident));
        AppTestData.AssertRedirect(r, $"/App/Incidents/Postmortem/{incident}");
        html = await admin.GetHtmlAsync(Url(incident));
        Assert.Contains("Shop flow was unavailable from", html); // public display name, server-rendered values
        Assert.Contains("(1 h 15 min)", html);
        Assert.Contains("AI-generated", html);
        Assert.Contains("data-state=\"draft\"", html);
        // Generating published nothing.
        Assert.Equal(0, await NotesAsync(incident));
        Assert.DoesNotContain("unavailable from", await f.CreateBrowser().GetStringAsync("/status/" + slug));

        // Editing saves, still nothing public.
        const string edited = "Shop checkout was slow to respond for a while this morning. It is fixed; sorry for the trouble.";
        r = await admin.PostFormAsync(Url(incident, "Save"), new() { ["likelyCause"] = "Upstream overload.", ["customerMessage"] = edited }, Url(incident));
        AppTestData.AssertRedirect(r, "/App/Incidents/Postmortem/");
        Assert.Equal(0, await NotesAsync(incident));
        var draft = await f.WithDbAsync(db => db.PostmortemDrafts.AsNoTracking().SingleAsync(d => d.IncidentId == incident));
        Assert.Equal(edited, draft.CustomerMessage);
        Assert.Equal(PostmortemDraftStatus.Draft, draft.Status);

        // Approve: the submitted (edited) text is what gets published, with who and when.
        const string final = "Checkout was unavailable this morning. It is fixed now; sorry for the trouble.";
        r = await admin.PostFormAsync(Url(incident, "Publish"),
            new() { ["likelyCause"] = "Upstream overload.", ["customerMessage"] = final, ["pageIds"] = page.ToString() }, Url(incident));
        AppTestData.AssertRedirect(r, "/App/Incidents/Postmortem/");
        var note = await f.WithDbAsync(db => db.StatusPageNotes.AsNoTracking().SingleAsync(n => n.IncidentId == incident));
        Assert.Equal(final, note.Body);
        Assert.Equal(page, note.StatusPageId);
        draft = await f.WithDbAsync(db => db.PostmortemDrafts.AsNoTracking().SingleAsync(d => d.IncidentId == incident));
        Assert.Equal(PostmortemDraftStatus.Published, draft.Status);
        Assert.Equal(final, draft.CustomerMessage);
        Assert.Equal(await f.UserIdAsync(TestWebAppFactory.DemoAdminEmail), draft.ApprovedByUserId);
        Assert.NotNull(draft.ApprovedAtUtc);
        Assert.Equal(page.ToString(), draft.PublishedStatusPageIds);
        Assert.Contains(final, await f.CreateBrowser().GetStringAsync("/status/" + slug));

        html = await admin.GetHtmlAsync(Url(incident));
        Assert.Contains("data-state=\"published\"", html);
        Assert.Contains("id=\"postmortem-approved\"", html);
        Assert.DoesNotContain("id=\"postmortem-form\"", html); // read-only now

        // A second approval, a save or a regeneration changes nothing.
        await admin.PostFormAsync(Url(incident, "Publish"), new() { ["customerMessage"] = "Again", ["pageIds"] = page.ToString() }, Url(incident));
        await admin.PostFormAsync(Url(incident, "Save"), new() { ["customerMessage"] = "Changed" }, Url(incident));
        await admin.PostFormAsync(Url(incident, "Generate"), new(), Url(incident));
        Assert.Equal(1, await NotesAsync(incident));
        Assert.Equal(final, await f.WithDbAsync(db => db.PostmortemDrafts.Where(d => d.IncidentId == incident).Select(d => d.CustomerMessage).SingleAsync()));
    }

    [Fact]
    public async Task Publishing_needs_an_approved_message_and_a_page_that_shows_the_monitor()
    {
        var (cid, _, incident, page, _) = await ArrangeAsync("rules");
        var otherCustomer = await f.AddCustomerAsync("Postmortem Other Co");
        var foreignPage = await f.WithDbAsync(async db =>
        {
            var p = new StatusPage { CustomerId = otherCustomer, Title = "Foreign", Slug = "pm-foreign" };
            db.StatusPages.Add(p);
            var own = new StatusPage { CustomerId = cid, Title = "Unrelated", Slug = "pm-unrelated" }; // does not show the monitor
            db.StatusPages.Add(own);
            await db.SaveChangesAsync();
            return (p.Id, Own: own.Id);
        });
        var admin = await AdminAsync();

        await admin.PostFormAsync(Url(incident, "Publish"), new() { ["customerMessage"] = "We had an outage." }, Url(incident)); // no page
        await admin.PostFormAsync(Url(incident, "Publish"), new() { ["customerMessage"] = "", ["pageIds"] = page.ToString() }, Url(incident)); // no text
        await admin.PostFormAsync(Url(incident, "Publish"), new() { ["customerMessage"] = "We had an outage.", ["pageIds"] = foreignPage.Id.ToString() }, Url(incident));
        await admin.PostFormAsync(Url(incident, "Publish"), new() { ["customerMessage"] = "We had an outage.", ["pageIds"] = foreignPage.Own.ToString() }, Url(incident));
        await admin.PostFormAsync(Url(incident, "Publish"), new() { ["customerMessage"] = new string('x', PostmortemDraft.MaxCustomerMessageLength + 1), ["pageIds"] = page.ToString() }, Url(incident));

        Assert.Equal(0, await NotesAsync(incident));
        Assert.Equal(PostmortemDraftStatus.Draft, await f.WithDbAsync(db => db.PostmortemDrafts.Where(d => d.IncidentId == incident).Select(d => d.Status).SingleAsync()));
    }

    [Fact]
    public async Task Without_AI_the_empty_form_is_written_by_hand_and_published_on_approval()
    {
        var (_, _, incident, page, slug) = await ArrangeAsync("manual", aiEnabled: false);
        var admin = await AdminAsync();
        var requests = f.Fake.Requests.Count;

        var html = await admin.GetHtmlAsync(Url(incident));
        Assert.Contains("data-ai-state=\"optout\"", html);
        Assert.Contains("id=\"postmortem-form\"", html);
        Assert.DoesNotContain("Draft with AI", html);

        var r = await admin.PostFormAsync(Url(incident, "Generate"), new(), Url(incident));
        AppTestData.AssertRedirect(r, "/App/Incidents/Postmortem/");
        Assert.False(await f.WithDbAsync(db => db.PostmortemDrafts.AnyAsync(d => d.IncidentId == incident)));

        await admin.PostFormAsync(Url(incident, "Publish"), new() { ["customerMessage"] = "Hand-written update.", ["pageIds"] = page.ToString() }, Url(incident));
        Assert.Equal("Hand-written update.", await f.WithDbAsync(db => db.StatusPageNotes.Where(n => n.IncidentId == incident).Select(n => n.Body).SingleAsync()));
        Assert.Null(await f.WithDbAsync(db => db.PostmortemDrafts.Where(d => d.IncidentId == incident).Select(d => d.DecisionId).SingleAsync()));
        Assert.Contains("Hand-written update.", await f.CreateBrowser().GetStringAsync("/status/" + slug));
        Assert.Equal(requests, f.Fake.Requests.Count);
    }

    [Fact]
    public async Task Viewer_can_read_but_not_generate_save_or_publish()
    {
        var (cid, _, incident, page, _) = await ArrangeAsync("viewer");
        if (!await f.WithDbAsync(db => db.Users.AnyAsync(u => u.Email == ViewerEmail)))
            await f.AddUserAsync(cid, ViewerEmail, ViewerPassword, Roles.CustomerViewer);
        var viewer = await f.LoginAsync(ViewerEmail, ViewerPassword);
        var requests = f.Fake.Requests.Count;

        var html = await viewer.GetHtmlAsync(Url(incident));
        Assert.Contains("id=\"postmortem-timeline\"", html);
        Assert.DoesNotContain("id=\"postmortem-form\"", html);
        Assert.DoesNotContain("Draft with AI", html);

        // The viewer page has no draft form; the antiforgery token comes from the incident list.
        foreach (var (handler, fields) in new (string, Dictionary<string, string>)[]
                 {
                     ("Generate", new()),
                     ("Save", new() { ["customerMessage"] = "Viewer text" }),
                     ("Publish", new() { ["customerMessage"] = "Viewer text", ["pageIds"] = page.ToString() }),
                 })
        {
            var r = await viewer.PostFormAsync(Url(incident, handler), fields, "/App/Incidents");
            Assert.True(r.StatusCode is HttpStatusCode.Forbidden || r.Headers.Location?.ToString().Contains("AccessDenied") == true, $"{handler}: {(int)r.StatusCode}");
        }
        Assert.Equal(requests, f.Fake.Requests.Count);
        Assert.False(await f.WithDbAsync(db => db.PostmortemDrafts.AnyAsync(d => d.IncidentId == incident)));
        Assert.Equal(0, await NotesAsync(incident));
    }

    [Fact]
    public async Task Other_tenants_cannot_see_or_change_the_postmortem()
    {
        var (_, _, incident, page, _) = await ArrangeAsync("tenant");
        var other = await f.AddCustomerAsync("Postmortem Tenant Co");
        const string email = "pm-other-admin@other.local";
        await f.AddUserAsync(other, email, "Admin123!", Roles.CustomerAdmin);
        var otherAdmin = await f.LoginAsync(email, "Admin123!");

        Assert.Equal(HttpStatusCode.NotFound, (await otherAdmin.GetAsync(Url(incident))).StatusCode);
        foreach (var (handler, fields) in new (string, Dictionary<string, string>)[]
                 {
                     ("Generate", new()),
                     ("Save", new() { ["customerMessage"] = "Foreign text" }),
                     ("Publish", new() { ["customerMessage"] = "Foreign text", ["pageIds"] = page.ToString() }),
                 })
            Assert.Equal(HttpStatusCode.NotFound, (await otherAdmin.PostFormAsync(Url(incident, handler), fields, "/App/Incidents")).StatusCode);

        Assert.False(await f.WithDbAsync(db => db.PostmortemDrafts.AnyAsync(d => d.IncidentId == incident)));
        Assert.Equal(0, await NotesAsync(incident));
    }

    [Fact]
    public async Task Open_incident_has_no_postmortem_yet_and_the_page_is_localized()
    {
        var (_, monitor, _, _, _) = await ArrangeAsync("open");
        var open = await f.WithDbAsync(async db =>
        {
            var i = new Incident { MonitorId = monitor, StartedAtUtc = DateTime.UtcNow.AddMinutes(-5), Cause = "HTTP 503" };
            db.Incidents.Add(i);
            await db.SaveChangesAsync();
            return i.Id;
        });
        var admin = await AdminAsync();
        Assert.DoesNotContain("id=\"postmortem-link\"", await admin.GetHtmlAsync($"/App/Incidents/Details/{open}"));
        Assert.Contains("id=\"postmortem-open\"", await admin.GetHtmlAsync(Url(open)));

        await admin.SetLanguageAsync("tr");
        Assert.Contains("Postmortem taslağı olay çözüldükten sonra hazırlanabilir.", await admin.GetHtmlAsync(Url(open)));
        await admin.SetLanguageAsync("en");
    }
}
