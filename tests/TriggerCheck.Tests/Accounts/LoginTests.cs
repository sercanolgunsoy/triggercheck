using System.Net;
using TriggerCheck.Domain;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Tests.SuperAdmin;

namespace TriggerCheck.Tests.Accounts;

/// <summary>UI-5: inactive / suspended accounts get a specific message, but only after a correct password.</summary>
public class LoginTests(TestWebAppFactory f) : IClassFixture<TestWebAppFactory>
{
    private const string Suspended = "Your account is suspended. Please contact your administrator.";
    private const string Invalid = "Invalid login attempt.";

    private async Task<string> LoginBodyAsync(string email, string password)
    {
        var r = await f.TryLoginAsync(email, password);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode); // never signed in
        return await r.BodyAsync();
    }

    [Fact]
    public async Task Deactivated_user_sees_suspended_message_only_with_correct_password()
    {
        var cid = await f.DemoCustomerIdAsync();
        await f.AddUserAsync(cid, "deactivated@demo.local", "Deactivated123", Roles.CustomerViewer, isActive: false);

        var ok = await LoginBodyAsync("deactivated@demo.local", "Deactivated123");
        Assert.Contains(Suspended, ok);
        Assert.DoesNotContain(Invalid, ok);

        var wrong = await LoginBodyAsync("deactivated@demo.local", "Wrong12345");
        Assert.Contains(Invalid, wrong);
        Assert.DoesNotContain(Suspended, wrong);
    }

    [Fact]
    public async Task Suspended_customer_user_sees_suspended_message_only_with_correct_password()
    {
        var cid = await f.AddCustomerAsync("Suspended Co");
        await f.WithDbAsync(async db =>
        {
            (await db.Customers.FindAsync(cid))!.IsActive = false;
            await db.SaveChangesAsync();
        });
        await f.AddUserAsync(cid, "user@suspended.test", "Suspended123", Roles.CustomerAdmin);

        var ok = await LoginBodyAsync("user@suspended.test", "Suspended123");
        Assert.Contains(Suspended, ok);
        Assert.DoesNotContain(Invalid, ok);

        var wrong = await LoginBodyAsync("user@suspended.test", "Wrong12345");
        Assert.Contains(Invalid, wrong);
        Assert.DoesNotContain(Suspended, wrong);

        // Unknown accounts get the same generic message as a wrong password.
        var unknown = await LoginBodyAsync("nobody@suspended.test", "Suspended123");
        Assert.Contains(Invalid, unknown);
        Assert.DoesNotContain(Suspended, unknown);
    }

    [Fact]
    public async Task Suspended_message_is_translated()
    {
        var cid = await f.DemoCustomerIdAsync();
        await f.AddUserAsync(cid, "deactivated-tr@demo.local", "Deactivated123", Roles.CustomerViewer, isActive: false);
        var c = f.CreateBrowser();
        await c.SetLanguageAsync("tr");
        var r = await c.PostFormAsync("/Account/Login", new() { ["Input.Email"] = "deactivated-tr@demo.local", ["Input.Password"] = "Deactivated123" });
        Assert.Contains("Hesabınız askıya alınmış.", await r.BodyAsync());
    }
}
