using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring.Escalation;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;
using static TriggerCheck.Tests.App.AppTestData;

namespace TriggerCheck.Tests.Escalation;

/// <summary>FEAT-5: acknowledging from the incident pages and through the signed link; escalation policy page.</summary>
public class IncidentAckPageTests(IncidentAckPageTests.Fixture f) : IClassFixture<IncidentAckPageTests.Fixture>
{
    public const string ViewerEmail = "ack-viewer@demo.local";
    private const string Refused = "This acknowledge link is invalid, has expired or belongs to another account.";
    public const string ViewerPassword = "Viewer123!";

    public class Fixture : TestWebAppFactory
    {
        private readonly SemaphoreSlim _lock = new(1, 1);
        private bool _seeded;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Site:PublicBaseUrl", "https://monitor.example.test");
        }

        public async Task EnsureSeededAsync()
        {
            await _lock.WaitAsync();
            try
            {
                if (_seeded) return;
                await this.AddUserAsync(await this.DemoCustomerIdAsync(), ViewerEmail, ViewerPassword, Roles.CustomerViewer);
                _seeded = true;
            }
            finally { _lock.Release(); }
        }
    }

    private async Task<int> AddIncidentAsync(int customerId, bool resolved = false)
    {
        var monitorId = await f.AddMonitorAsync(customerId, "Ack-" + Guid.NewGuid().ToString("N")[..6], m => m.Status = MonitorStatus.Down);
        return await f.WithDbAsync(async db =>
        {
            var i = new Incident { MonitorId = monitorId, StartedAtUtc = DateTime.UtcNow.AddMinutes(-20), Cause = "Connection refused", ResolvedAtUtc = resolved ? DateTime.UtcNow : null };
            db.Incidents.Add(i);
            await db.SaveChangesAsync();
            return i.Id;
        });
    }

    private Task<Incident> IncidentAsync(int id) => f.WithDbAsync(db => db.Incidents.AsNoTracking().SingleAsync(i => i.Id == id));

    private Task<HttpClient> AdminAsync() => f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);

    private string Token(int incidentId, int customerId) => f.Services.GetRequiredService<IncidentAckLinks>().CreateToken(incidentId, customerId);

    [Fact]
    public async Task Admin_acknowledges_from_the_incident_list_with_a_note()
    {
        var cid = await f.DemoCustomerIdAsync();
        var id = await AddIncidentAsync(cid);
        var admin = await AdminAsync();
        Assert.Contains($"/App/Incidents/Details/{id}", await admin.GetHtmlAsync("/App/Incidents"));

        var r = await admin.PostFormAsync($"/App/Incidents/Details/{id}?handler=Acknowledge", new() { ["note"] = "Looking into it", ["returnTo"] = "list" }, "/App/Escalation");
        AssertRedirect(r, "/App/Incidents");

        var incident = await IncidentAsync(id);
        Assert.NotNull(incident.AcknowledgedAtUtc);
        Assert.Equal(await f.UserIdAsync(TestWebAppFactory.DemoAdminEmail), incident.AcknowledgedByUserId);
        Assert.Equal("Looking into it", incident.AcknowledgeNote);
        Assert.Null(incident.ResolvedAtUtc);

        var details = await admin.GetHtmlAsync($"/App/Incidents/Details/{id}");
        Assert.Contains("id=\"ack-badge\"", details);
        Assert.Contains("Looking into it", details);
        Assert.Equal(MonitorStatus.Down, (await f.GetMonitorAsync(incident.MonitorId))!.Status);
    }

    [Fact]
    public async Task Resolved_incidents_cannot_be_acknowledged()
    {
        var id = await AddIncidentAsync(await f.DemoCustomerIdAsync(), resolved: true);
        var admin = await AdminAsync();
        var r = await admin.PostFormAsync($"/App/Incidents/Details/{id}?handler=Acknowledge", new(), "/App/Escalation");
        AssertRedirect(r, $"/App/Incidents/Details/{id}");
        Assert.Null((await IncidentAsync(id)).AcknowledgedAtUtc);
    }

    [Fact]
    public async Task Viewer_cannot_acknowledge()
    {
        await f.EnsureSeededAsync();
        var id = await AddIncidentAsync(await f.DemoCustomerIdAsync());
        var viewer = await f.LoginAsync(ViewerEmail, ViewerPassword);
        var details = await viewer.GetHtmlAsync($"/App/Incidents/Details/{id}");
        Assert.DoesNotContain("handler=Acknowledge", details);

        AssertAccessDenied(await viewer.PostFormAsync($"/App/Incidents/Details/{id}?handler=Acknowledge", new(), "/App/Incidents"));
        var link = await viewer.GetHtmlAsync($"/App/Incidents/Ack?token={Uri.EscapeDataString(Token(id, await f.DemoCustomerIdAsync()))}");
        Assert.Contains("Only account admins can acknowledge incidents.", link);
        Assert.Null((await IncidentAsync(id)).AcknowledgedAtUtc);
    }

    [Fact]
    public async Task Other_tenants_incident_is_not_found()
    {
        var other = await f.AddCustomerAsync("Ack Other " + Guid.NewGuid().ToString("N")[..6]);
        var id = await AddIncidentAsync(other);
        var admin = await AdminAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/App/Incidents/Details/{id}")).StatusCode);
        var r = await admin.PostFormAsync($"/App/Incidents/Details/{id}?handler=Acknowledge", new(), "/App/Escalation");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Null((await IncidentAsync(id)).AcknowledgedAtUtc);
    }

    [Fact]
    public async Task Link_requires_login_then_confirms_with_a_post()
    {
        var cid = await f.DemoCustomerIdAsync();
        var id = await AddIncidentAsync(cid);
        var url = $"/App/Incidents/Ack?token={Uri.EscapeDataString(Token(id, cid))}";

        var anonymous = await f.CreateBrowser().GetAsync(url);
        AssertRedirect(anonymous, "/Account/Login");

        var admin = await AdminAsync();
        var page = await admin.GetHtmlAsync(url);
        Assert.Contains("Acknowledge incident", page);
        Assert.Null((await IncidentAsync(id)).AcknowledgedAtUtc); // GET changes nothing

        var r = await admin.PostFormAsync(url, new() { ["note"] = "via link" });
        AssertRedirect(r, $"/App/Incidents/Details/{id}");
        var incident = await IncidentAsync(id);
        Assert.NotNull(incident.AcknowledgedAtUtc);
        Assert.Equal("via link", incident.AcknowledgeNote);
    }

    [Fact]
    public async Task Tampered_expired_and_other_tenant_links_are_refused()
    {
        var cid = await f.DemoCustomerIdAsync();
        var id = await AddIncidentAsync(cid);
        var other = await f.AddCustomerAsync("Ack Link Other " + Guid.NewGuid().ToString("N")[..6]);
        var foreign = await AddIncidentAsync(other);
        var admin = await AdminAsync();

        // One character of the protected token changed.
        var genuine = Token(id, cid);
        var middle = genuine.Length / 2;
        var tampered = genuine[..middle] + (genuine[middle] == 'A' ? 'B' : 'A') + genuine[(middle + 1)..];
        // Same key ring and purpose, but already expired.
        var expired = f.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(IncidentAckLinks.Purpose).ToTimeLimitedDataProtector()
            .Protect(IncidentAckLinks.Payload(id, cid), DateTimeOffset.UtcNow.AddMinutes(-1));
        // A genuine link of another tenant.
        var otherTenant = Token(foreign, other);

        foreach (var (token, message) in new[]
                 {
                     (tampered, Refused),
                     (expired, Refused),
                     (otherTenant, Refused)
                 })
        {
            var url = $"/App/Incidents/Ack?token={Uri.EscapeDataString(token)}";
            var html = await admin.GetHtmlAsync(url);
            Assert.Contains(message, html);
            Assert.DoesNotContain("Ack-", html); // nothing about the incident is shown
            var r = await admin.PostFormAsync(url, new(), "/App/Escalation");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }
        Assert.Null((await IncidentAsync(id)).AcknowledgedAtUtc);
        Assert.Null((await IncidentAsync(foreign)).AcknowledgedAtUtc);
    }

    [Fact]
    public async Task Admin_saves_an_escalation_policy_and_viewer_cannot()
    {
        await f.EnsureSeededAsync();
        var cid = await f.DemoCustomerIdAsync();
        var (a, b) = await f.WithDbAsync(async db =>
        {
            var x = new AlertChannel { CustomerId = cid, Name = "Esc-A", Type = AlertChannelType.Email, Target = "a@demo.local" };
            var y = new AlertChannel { CustomerId = cid, Name = "Esc-B", Type = AlertChannelType.Email, Target = "b@demo.local" };
            db.AlertChannels.AddRange(x, y);
            await db.SaveChangesAsync();
            return (x.Id, y.Id);
        });
        var otherChannel = await f.WithDbAsync(async db =>
        {
            var c = new AlertChannel { CustomerId = await f.AddCustomerAsync("Esc Other " + Guid.NewGuid().ToString("N")[..6]), Name = "X", Type = AlertChannelType.Email, Target = "x@o.local" };
            db.AlertChannels.Add(c);
            await db.SaveChangesAsync();
            return c.Id;
        });
        var admin = await AdminAsync();

        // Not increasing -> rejected.
        var bad = await admin.PostFormAsync("/App/Escalation", new()
        {
            ["IsEnabled"] = "true", ["Steps[0].DelayMinutes"] = "10", ["Steps[0].ChannelIds"] = a.ToString(),
            ["Steps[1].DelayMinutes"] = "5", ["Steps[1].ChannelIds"] = b.ToString()
        });
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Assert.Contains("each step must come later than the previous one", await bad.ReadHtmlAsync());

        var ok = await admin.PostFormAsync("/App/Escalation", new()
        {
            ["IsEnabled"] = "true", ["Steps[0].DelayMinutes"] = "5", ["Steps[0].ChannelIds"] = a.ToString(),
            ["Steps[1].DelayMinutes"] = "15", ["Steps[1].ChannelIds"] = b.ToString()
        });
        AssertRedirect(ok, "/App/Escalation");
        var steps = await f.WithDbAsync(db => db.EscalationSteps.AsNoTracking().Where(s => s.Policy.CustomerId == cid)
            .OrderBy(s => s.DelayMinutes).Select(s => new { s.DelayMinutes, Ids = s.Channels.Select(c => c.Id).ToList() }).ToListAsync());
        Assert.Equal([5, 15], steps.Select(s => s.DelayMinutes));
        Assert.Equal([a], steps[0].Ids);
        Assert.Equal([b], steps[1].Ids);

        // Another tenant's channel is not accepted.
        var foreign = await admin.PostFormAsync("/App/Escalation", new()
        {
            ["IsEnabled"] = "true", ["Steps[0].DelayMinutes"] = "5", ["Steps[0].ChannelIds"] = otherChannel.ToString()
        });
        Assert.Equal(HttpStatusCode.OK, foreign.StatusCode);
        Assert.Equal(2, await f.WithDbAsync(db => db.EscalationSteps.CountAsync(s => s.Policy.CustomerId == cid)));

        var viewer = await f.LoginAsync(ViewerEmail, ViewerPassword);
        Assert.Contains("Esc-A", await viewer.GetHtmlAsync("/App/Escalation"));
        AssertAccessDenied(await viewer.PostFormAsync("/App/Escalation", new() { ["IsEnabled"] = "false" }));
        Assert.True(await f.WithDbAsync(db => db.EscalationPolicies.Where(p => p.CustomerId == cid).Select(p => p.IsEnabled).SingleAsync()));
    }

    [Fact]
    public async Task Re_enabling_the_policy_restarts_its_clock_and_a_step_without_enabled_channels_is_flagged()
    {
        var cid = await f.AddCustomerAsync("Esc Clock " + Guid.NewGuid().ToString("N")[..6]);
        var email = "esc-clock-" + Guid.NewGuid().ToString("N")[..6] + "@demo.local";
        await f.AddUserAsync(cid, email, "Admin123!", Roles.CustomerAdmin);
        var channel = await f.WithDbAsync(async db =>
        {
            var c = new AlertChannel { CustomerId = cid, Name = "Clock-A", Type = AlertChannelType.Email, Target = "a@clock.local" };
            db.AlertChannels.Add(c);
            await db.SaveChangesAsync();
            return c.Id;
        });
        var admin = await f.LoginAsync(email, "Admin123!");
        Dictionary<string, string> Form(bool enabled) => new()
        {
            ["IsEnabled"] = enabled ? "true" : "false", ["Steps[0].DelayMinutes"] = "5", ["Steps[0].ChannelIds"] = channel.ToString()
        };
        Task<DateTime> EnabledAtAsync() => f.WithDbAsync(db => db.EscalationPolicies.Where(p => p.CustomerId == cid).Select(p => p.EnabledAtUtc).SingleAsync());

        AssertRedirect(await admin.PostFormAsync("/App/Escalation", Form(true)), "/App/Escalation");
        var created = await EnabledAtAsync();
        await f.WithDbAsync(db => db.EscalationPolicies.Where(p => p.CustomerId == cid).ExecuteUpdateAsync(x => x.SetProperty(p => p.EnabledAtUtc, created.AddHours(-2))));
        AssertRedirect(await admin.PostFormAsync("/App/Escalation", Form(true)), "/App/Escalation");
        Assert.Equal(created.AddHours(-2), await EnabledAtAsync()); // saving an enabled policy keeps its clock
        AssertRedirect(await admin.PostFormAsync("/App/Escalation", Form(false)), "/App/Escalation");
        AssertRedirect(await admin.PostFormAsync("/App/Escalation", Form(true)), "/App/Escalation");
        Assert.True(await EnabledAtAsync() >= created); // re-enabled: counts from now

        Assert.DoesNotContain("step-warning", await admin.GetHtmlAsync("/App/Escalation"));
        await f.WithDbAsync(db => db.AlertChannels.Where(c => c.Id == channel).ExecuteUpdateAsync(x => x.SetProperty(c => c.IsEnabled, false)));
        Assert.Contains("Step 1 has no enabled alert channel left", await admin.GetHtmlAsync("/App/Escalation"));
    }
}
