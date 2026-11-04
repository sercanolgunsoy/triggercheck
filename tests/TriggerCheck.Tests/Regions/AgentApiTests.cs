using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Regions;
using TriggerCheck.Tests.Api;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Web.Agents;

namespace TriggerCheck.Tests.Regions;

/// <summary>FEAT-6: the internal agent API - authentication, lease isolation, result validation, limits.</summary>
public class AgentApiTests(TestWebAppFactory f) : IClassFixture<TestWebAppFactory>
{
    private sealed record Arranged(int CustomerId, int MonitorA, int MonitorB, TestRegion A, TestRegion B);

    /// <summary>Two regions (unique codes per test), one monitor assigned to each; the demo plan allows regions.</summary>
    private async Task<Arranged> ArrangeAsync()
    {
        var cid = await f.DemoCustomerIdAsync();
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var ma = await f.AddMonitorAsync(cid, "Shop " + suffix, m => { m.Keyword = "secret-ish keyword"; m.Type = MonitorType.Keyword; });
        var mb = await f.AddMonitorAsync(cid, "Billing " + suffix);
        return await f.WithDbAsync(async db =>
        {
            await RegionTestData.SetPlanRegionsAsync(db, cid, 0);
            var a = await RegionTestData.AddRegionAsync(db, "a-" + suffix, DateTime.UtcNow);
            var b = await RegionTestData.AddRegionAsync(db, "b-" + suffix, DateTime.UtcNow);
            await RegionTestData.AssignAsync(db, ma, a);
            await RegionTestData.AssignAsync(db, mb, b);
            return new Arranged(cid, ma, mb, a, b);
        });
    }

    [Fact]
    public async Task Lease_returns_only_the_regions_own_probes_and_no_customer_data()
    {
        var x = await ArrangeAsync();
        var response = await f.AgentClient(x.A).PostAsJsonAsync(AgentProtocol.LeasePath, new LeaseRequest { MaxItems = 10 }, AgentProtocol.Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var lease = System.Text.Json.JsonSerializer.Deserialize<LeaseResponse>(json, AgentProtocol.Json)!;

        var item = Assert.Single(lease.Items);
        var monitor = (await f.GetMonitorAsync(x.MonitorA))!;
        Assert.Equal(monitor.Target, item.Target);
        Assert.Equal(MonitorType.Keyword, item.Type);
        Assert.Equal("secret-ish keyword", item.Keyword); // the probe needs it
        // Nothing identifying the customer or the monitor.
        Assert.DoesNotContain(monitor.Name, json);
        Assert.DoesNotContain("Demo Company", json);
        Assert.DoesNotContain("\"monitorId\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"customerId\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("admin@demo.local", json);

        // Leased once: a second lease of the same region does not hand it out again.
        Assert.Empty((await f.AgentClient(x.A).LeaseAsync()).Items);
    }

    [Fact]
    public async Task Results_for_foreign_expired_duplicate_or_stale_items_are_not_applied()
    {
        var x = await ArrangeAsync();
        var agentA = f.AgentClient(x.A);
        var agentB = f.AgentClient(x.B);
        var itemA = Assert.Single((await agentA.LeaseAsync()).Items).ItemId;
        var itemB = Assert.Single((await agentB.LeaseAsync()).Items).ItemId;
        var now = DateTime.UtcNow;

        // Region a cannot report region b's probe.
        Assert.Equal(ResultStatus.Rejected, Assert.Single(await agentA.PostResultsAsync(RegionTestData.Down(itemB, now))).Status);
        Assert.Equal(ResultStatus.Rejected, Assert.Single(await agentA.PostResultsAsync(RegionTestData.Down("not-a-lease", now))).Status);
        Assert.Equal(0, await f.WithDbAsync(db => db.CheckResults.CountAsync(c => c.MonitorId == x.MonitorB)));

        // b's own report is accepted; a replay is a duplicate and stored once.
        Assert.Equal(ResultStatus.Accepted, Assert.Single(await agentB.PostResultsAsync(RegionTestData.Up(itemB, now))).Status);
        Assert.Equal(ResultStatus.Duplicate, Assert.Single(await agentB.PostResultsAsync(RegionTestData.Up(itemB, now))).Status);
        var stored = Assert.Single(await f.WithDbAsync(db => db.CheckResults.Where(c => c.MonitorId == x.MonitorB).ToListAsync()));
        Assert.Equal(x.B.Id, stored.RegionId);
        Assert.Null(stored.VotedUp);
        var state = await f.WithDbAsync(db => db.MonitorRegionStates.SingleAsync(s => s.MonitorId == x.MonitorB));
        Assert.True(state.LastIsUp);
        Assert.Null(state.LeaseItemId);
        Assert.True(state.NextCheckAtUtc > now);

        // Expired lease.
        await f.WithDbAsync(db => db.MonitorRegionStates.Where(s => s.MonitorId == x.MonitorA)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.LeasedUntilUtc, DateTime.UtcNow.AddSeconds(-1))));
        Assert.Equal(ResultStatus.Expired, Assert.Single(await agentA.PostResultsAsync(RegionTestData.Up(itemA, now))).Status);

        // Probe settings edited after the lease: discarded.
        var itemA2 = Assert.Single((await agentA.LeaseAsync()).Items).ItemId;
        await f.WithDbAsync(db => db.Monitors.Where(m => m.Id == x.MonitorA)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.Target, "https://example.org/changed")));
        Assert.Equal(ResultStatus.Discarded, Assert.Single(await agentA.PostResultsAsync(RegionTestData.Up(itemA2, DateTime.UtcNow))).Status);
        Assert.Equal(0, await f.WithDbAsync(db => db.CheckResults.CountAsync(c => c.MonitorId == x.MonitorA)));

        // Malformed result.
        var itemA3 = Assert.Single((await agentA.LeaseAsync()).Items).ItemId;
        Assert.Equal(ResultStatus.Invalid, Assert.Single(await agentA.PostResultsAsync(RegionTestData.Up(itemA3, now) with { StatusCode = 42 })).Status);
    }

    [Fact]
    public async Task Paused_monitors_heartbeats_and_single_region_plans_are_not_leased()
    {
        var x = await ArrangeAsync();
        await f.WithDbAsync(db => db.Monitors.Where(m => m.Id == x.MonitorA)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, MonitorStatus.Paused)));
        Assert.Empty((await f.AgentClient(x.A).LeaseAsync()).Items);

        await f.WithDbAsync(db => db.Monitors.Where(m => m.Id == x.MonitorB)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.Type, MonitorType.Heartbeat)));
        Assert.Empty((await f.AgentClient(x.B).LeaseAsync()).Items);

        // A plan without regions (e.g. after a downgrade) stops remote probes.
        var mc = await f.AddMonitorAsync(x.CustomerId, "Plan gated " + x.A.Code);
        await f.WithDbAsync(async db =>
        {
            await RegionTestData.AssignAsync(db, mc, x.A);
            await RegionTestData.SetPlanRegionsAsync(db, x.CustomerId, 1);
        });
        try
        {
            Assert.Empty((await f.AgentClient(x.A).LeaseAsync()).Items);
        }
        finally
        {
            await f.WithDbAsync(db => RegionTestData.SetPlanRegionsAsync(db, x.CustomerId, 0));
        }
        Assert.Single((await f.AgentClient(x.A).LeaseAsync()).Items);
    }

    [Fact]
    public async Task Bad_rotated_or_disabled_credentials_are_rejected()
    {
        var x = await ArrangeAsync();
        async Task AssertUnauthorized(HttpClient c)
        {
            var r = await c.PostAsJsonAsync(AgentProtocol.LeasePath, new LeaseRequest(), AgentProtocol.Json);
            ApiTestHelpers.AssertProblem(r, HttpStatusCode.Unauthorized);
        }

        await AssertUnauthorized(f.CreateClient()); // no credential
        await AssertUnauthorized(f.AgentClient(x.A, token: x.A.Token + "x"));
        await AssertUnauthorized(f.AgentClient(x.A, token: AgentTokens.Generate().Token));
        await AssertUnauthorized(f.AgentClient(x.A, regionHeader: x.B.Code)); // credential of another region
        await AssertUnauthorized(f.AgentClient(x.A, token: await f.AddApiKeyAsync(x.CustomerId))); // customer API key

        // An agent credential is no customer API key either.
        var api = f.ApiClient(x.A.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/api/v1/monitors")).StatusCode);

        // Rotation: the old credential stops working immediately.
        var rotated = AgentTokens.Generate();
        await f.WithDbAsync(db => db.Regions.Where(r => r.Id == x.A.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Prefix, rotated.Prefix).SetProperty(r => r.SecretHash, rotated.Hash)));
        await AssertUnauthorized(f.AgentClient(x.A));
        Assert.Single((await f.AgentClient(x.A, token: rotated.Token).LeaseAsync()).Items);

        // Disabled region: rejected, even with a valid credential.
        await f.WithDbAsync(db => db.Regions.Where(r => r.Id == x.B.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.IsEnabled, false)));
        await AssertUnauthorized(f.AgentClient(x.B));
    }

    [Fact]
    public async Task Authenticated_requests_update_last_seen()
    {
        var x = await ArrangeAsync();
        await f.WithDbAsync(db => db.Regions.Where(r => r.Id == x.A.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.LastSeenAtUtc, (DateTime?)null)));
        await f.AgentClient(x.A).LeaseAsync();
        Assert.NotNull(await f.WithDbAsync(db => db.Regions.Where(r => r.Id == x.A.Id).Select(r => r.LastSeenAtUtc).SingleAsync()));
    }

    [Fact]
    public async Task Oversized_or_invalid_requests_are_refused()
    {
        var x = await ArrangeAsync();
        var agent = f.AgentClient(x.A);

        var huge = new StringContent("{\"results\":[" + new string(' ', AgentProtocol.MaxResultsBodyBytes) + "]}", Encoding.UTF8, "application/json");
        ApiTestHelpers.AssertProblem(await agent.PostAsync(AgentProtocol.ResultsPath, huge), HttpStatusCode.RequestEntityTooLarge);

        var many = Enumerable.Range(0, AgentProtocol.MaxResultsPerRequest + 1).Select(i => RegionTestData.Up("i" + i, DateTime.UtcNow)).ToArray();
        var r = await agent.PostAsJsonAsync(AgentProtocol.ResultsPath, new ResultsRequest { Results = many }, AgentProtocol.Json);
        ApiTestHelpers.AssertProblem(r, HttpStatusCode.BadRequest);

        r = await agent.PostAsJsonAsync(AgentProtocol.LeasePath, new LeaseRequest { MaxItems = 1000 }, AgentProtocol.Json);
        ApiTestHelpers.AssertProblem(r, HttpStatusCode.BadRequest);

        r = await agent.PostAsync(AgentProtocol.LeasePath, new StringContent("{not json", Encoding.UTF8, "application/json"));
        ApiTestHelpers.AssertProblem(r, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Agent_requests_are_rate_limited_per_region()
    {
        using var parent = new TestWebAppFactory();
        using var limited = parent.WithWebHostBuilder(b => b.UseSetting("Regions:AgentRequestsPerMinute", "2"));
        TestRegion region;
        using (var scope = limited.Services.CreateScope())
            region = await RegionTestData.AddRegionAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), "rl", DateTime.UtcNow);
        var client = limited.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", region.Token);
        client.DefaultRequestHeaders.Add(AgentProtocol.RegionHeader, region.Code);
        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(AgentProtocol.LeasePath, new LeaseRequest(), AgentProtocol.Json)).StatusCode);
        var r = await client.PostAsJsonAsync(AgentProtocol.LeasePath, new LeaseRequest(), AgentProtocol.Json);
        ApiTestHelpers.AssertProblem(r, HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Agent_api_is_not_in_the_public_openapi_document()
    {
        var doc = await f.CreateClient().GetStringAsync("/api/v1/openapi.json");
        Assert.DoesNotContain("/api/agent", doc);
    }
}
