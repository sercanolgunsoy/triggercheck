using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TriggerCheck.Domain.Reporting;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Reporting;
using TriggerCheck.Tests.App;
using TriggerCheck.Web.Reporting;
using static TriggerCheck.Tests.Reports.ReportTestData;

namespace TriggerCheck.Tests.Reports;

/// <summary>Report builder that fails for one customer (simulates a crash between the idempotency check and sending).</summary>
public sealed class FailingSlaReportService(AppDbContext db, TimeProvider time) : SlaReportService(db, time)
{
    public static volatile int FailFor;

    public override Task<SlaReport?> BuildAsync(int customerId, int year, int month, string? timeZoneId, CancellationToken ct = default) =>
        customerId == FailFor ? throw new InvalidOperationException("simulated failure") : base.BuildAsync(customerId, year, month, timeZoneId, ct);
}

public class FailingReportsTestFactory : ReportsTestFactory
{
    protected override void ConfigureServices(IServiceCollection services)
    {
        services.RemoveAll<SlaReportService>();
        services.AddScoped<SlaReportService, FailingSlaReportService>();
    }
}

/// <summary>FEAT-10 review fixes: DST month boundaries, retry after a failed run, parallel mailers, cached reports.</summary>
public class SlaReportHardeningTests(FailingReportsTestFactory f) : IClassFixture<FailingReportsTestFactory>
{
    private async Task<int> RunMailerAsync()
    {
        using var scope = f.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MonthlyReportMailer>().RunAsync(default);
    }

    [Fact]
    public async Task Month_boundaries_follow_daylight_saving_time()
    {
        var (cid, _, _) = await f.AddTenantAsync("dst");
        var m = await f.AddMonitorAsync(cid, "M", x => x.CreatedAtUtc = Utc(2026, 1, 1));
        using var scope = f.Services.CreateScope();
        var r = (await scope.ServiceProvider.GetRequiredService<SlaReportService>().BuildAsync(cid, 2026, 3, "Europe/Berlin"))!;
        Assert.Equal(Utc(2026, 2, 28, 23), r.PeriodStartUtc); // CET, UTC+1
        Assert.Equal(Utc(2026, 3, 31, 22), r.PeriodEndUtc);   // CEST, UTC+2
        Assert.Equal((31 * 24 - 1) * 60, r.Monitors.Single(x => x.MonitorId == m).MonitoredMinutes);
    }

    [Fact]
    public async Task A_failed_run_claims_nothing_and_the_next_run_sends_once()
    {
        var (cid, admin, _) = await f.AddTenantAsync("retry");
        await f.WithDbAsync(db => db.Customers.Where(c => c.Id == cid).ExecuteUpdateAsync(u => u.SetProperty(c => c.MonthlyReportEmail, true)));

        FailingSlaReportService.FailFor = cid;
        await RunMailerAsync();
        Assert.False(await f.WithDbAsync(db => db.MonthlyReportDeliveries.AnyAsync(d => d.CustomerId == cid)));
        Assert.False(await f.WithDbAsync(db => db.EmailOutbox.AnyAsync(x => x.To == admin)));

        FailingSlaReportService.FailFor = 0;
        await RunMailerAsync();
        await RunMailerAsync();
        Assert.Equal(1, await f.WithDbAsync(db => db.MonthlyReportDeliveries.CountAsync(d => d.CustomerId == cid)));
        Assert.Equal(1, await f.WithDbAsync(db => db.EmailOutbox.CountAsync(x => x.To == admin)));
    }

    [Fact]
    public async Task Parallel_mailers_send_once()
    {
        var (cid, admin, _) = await f.AddTenantAsync("parallel");
        await f.WithDbAsync(db => db.Customers.Where(c => c.Id == cid).ExecuteUpdateAsync(u => u.SetProperty(c => c.MonthlyReportEmail, true)));
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(RunMailerAsync)));
        Assert.Equal(1, await f.WithDbAsync(db => db.EmailOutbox.CountAsync(x => x.To == admin)));
        Assert.Equal(1, await f.WithDbAsync(db => db.MonthlyReportDeliveries.CountAsync(d => d.CustomerId == cid)));
    }

    [Fact]
    public async Task Closed_months_are_served_from_the_cache()
    {
        var (cid, admin, _) = await f.AddTenantAsync("cached");
        var m = await f.AddMonitorAsync(cid, "Cached monitor", x => x.CreatedAtUtc = Utc(2026, 1, 1));
        var c = await f.LoginAsync(admin, Password);
        Assert.Contains("Cached monitor", await c.GetHtmlAsync("/App/Reports?month=2026-03"));
        await f.WithDbAsync(db => db.Monitors.Where(x => x.Id == m).ExecuteUpdateAsync(u => u.SetProperty(x => x.Name, "Renamed")));
        Assert.Contains("Cached monitor", await c.GetHtmlAsync("/App/Reports?month=2026-03"));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/App/Reports?handler=Pdf&month=2026-03")).StatusCode);
    }
}
