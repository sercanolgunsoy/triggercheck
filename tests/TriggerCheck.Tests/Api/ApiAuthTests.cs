using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;

namespace TriggerCheck.Tests.Api;

/// <summary>API key authentication, scopes and the separation from the cookie-based panel.</summary>
public class ApiAuthTests(TestWebAppFactory f) : IClassFixture<TestWebAppFactory>
{
    private const string Monitors = "/api/v1/monitors";

    [Fact]
    public async Task Missing_key_is_401_problem_with_bearer_challenge()
    {
        var r = await f.ApiClient(null).GetAsync(Monitors);
        ApiTestHelpers.AssertProblem(r, HttpStatusCode.Unauthorized);
        Assert.Contains(r.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
    }

    [Theory]
    [InlineData("not-a-key")]
    [InlineData("tc_abcdefghij_wrongsecret")]
    [InlineData("tc_ABCDEFGHIJ_x")]
    public async Task Invalid_key_is_401(string token)
    {
        var r = await f.ApiClient(token).GetAsync(Monitors);
        ApiTestHelpers.AssertProblem(r, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Correct_prefix_with_wrong_secret_is_401()
    {
        var cid = await f.AddCustomerAsync("Auth-WrongSecret");
        var token = await f.AddApiKeyAsync(cid);
        var forged = token[..token.LastIndexOf('_')] + "_forgedsecretvalue";
        ApiTestHelpers.AssertProblem(await f.ApiClient(forged).GetAsync(Monitors), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Basic_scheme_is_401()
    {
        var c = f.ApiClient(null);
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", "dXNlcjpwdw==");
        ApiTestHelpers.AssertProblem(await c.GetAsync(Monitors), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Valid_key_works_and_records_last_use()
    {
        var cid = await f.AddCustomerAsync("Auth-Valid");
        var token = await f.AddApiKeyAsync(cid, ApiKeyScope.ReadOnly);
        var r = await f.ApiClient(token).GetAsync(Monitors);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var lastUsed = await f.WithDbAsync(db => db.ApiKeys.Where(k => k.CustomerId == cid).Select(k => k.LastUsedAtUtc).FirstAsync());
        Assert.NotNull(lastUsed);

        // Throttled: a second request within the minute does not rewrite it.
        await f.ApiClient(token).GetAsync(Monitors);
        Assert.Equal(lastUsed, await f.WithDbAsync(db => db.ApiKeys.Where(k => k.CustomerId == cid).Select(k => k.LastUsedAtUtc).FirstAsync()));
    }

    [Fact]
    public async Task Revoked_key_is_401()
    {
        var cid = await f.AddCustomerAsync("Auth-Revoked");
        var token = await f.AddApiKeyAsync(cid, revokedAtUtc: DateTime.UtcNow.AddMinutes(-1));
        var r = await f.ApiClient(token).GetAsync(Monitors);
        ApiTestHelpers.AssertProblem(r, HttpStatusCode.Unauthorized);
        Assert.Contains("revoked", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Expired_key_is_401()
    {
        var cid = await f.AddCustomerAsync("Auth-Expired");
        var token = await f.AddApiKeyAsync(cid, expiresAtUtc: DateTime.UtcNow.AddMinutes(-1));
        var r = await f.ApiClient(token).GetAsync(Monitors);
        ApiTestHelpers.AssertProblem(r, HttpStatusCode.Unauthorized);
        Assert.Contains("expired", await r.Content.ReadAsStringAsync());

        var valid = await f.AddApiKeyAsync(cid, expiresAtUtc: DateTime.UtcNow.AddDays(1));
        Assert.Equal(HttpStatusCode.OK, (await f.ApiClient(valid).GetAsync(Monitors)).StatusCode);
    }

    [Fact]
    public async Task Suspended_customer_key_is_401()
    {
        var cid = await f.AddCustomerAsync("Auth-Suspended");
        var token = await f.AddApiKeyAsync(cid);
        Assert.Equal(HttpStatusCode.OK, (await f.ApiClient(token).GetAsync(Monitors)).StatusCode);

        await f.WithDbAsync(db => db.Customers.Where(c => c.Id == cid).ExecuteUpdateAsync(s => s.SetProperty(c => c.IsActive, false)));
        var r = await f.ApiClient(token).GetAsync(Monitors);
        ApiTestHelpers.AssertProblem(r, HttpStatusCode.Unauthorized);
        Assert.Contains("suspended", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Cookie_session_cannot_call_the_api()
    {
        var browser = await f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/App")).StatusCode);

        ApiTestHelpers.AssertProblem(await browser.GetAsync(Monitors), HttpStatusCode.Unauthorized);
        var post = await browser.PostJsonAsync(Monitors, ApiTestHelpers.MonitorBody("ViaCookie"));
        ApiTestHelpers.AssertProblem(post, HttpStatusCode.Unauthorized);
        Assert.False(await f.WithDbAsync(db => db.Monitors.AnyAsync(m => m.Name == "ViaCookie")));

        // The session itself is untouched by the API request (SessionValidator / sign-out did not run there).
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/App")).StatusCode);
    }

    [Theory]
    [InlineData("/App")]
    [InlineData("/App/Monitors")]
    [InlineData("/App/ApiKeys")]
    [InlineData("/SuperAdmin")]
    public async Task Api_key_cannot_open_pages(string url)
    {
        var cid = await f.DemoCustomerIdAsync();
        var token = await f.AddApiKeyAsync(cid);
        var r = await f.ApiClient(token).GetAsync(url);
        AppTestData.AssertRedirect(r, "/Account/Login");
    }

    [Fact]
    public async Task Read_only_key_cannot_write()
    {
        var cid = await f.AddCustomerAsync("Scope-ReadOnly");
        var id = await f.AddMonitorAsync(cid, "ScopeTarget");
        var c = f.ApiClient(await f.AddApiKeyAsync(cid, ApiKeyScope.ReadOnly));

        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"{Monitors}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/status")).StatusCode);
        ApiTestHelpers.AssertProblem(await c.PostJsonAsync(Monitors, ApiTestHelpers.MonitorBody("RoCreate")), HttpStatusCode.Forbidden);
        ApiTestHelpers.AssertProblem(await c.PutJsonAsync($"{Monitors}/{id}", ApiTestHelpers.MonitorBody("RoEdit")), HttpStatusCode.Forbidden);
        ApiTestHelpers.AssertProblem(await c.DeleteAsync($"{Monitors}/{id}"), HttpStatusCode.Forbidden);
        ApiTestHelpers.AssertProblem(await c.PostAsync($"{Monitors}/{id}/pause", null), HttpStatusCode.Forbidden);
        ApiTestHelpers.AssertProblem(await c.PostAsync($"{Monitors}/{id}/resume", null), HttpStatusCode.Forbidden);

        var m = await f.GetMonitorAsync(id);
        Assert.Equal("ScopeTarget", m!.Name);
        Assert.NotEqual(MonitorStatus.Paused, m.Status);
        Assert.False(await f.WithDbAsync(db => db.Monitors.AnyAsync(x => x.Name == "RoCreate")));
    }

    [Fact]
    public async Task OpenApi_document_is_served_anonymously()
    {
        var r = await f.ApiClient(null).GetAsync("/api/v1/openapi.json");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var doc = await r.JsonAsync();
        var paths = doc.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/v1/monitors", out _));
        Assert.True(paths.TryGetProperty("/api/v1/monitors/{id}/pause", out _));
        Assert.True(paths.TryGetProperty("/api/v1/incidents", out _));
        Assert.True(paths.TryGetProperty("/api/v1/status", out _));
        Assert.False(paths.TryGetProperty("/set-language", out _));
        Assert.True(doc.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("apiKey", out _));
    }
}
