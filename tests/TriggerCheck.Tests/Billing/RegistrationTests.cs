using System.Net;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.Infrastructure;
using static TriggerCheck.Tests.App.AppTestData;

namespace TriggerCheck.Tests.Billing;

/// <summary>BIZ-1: self-service sign-up, e-mail confirmation, abuse protection.</summary>
public class RegistrationTests(BillingTestFactory f) : IClassFixture<BillingTestFactory>
{
    private Task<int> PlanIdAsync(string name) => f.WithDbAsync(db => db.Plans.Where(p => p.Name == name).Select(p => p.Id).FirstAsync());

    private static Dictionary<string, string> Form(string email, int planId, string password = "Signup123!", bool terms = true, string? website = null)
    {
        var d = new Dictionary<string, string>
        {
            ["Input.CompanyName"] = "Acme " + email, ["Input.FullName"] = "Ada Lovelace", ["Input.Email"] = email,
            ["Input.Password"] = password, ["Input.PlanId"] = planId.ToString(), ["Input.AcceptTerms"] = terms ? "true" : "false"
        };
        if (website is not null) d["Input.Website"] = website;
        return d;
    }

    private static string NewEmail() => $"signup-{Guid.NewGuid():N}@example.test";

    private Task<int> CustomerCountAsync() => f.WithDbAsync(db => db.Customers.CountAsync());

    [Fact]
    public async Task Sign_up_creates_a_trial_and_the_account_works_after_confirmation()
    {
        var email = NewEmail();
        var pro = await PlanIdAsync("Pro");
        var r = await f.CreateBrowser().PostFormAsync("/Account/Register", Form(email, pro));
        AssertRedirect(r, "/Account/RegisterConfirmation");

        var customer = await f.WithDbAsync(db => db.Customers.Include(c => c.Plan).FirstAsync(c => c.ContactEmail == email));
        Assert.Equal("Pro", customer.Plan.Name);
        Assert.Equal(BillingStatus.Trial, customer.BillingStatus);
        Assert.Equal(f.Now.AddDays(14), customer.TrialEndsAtUtc!.Value, TimeSpan.FromSeconds(5));
        var userId = await f.UserIdAsync(email);
        Assert.Equal([Roles.CustomerAdmin], await f.RolesOfAsync(userId));
        Assert.False(await f.IsEmailConfirmedAsync(email));

        // Not confirmed yet: the correct password gets the "confirm first" message, no session.
        var early = f.CreateBrowser();
        var login = await early.PostFormAsync("/Account/Login", new() { ["Input.Email"] = email, ["Input.Password"] = "Signup123!" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("confirm your e-mail address first", await login.ReadHtmlAsync());
        // A wrong password still gets the generic message (no enumeration of unconfirmed accounts).
        login = await early.PostFormAsync("/Account/Login", new() { ["Input.Email"] = email, ["Input.Password"] = "Wrong123!" });
        Assert.Contains("Invalid login attempt.", await login.ReadHtmlAsync());

        var link = f.Emails.LinkTo(email);
        Assert.StartsWith("/Account/ConfirmEmail?", link);
        Assert.Contains("data-testid=\"confirmed\"", await f.CreateBrowser().GetStringAsync(link));
        Assert.True(await f.IsEmailConfirmedAsync(email));

        var client = await f.LoginAsync(email, "Signup123!");
        var billing = await client.GetHtmlAsync("/App/Billing");
        Assert.Contains("data-billing=\"trial\"", billing);
    }

    [Fact]
    public async Task Tampered_confirmation_link_is_rejected()
    {
        var email = NewEmail();
        await f.CreateBrowser().PostFormAsync("/Account/Register", Form(email, await PlanIdAsync("Standard")));
        var userId = await f.UserIdAsync(email);
        var html = await f.CreateBrowser().GetStringAsync($"/Account/ConfirmEmail?userId={userId}&code=bm90LWEtdG9rZW4");
        Assert.Contains("data-testid=\"not-confirmed\"", html);
        Assert.False(await f.IsEmailConfirmedAsync(email));
    }

    [Fact]
    public async Task Duplicate_email_shows_the_same_result_and_mails_the_owner()
    {
        var before = await CustomerCountAsync();
        var r = await f.CreateBrowser().PostFormAsync("/Account/Register", Form(TestWebAppFactory.DemoAdminEmail, await PlanIdAsync("Standard")));
        AssertRedirect(r, "/Account/RegisterConfirmation");
        Assert.Equal(before, await CustomerCountAsync());
        var mail = Assert.Single(f.Emails.To(TestWebAppFactory.DemoAdminEmail));
        Assert.Contains("already exists", mail.Body);
        Assert.Contains("https://app.test/Account/Login", mail.Body);
    }

    [Fact]
    public async Task Signing_up_again_while_unconfirmed_resends_the_confirmation()
    {
        var email = NewEmail();
        var plan = await PlanIdAsync("Standard");
        await f.CreateBrowser().PostFormAsync("/Account/Register", Form(email, plan));
        var before = await CustomerCountAsync();
        AssertRedirect(await f.CreateBrowser().PostFormAsync("/Account/Register", Form(email, plan)), "/Account/RegisterConfirmation");
        Assert.Equal(before, await CustomerCountAsync());
        Assert.Equal(2, f.Emails.To(email).Count(m => m.Body.Contains("/Account/ConfirmEmail?")));
    }

    [Fact]
    public async Task Honeypot_submission_creates_nothing_and_looks_successful()
    {
        var email = NewEmail();
        var before = await CustomerCountAsync();
        var r = await f.CreateBrowser().PostFormAsync("/Account/Register", Form(email, await PlanIdAsync("Standard"), website: "http://spam.example"));
        AssertRedirect(r, "/Account/RegisterConfirmation");
        Assert.Equal(before, await CustomerCountAsync());
        Assert.Empty(f.Emails.To(email));
    }

    [Fact]
    public async Task Enterprise_is_not_offered_for_self_service()
    {
        var html = await f.CreateBrowser().GetHtmlAsync("/Account/Register");
        Assert.Contains("Pro", html);
        Assert.DoesNotContain("Enterprise", html);

        var email = NewEmail();
        var r = await f.CreateBrowser().PostFormAsync("/Account/Register", Form(email, await PlanIdAsync("Enterprise")));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Choose a plan.", await r.ReadHtmlAsync());
        Assert.False(await f.WithDbAsync(db => db.Customers.AnyAsync(c => c.ContactEmail == email)));
    }

    [Theory]
    [InlineData("Signup123!", false, "You must accept the terms")]
    [InlineData("short", true, "at least 8 characters")]
    [InlineData("alllowercase1", true, "uppercase")]
    public async Task Invalid_input_creates_nothing(string password, bool terms, string message)
    {
        var email = NewEmail();
        var r = await f.CreateBrowser().PostFormAsync("/Account/Register", Form(email, await PlanIdAsync("Standard"), password, terms));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains(message, await r.ReadHtmlAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.False(await f.WithDbAsync(db => db.Customers.AnyAsync(c => c.ContactEmail == email)));
        Assert.False(await f.WithDbAsync(db => db.Users.AnyAsync(u => u.Email == email)));
    }

    [Fact]
    public async Task Free_plan_sign_up_has_no_trial()
    {
        var email = NewEmail();
        await f.CreateBrowser().PostFormAsync("/Account/Register", Form(email, await PlanIdAsync("Basic")));
        var c = await f.WithDbAsync(db => db.Customers.FirstAsync(x => x.ContactEmail == email));
        Assert.Equal(BillingStatus.None, c.BillingStatus);
        Assert.Null(c.TrialEndsAtUtc);
    }

    [Fact]
    public async Task Marketing_pages_link_to_sign_up_and_keep_a_contact_for_Enterprise()
    {
        var pricing = await f.CreateBrowser().GetHtmlAsync("/Pricing");
        Assert.Contains($"/Account/Register?plan={await PlanIdAsync("Pro")}", pricing);
        Assert.Contains("Contact sales", pricing);
        Assert.Contains("mailto:", pricing);
        Assert.DoesNotContain("Request access", pricing);
        Assert.Contains("/Account/Register", await f.CreateBrowser().GetHtmlAsync("/"));
        Assert.Contains("/Account/Register", await f.CreateBrowser().GetHtmlAsync("/Account/Login"));
    }

    [Fact]
    public async Task Sign_up_page_is_translated()
    {
        var c = f.CreateBrowser();
        await c.SetLanguageAsync("tr");
        Assert.Contains("Hesabınızı oluşturun", await c.GetHtmlAsync("/Account/Register"));
    }
}

/// <summary>Sign-up POSTs are rate limited per client IP; showing the form is not.</summary>
public class RegistrationRateLimitTests(RegistrationRateLimitTests.Factory f) : IClassFixture<RegistrationRateLimitTests.Factory>
{
    public class Factory : BillingTestFactory
    {
        protected override int RegistrationLimit => 2;
    }

    [Fact]
    public async Task Third_sign_up_from_the_same_IP_within_an_hour_is_rejected()
    {
        var plan = await f.WithDbAsync(db => db.Plans.Where(p => p.Name == "Standard").Select(p => p.Id).FirstAsync());
        var client = f.CreateBrowser();
        Task<HttpResponseMessage> Post() => client.PostFormAsync("/Account/Register", new()
        {
            ["Input.CompanyName"] = "Rate", ["Input.FullName"] = "Rate", ["Input.Email"] = $"rate-{Guid.NewGuid():N}@example.test",
            ["Input.Password"] = "Signup123!", ["Input.PlanId"] = plan.ToString(), ["Input.AcceptTerms"] = "true"
        });

        Assert.Equal(HttpStatusCode.Redirect, (await Post()).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Post()).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Post()).StatusCode);
        // GET still works.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Account/Register")).StatusCode);
    }
}
