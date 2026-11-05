using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Domain;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;

namespace TriggerCheck.Tests.Security;

public class SecurityHeadersTests(TestWebAppFactory f) : IClassFixture<TestWebAppFactory>
{
    private static readonly Regex NonceRx = new("'nonce-([^']+)'", RegexOptions.Compiled);
    private static readonly Regex ScriptTagRx = new("<script\\b[^>]*>", RegexOptions.Compiled);

    private static string AssertHeaders(HttpResponseMessage r)
    {
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", r.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("strict-origin-when-cross-origin", r.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("camera=()", r.Headers.GetValues("Permissions-Policy").Single());

        var csp = r.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("object-src 'none'", csp);
        Assert.DoesNotContain("'unsafe-eval'", csp);
        Assert.DoesNotContain("'unsafe-hashes'", csp);
        Assert.DoesNotContain("https:", csp);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", csp);
        return NonceRx.Match(csp).Groups[1].Value;
    }

    /// <summary>Every &lt;script&gt; (inline theme script, Bootstrap, page scripts) carries this response's nonce.</summary>
    private static void AssertScriptsHaveNonce(string html, string nonce)
    {
        var tags = ScriptTagRx.Matches(html).Select(m => m.Value).ToList();
        Assert.NotEmpty(tags);
        Assert.All(tags, t => Assert.Contains($"nonce=\"{nonce}\"", t));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Account/Login")]
    public async Task Public_pages_send_security_headers(string url)
    {
        var r = await f.CreateBrowser().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var nonce = AssertHeaders(r);
        Assert.False(string.IsNullOrEmpty(nonce));
        var html = await r.Content.ReadAsStringAsync();
        AssertScriptsHaveNonce(html, nonce);
        Assert.Contains("localStorage.getItem('tc-theme')", html); // theme script still inline, now nonce'd
    }

    [Fact]
    public async Task Nonce_changes_per_request()
    {
        var c = f.CreateBrowser();
        var a = AssertHeaders(await c.GetAsync("/Account/Login"));
        var b = AssertHeaders(await c.GetAsync("/Account/Login"));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task Monitor_details_chart_is_self_hosted_and_nonced()
    {
        var cid = await f.DemoCustomerIdAsync();
        var id = await f.AddMonitorAsync(cid, "HeadersChart");
        await f.WithDbAsync(async db =>
        {
            db.CheckResults.Add(new TriggerCheck.Domain.Entities.CheckResult { MonitorId = id, CheckedAtUtc = DateTime.UtcNow.AddMinutes(-1), IsUp = true, ResponseTimeMs = 12 });
            await db.SaveChangesAsync();
        });
        var c = await f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);

        var r = await c.GetAsync($"/App/Monitors/Details/{id}");
        var nonce = AssertHeaders(r);
        var html = await r.Content.ReadAsStringAsync();
        AssertScriptsHaveNonce(html, nonce);
        Assert.Contains("/lib/chart.js/dist/chart.umd", html);
        Assert.DoesNotContain("cdn.jsdelivr.net", html);
        Assert.DoesNotContain("cdn.jsdelivr.net", r.Headers.GetValues("Content-Security-Policy").Single());

        var js = await c.GetAsync("/lib/chart.js/dist/chart.umd.js");
        Assert.Equal(HttpStatusCode.OK, js.StatusCode);
        Assert.Contains("Chart.js v4.4.1", await js.Content.ReadAsStringAsync());
    }

    /// <summary>The CSP forbids inline event handlers; confirmations go through site.js.</summary>
    [Fact]
    public async Task No_inline_event_handlers_in_views()
    {
        var web = Path.Combine(FindRoot(), "src", "TriggerCheck.Web");
        var hits = Directory.EnumerateFiles(web, "*.cshtml", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(p => Regex.IsMatch(File.ReadAllText(p), "\\son[a-z]+\\s*=\\s*\"", RegexOptions.IgnoreCase))
            .ToList();
        Assert.True(hits.Count == 0, "Inline handlers:\n" + string.Join("\n", hits));
        Assert.Contains("form[data-confirm]", await f.CreateBrowser().GetStringAsync("/js/site.js"));
    }

    private static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "TriggerCheck.sln"))) d = d.Parent;
        return d!.FullName;
    }

    [Fact]
    public async Task Static_files_get_nosniff()
    {
        var r = await f.CreateBrowser().GetAsync("/js/site.js");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
    }
}

/// <summary>Own factory: locks a dedicated user out without affecting other test classes.</summary>
public class LoginLockoutTests(TestWebAppFactory f) : IClassFixture<TestWebAppFactory>
{
    [Fact]
    public async Task Five_failed_logins_lock_the_account()
    {
        const string email = "lockme@demo.local", password = "Lockme123!";
        await f.AddUserAsync(await f.DemoCustomerIdAsync(), email, password, Roles.CustomerAdmin);

        for (var i = 0; i < 5; i++)
        {
            var bad = await f.CreateBrowser().PostFormAsync("/Account/Login", new() { ["Input.Email"] = email, ["Input.Password"] = "wrong-password" });
            Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        }

        var lockoutEnd = await f.WithDbAsync(db => db.Users.Where(u => u.Email == email).Select(u => u.LockoutEnd).SingleAsync());
        Assert.True(lockoutEnd > DateTimeOffset.UtcNow);

        // Even the correct password is refused while locked out.
        var good = await f.CreateBrowser().PostFormAsync("/Account/Login", new() { ["Input.Email"] = email, ["Input.Password"] = password });
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
    }
}

/// <summary>The Development exception page (inline scripts without our nonce) must stay usable.</summary>
public class DeveloperExceptionPageTests
{
    private sealed class ThrowAtEndFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            // Reached only when no endpoint matched; runs inside the exception page and security headers middleware.
            app.Use((ctx, nxt) => ctx.Request.Path == "/__throw" ? throw new InvalidOperationException("boom") : nxt(ctx));
        };
    }

    [Fact]
    public async Task Exception_page_in_development_has_no_csp_but_keeps_other_headers()
    {
        await using var f = new TestWebAppFactory();
        using var dev = f.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddTransient<IStartupFilter, ThrowAtEndFilter>()));

        var client = dev.CreateClient();
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        var r = await client.GetAsync("/__throw");

        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
        var html = await r.Content.ReadAsStringAsync();
        Assert.Contains("boom", html);
        Assert.Contains("<script", html); // the page relies on inline script...
        Assert.False(r.Headers.Contains("Content-Security-Policy")); // ...so no CSP here
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Production_error_page_keeps_the_csp()
    {
        await using var f = new TestWebAppFactory();
        using var prod = f.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.ConfigureServices(s => s.AddTransient<IStartupFilter, ThrowAtEndFilter>());
        });

        // https: production redirects plain http.
        var client = prod.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        var r = await client.GetAsync("/__throw");

        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
        var csp = r.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("'nonce-", csp);
        Assert.DoesNotContain("boom", await r.Content.ReadAsStringAsync());
    }
}
