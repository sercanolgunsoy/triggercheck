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
        // Post/Redirect/Get: the secret is neither in the redirect nor in a cookie.
        var r = await c.PostFormAsync("/App/ApiKeys?handler=Create", CreateForm("Deploy"), "/App/ApiKeys");
        AppTestData.AssertRedirect(r, "/App/ApiKeys");
        Assert.DoesNotContain("tc_", r.Headers.Location!.ToString());
        Assert.DoesNotContain(r.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [], h => h.Contains("tc_"));

        var shown = await c.GetAsync("/App/ApiKeys");
        Assert.Contains("no-store", shown.Headers.CacheControl?.ToString());
        var token = TokenRx.Match(await shown.ReadHtmlAsync()).Groups[1].Value;
        Assert.NotEmpty(token);
        Assert.Equal(1, await _f.WithDbAsync(db => db.ApiKeys.CountAsync(k => k.Name == "Deploy")));

        var key = await _f.WithDbAsync(db => db.ApiKeys.AsNoTracking().FirstAsync(k => k.Name == "Deploy"));
        Assert.Equal(await _f.DemoCustomerIdAsync(), key.CustomerId);
        Assert.Equal(ApiKeyScope.ReadWrite, key.Scope);
        Assert.Equal(ApiKeyTokens.Hash(token), key.SecretHash);
        Assert.DoesNotContain(token, key.SecretHash);
        Assert.NotNull(key.ExpiresAtUtc);
        Assert.Equal(TestWebAppFactory.DemoAdminEmail, key.CreatedBy);

        // The token works against the API ...
        Assert.Equal(HttpStatusCode.OK, (await _f.ApiClient(token).GetAsync("/api/v1/monitors")).StatusCode);
        // ... and is never shown again (a refresh shows the list only), not even to another admin session.
        var list = await c.GetHtmlAsync("/App/ApiKeys");
        Assert.DoesNotContain(token, list);
        Assert.Contains($"tc_{key.Prefix}_", list);
        Assert.Equal(1, await _f.WithDbAsync(db => db.ApiKeys.CountAsync(k => k.Name == "Deploy")));
    }

    [Fact]
    public async Task Pending_key_is_shown_only_to_its_creator()
    {
        var creator = await AdminAsync();
        AppTestData.AssertRedirect(await creator.PostFormAsync("/App/ApiKeys?handler=Create", CreateForm("Mine"), "/App/ApiKeys"), "/App/ApiKeys");

        await _f.InitAsync();
        var viewer = await _f.LoginAsync(ViewerEmail, ViewerPassword);
        Assert.DoesNotMatch(TokenRx, await viewer.GetHtmlAsync("/App/ApiKeys"));
        Assert.Matches(TokenRx, await creator.GetHtmlAsync("/App/ApiKeys"));
    }

    [Fact]
    public async Task Active_key_cap_is_enforced()
    {
        var cid = await _f.AddCustomerAsync("Cap");
        await _f.AddUserAsync(cid, "capadmin@cap.local", "CapAdmin123!", Roles.CustomerAdmin);
        for (var i = 0; i < TriggerCheck.Web.Areas.App.Pages.ApiKeys.IndexModel.MaxActiveKeys; i++) await _f.AddApiKeyAsync(cid);
        await _f.AddApiKeyAsync(cid, revokedAtUtc: DateTime.UtcNow); // revoked keys do not count

        var c = await _f.LoginAsync("capadmin@cap.local", "CapAdmin123!");
        var r = await c.PostFormAsync("/App/ApiKeys?handler=Create", CreateForm("OneTooMany"), "/App/ApiKeys");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("You can have at most 20 active API keys", await r.ReadHtmlAsync());
        Assert.False(await _f.WithDbAsync(db => db.ApiKeys.AnyAsync(k => k.Name == "OneTooMany")));
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
