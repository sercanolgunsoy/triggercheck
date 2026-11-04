using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TriggerCheck.Monitoring.Alerting;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Tests.Monitoring;

namespace TriggerCheck.Tests.Ops;

/// <summary>Background services survive database outages and a double stop (StopApplication, then host dispose).</summary>
public class HostedServiceResilienceTests
{
    /// <summary>Full app with the scheduler, retention and alert sender running for real (fast ticks).</summary>
    private sealed class RunningAppFactory : TestWebAppFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Monitoring:Enabled", "true");
            builder.UseSetting("Monitoring:SchedulerTickMilliseconds", "50");
            builder.UseSetting("Monitoring:RetentionCleanupMinutes", "1");
        }
    }

    [Fact]
    public async Task Stop_application_then_dispose_does_not_throw()
    {
        var f = new RunningAppFactory();
        _ = f.CreateClient();
        var lifetime = f.Services.GetRequiredService<IHostApplicationLifetime>();
        var stopped = new TaskCompletionSource();
        lifetime.ApplicationStopped.Register(() => stopped.TrySetResult());

        lifetime.StopApplication();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await f.DisposeAsync(); // stops the host a second time
    }

    [Fact]
    public async Task Database_disappearing_does_not_stop_the_host()
    {
        await using var f = new RunningAppFactory();
        _ = f.CreateClient();
        var lifetime = f.Services.GetRequiredService<IHostApplicationLifetime>();
        await Task.Delay(300); // a few scheduler ticks on the healthy database

        var dataSource = new SqliteConnectionStringBuilder(f.Services.GetRequiredService<IConfiguration>().GetConnectionString("Default")).DataSource;
        SqliteConnection.ClearAllPools();
        File.Delete(dataSource);
        File.WriteAllBytes(dataSource, [1, 2, 3]); // not even a database: every query fails

        await Task.Delay(1500); // dozens of failing scheduler ticks, alert sender and retention running
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested, "a background service stopped the host");
    }
}

/// <summary>Each hosted service on its own: catches and logs per iteration, and tolerates StopAsync after Dispose.</summary>
public class HostedServiceUnitResilienceTests
{
    [Fact]
    public async Task Scheduler_retention_and_alert_sender_keep_running_while_the_database_is_broken()
    {
        await using var host = await MonitoringTestHost.CreateAsync(settings: new()
        {
            ["Monitoring:Enabled"] = "true",
            ["Monitoring:SchedulerTickMilliseconds"] = "1000",
            ["Monitoring:RetentionCleanupMinutes"] = "1"
        });
        var dataSource = new SqliteConnectionStringBuilder(host.Services.GetRequiredService<IConfiguration>().GetConnectionString("Default")).DataSource;
        SqliteConnection.ClearAllPools();
        File.WriteAllBytes(dataSource, [1, 2, 3]); // every query fails from now on

        var services = host.Services.GetServices<IHostedService>().Cast<BackgroundService>().ToList();
        Assert.Equal(3, services.Count);
        foreach (var s in services) await s.StartAsync(CancellationToken.None);

        for (var i = 0; i < 10; i++) // 10 scheduler ticks and 2+ retention runs, all failing
        {
            host.Time.Advance(TimeSpan.FromSeconds(15));
            await Task.Delay(30);
        }
        host.Alerts.TryEnqueue(new AlertMessage(1, 1, "M", "http://x.test", AlertKind.Down, "e", host.Now)); // delivery fails on the DB
        await Task.Delay(200);

        Assert.All(services, s => Assert.False(s.ExecuteTask!.IsCompleted, $"{s.GetType().Name} stopped"));
        foreach (var s in services) await s.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Hosted_services_can_be_stopped_again_after_dispose()
    {
        await using var host = await MonitoringTestHost.CreateAsync(settings: new() { ["Monitoring:Enabled"] = "true" });
        foreach (var s in host.Services.GetServices<IHostedService>().Cast<BackgroundService>())
        {
            await s.StartAsync(CancellationToken.None);
            await s.StopAsync(CancellationToken.None);
            s.Dispose();
            await s.StopAsync(CancellationToken.None); // StopApplication, then the host being disposed
        }
    }
}
