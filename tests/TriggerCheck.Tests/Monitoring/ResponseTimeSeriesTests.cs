using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring.Stats;

namespace TriggerCheck.Tests.Monitoring;

/// <summary>UI-4: server-side bucketing of the response-time chart.</summary>
public class ResponseTimeSeriesTests
{
    private static Task<ResponseTimeSeries> GetAsync(MonitoringTestHost host, int monitorId, int customerId, DateTime from, DateTime to, int maxPoints = 300) =>
        host.InScopeAsync(sp => sp.GetRequiredService<IResponseTimeSeriesService>().GetAsync(monitorId, customerId, from, to, maxPoints));

    [Theory]
    [InlineData(24)]
    [InlineData(24 * 7)]
    [InlineData(24 * 30)]
    public async Task Ranges_are_bucketed_to_at_most_300_points(int hours)
    {
        await using var host = await MonitoringTestHost.CreateAsync();
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id);
        var to = host.Now;
        var from = to.AddHours(-hours);
        // One check every 2 minutes over the whole range (up to 21600 rows for 30 days).
        await host.WithDbAsync(async db =>
        {
            for (var t = from; t <= to; t = t.AddMinutes(2))
                db.CheckResults.Add(new CheckResult { MonitorId = monitor.Id, CheckedAtUtc = t, IsUp = true, ResponseTimeMs = 100 });
            await db.SaveChangesAsync();
        });

        var s = await GetAsync(host, monitor.Id, customer.Id, from, to);
        Assert.True(s.Buckets.Count <= 300);
        Assert.True(s.Buckets.Count >= 290);
        Assert.Equal(hours * 30 + 1, s.Buckets.Sum(b => b.Checks)); // every check lands in exactly one bucket
        Assert.All(s.Buckets.Where(b => b.Checks > 0), b => Assert.Equal(100, b.AvgMs));
        Assert.Equal(from, s.Buckets[0].StartUtc);
        Assert.True(s.Buckets[^1].StartUtc + s.BucketSize >= to);
    }

    [Fact]
    public async Task Bucket_has_avg_p95_failures_and_phases()
    {
        await using var host = await MonitoringTestHost.CreateAsync();
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id);
        var from = host.Now.AddHours(-1);
        await host.WithDbAsync(async db =>
        {
            // Bucket 0 (12 minute buckets for 1h / 5 points): 20 successful checks 10..200 ms, 2 failures.
            for (var i = 1; i <= 20; i++)
                db.CheckResults.Add(new CheckResult { MonitorId = monitor.Id, CheckedAtUtc = from.AddSeconds(i * 10), IsUp = true, ResponseTimeMs = i * 10 });
            db.CheckResults.Add(new CheckResult { MonitorId = monitor.Id, CheckedAtUtc = from.AddMinutes(5), IsUp = false, ResponseTimeMs = 10000, Error = "Timeout" });
            db.CheckResults.Add(new CheckResult { MonitorId = monitor.Id, CheckedAtUtc = from.AddMinutes(6), IsUp = false, ResponseTimeMs = 10000, Error = "Timeout" });
            // Bucket 2: one new connection and one reused connection.
            db.CheckResults.Add(new CheckResult { MonitorId = monitor.Id, CheckedAtUtc = from.AddMinutes(25), IsUp = true, ResponseTimeMs = 200, DnsMs = 20, ConnectMs = 10, TlsMs = 30, TtfbMs = 160 });
            db.CheckResults.Add(new CheckResult { MonitorId = monitor.Id, CheckedAtUtc = from.AddMinutes(26), IsUp = true, ResponseTimeMs = 100, TtfbMs = 80 });
            await db.SaveChangesAsync();
        });

        var s = await GetAsync(host, monitor.Id, customer.Id, from, host.Now, maxPoints: 5);
        Assert.Equal(5, s.Buckets.Count);
        Assert.Equal(TimeSpan.FromMinutes(12), s.BucketSize);

        var b0 = s.Buckets[0];
        Assert.Equal(22, b0.Checks);
        Assert.Equal(2, b0.Failures);
        Assert.Equal(105, b0.AvgMs);  // failures are excluded from response-time figures
        Assert.Equal(190, b0.P95Ms);  // nearest rank: ceil(0.95 * 20) = 19th value
        Assert.Null(b0.DnsMs);        // no timing data in this bucket

        Assert.Equal(0, s.Buckets[1].Checks);
        Assert.Null(s.Buckets[1].AvgMs);

        var b2 = s.Buckets[2];
        Assert.Equal(150, b2.AvgMs);
        Assert.Equal(10, b2.DnsMs);     // (20 + 0 reused) / 2
        Assert.Equal(5, b2.ConnectMs);
        Assert.Equal(15, b2.TlsMs);
        Assert.Equal(90, b2.WaitMs);    // ((160-60) + 80) / 2
        Assert.Equal(30, b2.TransferMs); // ((200-160) + (100-80)) / 2
        Assert.Equal(150, b2.DnsMs + b2.ConnectMs + b2.TlsMs + b2.WaitMs + b2.TransferMs);

        Assert.Equal(2, s.BucketIndexOf(from.AddMinutes(25)));
        Assert.Equal(4, s.BucketIndexOf(host.Now));
        Assert.Null(s.BucketIndexOf(from.AddMinutes(-1)));
    }

    [Fact]
    public async Task Large_ranges_use_a_sampled_p95_and_exact_aggregates()
    {
        await using var host = await MonitoringTestHost.CreateAsync();
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id);
        var from = host.Now.AddHours(-10);
        const int rows = ResponseTimeSeriesService.ExactP95MaxRows + 10_000;
        await host.WithDbAsync(async db =>
        {
            for (var i = 0; i < rows; i++) // response times cycle 1..100 ms within every bucket
                db.CheckResults.Add(new CheckResult { MonitorId = monitor.Id, CheckedAtUtc = from.AddSeconds(i), IsUp = true, ResponseTimeMs = i % 100 + 1 });
            await db.SaveChangesAsync();
        });

        var s = await GetAsync(host, monitor.Id, customer.Id, from, host.Now, maxPoints: 10);
        Assert.True(s.P95Approximate);
        Assert.Equal(rows, s.Buckets.Sum(b => b.Checks));
        Assert.All(s.Buckets.Where(b => b.Checks > 0), b =>
        {
            Assert.Equal(50.5, b.AvgMs!.Value, 0.5);  // exact (SQL)
            Assert.InRange(b.P95Ms!.Value, 90, 100);   // exact would be 95
        });

        var small = await GetAsync(host, monitor.Id, customer.Id, from, from.AddMinutes(30), maxPoints: 1);
        Assert.False(small.P95Approximate);
        Assert.Equal(95, small.Buckets[0].P95Ms);
    }

    [Fact]
    public async Task Recent_series_is_cached_per_monitor_and_range()
    {
        await using var host = await MonitoringTestHost.CreateAsync();
        var customer = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(customer.Id);
        Task<ResponseTimeSeries> Recent(TimeSpan range) =>
            host.InScopeAsync(sp => sp.GetRequiredService<IResponseTimeSeriesService>().GetRecentAsync(monitor.Id, customer.Id, range));

        var first = await Recent(TimeSpan.FromHours(24));
        Assert.Same(first, await Recent(TimeSpan.FromHours(24)));
        Assert.NotSame(first, await Recent(TimeSpan.FromDays(7)));
    }

    [Fact]
    public async Task Another_customers_monitor_yields_no_data()
    {
        await using var host = await MonitoringTestHost.CreateAsync();
        var owner = await host.AddCustomerAsync();
        var other = await host.AddCustomerAsync();
        var monitor = await host.AddMonitorAsync(owner.Id);
        await host.WithDbAsync(async db =>
        {
            db.CheckResults.Add(new CheckResult { MonitorId = monitor.Id, CheckedAtUtc = host.Now.AddMinutes(-5), IsUp = true, ResponseTimeMs = 50 });
            await db.SaveChangesAsync();
        });

        var mine = await GetAsync(host, monitor.Id, owner.Id, host.Now.AddHours(-24), host.Now);
        Assert.Equal(1, mine.Buckets.Sum(b => b.Checks));
        var theirs = await GetAsync(host, monitor.Id, other.Id, host.Now.AddHours(-24), host.Now);
        Assert.Equal(0, theirs.Buckets.Sum(b => b.Checks));
    }
}
