using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Tests.Reports;
using TriggerCheck.Web.StatusPages;

namespace TriggerCheck.Tests.StatusPages;

/// <summary>Status page host: fake clock, jobs off, no output cache, and a controllable custom-domain verifier.</summary>
public class StatusPageTestFactory : ReportsTestFactory
{
    public FakeDomainVerifier Verifier { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<ICustomDomainVerifier>();
            s.AddSingleton<ICustomDomainVerifier>(Verifier);
        });
    }

    /// <summary>Drops cached public responses and the custom domain map (after arranging data directly in the database).</summary>
    public async Task EvictAsync()
    {
        await Services.GetRequiredService<IOutputCacheStore>().EvictByTagAsync(StatusPageSetup.CacheTag, default);
        Services.GetRequiredService<CustomDomainMap>().Invalidate();
    }
}

/// <summary>Output cache on (30 s).</summary>
public class CachedStatusPageTestFactory : StatusPageTestFactory
{
    protected override int CacheSeconds => 30;
}

/// <summary>Small per-client limits (all test requests share one client partition).</summary>
public class RateLimitedStatusPageTestFactory : StatusPageTestFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("StatusPages:RequestsPerMinutePerClient", "8");
        builder.UseSetting("StatusPages:SubscriptionsPerHourPerClient", "2");
    }
}

public sealed class FakeDomainVerifier : ICustomDomainVerifier
{
    public bool Result { get; set; }
    public List<(string Domain, string Token)> Calls { get; } = [];

    public Task<bool> VerifyAsync(string domain, string token, CancellationToken ct)
    {
        lock (Calls) Calls.Add((domain, token));
        return Task.FromResult(Result);
    }
}

public static class StatusPageTestData
{
    /// <summary>A page with the given monitors (display name = "Public " + index).</summary>
    public static Task<int> AddStatusPageAsync(this TestWebAppFactory f, int customerId, string slug, int[] monitorIds,
        Action<StatusPage>? configure = null) =>
        f.WithDbAsync(async db =>
        {
            var p = new StatusPage { CustomerId = customerId, Title = "Title " + slug, Slug = slug, TimeZoneId = "UTC", CreatedAtUtc = new DateTime(2026, 1, 1) };
            for (var i = 0; i < monitorIds.Length; i++)
                p.Monitors.Add(new StatusPageMonitor { MonitorId = monitorIds[i], DisplayName = "Public " + i, SortOrder = i });
            configure?.Invoke(p);
            db.StatusPages.Add(p);
            await db.SaveChangesAsync();
            return p.Id;
        });
}
