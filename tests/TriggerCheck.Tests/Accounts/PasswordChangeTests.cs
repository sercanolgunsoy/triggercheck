using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Tests.SuperAdmin;

namespace TriggerCheck.Tests.Accounts;

/// <summary>SEC-2: the seeded SuperAdmin (and any user flagged MustChangePassword) must change the password first.</summary>
public class PasswordChangeTests
{
    private const string Sa = TestWebAppFactory.SuperAdminEmail;
    private const string OldPw = TestWebAppFactory.SuperAdminPassword;

    [Fact]
    public async Task Seeded_superadmin_must_change_password_before_using_the_panel()
    {
        using var f = new TestWebAppFactory();
        using var host = f.WithWebHostBuilder(b => b.UseSetting("Seed:SuperAdminMustChangePassword", "true"));
        var c = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        async Task<bool> FlagAsync()
        {
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.Users.Where(u => u.Email == Sa).Select(u => u.MustChangePassword).SingleAsync();
        }
        Assert.True(await FlagAsync());

        var login = await c.PostFormAsync("/Account/Login", new() { ["Input.Email"] = Sa, ["Input.Password"] = OldPw });
        AppTestData.AssertRedirect(login, "/Account/ChangePassword");

        // Every other page redirects to the change-password page.
        AppTestData.AssertRedirect(await c.GetAsync("/SuperAdmin"), "/Account/ChangePassword");
        AppTestData.AssertRedirect(await c.GetAsync("/SuperAdmin/Customers/Index"), "/Account/ChangePassword");
        AppTestData.AssertRedirect(await c.GetAsync("/"), "/Account/ChangePassword");
        Assert.Contains("You must choose a new password before you continue.", await c.GetHtmlAsync("/Account/ChangePassword"));

        Dictionary<string, string> Form(string current, string next) => new()
        {
            ["Input.CurrentPassword"] = current, ["Input.NewPassword"] = next, ["Input.ConfirmPassword"] = next
        };

        var same = await c.PostFormAsync("/Account/ChangePassword", Form(OldPw, OldPw));
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        Assert.Contains("The new password must be different from the current one.", await same.BodyAsync());

        var wrong = await c.PostFormAsync("/Account/ChangePassword", Form("Wrong12345", "Brandnew123"));
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Contains("The current password is incorrect.", await wrong.BodyAsync());
        Assert.True(await FlagAsync());

        var ok = await c.PostFormAsync("/Account/ChangePassword", Form(OldPw, "Brandnew123"));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.False(await FlagAsync());

        // The same session continues (cookie re-issued) and reaches the panel.
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/SuperAdmin")).StatusCode);

        // The new password works; the old one does not.
        var fresh = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var relogin = await fresh.PostFormAsync("/Account/Login", new() { ["Input.Email"] = Sa, ["Input.Password"] = "Brandnew123" });
        Assert.Equal(HttpStatusCode.Redirect, relogin.StatusCode);
        Assert.DoesNotContain("ChangePassword", relogin.Headers.Location!.ToString());
        var old = await host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true })
            .PostFormAsync("/Account/Login", new() { ["Input.Email"] = Sa, ["Input.Password"] = OldPw });
        Assert.Equal(HttpStatusCode.OK, old.StatusCode);
    }

    [Fact]
    public async Task Flagged_customer_user_is_redirected_until_password_is_changed()
    {
        using var f = new TestWebAppFactory();
        await f.WithDbAsync(async db =>
        {
            var u = await db.Users.SingleAsync(x => x.Email == TestWebAppFactory.DemoAdminEmail);
            u.MustChangePassword = true;
            await db.SaveChangesAsync();
        });
        var c = f.CreateBrowser();
        var login = await c.PostFormAsync("/Account/Login", new() { ["Input.Email"] = TestWebAppFactory.DemoAdminEmail, ["Input.Password"] = TestWebAppFactory.DemoAdminPassword, });
        AppTestData.AssertRedirect(login, "/Account/ChangePassword");
        AppTestData.AssertRedirect(await c.GetAsync("/App/Monitors/Index"), "/Account/ChangePassword");

        var ok = await c.PostFormAsync("/Account/ChangePassword?returnUrl=%2FApp%2FMonitors%2FIndex", new()
        {
            ["Input.CurrentPassword"] = TestWebAppFactory.DemoAdminPassword, ["Input.NewPassword"] = "Changed123", ["Input.ConfirmPassword"] = "Changed123"
        }, "/Account/ChangePassword");
        AppTestData.AssertRedirect(ok, "/App/Monitors/Index");
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/App/Monitors/Index")).StatusCode);
    }

    [Fact]
    public async Task Change_password_page_requires_sign_in()
    {
        using var f = new TestWebAppFactory();
        AppTestData.AssertRedirect(await f.CreateBrowser().GetAsync("/Account/ChangePassword"), "/Account/Login");
    }
}
