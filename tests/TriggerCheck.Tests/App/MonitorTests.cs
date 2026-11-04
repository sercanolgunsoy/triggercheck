using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Tests.Monitoring;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.Infrastructure;
using static TriggerCheck.Tests.App.AppTestData;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Tests.App;

public class MonitorTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _f;
    public MonitorTests(TestWebAppFactory f) => _f = f;

    private Task<HttpClient> AdminAsync() => _f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);

    private Task<MonitorEntityInfo?> FindByNameAsync(string name) =>
        _f.WithDbAsync(db => db.Monitors.AsNoTracking().Where(m => m.Name == name)
            .Select(m => new MonitorEntityInfo(m.Id, m.CustomerId, m.IntervalSeconds, m.NextCheckAtUtc, m.Type, m.Port, m.Keyword)).FirstOrDefaultAsync());

    private record MonitorEntityInfo(int Id, int CustomerId, int IntervalSeconds, DateTime? NextCheckAtUtc, MonitorType Type, int? Port, string? Keyword);

    [Fact]
    public async Task Create_monitor_happy_path()
    {
        var c = await AdminAsync();
        var before = DateTime.UtcNow.AddSeconds(-1);
        var r = await c.PostFormAsync("/App/Monitors/Create", MonitorForm("Happy"));
        AssertRedirect(r, "/App/Monitors/Details/");

        var m = await FindByNameAsync("Happy");
        Assert.NotNull(m);
        Assert.Equal(await _f.DemoCustomerIdAsync(), m!.CustomerId);
        Assert.Equal(60, m.IntervalSeconds);
        Assert.NotNull(m.NextCheckAtUtc);
        Assert.True(m.NextCheckAtUtc >= before && m.NextCheckAtUtc <= DateTime.UtcNow.AddSeconds(1));

        var details = await c.GetAsync(r.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        Assert.Contains("Happy", await details.ReadHtmlAsync());
    }

    [Fact]
    public async Task Create_form_offers_only_plan_intervals()
    {
        var page = await (await AdminAsync()).GetHtmlAsync("/App/Monitors/Create");
        var html = System.Text.RegularExpressions.Regex.Match(page, "<select[^>]*name=\"Input.IntervalSeconds\"[\\s\\S]*?</select>").Value;
        Assert.Contains("value=\"60\"", html);
        Assert.Contains("value=\"300\"", html);
        Assert.DoesNotContain("value=\"30\"", html);
        Assert.DoesNotContain("value=\"15\"", html);
    }

    [Theory]
    [InlineData(30, false)]
    [InlineData(15, false)]
    [InlineData(45, false)]
    [InlineData(60, true)]
    [InlineData(300, true)]
    public async Task Interval_must_be_allowed_by_plan(int interval, bool allowed)
    {
        var name = $"Interval-{interval}";
        var r = await (await AdminAsync()).PostFormAsync("/App/Monitors/Create",
            MonitorForm(name, ("IntervalSeconds", interval.ToString()), ("TimeoutSeconds", "5")));
        if (allowed)
        {
            AssertRedirect(r, "/App/Monitors/Details/");
            Assert.Equal(interval, (await FindByNameAsync(name))!.IntervalSeconds);
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Contains("This check interval is not available on your plan (Standard). Allowed: 5 min, 1 min.", await r.ReadHtmlAsync());
            Assert.Null(await FindByNameAsync(name));
        }
    }

    [Theory]
    [InlineData("Http", "example.com", null, null)]
    [InlineData("Http", "ftp://example.com", null, null)]
    [InlineData("Http", "not a url", null, null)]
    [InlineData("Keyword", "https://example.com", null, null)]            // keyword missing
    [InlineData("Tcp", "example.com", null, null)]                         // port missing
    [InlineData("Tcp", "example.com", "70000", null)]                      // port out of range
    [InlineData("Tcp", "https://example.com", "443", null)]                // scheme not allowed
    [InlineData("Ping", "http://example.com", null, null)]
    public async Task Invalid_input_is_rejected(string type, string target, string? port, string? keyword)
    {
        var name = $"Invalid-{Guid.NewGuid():N}";
        var form = MonitorForm(name, ("Type", type), ("Target", target));
        if (port is not null) form["Input.Port"] = port;
        if (keyword is not null) form["Input.Keyword"] = keyword;

        var r = await (await AdminAsync()).PostFormAsync("/App/Monitors/Create", form);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("field-validation-error", await r.ReadHtmlAsync());
        Assert.Null(await FindByNameAsync(name));
    }

    [Fact]
    public async Task Bad_url_shows_error()
    {
        var r = await (await AdminAsync()).PostFormAsync("/App/Monitors/Create", MonitorForm("BadUrl", ("Target", "example.com")));
        var html = await r.ReadHtmlAsync();
        Assert.Contains("The target must be a valid URL starting with http:// or https://.", html);
    }

    [Theory]
    [InlineData("60", "60")]   // timeout must be < interval
    [InlineData("0", "60")]
    [InlineData("61", "300")]
    public async Task Timeout_is_validated(string timeout, string interval)
    {
        var name = $"Timeout-{Guid.NewGuid():N}";
        var r = await (await AdminAsync()).PostFormAsync("/App/Monitors/Create",
            MonitorForm(name, ("TimeoutSeconds", timeout), ("IntervalSeconds", interval)));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Null(await FindByNameAsync(name));
    }

    [Fact]
    public async Task Tcp_and_keyword_monitors_store_type_specific_fields()
    {
        var c = await AdminAsync();
        AssertRedirect(await c.PostFormAsync("/App/Monitors/Create",
            MonitorForm("TcpOk", ("Type", "Tcp"), ("Target", "db.example.com"), ("Port", "5432"), ("Keyword", "ignored"))), "/Details/");
        AssertRedirect(await c.PostFormAsync("/App/Monitors/Create",
            MonitorForm("KwOk", ("Type", "Keyword"), ("Target", "https://example.com"), ("Keyword", "Example Domain"), ("Port", "1"))), "/Details/");
        AssertRedirect(await c.PostFormAsync("/App/Monitors/Create",
            MonitorForm("PingOk", ("Type", "Ping"), ("Target", "10.0.0.1"))), "/Details/");

        var tcp = (await FindByNameAsync("TcpOk"))!;
        Assert.Equal(MonitorType.Tcp, tcp.Type);
        Assert.Equal(5432, tcp.Port);
        Assert.Null(tcp.Keyword);

        var kw = (await FindByNameAsync("KwOk"))!;
        Assert.Equal("Example Domain", kw.Keyword);
        Assert.Null(kw.Port);
    }

    [Fact]
    public async Task Edit_updates_monitor_and_reschedules_on_interval_change()
    {
        var cid = await _f.DemoCustomerIdAsync();
        var id = await _f.AddMonitorAsync(cid, "ToEdit");
        var c = await AdminAsync();

        var get = await c.GetAsync($"/App/Monitors/Edit/{id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        var r = await c.PostFormAsync($"/App/Monitors/Edit/{id}", MonitorForm("Edited", ("IntervalSeconds", "60")));
        AssertRedirect(r, $"/App/Monitors/Details/{id}");

        var m = (await _f.GetMonitorAsync(id))!;
        Assert.Equal("Edited", m.Name);
        Assert.Equal(60, m.IntervalSeconds);
        Assert.True(m.NextCheckAtUtc <= DateTime.UtcNow.AddSeconds(1));
        Assert.Equal(cid, m.CustomerId);
    }

    [Fact]
    public async Task Edit_cannot_override_customer_id()
    {
        var cid = await _f.DemoCustomerIdAsync();
        var other = await _f.AddCustomerAsync("Overpost Target");
        var id = await _f.AddMonitorAsync(cid, "OverpostMe");
        var form = MonitorForm("OverpostMe2");
        form["CustomerId"] = other.ToString();
        form["Input.CustomerId"] = other.ToString();
        form["Input.Status"] = "Up";

        AssertRedirect(await (await AdminAsync()).PostFormAsync($"/App/Monitors/Edit/{id}", form), "/Details/");
        var m = (await _f.GetMonitorAsync(id))!;
        Assert.Equal(cid, m.CustomerId);
        Assert.Equal(MonitorStatus.Unknown, m.Status);
    }

    [Fact]
    public async Task Pause_and_resume()
    {
        var cid = await _f.DemoCustomerIdAsync();
        var id = await _f.AddMonitorAsync(cid, "Pausable", m => { m.Status = MonitorStatus.Down; m.ConsecutiveFailures = 3; });
        await _f.WithDbAsync(async db =>
        {
            db.Incidents.Add(new Incident { MonitorId = id, StartedAtUtc = DateTime.UtcNow.AddMinutes(-5), Cause = "timeout" });
            await db.SaveChangesAsync();
        });
        var c = await AdminAsync();
        var page = $"/App/Monitors/Details/{id}";

        AssertRedirect(await c.PostFormAsync($"{page}?handler=Pause", new(), page), page);
        var paused = (await _f.GetMonitorAsync(id))!;
        Assert.Equal(MonitorStatus.Paused, paused.Status);
        Assert.False(paused.IsEnabled);
        Assert.False(await _f.WithDbAsync(db => db.Incidents.AnyAsync(i => i.MonitorId == id && i.ResolvedAtUtc == null)));

        AssertRedirect(await c.PostFormAsync($"{page}?handler=Resume", new(), page), page);
        var resumed = (await _f.GetMonitorAsync(id))!;
        Assert.Equal(MonitorStatus.Unknown, resumed.Status);
        Assert.True(resumed.IsEnabled);
        Assert.Equal(0, resumed.ConsecutiveFailures);
        Assert.True(resumed.NextCheckAtUtc <= DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task Pause_from_list_returns_to_list()
    {
        var id = await _f.AddMonitorAsync(await _f.DemoCustomerIdAsync(), "PauseFromList");
        var r = await (await AdminAsync()).PostFormAsync($"/App/Monitors/Details/{id}?handler=Pause",
            new() { ["returnTo"] = "list" }, "/App/Monitors/Index");
        AssertRedirect(r, "/App/Monitors");
        Assert.DoesNotContain("Details", r.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Delete_requires_confirmation_and_removes_monitor()
    {
        var cid = await _f.DemoCustomerIdAsync();
        var id = await _f.AddMonitorAsync(cid, "Deletable");
        await _f.WithDbAsync(async db =>
        {
            db.CheckResults.Add(new CheckResult { MonitorId = id, CheckedAtUtc = DateTime.UtcNow, IsUp = true, ResponseTimeMs = 10 });
            await db.SaveChangesAsync();
        });
        var c = await AdminAsync();
        var confirm = await c.GetAsync($"/App/Monitors/Delete/{id}");
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        Assert.Contains("Deletable", await confirm.ReadHtmlAsync());

        AssertRedirect(await c.PostFormAsync($"/App/Monitors/Delete/{id}", new()), "/App/Monitors");
        Assert.Null(await _f.GetMonitorAsync(id));
        Assert.False(await _f.WithDbAsync(db => db.CheckResults.AnyAsync(x => x.MonitorId == id)));
    }

    [Fact]
    public async Task Index_filters_by_status()
    {
        var cid = await _f.DemoCustomerIdAsync();
        await _f.AddMonitorAsync(cid, "FilterDownOne", m => m.Status = MonitorStatus.Down);
        await _f.AddMonitorAsync(cid, "FilterUpOne", m => m.Status = MonitorStatus.Up);
        var html = await (await AdminAsync()).GetHtmlAsync("/App/Monitors?status=Down");
        Assert.Contains("FilterDownOne", html);
        Assert.DoesNotContain("FilterUpOne", html);
    }

    [Fact]
    public async Task Details_renders_stats_chart_and_history()
    {
        var cid = await _f.DemoCustomerIdAsync();
        var id = await _f.AddMonitorAsync(cid, "WithHistory", m => m.Status = MonitorStatus.Up);
        await _f.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            for (var i = 0; i < 1000; i++)
                db.CheckResults.Add(new CheckResult { MonitorId = id, CheckedAtUtc = now.AddSeconds(-60 * i), IsUp = i % 10 != 0, ResponseTimeMs = 100 + i % 50, Error = i % 10 == 0 ? "timeout" : null });
            db.Incidents.Add(new Incident { MonitorId = id, StartedAtUtc = now.AddHours(-3), ResolvedAtUtc = now.AddHours(-3).AddMinutes(65), Cause = "HTTP 503" });
            await db.SaveChangesAsync();
        });

        var html = await (await AdminAsync()).GetHtmlAsync($"/App/Monitors/Details/{id}");
        Assert.Contains("rtChart", html);
        Assert.Contains("/lib/chart.js/dist/chart.umd", html);
        Assert.Contains("90%", html);          // 24h uptime
        Assert.Contains("1 h 5 min", html);    // incident duration
        Assert.Contains("HTTP 503", html);
        Assert.Contains("Check now", html);
    }

    private static System.Text.Json.JsonElement ChartJson(string html, string variable)
    {
        var json = System.Text.RegularExpressions.Regex.Match(html, "var " + variable + @" = (\[.*?\]);").Groups[1].Value;
        return System.Text.Json.JsonDocument.Parse(json).RootElement;
    }

    [Theory]
    [InlineData("24h")]
    [InlineData("7d")]
    [InlineData("30d")]
    public async Task Details_chart_renders_each_range_with_phases_and_incident_markers(string range)
    {
        var cid = await _f.DemoCustomerIdAsync();
        var id = await _f.AddMonitorAsync(cid, "Ranges" + range, m => m.Status = MonitorStatus.Up);
        await _f.WithDbAsync(async db =>
        {
            var now = DateTime.UtcNow;
            for (var i = 0; i < 2000; i++) // one check every 20 minutes for ~28 days
                db.CheckResults.Add(new CheckResult
                {
                    MonitorId = id, CheckedAtUtc = now.AddMinutes(-20 * i), IsUp = i % 25 != 0, ResponseTimeMs = 150,
                    DnsMs = i % 2 == 0 ? 10 : null, ConnectMs = i % 2 == 0 ? 5 : null, TlsMs = i % 2 == 0 ? 20 : null, TtfbMs = 120
                });
            db.Incidents.Add(new Incident { MonitorId = id, StartedAtUtc = now.AddHours(-2), ResolvedAtUtc = now.AddHours(-1), Cause = "HTTP 502" });
            await db.SaveChangesAsync();
        });

        var html = await (await AdminAsync()).GetHtmlAsync($"/App/Monitors/Details/{id}?range={range}");
        Assert.Contains("id=\"rtChart\"", html);
        Assert.Contains("id=\"phaseChart\"", html);
        Assert.Matches($"<a class=\"btn btn-primary\"[^>]*range={range}\"", html);
        var points = ChartJson(html, "data");
        Assert.InRange(points.GetArrayLength(), 280, 300);
        Assert.Contains(points.EnumerateArray(), p => p.GetProperty("failures").GetInt32() > 0);
        var withData = points.EnumerateArray().First(p => p.GetProperty("checks").GetInt32() > 0 && p.GetProperty("failures").GetInt32() == 0);
        Assert.Equal(150, withData.GetProperty("avg").GetDouble());
        Assert.Equal(150, withData.GetProperty("p95").GetInt32());
        Assert.True(withData.GetProperty("wait").GetDouble() > 0);
        var incidents = ChartJson(html, "incidents");
        Assert.Equal(1, incidents.GetArrayLength());
        Assert.True(incidents[0].GetProperty("start").GetInt32() > 200); // near the end of the range
        Assert.Contains("DNS 10 · TCP 5 · TLS 20 · TTFB 120 ms", html);
        Assert.Contains("reused connection, TTFB 120 ms", html);
    }

    [Fact]
    public async Task Details_unknown_range_falls_back_to_24h_and_empty_chart_shows_message()
    {
        var cid = await _f.DemoCustomerIdAsync();
        var id = await _f.AddMonitorAsync(cid, "NoChecksYet");
        var html = await (await AdminAsync()).GetHtmlAsync($"/App/Monitors/Details/{id}?range=1y");
        Assert.Contains("No check results in this period.", html);
        Assert.Matches("<a class=\"btn btn-primary\"[^>]*range=24h\"", html);
        Assert.DoesNotContain("rtChart", html);
    }

    [Fact]
    public async Task Details_shows_certificate_info_for_https_monitors()
    {
        var cid = await _f.DemoCustomerIdAsync();
        var id = await _f.AddMonitorAsync(cid, "CertShown", m =>
        {
            m.Target = "https://secure.example.org/";
            m.CertExpiresAtUtc = DateTime.UtcNow.AddDays(10.5);
            m.CertIssuer = "CN=Example Issuing CA";
            m.CertSubject = "CN=secure.example.org";
            m.CertWarnDays = 30;
        });
        var html = await (await AdminAsync()).GetHtmlAsync($"/App/Monitors/Details/{id}");
        Assert.Contains("TLS certificate", html);
        Assert.Contains("(10 days left)", html);
        Assert.Contains("Issuer: CN=Example Issuing CA", html);
        Assert.Contains("Expiry warning: 30 days before expiry", html);

        var plain = await _f.AddMonitorAsync(cid, "PlainHttp", m => m.Target = "http://plain.example.org/");
        Assert.DoesNotContain("TLS certificate", await (await AdminAsync()).GetHtmlAsync($"/App/Monitors/Details/{plain}"));
    }

    [Fact]
    public async Task Failing_check_now_does_not_wait_for_the_confirmation_probe()
    {
        // ConfirmBeforeDown is on (default) with the default 5 s delay; the manual check must not wait for it.
        int port;
        using (var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0)) { l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; }
        var cid = await _f.DemoCustomerIdAsync();
        var id = await _f.AddMonitorAsync(cid, "ConfirmLater", m =>
        {
            m.Target = $"http://127.0.0.1:{port}/";
            m.Status = MonitorStatus.Up;
            m.FailureThreshold = 1;
        });
        var c = await AdminAsync();
        var url = $"/App/Monitors/Details/{id}";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await c.PostFormAsync(url + "?handler=CheckNow", new(), url);
        sw.Stop();
        AssertRedirect(r, url);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"check now took {sw.Elapsed}");

        var html = await c.GetHtmlAsync(url);
        Assert.Contains("Check failed: Connection refused. The failure was recorded; a confirmation check runs shortly", html);
        var m = (await _f.GetMonitorAsync(id))!;
        Assert.Equal(MonitorStatus.Up, m.Status);
        Assert.NotNull(m.PendingConfirmationSinceUtc);
        Assert.True(m.NextCheckAtUtc <= DateTime.UtcNow.AddSeconds(6));
        Assert.Equal(0, await _f.WithDbAsync(db => db.Incidents.CountAsync(i => i.MonitorId == id)));
    }

    [Fact]
    public async Task Cert_warning_days_are_saved_validated_and_reset_with_the_target()
    {
        var c = await AdminAsync();
        AssertRedirect(await c.PostFormAsync("/App/Monitors/Create", MonitorForm("CertDays", ("CertWarnDays", "7"))), "/Details/");
        var id = (await FindByNameAsync("CertDays"))!.Id;
        Assert.Equal(7, (await _f.GetMonitorAsync(id))!.CertWarnDays);

        var bad = await c.PostFormAsync("/App/Monitors/Create", MonitorForm("CertDaysBad", ("CertWarnDays", "3")));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Assert.Contains("Invalid certificate expiry warning.", await bad.ReadHtmlAsync());
        Assert.Null(await FindByNameAsync("CertDaysBad"));

        await _f.WithDbAsync(async db =>
        {
            var m = await db.Monitors.SingleAsync(x => x.Id == id);
            m.CertExpiresAtUtc = DateTime.UtcNow.AddDays(5);
            m.CertWarningSentForUtc = m.CertExpiresAtUtc;
            await db.SaveChangesAsync();
        });
        AssertRedirect(await c.PostFormAsync($"/App/Monitors/Edit/{id}", MonitorForm("CertDays", ("CertWarnDays", "0"), ("Target", "https://other.example.org"))), "/Details/");
        var edited = (await _f.GetMonitorAsync(id))!;
        Assert.Equal(0, edited.CertWarnDays);
        Assert.Null(edited.CertExpiresAtUtc);
        Assert.Null(edited.CertWarningSentForUtc);
    }

    [Fact]
    public async Task Dashboard_shows_counts_and_open_incidents()
    {
        var cid = await _f.DemoCustomerIdAsync();
        var id = await _f.AddMonitorAsync(cid, "DashDown", m => m.Status = MonitorStatus.Down);
        await _f.WithDbAsync(async db =>
        {
            db.Incidents.Add(new Incident { MonitorId = id, StartedAtUtc = DateTime.UtcNow.AddMinutes(-10), Cause = "DashCause" });
            await db.SaveChangesAsync();
        });
        var r = await (await AdminAsync()).GetAsync("/App/Index");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var html = await r.ReadHtmlAsync();
        Assert.Contains("DashDown", html);
        Assert.Contains("DashCause", html);
        Assert.Contains("http-equiv=\"refresh\"", html);
    }
}

/// <summary>Edit page retry on the Monitor concurrency token (a check or delete lands between its read and save).</summary>
public class MonitorEditConcurrencyTests
{
    private sealed class HookedFactory(SaveHook hook) : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s => s.ConfigureDbContext<AppDbContext>(o => o.AddInterceptors(hook)));
        }
    }

    private static SaveHook HookOnEditSave() =>
        new(db => db.ChangeTracker.Entries<MonitorEntity>().Any(e => e.State == EntityState.Modified && e.Entity.Name.StartsWith("Edited")));

    [Fact]
    public async Task Edit_retries_on_concurrent_status_change_and_keeps_both_changes()
    {
        var hook = HookOnEditSave();
        using var f = new HookedFactory(hook);
        var id = await f.AddMonitorAsync(await f.DemoCustomerIdAsync(), "Racy", m => m.Status = MonitorStatus.Up);
        // Simulates a check that takes the monitor Down (and bumps the token) right before the edit is saved.
        hook.Action = () => f.WithDbAsync(db => db.Monitors.Where(m => m.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(m => m.Status, MonitorStatus.Down).SetProperty(m => m.Version, m => m.Version + 1)));
        var c = await f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);
        var target = (await f.GetMonitorAsync(id))!.Target;

        var r = await c.PostFormAsync($"/App/Monitors/Edit/{id}", MonitorForm("Edited-Racy", ("Input.Target", target), ("Input.IntervalSeconds", "300")));

        AssertRedirect(r, "/Details/");
        Assert.True(hook.Fired);
        var m = (await f.GetMonitorAsync(id))!;
        Assert.Equal("Edited-Racy", m.Name);           // form applied on the retry
        Assert.Equal(MonitorStatus.Down, m.Status);    // concurrent runtime change preserved
        Assert.Equal(2, m.Version);                    // concurrent bump + edit bump
    }

    [Fact]
    public async Task Edit_of_monitor_deleted_meanwhile_returns_not_found()
    {
        var hook = HookOnEditSave();
        using var f = new HookedFactory(hook);
        var id = await f.AddMonitorAsync(await f.DemoCustomerIdAsync(), "Doomed");
        hook.Action = () => f.WithDbAsync(db => db.Monitors.Where(m => m.Id == id).ExecuteDeleteAsync());
        var c = await f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);

        var r = await c.PostFormAsync($"/App/Monitors/Edit/{id}", MonitorForm("Edited-Doomed", ("Input.IntervalSeconds", "300")));

        Assert.True(hook.Fired);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Null(await f.GetMonitorAsync(id));
    }
}
