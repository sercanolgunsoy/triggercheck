using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring;
using TriggerCheck.Monitoring.Checks;
using TriggerCheck.ProbeAgent;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Tests.Monitoring;

namespace TriggerCheck.Tests.Regions;

/// <summary>
/// FEAT-6 end to end, in process: the real agent (DI, checkers, API client, worker) talks to the real central app on a
/// TestServer. Only the agent's network hop to the central app is replaced by the TestServer handler.
/// </summary>
public class ProbeAgentIntegrationTests(TestWebAppFactory f) : IClassFixture<TestWebAppFactory>
{
    private ServiceProvider CreateAgent(TestRegion region, bool allowPrivateTargets)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Agent:CentralUrl"] = "http://localhost/",
            ["Agent:RegionId"] = region.Code,
            ["Agent:Credential"] = region.Token,
            ["Agent:AllowInsecureCentral"] = "true",
            ["Agent:MaxConcurrency"] = "5"
        };
        // The agent's default is AllowPrivateTargets=false; only set it when the test needs to reach the loopback target.
        if (allowPrivateTargets) settings["Monitoring:AllowPrivateTargets"] = "true";
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddProbeAgent(config);
        services.AddHttpClient(AgentApiClient.ClientName).ConfigurePrimaryHttpMessageHandler(() => f.Server.CreateHandler());
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private async Task<(int MonitorId, TestRegion Region)> ArrangeAsync(string target)
    {
        var cid = await f.DemoCustomerIdAsync();
        var monitorId = await f.AddMonitorAsync(cid, "Agent " + Guid.NewGuid().ToString("N")[..6], m => m.Target = target);
        var region = await f.WithDbAsync(async db =>
        {
            await RegionTestData.SetPlanRegionsAsync(db, cid, 0);
            var r = await RegionTestData.AddRegionAsync(db, "it-" + Guid.NewGuid().ToString("N")[..6], DateTime.UtcNow);
            await RegionTestData.AssignAsync(db, monitorId, r);
            return r;
        });
        return (monitorId, region);
    }

    private static async Task RunOnceAsync(ServiceProvider agent)
    {
        var worker = agent.GetRequiredService<ProbeAgentWorker>();
        Assert.Equal(1, await worker.LeaseAndStartAsync(CancellationToken.None));
        await worker.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, await worker.FlushAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Agent_leases_probes_runs_them_and_reports_to_central()
    {
        await using var target = await LocalHttpServer.StartAsync(app => app.MapGet("/health", () => Results.Text("ok")));
        var (monitorId, region) = await ArrangeAsync(target.Url("/health"));
        await using var agent = CreateAgent(region, allowPrivateTargets: true);

        await RunOnceAsync(agent);

        var result = Assert.Single(await f.WithDbAsync(db => db.CheckResults.Where(c => c.MonitorId == monitorId).ToListAsync()));
        Assert.Equal(region.Id, result.RegionId);
        Assert.True(result.IsUp, result.Error);
        Assert.Equal(200, result.StatusCode);
        Assert.NotNull(result.TtfbMs);
        var state = await f.WithDbAsync(db => db.MonitorRegionStates.SingleAsync(s => s.MonitorId == monitorId));
        Assert.True(state.LastIsUp);
        Assert.NotNull(await f.WithDbAsync(db => db.Regions.Where(r => r.Id == region.Id).Select(r => r.LastSeenAtUtc).SingleAsync()));
        // Nothing left to do until the next interval.
        Assert.Equal(0, await agent.GetRequiredService<ProbeAgentWorker>().LeaseAndStartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Agent_enforces_the_ssrf_guard_by_default()
    {
        await using var target = await LocalHttpServer.StartAsync(app => app.MapGet("/internal", () => Results.Text("ok")));
        var (monitorId, region) = await ArrangeAsync(target.Url("/internal"));
        await using var agent = CreateAgent(region, allowPrivateTargets: false);
        Assert.False(agent.GetRequiredService<IOptionsMonitor<MonitoringOptions>>().CurrentValue.AllowPrivateTargets);

        await RunOnceAsync(agent);

        var result = Assert.Single(await f.WithDbAsync(db => db.CheckResults.Where(c => c.MonitorId == monitorId).ToListAsync()));
        Assert.False(result.IsUp);
        Assert.Contains("Blocked target", result.Error);
    }

    [Fact]
    public async Task Rejected_credential_is_reported_without_leaking_it()
    {
        var (_, region) = await ArrangeAsync("https://example.org/");
        await using var agent = CreateAgent(region with { Token = region.Token + "x" }, allowPrivateTargets: false);
        var ex = await Assert.ThrowsAsync<AgentRejectedException>(() => agent.GetRequiredService<ProbeAgentWorker>().LeaseAndStartAsync(CancellationToken.None));
        Assert.DoesNotContain(region.Token, ex.Message);
    }

    [Fact]
    public void Agent_options_are_validated()
    {
        Assert.NotEmpty(new AgentOptions().Validate());
        Assert.Contains(new AgentOptions { CentralUrl = "http://central", RegionId = "x", Credential = "tcr_x" }.Validate(), e => e.Contains("https"));
        Assert.Empty(new AgentOptions { CentralUrl = "https://central", RegionId = "x", Credential = "tcr_x" }.Validate());
    }

    [Fact]
    public void Agent_registers_the_probing_checkers_only()
    {
        var config = new ConfigurationBuilder().Build();
        var services = new ServiceCollection().AddLogging();
        services.AddProbeAgent(config);
        using var sp = services.BuildServiceProvider();
        var types = sp.GetServices<IMonitorChecker>().Select(c => c.Type).ToHashSet();
        Assert.Equal([MonitorType.Http, MonitorType.Keyword, MonitorType.Tcp, MonitorType.Ping, MonitorType.Dns, MonitorType.JsonApi], types.Order());
    }
}
