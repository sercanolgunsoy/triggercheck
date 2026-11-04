using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TriggerCheck.Monitoring.Diagnostics;
using TriggerCheck.Monitoring.Scheduling;
using TriggerCheck.Tests.Infrastructure;

namespace TriggerCheck.Tests.Ops;

/// <summary>
/// App with Monitoring:Enabled=true but without the scheduler's background loop (ticks are simulated through
/// <see cref="MonitoringMetrics"/>), on a <see cref="FakeTimeProvider"/>.
/// </summary>
public sealed class SchedulerEnabledAppFactory(string environment = "Development") : TestWebAppFactory
{
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment(environment);
        builder.UseSetting("Monitoring:Enabled", "true");
        builder.UseSetting("Seed:DemoData", "false");
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Time);
            foreach (var d in s.Where(d => d.ServiceType == typeof(IHostedService)
                                           && d.ImplementationFactory?.Method.ReturnType == typeof(MonitorScheduler)).ToList())
                s.Remove(d);
        });
    }

    public void Tick() => Services.GetRequiredService<MonitoringMetrics>().SchedulerTickCompleted(0, 0);
}

public class HealthEndpointTests
{
    private static async Task<(HttpStatusCode Code, JsonElement Body)> GetAsync(HttpClient client, string url)
    {
        var r = await client.GetAsync(url);
        var body = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        return (r.StatusCode, body);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Endpoints_are_anonymous_and_healthy_when_scheduler_disabled(string url)
    {
        await using var f = new TestWebAppFactory(); // Monitoring:Enabled=false
        var (code, body) = await GetAsync(f.CreateBrowser(), url);
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("Healthy", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Ready_follows_the_scheduler_heartbeat_while_live_stays_up()
    {
        await using var f = new SchedulerEnabledAppFactory();
        var client = f.CreateBrowser();

        // Just started, no tick yet: within the grace period.
        var (code, body) = await GetAsync(client, "/health/ready");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("Degraded", body.GetProperty("status").GetString());

        // No tick for longer than the threshold.
        f.Time.Advance(SchedulerHeartbeatHealthCheckThreshold + TimeSpan.FromSeconds(1));
        (code, body) = await GetAsync(client, "/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, code);
        Assert.Equal("Unhealthy", body.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);

        f.Tick();
        (code, body) = await GetAsync(client, "/health/ready");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("Healthy", body.GetProperty("status").GetString());

        f.Time.Advance(TimeSpan.FromSeconds(90)); // still fresh
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);

        f.Time.Advance(TimeSpan.FromSeconds(31)); // 2 min 1 s since the last tick
        (code, body) = await GetAsync(client, "/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, code);
        // Development shows which check failed, never an exception text.
        var scheduler = body.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "scheduler");
        Assert.Equal("Unhealthy", scheduler.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Ready_is_unhealthy_when_database_is_gone_and_production_body_has_status_only()
    {
        await using var f = new SchedulerEnabledAppFactory("Production");
        var client = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        f.Tick();

        var (code, body) = await GetAsync(client, "/health/ready");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("Healthy", body.GetProperty("status").GetString());

        var dataSource = new SqliteConnectionStringBuilder(f.Services.GetRequiredService<IConfiguration>().GetConnectionString("Default")).DataSource;
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={dataSource}")); // not ClearAllPools: other tests run in parallel
        File.Delete(dataSource);

        var r = await client.GetAsync("/health/ready");
        var text = await r.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Equal("{\"status\":\"Unhealthy\"}", text); // no per-check details, no exception text outside Development
    }

    private static readonly TimeSpan SchedulerHeartbeatHealthCheckThreshold = TriggerCheck.Web.Infrastructure.SchedulerHeartbeatHealthCheck.MinStaleAfter;
}
