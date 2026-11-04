using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
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
    public async Task Monitor_details_page_scripts_have_nonce_and_chart_cdn_is_allowed()
    {
        var cid = await f.DemoCustomerIdAsync();
        var id = await f.AddMonitorAsync(cid, "HeadersChart");
        var c = await f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);

        var r = await c.GetAsync($"/App/Monitors/Details/{id}");
        var nonce = AssertHeaders(r);
        AssertScriptsHaveNonce(await r.Content.ReadAsStringAsync(), nonce);
        Assert.Contains("https://cdn.jsdelivr.net", r.Headers.GetValues("Content-Security-Policy").Single());
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
