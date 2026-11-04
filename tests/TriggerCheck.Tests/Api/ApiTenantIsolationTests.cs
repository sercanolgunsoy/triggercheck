using System.Net;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;
using static TriggerCheck.Tests.Api.ApiTestHelpers;

namespace TriggerCheck.Tests.Api;

/// <summary>Every endpoint is scoped to the key's customer; other tenants' ids answer 404 and change nothing.</summary>
public class ApiTenantIsolationTests : IClassFixture<ApiTenantIsolationTests.Fixture>
{
    public sealed class Fixture : TestWebAppFactory
    {
        public int MineId, TheirsId, TheirMonitorId;
        public string MyToken = "";

        public async Task InitAsync()
        {
            if (MyToken.Length > 0) return;
            MineId = await this.AddCustomerAsync("Iso-Mine");
            TheirsId = await this.AddCustomerAsync("Iso-Theirs");
            await this.AddMonitorAsync(MineId, "MyMonitor");
            TheirMonitorId = await this.AddMonitorAsync(TheirsId, "TheirMonitor", m => m.Status = MonitorStatus.Down);
            await WithDbAsync(async db =>
            {
                db.Incidents.Add(new Incident { MonitorId = TheirMonitorId, StartedAtUtc = DateTime.UtcNow.AddMinutes(-3) });
                db.CheckResults.Add(new CheckResult { MonitorId = TheirMonitorId, CheckedAtUtc = DateTime.UtcNow.AddMinutes(-1), IsUp = false, ResponseTimeMs = 5 });
                await db.SaveChangesAsync();
            });
            MyToken = await this.AddApiKeyAsync(MineId);
        }
    }

    private readonly Fixture _f;
    public ApiTenantIsolationTests(Fixture f) => _f = f;

    private async Task<HttpClient> ClientAsync()
    {
        await _f.InitAsync();
        return _f.ApiClient(_f.MyToken);
    }

    [Fact]
    public async Task Other_tenants_monitor_is_404_on_every_endpoint()
    {
        var c = await ClientAsync();
        var id = _f.TheirMonitorId;
        var before = await _f.GetMonitorAsync(id);

        var responses = new[]
        {
            await c.GetAsync($"/api/v1/monitors/{id}"),
            await c.GetAsync($"/api/v1/monitors/{id}/checks"),
            await c.PutJsonAsync($"/api/v1/monitors/{id}", MonitorBody("Hijacked")),
            await c.PostAsync($"/api/v1/monitors/{id}/pause", null),
            await c.PostAsync($"/api/v1/monitors/{id}/resume", null),
            await c.DeleteAsync($"/api/v1/monitors/{id}"),
            await c.GetAsync("/api/v1/monitors/999999"),
        };
        Assert.All(responses, r => AssertProblem(r, HttpStatusCode.NotFound));

        var after = await _f.GetMonitorAsync(id);
        Assert.NotNull(after);
        Assert.Equal(before!.Name, after.Name);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Version, after.Version);
        Assert.True(await _f.WithDbAsync(db => db.Incidents.AnyAsync(i => i.MonitorId == id && i.ResolvedAtUtc == null)));
    }

    [Fact]
    public async Task Lists_and_status_contain_only_own_data()
    {
        var c = await ClientAsync();

        var monitors = await (await c.GetAsync("/api/v1/monitors")).JsonAsync();
        Assert.Equal("MyMonitor", Assert.Single(monitors.EnumerateArray()).GetProperty("name").GetString());

        Assert.Equal(0, (await (await c.GetAsync("/api/v1/incidents")).JsonAsync()).GetArrayLength());
        Assert.Equal(0, (await (await c.GetAsync($"/api/v1/incidents?monitorId={_f.TheirMonitorId}")).JsonAsync()).GetArrayLength());

        var status = await (await c.GetAsync("/api/v1/status")).JsonAsync();
        Assert.Equal(1, status.GetProperty("monitors").GetProperty("total").GetInt32());
        Assert.Equal(0, status.GetProperty("openIncidents").GetInt32());
        Assert.StartsWith("Iso-Mine", status.GetProperty("customer").GetString());
    }
}
