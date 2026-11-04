using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;
using static TriggerCheck.Tests.Api.ApiTestHelpers;

namespace TriggerCheck.Tests.Api;

/// <summary>FEAT-5 / FEAT-3 API parity: acknowledging incidents and the ack / maintenance fields in responses.</summary>
public class ApiIncidentAckTests(TestWebAppFactory f) : IClassFixture<TestWebAppFactory>
{
    private async Task<(int CustomerId, int MonitorId, int IncidentId)> ArrangeAsync(bool resolved = false)
    {
        var cid = await f.AddCustomerAsync("Api-Ack " + Guid.NewGuid().ToString("N")[..6]);
        var mid = await f.AddMonitorAsync(cid, "Api-Ack-Monitor", m => m.Status = MonitorStatus.Down);
        var iid = await f.WithDbAsync(async db =>
        {
            var i = new Incident { MonitorId = mid, StartedAtUtc = DateTime.UtcNow.AddMinutes(-5), ResolvedAtUtc = resolved ? DateTime.UtcNow : null };
            db.Incidents.Add(i);
            await db.SaveChangesAsync();
            return i.Id;
        });
        return (cid, mid, iid);
    }

    private Task<Incident> IncidentAsync(int id) => f.WithDbAsync(db => db.Incidents.AsNoTracking().SingleAsync(i => i.Id == id));

    [Fact]
    public async Task Read_write_key_acknowledges_with_a_note_and_the_incident_list_shows_it()
    {
        var (cid, _, iid) = await ArrangeAsync();
        var c = f.ApiClient(await f.AddApiKeyAsync(cid));

        var r = await c.PostJsonAsync($"/api/v1/incidents/{iid}/acknowledge", new { note = "on it" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.JsonAsync();
        Assert.Equal(iid, body.GetProperty("id").GetInt32());
        Assert.Equal("on it", body.GetProperty("acknowledgeNote").GetString());
        Assert.EndsWith("Z", body.GetProperty("acknowledgedAtUtc").GetString());
        Assert.True(body.GetProperty("isOpen").GetBoolean()); // ack does not resolve

        var keyId = await f.WithDbAsync(db => db.ApiKeys.Where(k => k.CustomerId == cid).Select(k => k.Id).SingleAsync());
        Assert.Equal($"api-key:{keyId}", (await IncidentAsync(iid)).AcknowledgedByUserId);

        var list = await (await c.GetAsync("/api/v1/incidents")).JsonAsync();
        var item = list.EnumerateArray().Single(x => x.GetProperty("id").GetInt32() == iid);
        Assert.Equal("on it", item.GetProperty("acknowledgeNote").GetString());
        Assert.Equal(0, item.GetProperty("escalationLevel").GetInt32());
        Assert.False(item.GetProperty("resolvedDuringMaintenance").GetBoolean());

        // Second time: conflict.
        AssertProblem(await c.PostAsync($"/api/v1/incidents/{iid}/acknowledge", null), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Acknowledge_without_a_body_works()
    {
        var (cid, _, iid) = await ArrangeAsync();
        var r = await f.ApiClient(await f.AddApiKeyAsync(cid)).PostAsync($"/api/v1/incidents/{iid}/acknowledge", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.NotNull((await IncidentAsync(iid)).AcknowledgedAtUtc);
    }

    [Fact]
    public async Task Resolved_other_tenant_read_only_and_invalid_requests_are_refused()
    {
        var (cid, _, iid) = await ArrangeAsync();
        var (_, _, resolved) = await ArrangeAsync(resolved: true);
        var (otherCid, _, foreign) = await ArrangeAsync();
        var c = f.ApiClient(await f.AddApiKeyAsync(cid));

        AssertProblem(await f.ApiClient(await f.AddApiKeyAsync(cid, ApiKeyScope.ReadOnly)).PostAsync($"/api/v1/incidents/{iid}/acknowledge", null), HttpStatusCode.Forbidden);
        AssertProblem(await c.PostAsync($"/api/v1/incidents/{foreign}/acknowledge", null), HttpStatusCode.NotFound);
        AssertProblem(await c.PostAsync("/api/v1/incidents/999999/acknowledge", null), HttpStatusCode.NotFound);
        AssertProblem(await c.PostJsonAsync($"/api/v1/incidents/{iid}/acknowledge", new { note = new string('x', 501) }), HttpStatusCode.BadRequest);
        var notJson = await c.PostAsync($"/api/v1/incidents/{iid}/acknowledge", new StringContent("note", System.Text.Encoding.UTF8, "text/plain"));
        AssertProblem(notJson, HttpStatusCode.UnsupportedMediaType);

        // Resolved incidents of the key's own tenant cannot be acknowledged (another customer created it; use its key).
        var resolvedCid = await f.WithDbAsync(db => db.Incidents.Where(i => i.Id == resolved).Select(i => i.Monitor.CustomerId).SingleAsync());
        AssertProblem(await f.ApiClient(await f.AddApiKeyAsync(resolvedCid)).PostAsync($"/api/v1/incidents/{resolved}/acknowledge", null), HttpStatusCode.Conflict);

        Assert.Null((await IncidentAsync(iid)).AcknowledgedAtUtc);
        Assert.Null((await IncidentAsync(foreign)).AcknowledgedAtUtc);
        Assert.Null((await IncidentAsync(resolved)).AcknowledgedAtUtc);
        Assert.NotEqual(cid, otherCid);
    }

    [Fact]
    public async Task Monitors_and_checks_report_maintenance()
    {
        var (cid, mid, _) = await ArrangeAsync();
        var other = await f.AddMonitorAsync(cid, "Api-Not-In-Maintenance");
        await f.WithDbAsync(async db =>
        {
            var w = new MaintenanceWindow { CustomerId = cid, Name = "Now", StartsAtUtc = DateTime.UtcNow.AddMinutes(-5), EndsAtUtc = DateTime.UtcNow.AddHours(1), AppliesToAllMonitors = false };
            w.Monitors.Add(await db.Monitors.SingleAsync(m => m.Id == mid));
            db.MaintenanceWindows.Add(w);
            db.CheckResults.Add(new CheckResult { MonitorId = mid, CheckedAtUtc = DateTime.UtcNow.AddMinutes(-1), IsUp = false, ResponseTimeMs = 5, InMaintenance = true });
            await db.SaveChangesAsync();
        });
        var c = f.ApiClient(await f.AddApiKeyAsync(cid, ApiKeyScope.ReadOnly));

        Assert.True((await (await c.GetAsync($"/api/v1/monitors/{mid}")).JsonAsync()).GetProperty("inMaintenance").GetBoolean());
        Assert.False((await (await c.GetAsync($"/api/v1/monitors/{other}")).JsonAsync()).GetProperty("inMaintenance").GetBoolean());
        var list = await (await c.GetAsync("/api/v1/monitors")).JsonAsync();
        Assert.True(list.EnumerateArray().Single(m => m.GetProperty("id").GetInt32() == mid).GetProperty("inMaintenance").GetBoolean());
        var checks = await (await c.GetAsync($"/api/v1/monitors/{mid}/checks")).JsonAsync();
        Assert.True(checks.EnumerateArray().Single().GetProperty("inMaintenance").GetBoolean());
    }
}
