using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring;
using TriggerCheck.Monitoring.Alerting;
using TriggerCheck.Monitoring.Checks;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Tests.Monitoring;

/// <summary>
/// DI container wired exactly like production (AddInfrastructure + AddMonitoringEngine) on a fresh SQLite file,
/// with a <see cref="FakeTimeProvider"/>. Use <paramref name="configure"/> to swap checkers/notifiers for fakes.
/// </summary>
public sealed class MonitoringTestHost : IAsyncDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dbPath;
    public FakeTimeProvider Time { get; }
    public ServiceProvider Services { get; }

    private MonitoringTestHost(string dbPath, FakeTimeProvider time, ServiceProvider services)
    {
        _dbPath = dbPath;
        Time = time;
        Services = services;
    }

    public DateTime Now => Time.GetUtcNow().UtcDateTime;

    public static async Task<MonitoringTestHost> CreateAsync(Action<IServiceCollection>? configure = null, Dictionary<string, string?>? settings = null)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"triggercheck-mon-{Guid.NewGuid():N}.db");
        var all = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = $"Data Source={dbPath}",
            ["Monitoring:Enabled"] = "false",
            ["Monitoring:MaxConcurrency"] = "4",
        };
        foreach (var kv in settings ?? new()) all[kv.Key] = kv.Value;
        var config = new ConfigurationBuilder().AddInMemoryCollection(all).Build();

        var time = new FakeTimeProvider(Start);
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<TimeProvider>(time);
        services.AddInfrastructure(config);
        services.AddMonitoringEngine(config);
        configure?.Invoke(services);

        var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync(); // same startup steps as DbSeeder
            await db.EnableSqliteWalAsync();
        }
        return new MonitoringTestHost(dbPath, time, sp);
    }

    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public Task WithDbAsync(Func<AppDbContext, Task> action) => WithDbAsync(async db => { await action(db); return 0; });

    public Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action) => InScope(action);

    private async Task<T> InScope<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    public Task<CheckResult> RunCheckAsync(int monitorId) =>
        InScope(sp => sp.GetRequiredService<IMonitorCheckRunner>().RunAsync(monitorId));

    public Task<Customer> AddCustomerAsync(bool isActive = true, int retentionDays = 30, string contactEmail = "ops@example.test") =>
        WithDbAsync(async db =>
        {
            var plan = new Plan { Name = "P-" + Guid.NewGuid().ToString("N")[..8], MinIntervalSeconds = 15, MaxMonitors = 100, RetentionDays = retentionDays };
            var c = new Customer { Name = "C", ContactEmail = contactEmail, Plan = plan, IsActive = isActive };
            db.Customers.Add(c);
            await db.SaveChangesAsync();
            return c;
        });

    public Task<MonitorEntity> AddMonitorAsync(int customerId, Action<MonitorEntity>? configure = null) =>
        WithDbAsync(async db =>
        {
            var m = new MonitorEntity { CustomerId = customerId, Name = "M", Type = MonitorType.Http, Target = "http://127.0.0.1/", IntervalSeconds = 60, FailureThreshold = 2 };
            configure?.Invoke(m);
            db.Monitors.Add(m);
            await db.SaveChangesAsync();
            return m;
        });

    public Task<MonitorEntity> GetMonitorAsync(int id) =>
        WithDbAsync(db => db.Monitors.AsNoTracking().SingleAsync(m => m.Id == id));

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* ignore */ }
    }
}

/// <summary>Checker returning scripted outcomes (last one repeats).</summary>
public sealed class ScriptedChecker(MonitorType type) : IMonitorChecker
{
    private readonly ConcurrentQueue<CheckOutcome> _queue = new();
    private CheckOutcome _last = CheckOutcome.Up(5, 200);
    public int Calls;

    public MonitorType Type => type;

    public ScriptedChecker Then(params CheckOutcome[] outcomes)
    {
        foreach (var o in outcomes) _queue.Enqueue(o);
        return this;
    }

    public Task<CheckOutcome> CheckAsync(MonitorEntity monitor, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        if (_queue.TryDequeue(out var o)) _last = o;
        return Task.FromResult(_last);
    }
}

public sealed class RecordingNotifier(AlertChannelType type, Func<string, NotifyResult>? behavior = null) : INotifier
{
    public ConcurrentQueue<(string Target, AlertMessage Message)> Sent { get; } = new();
    public AlertChannelType Type => type;

    public Task<NotifyResult> SendAsync(string target, AlertMessage message, CancellationToken ct)
    {
        Sent.Enqueue((target, message));
        return Task.FromResult(behavior?.Invoke(target) ?? NotifyResult.Ok);
    }
}

public static class TestServiceCollectionExtensions
{
    public static IServiceCollection ReplaceCheckers(this IServiceCollection services, params IMonitorChecker[] checkers)
    {
        services.RemoveAll<IMonitorChecker>();
        foreach (var c in checkers) services.AddSingleton(c);
        return services;
    }

    public static IServiceCollection ReplaceNotifiers(this IServiceCollection services, params INotifier[] notifiers)
    {
        services.RemoveAll<INotifier>();
        foreach (var n in notifiers) services.AddSingleton(n);
        return services;
    }
}

/// <summary>Minimal real Kestrel server on a random loopback port.</summary>
public sealed class LocalHttpServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    public string BaseUrl { get; }

    private LocalHttpServer(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl.TrimEnd('/');
    }

    public static async Task<LocalHttpServer> StartAsync(Action<WebApplication> map)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        map(app);
        await app.StartAsync();
        return new LocalHttpServer(app, app.Urls.First());
    }

    public string Url(string path) => BaseUrl + path;

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
