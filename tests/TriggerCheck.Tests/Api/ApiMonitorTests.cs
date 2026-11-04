using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;
using static TriggerCheck.Tests.Api.ApiTestHelpers;

namespace TriggerCheck.Tests.Api;

/// <summary>Monitor CRUD, lifecycle, checks, incidents and status through the API, with the panel's rules.</summary>
public class ApiMonitorTests(TestWebAppFactory f) : IClassFixture<TestWebAppFactory>
{
    private const string Monitors = "/api/v1/monitors";

    private async Task<(int Cid, HttpClient Client)> TenantAsync(string name, string plan = "Standard")
    {
        var cid = await f.AddCustomerAsync(name + "-" + Guid.NewGuid().ToString("N")[..6], plan);
        return (cid, f.ApiClient(await f.AddApiKeyAsync(cid)));
    }

    [Fact]
    public async Task Create_get_update_delete_round_trip()
    {
        var (cid, c) = await TenantAsync("Crud");

        var created = await c.PostJsonAsync(Monitors, MonitorBody("Api monitor", ("intervalSeconds", 60)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.JsonAsync();
        var id = body.GetProperty("id").GetInt32();
        Assert.Equal($"/api/v1/monitors/{id}", created.Headers.Location!.ToString());
        Assert.Equal("Http", body.GetProperty("type").GetString());
        Assert.Equal("Unknown", body.GetProperty("status").GetString());
        Assert.EndsWith("Z", body.GetProperty("createdAtUtc").GetString());

        var m = await f.GetMonitorAsync(id);
        Assert.Equal(cid, m!.CustomerId);
        Assert.True(m.IsEnabled);
        Assert.Equal(60, m.IntervalSeconds);

        var get = await c.GetAsync($"{Monitors}/{id}");
        Assert.Equal("Api monitor", (await get.JsonAsync()).GetProperty("name").GetString());
        var list = await (await c.GetAsync(Monitors)).JsonAsync();
        Assert.Equal(1, list.GetArrayLength());

        var put = await c.PutJsonAsync($"{Monitors}/{id}", MonitorBody("Renamed", ("type", "Tcp"), ("target", "example.org"), ("port", 443)));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var edited = await f.GetMonitorAsync(id);
        Assert.Equal("Renamed", edited!.Name);
        Assert.Equal(MonitorType.Tcp, edited.Type);
        Assert.Equal(443, edited.Port);
        Assert.Equal(m.Version + 1, edited.Version); // Version bump lets an in-flight check notice the edit

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"{Monitors}/{id}")).StatusCode);
        Assert.Null(await f.GetMonitorAsync(id));
        ApiTestHelpers.AssertProblem(await c.GetAsync($"{Monitors}/{id}"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Customer_id_in_the_body_is_ignored()
    {
        var (cid, c) = await TenantAsync("NoCidOverride");
        var other = await f.AddCustomerAsync("Victim-" + Guid.NewGuid().ToString("N")[..6]);
        var r = await c.PostJsonAsync(Monitors, MonitorBody("Smuggled", ("customerId", other)));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var m = await f.GetMonitorAsync((await r.JsonAsync()).GetProperty("id").GetInt32());
        Assert.Equal(cid, m!.CustomerId);
    }

    [Fact]
    public async Task Validation_errors_are_reported_per_field()
    {
        var (_, c) = await TenantAsync("Validation");
        var r = await c.PostJsonAsync(Monitors, MonitorBody("", ("target", "not a url"), ("timeoutSeconds", 99), ("failureThreshold", 9)));
        ApiTestHelpers.AssertProblem(r, HttpStatusCode.BadRequest);
        var errors = (await r.JsonAsync()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("name", out _));
        Assert.True(errors.TryGetProperty("timeoutSeconds", out _));
        Assert.True(errors.TryGetProperty("failureThreshold", out _));

        var tcp = await c.PostJsonAsync(Monitors, MonitorBody("Tcp", ("type", "Tcp"), ("target", "example.org"), ("port", null)));
        Assert.Contains("A TCP monitor needs a port between 1 and 65535.", (await tcp.JsonAsync()).GetProperty("errors").GetProperty("port")[0].GetString());
    }

    [Fact]
    public async Task Malformed_or_non_json_body_is_a_problem_response()
    {
        var (_, c) = await TenantAsync("BadBody");
        var bad = await c.PostAsync(Monitors, new StringContent("{\"name\": ", Encoding.UTF8, "application/json"));
        ApiTestHelpers.AssertProblem(bad, HttpStatusCode.BadRequest);
        var wrongType = await c.PostAsync(Monitors, new StringContent("{\"name\":\"x\",\"type\":\"Smtp\"}", Encoding.UTF8, "application/json"));
        ApiTestHelpers.AssertProblem(wrongType, HttpStatusCode.BadRequest);
        var form = await c.PostAsync(Monitors, new FormUrlEncodedContent(new Dictionary<string, string> { ["name"] = "x" }));
        ApiTestHelpers.AssertProblem(form, HttpStatusCode.UnsupportedMediaType);
        ApiTestHelpers.AssertProblem(await c.GetAsync("/api/v1/unknown"), HttpStatusCode.NotFound);
        ApiTestHelpers.AssertProblem(await c.PatchAsync(Monitors, null), HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task Interval_not_on_the_plan_is_rejected_like_the_form()
    {
        var (cid, c) = await TenantAsync("Interval", plan: "Basic"); // Basic: 300 s minimum
        var r = await c.PostJsonAsync(Monitors, MonitorBody("TooFast", ("intervalSeconds", 60)));
        ApiTestHelpers.AssertProblem(r, HttpStatusCode.BadRequest);
        var msg = (await r.JsonAsync()).GetProperty("errors").GetProperty("intervalSeconds")[0].GetString();
        Assert.Contains("This check interval is not available on your plan (Basic)", msg);

        var id = await f.AddMonitorAsync(cid, "Slow");
        var put = await c.PutJsonAsync($"{Monitors}/{id}", MonitorBody("Slow", ("intervalSeconds", 15)));
        ApiTestHelpers.AssertProblem(put, HttpStatusCode.BadRequest);
        Assert.Equal(300, (await f.GetMonitorAsync(id))!.IntervalSeconds);
    }

    [Fact]
    public async Task Plan_quota_is_enforced()
    {
        var (cid, c) = await TenantAsync("Quota", plan: "Basic");
        var max = await f.WithDbAsync(db => db.Plans.Where(p => p.Name == "Basic").Select(p => p.MaxMonitors).FirstAsync());
        for (var i = 0; i < max - 1; i++) await f.AddMonitorAsync(cid, $"Fill-{i}");

        Assert.Equal(HttpStatusCode.Created, (await c.PostJsonAsync(Monitors, MonitorBody("LastSlot"))).StatusCode);
        var over = await c.PostJsonAsync(Monitors, MonitorBody("OverQuota"));
        ApiTestHelpers.AssertProblem(over, HttpStatusCode.Conflict);
        Assert.Contains("monitor limit of your plan", await over.Content.ReadAsStringAsync());
        Assert.Equal(max, await f.WithDbAsync(db => db.Monitors.CountAsync(m => m.CustomerId == cid)));
    }

    [Fact]
    public async Task Parallel_creates_never_exceed_the_quota()
    {
        var (cid, c) = await TenantAsync("QuotaRace", plan: "Basic");
        var max = await f.WithDbAsync(db => db.Plans.Where(p => p.Name == "Basic").Select(p => p.MaxMonitors).FirstAsync());
        for (var i = 0; i < max - 3; i++) await f.AddMonitorAsync(cid, $"Fill-{i}");

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => c.PostJsonAsync(Monitors, MonitorBody($"Race-{i}"))));
        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict or HttpStatusCode.ServiceUnavailable,
            $"Unexpected {(int)r.StatusCode}"));
        Assert.True(responses.Count(r => r.StatusCode == HttpStatusCode.Created) <= 3);
        Assert.True(await f.WithDbAsync(db => db.Monitors.CountAsync(m => m.CustomerId == cid)) <= max);
    }

    [Fact]
    public async Task Pause_and_resume_use_the_lifecycle()
    {
        var (cid, c) = await TenantAsync("Lifecycle");
        var id = await f.AddMonitorAsync(cid, "Downed", m => { m.Status = MonitorStatus.Down; m.ConsecutiveFailures = 3; });
        await f.WithDbAsync(async db =>
        {
            db.Incidents.Add(new Incident { MonitorId = id, StartedAtUtc = DateTime.UtcNow.AddMinutes(-5), Cause = "timeout" });
            await db.SaveChangesAsync();
        });
        var version = (await f.GetMonitorAsync(id))!.Version;

        var paused = await c.PostAsync($"{Monitors}/{id}/pause", null);
        Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
        var pj = await paused.JsonAsync();
        Assert.Equal("Paused", pj.GetProperty("status").GetString());
        Assert.True(pj.GetProperty("isPaused").GetBoolean());
        var m = await f.GetMonitorAsync(id);
        Assert.False(m!.IsEnabled);
        Assert.True(m.DownWhenPaused);
        Assert.True(m.Version > version);
        Assert.False(await f.WithDbAsync(db => db.Incidents.AnyAsync(i => i.MonitorId == id && i.ResolvedAtUtc == null)));

        // Idempotent.
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"{Monitors}/{id}/pause", null)).StatusCode);

        var resumed = await c.PostAsync($"{Monitors}/{id}/resume", null);
        Assert.Equal("Unknown", (await resumed.JsonAsync()).GetProperty("status").GetString());
        m = await f.GetMonitorAsync(id);
        Assert.True(m!.IsEnabled);
        Assert.Equal(0, m.ConsecutiveFailures);
    }

    [Fact]
    public async Task Checks_filter_by_range_and_limit()
    {
        var (cid, c) = await TenantAsync("Checks");
        var id = await f.AddMonitorAsync(cid, "Checked");
        var now = DateTime.UtcNow;
        await f.WithDbAsync(async db =>
        {
            for (var i = 0; i < 10; i++)
                db.CheckResults.Add(new CheckResult { MonitorId = id, CheckedAtUtc = now.AddMinutes(-i * 10), IsUp = i % 2 == 0, ResponseTimeMs = 100 + i });
            db.CheckResults.Add(new CheckResult { MonitorId = id, CheckedAtUtc = now.AddDays(-3), IsUp = true, ResponseTimeMs = 1 });
            await db.SaveChangesAsync();
        });

        var all = await (await c.GetAsync($"{Monitors}/{id}/checks")).JsonAsync();
        Assert.Equal(10, all.GetArrayLength()); // default window: last 24 h
        Assert.Equal(100, all[0].GetProperty("responseTimeMs").GetInt32()); // newest first

        var limited = await (await c.GetAsync($"{Monitors}/{id}/checks?limit=3")).JsonAsync();
        Assert.Equal(3, limited.GetArrayLength());

        var from = Uri.EscapeDataString(now.AddDays(-4).ToString("o"));
        var to = Uri.EscapeDataString(now.AddDays(-2).ToString("o"));
        var old = await (await c.GetAsync($"{Monitors}/{id}/checks?from={from}&to={to}")).JsonAsync();
        Assert.Equal(1, old.GetArrayLength());

        ApiTestHelpers.AssertProblem(await c.GetAsync($"{Monitors}/{id}/checks?limit=0"), HttpStatusCode.BadRequest);
        ApiTestHelpers.AssertProblem(await c.GetAsync($"{Monitors}/{id}/checks?limit=abc"), HttpStatusCode.BadRequest);
        ApiTestHelpers.AssertProblem(await c.GetAsync($"{Monitors}/{id}/checks?from=yesterday"), HttpStatusCode.BadRequest);
        ApiTestHelpers.AssertProblem(await c.GetAsync($"{Monitors}/{id}/checks?from={to}&to={from}"), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Incidents_filter_by_status_and_monitor()
    {
        var (cid, c) = await TenantAsync("Incidents");
        var a = await f.AddMonitorAsync(cid, "A");
        var b = await f.AddMonitorAsync(cid, "B");
        await f.WithDbAsync(async db =>
        {
            db.Incidents.Add(new Incident { MonitorId = a, StartedAtUtc = DateTime.UtcNow.AddHours(-2), ResolvedAtUtc = DateTime.UtcNow.AddHours(-1) });
            db.Incidents.Add(new Incident { MonitorId = a, StartedAtUtc = DateTime.UtcNow.AddMinutes(-10) });
            db.Incidents.Add(new Incident { MonitorId = b, StartedAtUtc = DateTime.UtcNow.AddMinutes(-5) });
            await db.SaveChangesAsync();
        });

        Assert.Equal(3, (await (await c.GetAsync("/api/v1/incidents")).JsonAsync()).GetArrayLength());
        var open = await (await c.GetAsync("/api/v1/incidents?status=open")).JsonAsync();
        Assert.Equal(2, open.GetArrayLength());
        Assert.All(open.EnumerateArray(), i => Assert.True(i.GetProperty("isOpen").GetBoolean()));
        Assert.Equal(1, (await (await c.GetAsync("/api/v1/incidents?status=resolved")).JsonAsync()).GetArrayLength());
        var forA = await (await c.GetAsync($"/api/v1/incidents?monitorId={a}")).JsonAsync();
        Assert.Equal(2, forA.GetArrayLength());
        Assert.Equal("A", forA[0].GetProperty("monitorName").GetString());
        ApiTestHelpers.AssertProblem(await c.GetAsync("/api/v1/incidents?status=closed"), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Status_summarizes_the_tenant()
    {
        var (cid, c) = await TenantAsync("Status");
        var up = await f.AddMonitorAsync(cid, "Up", m => m.Status = MonitorStatus.Up);
        var down = await f.AddMonitorAsync(cid, "Down", m => m.Status = MonitorStatus.Down);
        await f.AddMonitorAsync(cid, "Paused", m => { m.Status = MonitorStatus.Paused; m.IsEnabled = false; });
        await f.WithDbAsync(async db =>
        {
            db.Incidents.Add(new Incident { MonitorId = down, StartedAtUtc = DateTime.UtcNow.AddMinutes(-5) });
            db.CheckResults.Add(new CheckResult { MonitorId = up, CheckedAtUtc = DateTime.UtcNow.AddMinutes(-1), IsUp = true, ResponseTimeMs = 10 });
            db.CheckResults.Add(new CheckResult { MonitorId = down, CheckedAtUtc = DateTime.UtcNow.AddMinutes(-1), IsUp = false, ResponseTimeMs = 10 });
            await db.SaveChangesAsync();
        });

        var s = await (await c.GetAsync("/api/v1/status")).JsonAsync();
        var monitors = s.GetProperty("monitors");
        Assert.Equal(3, monitors.GetProperty("total").GetInt32());
        Assert.Equal(1, monitors.GetProperty("up").GetInt32());
        Assert.Equal(1, monitors.GetProperty("down").GetInt32());
        Assert.Equal(1, monitors.GetProperty("paused").GetInt32());
        Assert.Equal(1, s.GetProperty("openIncidents").GetInt32());
        Assert.Equal(50.0, s.GetProperty("uptime24hPercent").GetDouble());
        Assert.Equal("Standard", s.GetProperty("plan").GetProperty("name").GetString());
    }
}
