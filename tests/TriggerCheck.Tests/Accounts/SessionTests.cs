using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Domain;
using TriggerCheck.Infrastructure.Identity;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;
using static TriggerCheck.Tests.App.AppTestData;

namespace TriggerCheck.Tests.Accounts;

/// <summary>Session validation (SEC-5), the forced password change edge cases and the change-password attempt limit.</summary>
public class SessionTests(TestWebAppFactory f) : IClassFixture<TestWebAppFactory>
{
    private async Task<(string Id, HttpClient Client)> FlaggedUserAsync(string email, string password)
    {
        var id = await f.AddUserAsync(await f.DemoCustomerIdAsync(), email, password, Roles.CustomerViewer);
        await f.WithDbAsync(async db =>
        {
            (await db.Users.SingleAsync(u => u.Id == id)).MustChangePassword = true;
            await db.SaveChangesAsync();
        });
        return (id, await f.LoginAsync(email, password));
    }

    private async Task WithUserManagerAsync(Func<UserManager<ApplicationUser>, Task> action)
    {
        using var scope = f.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>());
    }

    [Fact]
    public async Task Flagged_user_can_sign_out()
    {
        var (_, c) = await FlaggedUserAsync("flag-logout@demo.local", "Flagged123");
        AssertRedirect(await c.GetAsync("/App"), "/Account/ChangePassword");
        AssertRedirect(await c.PostFormAsync("/Account/Logout", [], "/Account/ChangePassword"), "/Account/Login");
        AssertRedirect(await c.GetAsync("/App"), "/Account/Login");
    }

    [Fact]
    public async Task Flagged_user_still_sees_the_error_page()
    {
        var (_, c) = await FlaggedUserAsync("flag-error@demo.local", "Flagged123");
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/Error")).StatusCode);
    }

    [Theory]
    [InlineData("//evil.example")]
    [InlineData("https://evil.example/")]
    public async Task Change_password_return_url_stays_local(string returnUrl)
    {
        var (_, c) = await FlaggedUserAsync($"flag-return-{Guid.NewGuid():N}@demo.local", "Flagged123");
        var r = await c.PostFormAsync($"/Account/ChangePassword?returnUrl={Uri.EscapeDataString(returnUrl)}", new()
        {
            ["Input.CurrentPassword"] = "Flagged123", ["Input.NewPassword"] = "Unflagged123", ["Input.ConfirmPassword"] = "Unflagged123"
        }, "/Account/ChangePassword");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/", r.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Deleted_users_cookie_is_rejected()
    {
        var id = await f.AddUserAsync(await f.DemoCustomerIdAsync(), "deleted@demo.local", "Deleted123", Roles.CustomerAdmin);
        var c = await f.LoginAsync("deleted@demo.local", "Deleted123");
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/App")).StatusCode);

        await WithUserManagerAsync(async um => await um.DeleteAsync((await um.FindByIdAsync(id))!));
        AssertRedirect(await c.GetAsync("/App"), "/Account/Login");
    }

    [Fact]
    public async Task SuperAdmin_cookie_is_rejected_after_security_stamp_change()
    {
        var sa = await f.LoginAsync(TestWebAppFactory.SuperAdminEmail, TestWebAppFactory.SuperAdminPassword);
        Assert.Equal(HttpStatusCode.OK, (await sa.GetAsync("/SuperAdmin")).StatusCode);

        await WithUserManagerAsync(async um => await um.UpdateSecurityStampAsync((await um.FindByEmailAsync(TestWebAppFactory.SuperAdminEmail))!));
        var r = await sa.GetAsync("/SuperAdmin");
        AssertRedirect(r, "/Account/Login");
        Assert.Contains("reason=changed", r.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Static_assets_and_health_do_not_need_a_valid_session()
    {
        var id = await f.AddUserAsync(await f.DemoCustomerIdAsync(), "static@demo.local", "Static123", Roles.CustomerViewer);
        var c = await f.LoginAsync("static@demo.local", "Static123");
        await WithUserManagerAsync(async um => await um.UpdateSecurityStampAsync((await um.FindByIdAsync(id))!));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/css/site.css")).StatusCode);
        // The stale cookie is still rejected on the next page request.
        AssertRedirect(await c.GetAsync("/App"), "/Account/Login");
    }

    public static TheoryData<string, string, string> ChangePasswordForms => new()
    {
        { "/Account/ChangePassword", "Input", "/Account/ChangePassword" },
        { "/App/Account/Index?handler=ChangePassword", "Password", "/App/Account/Index" },
    };

    [Theory]
    [MemberData(nameof(ChangePasswordForms))]
    public async Task Repeated_wrong_current_password_locks_the_account_and_ends_the_session(string url, string prefix, string formPage)
    {
        var email = $"guess-{Guid.NewGuid():N}@demo.local";
        await f.AddUserAsync(await f.DemoCustomerIdAsync(), email, "Correct123", Roles.CustomerViewer);
        var c = await f.LoginAsync(email, "Correct123");
        Dictionary<string, string> Form(string current) => new()
        {
            [$"{prefix}.CurrentPassword"] = current, [$"{prefix}.NewPassword"] = "Guessed123", [$"{prefix}.ConfirmPassword"] = "Guessed123"
        };

        for (var i = 0; i < 4; i++)
        {
            var wrong = await c.PostFormAsync(url, Form($"Wrong{i}2345"), formPage);
            Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
            Assert.Contains("The current password is incorrect.", await wrong.ReadHtmlAsync());
        }
        var locked = await c.PostFormAsync(url, Form("Wrong52345"), formPage);
        AssertRedirect(locked, "reason=locked");
        Assert.Contains("The account is temporarily locked.", await c.GetHtmlAsync(locked.Headers.Location!.ToString()));

        // The session is gone and even the correct password cannot sign in while locked.
        AssertRedirect(await c.GetAsync("/App"), "/Account/Login");
        var login = await f.CreateBrowser().PostFormAsync("/Account/Login", new() { ["Input.Email"] = email, ["Input.Password"] = "Correct123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("The account is temporarily locked.", await login.ReadHtmlAsync());
    }

    [Fact]
    public async Task Successful_change_resets_the_failed_attempt_counter()
    {
        var id = await f.AddUserAsync(await f.DemoCustomerIdAsync(), "reset-count@demo.local", "Correct123", Roles.CustomerViewer);
        var c = await f.LoginAsync("reset-count@demo.local", "Correct123");
        Dictionary<string, string> Form(string current, string next) => new()
        {
            ["Input.CurrentPassword"] = current, ["Input.NewPassword"] = next, ["Input.ConfirmPassword"] = next
        };
        await c.PostFormAsync("/Account/ChangePassword", Form("Wrong12345", "Another123"));
        Assert.Equal(1, await f.WithDbAsync(db => db.Users.Where(u => u.Id == id).Select(u => u.AccessFailedCount).SingleAsync()));
        AssertRedirect(await c.PostFormAsync("/Account/ChangePassword", Form("Correct123", "Another123")), "/");
        Assert.Equal(0, await f.WithDbAsync(db => db.Users.Where(u => u.Id == id).Select(u => u.AccessFailedCount).SingleAsync()));
    }
}
