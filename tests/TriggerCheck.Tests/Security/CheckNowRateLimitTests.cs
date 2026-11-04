using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Web.Infrastructure;
using static TriggerCheck.Web.Infrastructure.ManualCheckLimiter;

namespace TriggerCheck.Tests.Security;

public class ManualCheckLimiterTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    [Theory]
    [InlineData(15, 30)]
    [InlineData(30, 30)]
    [InlineData(60, 60)]
    [InlineData(300, 300)]
    public void Cooldown_is_plan_minimum_or_30_seconds(int planMin, int expected) =>
        Assert.Equal(TimeSpan.FromSeconds(expected), CooldownFor(planMin));

    [Fact]
    public void Recently_checked_monitor_is_in_cooldown()
    {
        var limiter = new ManualCheckLimiter(_time);
        var d = limiter.TryAcquire(1, 10, Now.AddSeconds(-20), planMinIntervalSeconds: 60);
        Assert.Equal(Denial.MonitorCooldown, d.Denial);
        Assert.Equal(40, d.RetryAfterSeconds);

        Assert.True(limiter.TryAcquire(1, 10, Now.AddSeconds(-61), 60).Allowed);
    }

    [Fact]
    public void Second_manual_check_waits_for_the_cooldown_even_before_the_result_is_saved()
    {
        var limiter = new ManualCheckLimiter(_time);
        Assert.True(limiter.TryAcquire(1, 10, null, 15).Allowed);
        Assert.Equal(Denial.MonitorCooldown, limiter.TryAcquire(1, 10, null, 15).Denial);
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.True(limiter.TryAcquire(1, 10, null, 15).Allowed);
    }

    [Fact]
    public void Customer_quota_is_per_minute_and_per_customer()
    {
        var limiter = new ManualCheckLimiter(_time);
        for (var i = 0; i < PerCustomerPerMinute; i++)
            Assert.True(limiter.TryAcquire(1, 100 + i, null, 15).Allowed);

        var denied = limiter.TryAcquire(1, 999, null, 15);
        Assert.Equal(Denial.CustomerQuota, denied.Denial);
        Assert.Equal(60, denied.RetryAfterSeconds);

        Assert.True(limiter.TryAcquire(2, 500, null, 15).Allowed);               // other customer unaffected
        Assert.True(limiter.TryAcquire(1, 998, null, 15, bypassCustomerQuota: true).Allowed);

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(limiter.TryAcquire(1, 999, null, 15).Allowed);
    }
}

public class CheckNowRateLimitTests : IAsyncLifetime
{
    private readonly CountingRunner _runner = new();
    private TestWebAppFactory _base = null!;
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _f = null!;

    public Task InitializeAsync()
    {
        _base = new TestWebAppFactory();
        _f = _base.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IMonitorCheckRunner>();
            s.AddSingleton<IMonitorCheckRunner>(_runner);
        }));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _f.DisposeAsync();
        await _base.DisposeAsync();
    }

    private async Task<HttpClient> AdminAsync()
    {
        var client = _f.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });
        var r = await client.PostFormAsync("/Account/Login", new() { ["Input.Email"] = TestWebAppFactory.DemoAdminEmail, ["Input.Password"] = TestWebAppFactory.DemoAdminPassword });
        Assert.Equal(System.Net.HttpStatusCode.Redirect, r.StatusCode);
        return client;
    }

    private async Task<int> AddMonitorAsync(DateTime? lastChecked)
    {
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TriggerCheck.Infrastructure.Data.AppDbContext>();
        var cid = db.Users.Where(u => u.Email == TestWebAppFactory.DemoAdminEmail).Select(u => u.CustomerId!.Value).First();
        var m = new TriggerCheck.Domain.Entities.Monitor
        {
            CustomerId = cid, Name = "Rate-" + Guid.NewGuid().ToString("N")[..6], Type = MonitorType.Http, Target = "https://example.org/",
            IntervalSeconds = 300, Status = MonitorStatus.Up, LastCheckedAtUtc = lastChecked, NextCheckAtUtc = DateTime.UtcNow.AddHours(1)
        };
        db.Monitors.Add(m);
        await db.SaveChangesAsync();
        return m.Id;
    }

    private static async Task<string> CheckNowAsync(HttpClient c, int id)
    {
        var url = $"/App/Monitors/Details/{id}";
        var r = await c.PostFormAsync(url + "?handler=CheckNow", new(), url);
        AppTestData.AssertRedirect(r, url);
        return await c.GetHtmlAsync(url);
    }

    [Fact]
    public async Task Check_within_cooldown_is_refused_without_probing()
    {
        var c = await AdminAsync();
        var id = await AddMonitorAsync(DateTime.UtcNow.AddSeconds(-5));

        var html = await CheckNowAsync(c, id);

        Assert.Contains("This monitor was checked moments ago. Try again in", html);
        Assert.Equal(0, _runner.Calls(id));
    }

    [Fact]
    public async Task First_check_runs_and_an_immediate_second_one_is_refused()
    {
        var c = await AdminAsync();
        var id = await AddMonitorAsync(DateTime.UtcNow.AddMinutes(-10));

        Assert.Contains("Check succeeded", await CheckNowAsync(c, id));
        Assert.Contains("This monitor was checked moments ago.", await CheckNowAsync(c, id));
        Assert.Equal(1, _runner.Calls(id));
    }

    [Fact]
    public async Task Cooldown_message_is_localized()
    {
        var c = await AdminAsync();
        await c.SetLanguageAsync("tr");
        var id = await AddMonitorAsync(DateTime.UtcNow);
        Assert.Contains("Bu monitör az önce kontrol edildi.", await CheckNowAsync(c, id));
    }

    private sealed class CountingRunner : IMonitorCheckRunner
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> _calls = new();
        public int Calls(int id) => _calls.GetValueOrDefault(id);

        public Task<CheckResult> RunAsync(int monitorId, CancellationToken ct = default)
        {
            _calls.AddOrUpdate(monitorId, 1, (_, n) => n + 1);
            return Task.FromResult(new CheckResult { MonitorId = monitorId, CheckedAtUtc = DateTime.UtcNow, IsUp = true, ResponseTimeMs = 5, StatusCode = 200 });
        }
    }
}
