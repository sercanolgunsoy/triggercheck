using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Web.Api;

namespace TriggerCheck.Tests.Api;

/// <summary>/App/ApiKeys: admins create (secret shown once) and revoke; viewers only see the list.</summary>
public class ApiKeysPageTests : IClassFixture<ApiKeysPageTests.Fixture>
{
    public const string ViewerEmail = "apiviewer@demo.local";
    public const string ViewerPassword = "Viewer123!";

    public sealed class Fixture : TestWebAppFactory
    {
        private bool _ready;
        public async Task InitAsync()
        {
            if (_ready) return;
            await this.AddUserAsync(await this.DemoCustomerIdAsync(), ViewerEmail, ViewerPassword, Roles.CustomerViewer);
            _ready = true;
        }
    }

    private static readonly Regex TokenRx = new(@"value=""(tc_[a-z2-7]{10}_[A-Za-z0-9_-]+)""", RegexOptions.Compiled);

    private readonly Fixture _f;
    public ApiKeysPageTests(Fixture f) => _f = f;

    private Task<HttpClient> AdminAsync() => _f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);

    private async Task<HttpClient> ViewerAsync()
    {
        await _f.InitAsync();
        return await _f.LoginAsync(ViewerEmail, ViewerPassword);
    }

    private static Dictionary<string, string> CreateForm(string name, string scope = "ReadWrite", string days = "30") =>
        new() { ["Input.Name"] = name, ["Input.Scope"] = scope, ["Input.ExpiresInDays"] = days };

    [Fact]
    public async Task Admin_creates_a_key_that_is_shown_once_and_stored_hashed()
    {
        var c = await AdminAsync();
        var r = await c.PostFormAsync("/App/ApiKeys?handler=Create", CreateForm("Deploy"), "/App/ApiKeys");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("no-store", r.Headers.CacheControl?.ToString());
        var html = await r.ReadHtmlAsync();
        var token = TokenRx.Match(html).Groups[1].Value;
        Assert.NotEmpty(token);

        var key = await _f.WithDbAsync(db => db.ApiKeys.AsNoTracking().FirstAsync(k => k.Name == "Deploy"));
        Assert.Equal(await _f.DemoCustomerIdAsync(), key.CustomerId);
        Assert.Equal(ApiKeyScope.ReadWrite, key.Scope);
        Assert.Equal(ApiKeyTokens.Hash(token), key.SecretHash);
        Assert.DoesNotContain(token, key.SecretHash);
        Assert.NotNull(key.ExpiresAtUtc);
        Assert.Equal(TestWebAppFactory.DemoAdminEmail, key.CreatedBy);

        // The token works against the API ...
        Assert.Equal(HttpStatusCode.OK, (await _f.ApiClient(token).GetAsync("/api/v1/monitors")).StatusCode);
        // ... and is never shown again.
        var list = await c.GetHtmlAsync("/App/ApiKeys");
        Assert.DoesNotContain(token, list);
        Assert.Contains($"tc_{key.Prefix}_", list);
    }

    [Fact]
    public async Task Name_is_required()
    {
        var r = await (await AdminAsync()).PostFormAsync("/App/ApiKeys?handler=Create", CreateForm(" "), "/App/ApiKeys");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.DoesNotMatch(TokenRx, await r.ReadHtmlAsync());
    }

    [Fact]
    public async Task Admin_revokes_a_key_and_the_api_rejects_it()
    {
        var cid = await _f.DemoCustomerIdAsync();
        var token = await _f.AddApiKeyAsync(cid);
        var prefix = token.Split('_', 3)[1];
        var id = await _f.WithDbAsync(db => db.ApiKeys.Where(k => k.Prefix == prefix).Select(k => k.Id).FirstAsync());

        var r = await (await AdminAsync()).PostFormAsync($"/App/ApiKeys?handler=Revoke&id={id}", new(), "/App/ApiKeys");
        AppTestData.AssertRedirect(r, "/App/ApiKeys");
        Assert.NotNull(await _f.WithDbAsync(db => db.ApiKeys.Where(k => k.Id == id).Select(k => k.RevokedAtUtc).FirstAsync()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.ApiClient(token).GetAsync("/api/v1/monitors")).StatusCode);
    }

    [Fact]
    public async Task Other_tenants_key_cannot_be_revoked()
    {
        var other = await _f.AddCustomerAsync("KeyOwner");
        var token = await _f.AddApiKeyAsync(other);
        var id = await _f.WithDbAsync(db => db.ApiKeys.Where(k => k.CustomerId == other).Select(k => k.Id).FirstAsync());

        var c = await AdminAsync();
        var r = await c.PostFormAsync($"/App/ApiKeys?handler=Revoke&id={id}", new(), "/App/ApiKeys");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Null(await _f.WithDbAsync(db => db.ApiKeys.Where(k => k.Id == id).Select(k => k.RevokedAtUtc).FirstAsync()));
        Assert.DoesNotContain("tc_" + token.Split('_', 3)[1], await c.GetHtmlAsync("/App/ApiKeys"));
    }

    [Fact]
    public async Task Viewer_sees_the_list_but_cannot_create_or_revoke()
    {
        var cid = await _f.DemoCustomerIdAsync();
        var token = await _f.AddApiKeyAsync(cid, ApiKeyScope.ReadOnly);
        var prefix = token.Split('_', 3)[1];
        var id = await _f.WithDbAsync(db => db.ApiKeys.Where(k => k.Prefix == prefix).Select(k => k.Id).FirstAsync());
        var c = await ViewerAsync();

        var page = await c.GetHtmlAsync("/App/ApiKeys");
        Assert.Contains($"tc_{prefix}_", page);
        Assert.DoesNotContain("handler=Create", page);
        Assert.DoesNotContain("handler=Revoke", page);

        AppTestData.AssertAccessDenied(await c.PostFormAsync("/App/ApiKeys?handler=Create", CreateForm("ViewerKey"), "/App/ApiKeys"));
        AppTestData.AssertAccessDenied(await c.PostFormAsync($"/App/ApiKeys?handler=Revoke&id={id}", new(), "/App/ApiKeys"));
        Assert.False(await _f.WithDbAsync(db => db.ApiKeys.AnyAsync(k => k.Name == "ViewerKey")));
        Assert.Null(await _f.WithDbAsync(db => db.ApiKeys.Where(k => k.Id == id).Select(k => k.RevokedAtUtc).FirstAsync()));
    }

    [Fact]
    public async Task Page_is_translated()
    {
        var c = await AdminAsync();
        await c.SetLanguageAsync("tr");
        var page = await c.GetHtmlAsync("/App/ApiKeys");
        Assert.Contains("API anahtarları", page);
        Assert.Contains("Yeni API anahtarı", page);
    }
}
