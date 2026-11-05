using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Ai.Features.Correlation;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Tests.Ai.Features;

/// <summary>AI-4 UI: the related incidents page (A1 accept / reject, switch, viewer read-only, tenant isolation).</summary>
public class CorrelationWebTests(AiEnabledWebAppFactory f) : IClassFixture<AiEnabledWebAppFactory>
{
    private const string ViewerEmail = "groups-viewer@demo.local", ViewerPassword = "Viewer123!";

    private Task<HttpClient> AdminAsync() => f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);

    /// <summary>Demo customer on a plan with AI-4, opted in, grouping on.</summary>
    private async Task<int> EnableAsync()
    {
        var cid = await f.DemoCustomerIdAsync();
        await f.WithDbAsync(async db =>
        {
            var c = await db.Customers.Include(x => x.Plan).SingleAsync(x => x.Id == cid);
            c.AiEnabled = true;
            c.AlertGroupingEnabled = true;
            c.Plan.AiFeatures = AiFeatures.Format(AiFeatures.All.Select(x => x.Name));
            c.Plan.AiMonthlyCallLimit = Math.Max(100, c.Plan.AiMonthlyCallLimit);
            await db.SaveChangesAsync();
        });
        return cid;
    }

    /// <summary>A proposal with a primary, one rule member and one ambiguous candidate; returns (group, rule, candidate) ids.</summary>
    private Task<(int Group, int Rule, int Candidate)> AddProposalAsync(int customerId, string prefix) => f.WithDbAsync(async db =>
    {
        var now = DateTime.UtcNow;
        Incident Inc(string name, string target, double minutesAgo)
        {
            var m = new MonitorEntity { CustomerId = customerId, Name = prefix + name, Type = MonitorType.Http, Target = target, IntervalSeconds = 300, NextCheckAtUtc = now.AddHours(1) };
            db.Monitors.Add(m);
            var i = new Incident { Monitor = m, StartedAtUtc = now.AddMinutes(-minutesAgo), Cause = "Timeout after 10s" };
            db.Incidents.Add(i);
            return i;
        }
        var p = Inc(" primary", "https://api.example.test/a", 3);
        var r = Inc(" rule", "https://api.example.test/b", 2.5);
        var a = Inc(" candidate", "https://www.example.test/", 2);
        await db.SaveChangesAsync();
        var g = new IncidentGroup { CustomerId = customerId, PrimaryIncidentId = p.Id, Status = IncidentGroupStatus.Proposed, Mode = AiAutonomy.A1, Signals = "same_host,same_error_type,same_window", Host = "api.example.test", CreatedAtUtc = now };
        g.Members.Add(new IncidentGroupMember { IncidentId = p.Id, CustomerId = customerId, Role = IncidentGroupRole.Primary, Included = true });
        g.Members.Add(new IncidentGroupMember { IncidentId = r.Id, CustomerId = customerId, Role = IncidentGroupRole.Rule, Included = true });
        g.Members.Add(new IncidentGroupMember { IncidentId = a.Id, CustomerId = customerId, Role = IncidentGroupRole.Ambiguous });
        db.IncidentGroups.Add(g);
        await db.SaveChangesAsync();
        return (g.Id, r.Id, a.Id);
    });

    private Task<IncidentGroup> GroupAsync(int id) =>
        f.WithDbAsync(db => db.IncidentGroups.AsNoTracking().Include(g => g.Members).SingleAsync(g => g.Id == id));

    private static readonly Regex TokenRx = new("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");

    private static async Task<HttpResponseMessage> PostAsync(HttpClient c, string url, params (string Key, string Value)[] fields)
    {
        var page = await c.GetStringAsync("/App/Incidents/Groups");
        var token = WebUtility.HtmlDecode(TokenRx.Match(page).Groups[1].Value);
        var all = fields.Select(x => new KeyValuePair<string, string>(x.Key, x.Value)).Append(new("__RequestVerificationToken", token));
        return await c.PostAsync(url, new FormUrlEncodedContent(all));
    }

    [Fact]
    public async Task Admin_sees_the_proposal_and_accepts_the_selected_members()
    {
        var cid = await EnableAsync();
        var (gid, rule, candidate) = await AddProposalAsync(cid, "Accept");
        var admin = await AdminAsync();

        var list = await admin.GetHtmlAsync("/App/Incidents");
        Assert.Contains("id=\"related-incidents-link\"", list);
        Assert.Contains("id=\"group-suggestion-count\"", list);

        var html = await admin.GetHtmlAsync("/App/Incidents/Groups");
        Assert.Contains($"id=\"group-{gid}\"", html);
        Assert.Contains("Group these 3 incidents?", html);
        Assert.Contains("Accept primary", html);
        Assert.Contains("same host", html);
        Assert.Contains("data-grouping-mode=\"suggest\"", html);
        Assert.Contains($"name=\"keep\" value=\"{rule}\"", html);

        var r = await PostAsync(admin, $"/App/Incidents/Groups?handler=Accept&id={gid}", ("keep", rule.ToString()), ("keep", candidate.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        var g = await GroupAsync(gid);
        Assert.Equal(IncidentGroupStatus.Accepted, g.Status);
        Assert.True(g.Members.Single(m => m.IncidentId == candidate).Included);
        Assert.NotNull(g.DecidedByUserId);

        // The incident page shows the membership; the acceptance rate counts it.
        Assert.Contains("id=\"incident-group\"", await admin.GetHtmlAsync($"/App/Incidents/Details/{rule}"));
        Assert.Contains("id=\"acceptance-rate\"", await admin.GetHtmlAsync("/App/Incidents/Groups"));

        // Deciding twice is refused.
        r = await PostAsync(admin, $"/App/Incidents/Groups?handler=Reject&id={gid}");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal(IncidentGroupStatus.Accepted, (await GroupAsync(gid)).Status);
    }

    [Fact]
    public async Task Admin_rejects_and_switches_grouping_off()
    {
        var cid = await EnableAsync();
        var (gid, _, _) = await AddProposalAsync(cid, "Reject");
        var admin = await AdminAsync();

        await PostAsync(admin, $"/App/Incidents/Groups?handler=Reject&id={gid}");
        Assert.Equal(IncidentGroupStatus.Rejected, (await GroupAsync(gid)).Status);

        var (open, _, _) = await AddProposalAsync(cid, "Off");
        var r = await PostAsync(admin, "/App/Incidents/Groups?handler=Enabled", ("enabled", "false"));
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.False(await f.WithDbAsync(db => db.Customers.Where(c => c.Id == cid).Select(c => c.AlertGroupingEnabled).SingleAsync()));
        Assert.Equal(IncidentGroupStatus.Expired, (await GroupAsync(open)).Status);
        Assert.Contains("data-grouping-mode=\"off\"", await admin.GetHtmlAsync("/App/Incidents/Groups"));

        await PostAsync(admin, "/App/Incidents/Groups?handler=Enabled", ("enabled", "true"));
        Assert.True(await f.WithDbAsync(db => db.Customers.Where(c => c.Id == cid).Select(c => c.AlertGroupingEnabled).SingleAsync()));
    }

    [Fact]
    public async Task Viewer_is_read_only_and_other_tenants_groups_are_not_found()
    {
        var cid = await EnableAsync();
        var (gid, rule, _) = await AddProposalAsync(cid, "Viewer");
        if (!await f.WithDbAsync(db => db.Users.AnyAsync(u => u.Email == ViewerEmail)))
            await f.AddUserAsync(cid, ViewerEmail, ViewerPassword, Roles.CustomerViewer);
        var viewer = await f.LoginAsync(ViewerEmail, ViewerPassword);

        var html = await viewer.GetHtmlAsync("/App/Incidents/Groups");
        Assert.Contains($"id=\"group-{gid}\"", html);
        Assert.DoesNotContain("Group the selected incidents", html);
        Assert.DoesNotContain("id=\"grouping-switch\"", html);
        var r = await PostAsync(viewer, $"/App/Incidents/Groups?handler=Accept&id={gid}", ("keep", rule.ToString()));
        Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(IncidentGroupStatus.Proposed, (await GroupAsync(gid)).Status);

        // Another tenant's proposal: not shown, and deciding it is a 404.
        var other = await f.AddCustomerAsync("Groups other tenant");
        var (foreign, _, _) = await AddProposalAsync(other, "Foreign");
        var admin = await AdminAsync();
        Assert.DoesNotContain($"id=\"group-{foreign}\"", await admin.GetHtmlAsync("/App/Incidents/Groups"));
        r = await PostAsync(admin, $"/App/Incidents/Groups?handler=Reject&id={foreign}");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        r = await PostAsync(admin, $"/App/Incidents/Groups?handler=Ungroup&id={foreign}");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal(IncidentGroupStatus.Proposed, (await GroupAsync(foreign)).Status);
    }

    [Fact]
    public async Task Page_renders_in_Turkish()
    {
        var cid = await EnableAsync();
        await AddProposalAsync(cid, "Turkish");
        var admin = await AdminAsync();
        await admin.SetLanguageAsync("tr");
        try
        {
            var html = await admin.GetHtmlAsync("/App/Incidents/Groups");
            Assert.Contains("İlişkili olaylar", html);
            Assert.Contains("Bu 3 olay gruplansın mı?", html);
        }
        finally
        {
            await admin.SetLanguageAsync("en");
        }
    }
}
