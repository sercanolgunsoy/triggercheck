using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using TriggerCheck.Tests.Infrastructure;

namespace TriggerCheck.Tests.Reports;

/// <summary>
/// Host for report and status page tests: a FakeTimeProvider (default 2026-04-02 10:00 UTC, so March 2026 is the
/// previous month), the background jobs off (tests run them directly) and no output caching.
/// </summary>
public class ReportsTestFactory : TestWebAppFactory
{
    public static readonly DateTimeOffset DefaultNow = new(2026, 4, 2, 10, 0, 0, TimeSpan.Zero);
    public FakeTimeProvider Time { get; } = new(DefaultNow);
    public DateTime Now => Time.GetUtcNow().UtcDateTime;

    protected virtual int CacheSeconds => 0;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Reports:JobIntervalMinutes", "0");
        builder.UseSetting("StatusPages:NotifyIntervalSeconds", "0");
        builder.UseSetting("StatusPages:CacheSeconds", CacheSeconds.ToString());
        builder.UseSetting("Billing:MaintenanceIntervalMinutes", "0");
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Time);
        });
    }
}
