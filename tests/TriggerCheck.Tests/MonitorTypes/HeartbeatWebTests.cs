using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using OpenTelemetry.Trace;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring.Checks;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Tests.Ops;
using TriggerCheck.Web.Api;
using TriggerCheck.Web.Heartbeat;
using static TriggerCheck.Tests.App.AppTestData;

namespace TriggerCheck.Tests.MonitorTypes;

/// <summary>Heartbeat monitors in the web app: creation, one-time ping URL, /hb endpoint, regeneration, isolation.</summary>
public class HeartbeatWebTests(TestWebAppFactory f) : IClassFixture<TestWebAppFactory>
{
    private static readonly Regex PingUrlRx = new(@"https?://[^\s<""]+/hb/([A-Za-z0-9_-]{43})", RegexOptions.Compiled);

    private Task<HttpClient> AdminAsync() => f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);

    private static Dictionary<string, string> HeartbeatForm(string name, string interval = "3600", string grace = "120") => new()
    {
        ["Input.Name"] = name,
        ["Input.Type"] = "Heartbeat",
        ["Input.IntervalSeconds"] = interval,
        ["Input.GracePeriodSeconds"] = grace,
    };

    /// <summary>Creates a heartbeat through the form; returns the monitor id and the token shown once on the details page.</summary>
    private async Task<(int Id, string Token, HttpClient Admin)> CreateAsync(string name)
    {
        var c = await AdminAsync();
        var r = await c.PostFormAsync("/App/Monitors/Create", HeartbeatForm(name));
        AssertRedirect(r, "/App/Monitors/Details/");
        var html = await c.GetHtmlAsync(r.Headers.Location!.ToString());
        var token = PingUrlRx.Match(html).Groups[1].Value;
        Assert.Equal(43, token.Length);
        var id = await f.WithDbAsync(db => db.Monitors.Where(m => m.Name == name).Select(m => m.Id).SingleAsync());
        return (id, token, c);
    }

    private Task<Domain.Entities.Monitor> MonitorAsync(int id) => f.WithDbAsync(db => db.Monitors.AsNoTracking().SingleAsync(m => m.Id == id));

    [Fact]
    public async Task Create_stores_only_hash_and_prefix_and_shows_the_url_once()
    {
        var before = DateTime.UtcNow;
        var (id, token, c) = await CreateAsync("Nightly backup");

        var m = await MonitorAsync(id);
        Assert.Equal(MonitorType.Heartbeat, m.Type);
        Assert.Equal(HeartbeatTokens.Hash(token), m.HeartbeatTokenHash);
        Assert.Equal(token[..8], m.HeartbeatTokenPrefix);
        Assert.Equal("/hb/" + token[..8] + "…", m.Target);
        Assert.Equal(3600, m.IntervalSeconds);
        Assert.Equal(120, m.GracePeriodSeconds);
        Assert.Equal(1, m.FailureThreshold);
        Assert.True(m.NextCheckAtUtc >= before.AddSeconds(3600 + 120 - 1)); // first decision at the deadline
        Assert.DoesNotContain(await f.WithDbAsync(db => db.Monitors.Select(x => x.Target + x.Name + x.HeartbeatTokenHash).ToListAsync()),
            s => s.Contains(token));

        // Second view: only the prefix, no check-now button.
        var again = await c.GetHtmlAsync($"/App/Monitors/Details/{id}");
        Assert.DoesNotContain(token, again);
        Assert.Contains("/hb/" + token[..8], again);
        Assert.DoesNotContain("handler=CheckNow", again);
        Assert.Contains("handler=RegenerateToken", again);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("HEAD")]
    public async Task Ping_records_the_heartbeat_and_makes_the_monitor_due(string method)
    {
        var (id, token, _) = await CreateAsync("Ping " + method);
        var versionBefore = (await MonitorAsync(id)).Version;

        var anon = f.CreateClient();
        var r = await anon.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/hb/" + token));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.False(r.Headers.Contains("Set-Cookie"));
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());

        var m = await MonitorAsync(id);
        Assert.NotNull(m.LastHeartbeatAtUtc);
        Assert.False(m.LastHeartbeatFailed);
        Assert.True(m.NextCheckAtUtc <= DateTime.UtcNow);
        Assert.True(m.Version > versionBefore);
    }

    [Fact]
    public async Task Fail_status_and_message_are_stored_truncated_and_masked()
    {
        var (id, token, c) = await CreateAsync("Failing job");
        var msg = "db dump failed https://user:pw@internal.example/x?key=s3cr3t token=ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 \n" + new string('z', 500);
        var r = await f.CreateClient().PostAsync($"/hb/{token}?status=fail&msg={Uri.EscapeDataString(msg)}", new StringContent("ignored body " + new string('b', 10000)));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var m = await MonitorAsync(id);
        Assert.True(m.LastHeartbeatFailed);
        Assert.NotNull(m.LastHeartbeatMessage);
        Assert.True(m.LastHeartbeatMessage!.Length <= HeartbeatEndpoint.MaxMessageLength);
        Assert.StartsWith("db dump failed https://internal.example/x?…", m.LastHeartbeatMessage);
        Assert.DoesNotContain("s3cr3t", m.LastHeartbeatMessage);
        Assert.DoesNotContain("pw@", m.LastHeartbeatMessage);
        Assert.DoesNotContain("ABCDEFGHIJKLMNOPQRSTUVWXYZ", m.LastHeartbeatMessage);
        Assert.DoesNotContain("ignored body", m.LastHeartbeatMessage);
        Assert.DoesNotContain("\n", m.LastHeartbeatMessage);

        var html = await c.GetHtmlAsync($"/App/Monitors/Details/{id}");
        Assert.Contains("reported failure", html);

        // A following success ping clears the failure flag.
        await f.CreateClient().GetAsync($"/hb/{token}");
        Assert.False((await MonitorAsync(id)).LastHeartbeatFailed);
    }

    [Fact]
    public async Task Unknown_malformed_and_known_tokens_get_the_same_response()
    {
        var (_, token, _) = await CreateAsync("Oracle check");
        var anon = f.CreateClient();
        async Task<(HttpStatusCode, string, string?)> Hit(string t)
        {
            var r = await anon.GetAsync("/hb/" + t);
            return (r.StatusCode, await r.Content.ReadAsStringAsync(), r.Content.Headers.ContentType?.ToString());
        }

        var known = await Hit(token);
        Assert.Equal((HttpStatusCode.OK, HeartbeatEndpoint.ResponseBody), (known.Item1, known.Item2));
        Assert.Equal(known, await Hit(HeartbeatTokens.NewToken()));
        Assert.Equal(known, await Hit("short"));
        Assert.Equal(known, await Hit(new string('!', 43)));
    }

    [Fact]
    public async Task Ping_works_for_a_logged_in_browser_and_never_touches_its_session()
    {
        var (id, token, admin) = await CreateAsync("Browser ping");
        var r = await admin.GetAsync("/hb/" + token);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.NotNull((await MonitorAsync(id)).LastHeartbeatAtUtc);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/App/Monitors")).StatusCode); // session still valid
    }

    [Fact]
    public async Task Regenerate_replaces_the_token_and_the_old_url_stops_working()
    {
        var (id, oldToken, c) = await CreateAsync("Regen");
        var r = await c.PostFormAsync($"/App/Monitors/Details/{id}?handler=RegenerateToken", new(), $"/App/Monitors/Details/{id}");
        AssertRedirect(r, $"/App/Monitors/Details/{id}");
        var html = await c.GetHtmlAsync(r.Headers.Location!.ToString());
        var newToken = PingUrlRx.Match(html).Groups[1].Value;
        Assert.Equal(43, newToken.Length);
        Assert.NotEqual(oldToken, newToken);

        await f.CreateClient().GetAsync("/hb/" + oldToken);
        Assert.Null((await MonitorAsync(id)).LastHeartbeatAtUtc);
        await f.CreateClient().GetAsync("/hb/" + newToken);
        var m = await MonitorAsync(id);
        Assert.NotNull(m.LastHeartbeatAtUtc);
        Assert.Equal(HeartbeatTokens.Hash(newToken), m.HeartbeatTokenHash);
    }

    [Fact]
    public async Task Viewers_and_other_tenants_cannot_see_or_regenerate_the_url()
    {
        var (id, token, _) = await CreateAsync("Isolated");
        var cid = await f.DemoCustomerIdAsync();
        await f.AddUserAsync(cid, "hbviewer@demo.local", "Viewer123!", Roles.CustomerViewer);
        var viewer = await f.LoginAsync("hbviewer@demo.local", "Viewer123!");
        var page = await viewer.GetHtmlAsync($"/App/Monitors/Details/{id}");
        Assert.DoesNotContain(token, page);
        Assert.DoesNotContain("handler=RegenerateToken", page);
        var denied = await viewer.PostFormAsync($"/App/Monitors/Details/{id}?handler=RegenerateToken", new(), $"/App/Monitors/Details/{id}");
        AssertAccessDenied(denied);

        var other = await f.AddCustomerAsync("HB Other");
        await f.AddUserAsync(other, "hbother@other.local", "Other123!", Roles.CustomerAdmin);
        var otherAdmin = await f.LoginAsync("hbother@other.local", "Other123!");
        Assert.Equal(HttpStatusCode.NotFound, (await otherAdmin.GetAsync($"/App/Monitors/Details/{id}")).StatusCode);
        var foreign = await otherAdmin.PostFormAsync($"/App/Monitors/Details/{id}?handler=RegenerateToken", new(), "/App/Monitors");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        Assert.Equal(HeartbeatTokens.Hash(token), (await MonitorAsync(id)).HeartbeatTokenHash); // unchanged
    }

    [Fact]
    public async Task The_ping_url_is_revealed_once_without_caching_and_never_via_tempdata()
    {
        var c = await AdminAsync();
        var r = await c.PostFormAsync("/App/Monitors/Create", HeartbeatForm("Reveal once"));
        var location = r.Headers.Location!.ToString();
        Assert.Contains("reveal=", location);
        Assert.DoesNotContain(r.Headers.TryGetValues("Set-Cookie", out var cookies) ? string.Join(";", cookies) : "", "HeartbeatUrl");

        var first = await c.GetAsync(location);
        Assert.True(first.Headers.CacheControl?.NoStore);
        var token = PingUrlRx.Match(await first.ReadHtmlAsync()).Groups[1].Value;
        Assert.Equal(43, token.Length);

        var second = await c.GetHtmlAsync(location); // same reveal id again: gone
        Assert.DoesNotContain(token, second);

        // Another admin of the same customer cannot redeem someone else's reveal id.
        var cid = await f.DemoCustomerIdAsync();
        var r2 = await c.PostFormAsync("/App/Monitors/Create", HeartbeatForm("Reveal other user"));
        await f.AddUserAsync(cid, "hbadmin2@demo.local", "Admin123!", Roles.CustomerAdmin);
        var other = await f.LoginAsync("hbadmin2@demo.local", "Admin123!");
        Assert.DoesNotMatch(PingUrlRx, await other.GetHtmlAsync(r2.Headers.Location!.ToString()));
    }

    [Fact]
    public async Task Pings_to_paused_monitors_and_suspended_customers_are_ignored()
    {
        var (id, token, c) = await CreateAsync("Paused ping");
        await c.PostFormAsync($"/App/Monitors/Details/{id}?handler=Pause", new(), $"/App/Monitors/Details/{id}");
        Assert.Equal(HttpStatusCode.OK, (await f.CreateClient().GetAsync("/hb/" + token)).StatusCode);
        Assert.Null((await MonitorAsync(id)).LastHeartbeatAtUtc);

        var otherCid = await f.AddCustomerAsync("HB Suspended");
        var hbId = await f.AddMonitorAsync(otherCid, "Suspended hb", m =>
        {
            m.Type = MonitorType.Heartbeat;
            m.IntervalSeconds = 300;
            m.GracePeriodSeconds = 60;
        });
        var suspendedToken = await f.WithDbAsync(async db =>
        {
            var m = await db.Monitors.SingleAsync(x => x.Id == hbId);
            var t = HeartbeatTokens.Issue(m);
            (await db.Customers.SingleAsync(x => x.Id == otherCid)).IsActive = false;
            await db.SaveChangesAsync();
            return t;
        });
        Assert.Equal(HttpStatusCode.OK, (await f.CreateClient().GetAsync("/hb/" + suspendedToken)).StatusCode);
        Assert.Null((await MonitorAsync(hbId)).LastHeartbeatAtUtc);

        // Reactivation gives the heartbeat a full window instead of an immediate (DOWN) evaluation.
        var sa = await f.LoginAsync(TestWebAppFactory.SuperAdminEmail, TestWebAppFactory.SuperAdminPassword);
        var before = DateTime.UtcNow;
        var detailsUrl = $"/SuperAdmin/Customers/Details/{otherCid}";
        AssertRedirect(await sa.PostFormAsync($"{detailsUrl}?handler=Activate", new(), detailsUrl), detailsUrl);
        Assert.True((await MonitorAsync(hbId)).NextCheckAtUtc >= before.AddSeconds(360 - 1));
        await f.CreateClient().GetAsync("/hb/" + suspendedToken);
        Assert.NotNull((await MonitorAsync(hbId)).LastHeartbeatAtUtc);
    }

    [Fact]
    public async Task Check_now_is_refused_for_heartbeats()
    {
        var (id, _, c) = await CreateAsync("No check now");
        var r = await c.PostFormAsync($"/App/Monitors/Details/{id}?handler=CheckNow", new(), $"/App/Monitors/Details/{id}");
        AssertRedirect(r, $"/App/Monitors/Details/{id}");
        Assert.Equal(0, await f.WithDbAsync(db => db.CheckResults.CountAsync(x => x.MonitorId == id)));
    }

    [Theory]
    [InlineData("30", "120", false)]     // below the 60 s heartbeat minimum
    [InlineData("60", "120", true)]
    [InlineData("86400", "10", true)]
    [InlineData("3600", "9", false)]      // below the 10 s minimum grace
    [InlineData("7200", "120", false)]   // not an offered interval
    [InlineData("3600", "-1", false)]
    [InlineData("3600", "86401", false)]
    public async Task Heartbeat_form_validation(string interval, string grace, bool ok)
    {
        var c = await AdminAsync();
        var name = $"HB {interval}/{grace}";
        var r = await c.PostFormAsync("/App/Monitors/Create", HeartbeatForm(name, interval, grace));
        Assert.Equal(ok, r.StatusCode == HttpStatusCode.Redirect);
        Assert.Equal(ok, await f.WithDbAsync(db => db.Monitors.AnyAsync(m => m.Name == name)));
    }

    [Fact]
    public async Task Edit_away_from_heartbeat_clears_the_token_and_back_issues_a_new_one()
    {
        var (id, token, c) = await CreateAsync("Switcher");
        var r = await c.PostFormAsync($"/App/Monitors/Edit/{id}", MonitorForm("Switcher"));
        AssertRedirect(r, "/App/Monitors/Details/");
        var m = await MonitorAsync(id);
        Assert.Equal(MonitorType.Http, m.Type);
        Assert.Null(m.HeartbeatTokenHash);
        Assert.Equal("https://example.org", m.Target);

        r = await c.PostFormAsync($"/App/Monitors/Edit/{id}", HeartbeatForm("Switcher", "300", "30"));
        AssertRedirect(r, "/App/Monitors/Details/");
        var html = await c.GetHtmlAsync(r.Headers.Location!.ToString());
        var newToken = PingUrlRx.Match(html).Groups[1].Value;
        Assert.Equal(43, newToken.Length);
        Assert.NotEqual(token, newToken);
        m = await MonitorAsync(id);
        Assert.Equal(HeartbeatTokens.Hash(newToken), m.HeartbeatTokenHash);
        Assert.True(m.NextCheckAtUtc > DateTime.UtcNow.AddSeconds(300)); // full window for the first ping
    }

    [Fact]
    public async Task Edit_form_of_a_heartbeat_renders_heartbeat_fields_enabled_and_probe_fields_disabled()
    {
        var (id, _, c) = await CreateAsync("Form state");
        var html = await c.GetStringAsync($"/App/Monitors/Edit/{id}");
        Assert.Matches(new Regex(@"<fieldset[^>]*data-types=""Heartbeat""(?![^>]*disabled)[^>]*>\s*<label for=""heartbeat-interval"""), html);
        Assert.Matches(new Regex(@"<fieldset[^>]*data-types=""Http Keyword Tcp Ping Dns JsonApi""[^>]*disabled"), html);
    }
}

/// <summary>Rate limits of the ping endpoint (own app instance: the limiter is per process).</summary>
public class HeartbeatRateLimitTests(TestWebAppFactory f) : IClassFixture<TestWebAppFactory>
{
    [Fact]
    public async Task Per_token_limit_applies_to_known_and_unknown_tokens_alike()
    {
        var anon = f.CreateClient();
        var unknown = HeartbeatTokens.NewToken();
        for (var i = 0; i < new HeartbeatOptions().PingsPerMinutePerToken; i++)
            Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/hb/" + unknown)).StatusCode);
        var limited = await anon.GetAsync("/hb/" + unknown);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);

        // Another token is not affected by the first one's limit.
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/hb/" + HeartbeatTokens.NewToken())).StatusCode);
    }

    private static HeartbeatRateLimiter Limiter(FakeTimeProvider time, int perToken = 10, int perClient = 6000, int global = 1000, int maxEntries = HeartbeatRateLimiter.DefaultMaxEntries) =>
        new(time, Options.Create(new HeartbeatOptions { PingsPerMinutePerToken = perToken, PingsPerMinutePerClient = perClient, PingsPerSecondGlobal = global }))
        { MaxEntries = maxEntries };

    [Fact]
    public void Limiter_windows_per_token_and_per_client()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var limiter = Limiter(time);
        for (var i = 0; i < 10; i++) Assert.True(limiter.TryAcquire("t1", "ip:1.1.1.1").Allowed);
        var denied = limiter.TryAcquire("t1", "ip:1.1.1.1");
        Assert.False(denied.Allowed);
        Assert.Equal(60, denied.RetryAfterSeconds);
        time.Advance(TimeSpan.FromSeconds(60));
        Assert.True(limiter.TryAcquire("t1", "ip:1.1.1.1").Allowed);

        // Per client: the default is high enough for a fleet behind one NAT (500 hosts pinging every minute).
        var nat = Limiter(time);
        for (var i = 0; i < 500; i++) Assert.True(nat.TryAcquire("host" + i, "ip:2.2.2.2").Allowed);

        var small = Limiter(time, perClient: 5);
        for (var i = 0; i < 5; i++) Assert.True(small.TryAcquire("tok" + i, "ip6:2001:db8::/64").Allowed);
        Assert.False(small.TryAcquire("fresh", "ip6:2001:db8::/64").Allowed);
        Assert.True(small.TryAcquire("fresh", "ip:3.3.3.3").Allowed);
    }

    [Fact]
    public void Client_partition_groups_ipv6_by_64()
    {
        Assert.Equal(ApiSetup.ClientPartition(IPAddress.Parse("2001:db8:1:2::1")), ApiSetup.ClientPartition(IPAddress.Parse("2001:db8:1:2:ffff::9")));
        Assert.NotEqual(ApiSetup.ClientPartition(IPAddress.Parse("2001:db8:1:2::1")), ApiSetup.ClientPartition(IPAddress.Parse("2001:db8:1:3::1")));
    }

    [Fact]
    public void Global_flood_guard()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var limiter = Limiter(time, global: 3);
        for (var i = 0; i < 3; i++) Assert.True(limiter.TryAcquire("t" + i, "ip:" + i).Allowed);
        Assert.False(limiter.TryAcquire("t9", "ip:9").Allowed);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(limiter.TryAcquire("t9", "ip:9").Allowed);
    }

    [Fact]
    public void Under_memory_pressure_new_keys_are_denied_and_existing_counters_are_kept()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var limiter = Limiter(time, perToken: 2, maxEntries: 10);
        for (var i = 0; i < 5; i++) Assert.True(limiter.TryAcquire("t" + i, "ip:" + i).Allowed); // 10 entries
        Assert.Equal(10, limiter.TrackedEntries);

        Assert.False(limiter.TryAcquire("new", "ip:new").Allowed); // full: deny, do not forget
        Assert.True(limiter.TryAcquire("t0", "ip:0").Allowed);      // existing key still works...
        Assert.False(limiter.TryAcquire("t0", "ip:0").Allowed);     // ...and its limit still applies (no fail-open reset)
        Assert.Equal(10, limiter.TrackedEntries);

        time.Advance(TimeSpan.FromMinutes(1));                       // entries expire by age only
        Assert.True(limiter.TryAcquire("new", "ip:new").Allowed);
        Assert.True(limiter.TrackedEntries <= 10);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("ok\r\ndone\t!", "ok done !")]
    [InlineData("see https://h.example/p?secret=1", "see https://h.example/p?…")]
    public void Messages_are_cleaned(string? raw, string? expected) => Assert.Equal(expected, HeartbeatEndpoint.CleanMessage(raw));
}

/// <summary>Ping URL base from Site:PublicBaseUrl.</summary>
public sealed class PublicBaseUrlFactory : TestWebAppFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Site:PublicBaseUrl", "https://status.example.com/");
    }
}

public class HeartbeatPublicUrlTests(PublicBaseUrlFactory f) : IClassFixture<PublicBaseUrlFactory>
{
    [Fact]
    public async Task Ping_url_uses_the_configured_public_base_url()
    {
        var c = await f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);
        var r = await c.PostFormAsync("/App/Monitors/Create", new()
        {
            ["Input.Name"] = "Public url", ["Input.Type"] = "Heartbeat", ["Input.IntervalSeconds"] = "300", ["Input.GracePeriodSeconds"] = "60"
        });
        var html = await c.GetHtmlAsync(r.Headers.Location!.ToString());
        Assert.Matches(new Regex(@"https://status\.example\.com/hb/[A-Za-z0-9_-]{43}"), html);
    }
}

public class HeartbeatTraceTests
{
    [Fact]
    public async Task Ping_tokens_and_messages_never_reach_trace_spans()
    {
        await using var f = new TracingAppFactory();
        var token = HeartbeatTokens.NewToken();
        Assert.Equal(HttpStatusCode.OK, (await f.CreateClient().GetAsync($"/hb/{token}?status=fail&msg=secret-msg-77")).StatusCode);

        Assert.True(f.Services.GetRequiredService<TracerProvider>().ForceFlush(5000));
        List<Activity> spans;
        lock (f.Spans) spans = f.Spans.Where(s => s.Source.Name == "Microsoft.AspNetCore").ToList();
        Assert.NotEmpty(spans);
        foreach (var span in spans)
        {
            var text = span.DisplayName + " " + string.Join(" ", span.TagObjects.Select(t => $"{t.Key}={t.Value}"));
            Assert.DoesNotContain(token, text);
            Assert.DoesNotContain("secret-msg-77", text);
        }
        Assert.Contains(spans, s => Equals(s.GetTagItem("url.path"), HeartbeatEndpoint.Pattern));
    }
}
