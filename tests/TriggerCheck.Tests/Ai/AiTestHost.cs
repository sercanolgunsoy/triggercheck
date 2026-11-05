using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TriggerCheck.Ai;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Context;
using TriggerCheck.Ai.Execution;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Tests.Infrastructure;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Tests.Ai;

/// <summary>
/// DI container wired like production (AddInfrastructure + AddAi) on a fresh SQLite file, with a
/// <see cref="FakeAiClient"/> (no key, no network) and a <see cref="FakeTimeProvider"/>. AI is enabled unless
/// <c>Ai:Enabled</c> is overridden. Hosted services are not started (tests drive the worker / job directly).
/// </summary>
public sealed class AiTestHost : IAsyncDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly TestDatabase _db;
    public FakeTimeProvider Time { get; }
    public ServiceProvider Services { get; }
    public FakeAiClient Client { get; }
    public TestContextProvider Provider { get; }

    private AiTestHost(TestDatabase db, FakeTimeProvider time, ServiceProvider services, FakeAiClient client, TestContextProvider provider)
    {
        _db = db;
        Time = time;
        Services = services;
        Client = client;
        Provider = provider;
    }

    public DateTime Now => Time.GetUtcNow().UtcDateTime;

    public static async Task<AiTestHost> CreateAsync(Dictionary<string, string?>? settings = null, IAiClient? client = null, Action<IServiceCollection>? configure = null)
    {
        // SQLite without pooling: the file can be deleted on dispose without SqliteConnection.ClearAllPools(), which
        // would also close pooled connections of tests running in parallel.
        var tdb = TestDatabase.Create("ai", sqliteOptions: "Pooling=False");
        var all = new Dictionary<string, string?>(tdb.Settings)
        {
            ["Ai:Enabled"] = "true",
            ["Ai:ApiKey"] = "", // never a real key in tests
        };
        foreach (var kv in settings ?? new()) all[kv.Key] = kv.Value;
        var config = new ConfigurationBuilder().AddInMemoryCollection(all).Build();

        var time = new FakeTimeProvider(Start);
        var fake = new FakeAiClient();
        var provider = new TestContextProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<TimeProvider>(time);
        services.AddInfrastructure(config);
        services.AddAi(config);
        services.RemoveAll<IAiClient>();
        services.AddSingleton(client ?? fake);
        services.AddSingleton<IAiContextProvider>(provider);
        configure?.Invoke(services);

        var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        provider.Services = sp;
        using (var scope = sp.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
        return new AiTestHost(tdb, time, sp, fake, provider);
    }

    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public Task WithDbAsync(Func<AppDbContext, Task> action) => WithDbAsync(async db => { await action(db); return 0; });

    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    public Task<AiDecision?> RunAsync(AiJob job) => InScopeAsync(sp => sp.GetRequiredService<IAiInterpreter>().RunAsync(job));

    /// <summary>Customer on a plan that includes every AI feature (100 calls / month unless overridden).</summary>
    public Task<Customer> AddCustomerAsync(bool aiEnabled = true, int callLimit = 100, long tokenLimit = 0, string? features = null) =>
        WithDbAsync(async db =>
        {
            var plan = new Plan
            {
                Name = "P-" + Guid.NewGuid().ToString("N")[..8], MinIntervalSeconds = 15, MaxMonitors = 100,
                AiFeatures = features ?? AiFeatures.Format(AiFeatures.All.Select(f => f.Name)),
                AiMonthlyCallLimit = callLimit, AiMonthlyTokenLimit = tokenLimit
            };
            var c = new Customer { Name = "C-" + Guid.NewGuid().ToString("N")[..6], ContactEmail = "ops@example.test", Plan = plan, AiEnabled = aiEnabled };
            db.Customers.Add(c);
            await db.SaveChangesAsync();
            return c;
        });

    public Task<MonitorEntity> AddMonitorAsync(int customerId, Action<MonitorEntity>? configure = null) =>
        WithDbAsync(async db =>
        {
            var m = new MonitorEntity { CustomerId = customerId, Name = "M", Type = MonitorType.Http, Target = "https://api.example.test/health", IntervalSeconds = 60 };
            configure?.Invoke(m);
            db.Monitors.Add(m);
            await db.SaveChangesAsync();
            return m;
        });

    public static AiJob Job(int customerId, string subjectId = "1", string feature = AiFeatures.IncidentInterpretation) =>
        new(feature, customerId, "Monitor", subjectId);

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        _db.Dispose();
    }
}

/// <summary>
/// Context provider for <see cref="AiFeatures.IncidentInterpretation"/> used by the tests: loads the monitor through
/// the tenant-scoped <see cref="AiTenantData"/> (subject id = monitor id) and adds its last error as untrusted data.
/// </summary>
public sealed class TestContextProvider : IAiContextProvider
{
    public string Feature => AiFeatures.IncidentInterpretation;
    public IReadOnlyList<string> AllowedActions { get; set; } = ["increase_failure_threshold"];
    /// <summary>Changes the input (and so the InputHash) between runs.</summary>
    public string Extra { get; set; } = "";
    public IServiceProvider? Services { get; set; }

    public async Task<AiPrompt?> BuildAsync(AiJob job, CancellationToken ct)
    {
        // The provider is a singleton in the test host; resolve scoped data access per call.
        using var scope = Services!.CreateScope();
        var data = scope.ServiceProvider.GetRequiredService<AiTenantData>();
        var monitor = await data.MonitorAsync(job.CustomerId, int.Parse(job.SubjectId), ct);
        if (monitor is null) return null;
        var content = new AiContextBuilder(job.Language)
            .Section("Monitor")
            .Fact("type", monitor.Type)
            .Fact("target", AiMasker.MaskTarget(monitor.Type, monitor.Target))
            .Fact("extra", Extra)
            .Untrusted("last_error", monitor.LastError)
            .Build();
        return new AiPrompt("Explain the monitor state.", content, AllowedActions);
    }
}
