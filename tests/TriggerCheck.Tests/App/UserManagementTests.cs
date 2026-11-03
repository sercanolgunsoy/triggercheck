using System.Net;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Tests.Infrastructure;
using static TriggerCheck.Tests.App.AppTestData;

namespace TriggerCheck.Tests.App;

public class UserManagementTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _f;
    public UserManagementTests(TestWebAppFactory f) => _f = f;

    private Task<HttpClient> AdminAsync() => _f.LoginAsync(TestWebAppFactory.DemoAdminEmail, TestWebAppFactory.DemoAdminPassword);

    private Task<bool> IsActiveAsync(string userId) =>
        _f.WithDbAsync(db => db.Users.Where(u => u.Id == userId).Select(u => u.IsActive).FirstAsync());

    [Fact]
    public async Task Admin_creates_user_bound_to_own_customer()
    {
        var c = await AdminAsync();
        var r = await c.PostFormAsync("/App/Users/Create", new()
        {
            ["Input.FullName"] = "Yeni Kişi", ["Input.Email"] = "new@demo.local",
            ["Input.Password"] = "Newuser123", ["Input.ConfirmPassword"] = "Newuser123", ["Input.Role"] = Roles.CustomerViewer
        });
        AssertRedirect(r, "/App/Users");

        var id = await _f.UserIdAsync("new@demo.local");
        var cid = await _f.WithDbAsync(db => db.Users.Where(u => u.Id == id).Select(u => u.CustomerId).FirstAsync());
        Assert.Equal(await _f.DemoCustomerIdAsync(), cid);
        Assert.Equal([Roles.CustomerViewer], await _f.RolesOfAsync(id));
        Assert.Contains("new@demo.local", await c.GetHtmlAsync("/App/Users/Index"));

        // The new user can log in and use the panel.
        var nu = await _f.LoginAsync("new@demo.local", "Newuser123");
        Assert.Equal(HttpStatusCode.OK, (await nu.GetAsync("/App")).StatusCode);
    }

    [Theory]
    [InlineData("dup@demo.local", "Dupuser123", "Dupuser123", "CustomerViewer", true)]   // duplicate (created twice)
    [InlineData("weak@demo.local", "short", "short", "CustomerViewer", false)]
    [InlineData("nodigit@demo.local", "Nodigitpass", "Nodigitpass", "CustomerViewer", false)]
    [InlineData("mismatch@demo.local", "Mismatch123", "Mismatch124", "CustomerViewer", false)]
    [InlineData("badrole@demo.local", "Badrole123", "Badrole123", "SuperAdmin", false)]
    public async Task Invalid_user_creation_is_rejected(string email, string pw, string confirm, string role, bool createFirst)
    {
        var c = await AdminAsync();
        Dictionary<string, string> Form() => new()
        {
            ["Input.FullName"] = "X", ["Input.Email"] = email, ["Input.Password"] = pw, ["Input.ConfirmPassword"] = confirm, ["Input.Role"] = role
        };
        if (createFirst) AssertRedirect(await c.PostFormAsync("/App/Users/Create", Form()), "/App/Users");

        var r = await c.PostFormAsync("/App/Users/Create", Form());
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var count = await _f.WithDbAsync(db => db.Users.CountAsync(u => u.Email == email));
        Assert.Equal(createFirst ? 1 : 0, count);
        if (role == "SuperAdmin") Assert.Contains("Geçersiz rol", await r.ReadHtmlAsync());
    }

    [Theory]
    [InlineData("ToggleActive")]
    [InlineData("Delete")]
    [InlineData("ChangeRole")]
    public async Task Admin_cannot_deactivate_delete_or_demote_self(string handler)
    {
        var c = await AdminAsync();
        var me = await _f.UserIdAsync(TestWebAppFactory.DemoAdminEmail);
        var r = await c.PostFormAsync($"/App/Users/Index?handler={handler}&id={me}", new() { ["role"] = Roles.CustomerViewer }, "/App/Users/Index");
        AssertRedirect(r, "/App/Users");

        Assert.True(await IsActiveAsync(me));
        Assert.Contains(Roles.CustomerAdmin, await _f.RolesOfAsync(me));
        // Still a working admin session.
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/App/Users/Index")).StatusCode);
    }

    [Fact]
    public async Task Admin_manages_other_user_role_and_activation()
    {
        var cid = await _f.DemoCustomerIdAsync();
        var id = await _f.AddUserAsync(cid, "managed@demo.local", "Managed123", Roles.CustomerViewer);
        var stamp0 = await _f.WithDbAsync(db => db.Users.Where(u => u.Id == id).Select(u => u.SecurityStamp).FirstAsync());
        var userSession = await _f.LoginAsync("managed@demo.local", "Managed123");
        var c = await AdminAsync();

        AssertRedirect(await c.PostFormAsync($"/App/Users/Index?handler=ChangeRole&id={id}", new() { ["role"] = Roles.CustomerAdmin }, "/App/Users/Index"), "/App/Users");
        Assert.Equal([Roles.CustomerAdmin], await _f.RolesOfAsync(id));

        AssertRedirect(await c.PostFormAsync($"/App/Users/Index?handler=ChangeRole&id={id}", new() { ["role"] = Roles.CustomerViewer }, "/App/Users/Index"), "/App/Users");
        Assert.Equal([Roles.CustomerViewer], await _f.RolesOfAsync(id));

        AssertRedirect(await c.PostFormAsync($"/App/Users/Index?handler=ToggleActive&id={id}", new(), "/App/Users/Index"), "/App/Users");
        Assert.False(await IsActiveAsync(id));
        var stamp1 = await _f.WithDbAsync(db => db.Users.Where(u => u.Id == id).Select(u => u.SecurityStamp).FirstAsync());
        Assert.NotEqual(stamp0, stamp1);

        // Deactivated user's existing session is dropped and they cannot log in.
        var resp = await userSession.GetAsync("/App");
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("/Account/Login", resp.Headers.Location!.ToString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => _f.LoginAsync("managed@demo.local", "Managed123"));

        AssertRedirect(await c.PostFormAsync($"/App/Users/Index?handler=ToggleActive&id={id}", new(), "/App/Users/Index"), "/App/Users");
        Assert.True(await IsActiveAsync(id));

        AssertRedirect(await c.PostFormAsync($"/App/Users/Index?handler=Delete&id={id}", new(), "/App/Users/Index"), "/App/Users");
        Assert.False(await _f.WithDbAsync(db => db.Users.AnyAsync(u => u.Id == id)));
    }

    [Fact]
    public async Task Invalid_role_value_is_rejected()
    {
        var cid = await _f.DemoCustomerIdAsync();
        var id = await _f.AddUserAsync(cid, "rolehack@demo.local", "Rolehack123", Roles.CustomerViewer);
        var c = await AdminAsync();
        AssertRedirect(await c.PostFormAsync($"/App/Users/Index?handler=ChangeRole&id={id}", new() { ["role"] = Roles.SuperAdmin }, "/App/Users/Index"), "/App/Users");
        Assert.Equal([Roles.CustomerViewer], await _f.RolesOfAsync(id));
    }
}
