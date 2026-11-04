using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Web.Billing;
using static TriggerCheck.Tests.App.AppTestData;

namespace TriggerCheck.Tests.Billing;

/// <summary>Review fixes: no enumeration via validation, unconfirmed takeover, password reset, trial limits, startup config, SuperAdmin trial actions.</summary>
public class AccountSecurityTests(BillingTestFactory f) : IClassFixture<BillingTestFactory>
{
    private Task<int> PlanIdAsync(string name) => f.WithDbAsync(db => db.Plans.Where(p => p.Name == name).Select(p => p.Id).FirstAsync());

    private async Task<HttpResponseMessage> SignUpAsync(string email, string password, string plan = "Pro", string company = "Acme") =>
        await f.CreateBrowser().PostFormAsync("/Account/Register", new()
        {
            ["Input.CompanyName"] = company, ["Input.FullName"] = "Someone", ["Input.Email"] = email, ["Input.Password"] = password,
            ["Input.PlanId"] = (await PlanIdAsync(plan)).ToString(), ["Input.AcceptTerms"] = "true"
        });

    private static string NewEmail(string domain = "") => $"u{Guid.NewGuid():N}@{(domain.Length > 0 ? domain : Guid.NewGuid().ToString("N") + ".test")}";

    [Fact]
    public async Task Weak_password_gets_the_same_answer_for_existing_and_new_addresses()
    {
        var existing = await SignUpAsync(TestWebAppFactory.DemoAdminEmail, "weak");
        var fresh = await SignUpAsync(NewEmail(), "weak");
        Assert.Equal(HttpStatusCode.OK, existing.StatusCode);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.Contains("at least 8 characters", await existing.ReadHtmlAsync());
        Assert.Contains("at least 8 characters", await fresh.ReadHtmlAsync());
        Assert.DoesNotContain(f.Emails.To(TestWebAppFactory.DemoAdminEmail), m => m.Body.Contains("already exists"));
    }

    [Fact]
    public async Task Requests_do_not_wait_for_e_mail_delivery()
    {
        var (_, email) = await f.AddCustomerWithAdminAsync("Standard");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Emails.Gate = gate;
        try
        {
            // SMTP is "stuck": the forgot-password and duplicate sign-up answers must still come back right away.
            var forgot = f.CreateBrowser().PostFormAsync("/Account/ForgotPassword", new() { ["Input.Email"] = email });
            var duplicate = SignUpAsync(email, "Signup123!");
            var both = Task.WhenAll(forgot, duplicate);
            Assert.Same(both, await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(15))));
            Assert.Contains("data-testid=\"reset-sent\"", await (await forgot).ReadHtmlAsync());
            AssertRedirect(await duplicate, "RegisterConfirmation");
        }
        finally
        {
            f.Emails.Gate = null;
            gate.SetResult();
        }
        var mails = f.Emails.To(email);
        Assert.Contains(mails, m => m.Subject.Contains("Reset your TriggerCheck password"));
        Assert.Contains(mails, m => m.Body.Contains("already exists"));
    }

    [Fact]
    public async Task Operator_touched_unconfirmed_account_is_not_taken_over_by_a_new_sign_up()
    {
        var email = NewEmail();
        await SignUpAsync(email, "Original1!");
        var cid = await f.WithDbAsync(db => db.Customers.Where(c => c.ContactEmail == email).Select(c => c.Id).SingleAsync());
        var sa = await f.LoginAsync(TestWebAppFactory.SuperAdminEmail, TestWebAppFactory.SuperAdminPassword);
        await sa.PostFormAsync($"/SuperAdmin/Customers/Edit/{cid}", EditForm(cid, await PlanIdAsync("Standard")));

        AssertRedirect(await SignUpAsync(email, "Takeover1!", company: "Other"), "RegisterConfirmation");
        Assert.Contains(f.Emails.To(email), m => m.Body.Contains("already exists"));
        using var scope = f.Services.CreateScope();
        var um = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<TriggerCheck.Infrastructure.Identity.ApplicationUser>>();
        var user = (await um.FindByEmailAsync(email))!;
        Assert.True(await um.CheckPasswordAsync(user, "Original1!"));
        Assert.False(await um.CheckPasswordAsync(user, "Takeover1!"));
        Assert.Equal("Edited " + cid, (await f.GetCustomerAsync(cid)).Name);
    }

    [Fact]
    public async Task End_trial_always_clears_the_pending_sign_up_flag()
    {
        var email = NewEmail();
        await SignUpAsync(email, "Signup123!", plan: "Basic"); // free plan: no trial
        var cid = await f.WithDbAsync(db => db.Customers.Where(c => c.ContactEmail == email).Select(c => c.Id).SingleAsync());
        Assert.True(await f.WithDbAsync(db => db.Customers.Where(c => c.Id == cid).Select(c => c.SignupPending).SingleAsync()));
        var sa = await f.LoginAsync(TestWebAppFactory.SuperAdminEmail, TestWebAppFactory.SuperAdminPassword);
        await sa.PostFormAsync($"/SuperAdmin/Customers/Details/{cid}?handler=EndTrial", new(), $"/SuperAdmin/Customers/Details/{cid}");
        Assert.False(await f.WithDbAsync(db => db.Customers.Where(c => c.Id == cid).Select(c => c.SignupPending).SingleAsync()));
    }

    [Fact]
    public async Task Superadmin_touched_sign_ups_are_never_purged()
    {
        var email = NewEmail();
        await SignUpAsync(email, "Signup123!");
        var cid = await f.WithDbAsync(db => db.Customers.Where(c => c.ContactEmail == email).Select(c => c.Id).SingleAsync());
        var sa = await f.LoginAsync(TestWebAppFactory.SuperAdminEmail, TestWebAppFactory.SuperAdminPassword);
        await sa.PostFormAsync($"/SuperAdmin/Customers/Edit/{cid}", EditForm(cid, await PlanIdAsync("Standard")));
        Assert.False(await f.WithDbAsync(db => db.Customers.Where(c => c.Id == cid).Select(c => c.SignupPending).SingleAsync()));

        f.Time.Advance(TimeSpan.FromDays(3));
        await f.RunMaintenanceAsync();
        Assert.True(await f.WithDbAsync(db => db.Customers.AnyAsync(c => c.Id == cid)));
    }

    [Fact]
    public async Task Second_unconfirmed_sign_up_takes_over_and_old_links_stop_working()
    {
        var email = NewEmail();
        AssertRedirect(await SignUpAsync(email, "Attacker123!", company: "Evil"), "RegisterConfirmation");
        var attackerLink = f.Emails.LinkTo(email);
        AssertRedirect(await SignUpAsync(email, "Owner1234!", company: "Real Co"), "RegisterConfirmation");
        var ownerLink = f.Emails.LinkTo(email);
        Assert.NotEqual(attackerLink, ownerLink);

        Assert.Contains("not-confirmed", await f.CreateBrowser().GetStringAsync(attackerLink));
        Assert.Contains("data-testid=\"confirmed\"", await f.CreateBrowser().GetStringAsync(ownerLink));

        await f.LoginAsync(email, "Owner1234!");
        var bad = await f.CreateBrowser().PostFormAsync("/Account/Login", new() { ["Input.Email"] = email, ["Input.Password"] = "Attacker123!" });
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        var customer = await f.WithDbAsync(db => db.Customers.SingleAsync(c => c.ContactEmail == email));
        Assert.Equal("Real Co", customer.Name);
    }

    [Fact]
    public async Task Unconfirmed_sign_ups_are_purged_after_the_token_lifetime()
    {
        var email = NewEmail();
        await SignUpAsync(email, "Signup123!");
        var cid = await f.WithDbAsync(db => db.Customers.Where(c => c.ContactEmail == email).Select(c => c.Id).SingleAsync());
        var confirmedEmail = NewEmail();
        await SignUpAsync(confirmedEmail, "Signup123!");
        await f.CreateBrowser().GetStringAsync(f.Emails.LinkTo(confirmedEmail));

        await f.RunMaintenanceAsync();
        Assert.True(await f.WithDbAsync(db => db.Customers.AnyAsync(c => c.Id == cid)));
        f.Time.Advance(TimeSpan.FromDays(2.5));
        var result = await f.RunMaintenanceAsync();
        Assert.True(result.UnconfirmedPurged >= 1);
        Assert.False(await f.WithDbAsync(db => db.Customers.AnyAsync(c => c.Id == cid)));
        Assert.False(await f.WithDbAsync(db => db.Users.AnyAsync(u => u.Email == email)));
        Assert.True(await f.WithDbAsync(db => db.Users.AnyAsync(u => u.Email == confirmedEmail)));
    }

    [Fact]
    public async Task Password_reset_flow_without_enumeration()
    {
        var (_, email) = await f.AddCustomerWithAdminAsync("Standard");
        var known = await f.CreateBrowser().PostFormAsync("/Account/ForgotPassword", new() { ["Input.Email"] = email });
        var unknown = await f.CreateBrowser().PostFormAsync("/Account/ForgotPassword", new() { ["Input.Email"] = NewEmail() });
        Assert.Contains("data-testid=\"reset-sent\"", await known.ReadHtmlAsync());
        Assert.Contains("data-testid=\"reset-sent\"", await unknown.ReadHtmlAsync());

        var link = f.Emails.LinkTo(email);
        Assert.StartsWith("/Account/ResetPassword?", link);
        var reset = await f.CreateBrowser().PostFormAsync("/Account/ResetPassword", new()
        {
            ["Input.UserId"] = Query(link, "userId"), ["Input.Code"] = Query(link, "code"), ["Input.Password"] = "Changed123!", ["Input.ConfirmPassword"] = "Changed123!"
        }, link);
        Assert.Contains("data-testid=\"reset-done\"", await reset.ReadHtmlAsync());
        await f.LoginAsync(email, "Changed123!");

        // The link works once.
        var again = await f.CreateBrowser().PostFormAsync("/Account/ResetPassword", new()
        {
            ["Input.UserId"] = Query(link, "userId"), ["Input.Code"] = Query(link, "code"), ["Input.Password"] = "Again1234!", ["Input.ConfirmPassword"] = "Again1234!"
        }, link);
        Assert.Contains("invalid or has expired", await again.ReadHtmlAsync());
        Assert.Contains("/Account/ForgotPassword", await f.CreateBrowser().GetStringAsync("/Account/Login"));
    }

    [Fact]
    public async Task Password_reset_confirms_an_unconfirmed_address()
    {
        var email = NewEmail();
        await SignUpAsync(email, "Signup123!");
        await f.CreateBrowser().PostFormAsync("/Account/ForgotPassword", new() { ["Input.Email"] = email });
        var link = f.Emails.LinkTo(email);
        await f.CreateBrowser().PostFormAsync("/Account/ResetPassword", new()
        {
            ["Input.UserId"] = Query(link, "userId"), ["Input.Code"] = Query(link, "code"), ["Input.Password"] = "Mine12345!", ["Input.ConfirmPassword"] = "Mine12345!"
        }, link);
        Assert.True(await f.IsEmailConfirmedAsync(email));
        await f.LoginAsync(email, "Mine12345!");
    }

    private static string Query(string pathAndQuery, string key) =>
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri("https://x" + pathAndQuery).Query)[key].ToString();

    [Fact]
    public async Task Trial_is_granted_once_per_address_including_plus_aliases()
    {
        var domain = Guid.NewGuid().ToString("N") + ".test";
        await SignUpAsync($"ops@{domain}", "Signup123!");
        await SignUpAsync($"Ops+second@{domain}", "Signup123!");
        var second = await f.WithDbAsync(db => db.Customers.Include(c => c.Plan).SingleAsync(c => c.ContactEmail == $"Ops+second@{domain}"));
        Assert.Equal(BillingStatus.None, second.BillingStatus);
        Assert.Null(second.TrialEndsAtUtc);
        Assert.Equal("Basic", second.Plan.Name);
        // The page is the same for everyone; the e-mail says that no trial was started.
        Assert.Contains("No free trial was started", f.Emails.To($"Ops+second@{domain}").Single().Body);
        Assert.DoesNotContain("No free trial", f.Emails.To($"ops@{domain}").Single().Body);
        Assert.Equal($"ops@{domain}", TrialEligibility.Normalize($"Ops+second@{domain}"));
        Assert.Equal("johndoe@gmail.com", TrialEligibility.Normalize("John.Doe+x@googlemail.com"));
    }

    [Fact]
    public async Task Trials_per_company_domain_are_capped_but_not_for_public_mail()
    {
        var domain = Guid.NewGuid().ToString("N") + ".test";
        for (var i = 0; i < 4; i++) await SignUpAsync($"p{i}@{domain}", "Signup123!");
        var statuses = await f.WithDbAsync(db => db.Customers.Where(c => c.ContactEmail.EndsWith("@" + domain)).Select(c => c.BillingStatus).ToListAsync());
        Assert.Equal(3, statuses.Count(s => s == BillingStatus.Trial));
        Assert.Equal(1, statuses.Count(s => s == BillingStatus.None));

        var gmailUser = $"x{Guid.NewGuid():N}@gmail.com";
        await SignUpAsync(gmailUser, "Signup123!");
        Assert.Equal(BillingStatus.Trial, await f.WithDbAsync(db => db.Customers.Where(c => c.ContactEmail == gmailUser).Select(c => c.BillingStatus).SingleAsync()));
    }

    [Fact]
    public void Production_requires_a_public_base_url()
    {
        using var prod = f.WithWebHostBuilder(b => { b.UseEnvironment("Production"); b.UseSetting("Site:PublicBaseUrl", ""); });
        var ex = Assert.ThrowsAny<Exception>(() => prod.CreateClient());
        Assert.Contains("Site:PublicBaseUrl", ex.ToString());

        // The former Billing:PublicBaseUrl still works as a fallback.
        using var legacy = f.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Site:PublicBaseUrl", "");
            b.UseSetting("Billing:PublicBaseUrl", "https://legacy.example");
        });
        legacy.CreateClient();
    }

    [Fact]
    public async Task Superadmin_extends_ends_and_overrides_trials_but_not_active_subscriptions()
    {
        var sa = await f.LoginAsync(TestWebAppFactory.SuperAdminEmail, TestWebAppFactory.SuperAdminPassword);
        var (trial, trialEmail) = await f.AddCustomerWithAdminAsync("Pro", c => { c.BillingStatus = BillingStatus.Trial; c.TrialEndsAtUtc = f.Now.AddDays(2); c.TrialReminderSentAtUtc = f.Now; });

        await sa.PostFormAsync($"/SuperAdmin/Customers/Details/{trial}?handler=ExtendTrial", new() { ["days"] = "10" }, $"/SuperAdmin/Customers/Details/{trial}");
        var c = await f.GetCustomerAsync(trial);
        Assert.Equal(f.Now.AddDays(12), c.TrialEndsAtUtc!.Value, TimeSpan.FromSeconds(5));
        Assert.Null(c.TrialReminderSentAtUtc);

        await sa.PostFormAsync($"/SuperAdmin/Customers/Details/{trial}?handler=EndTrial", new(), $"/SuperAdmin/Customers/Details/{trial}");
        c = await f.GetCustomerAsync(trial);
        Assert.Equal(BillingStatus.None, c.BillingStatus);
        Assert.Equal("Basic", c.Plan.Name);
        Assert.Contains(f.Emails.To(trialEmail), m => m.Body.Contains("free trial has ended"));

        // A manual plan change clears a trial.
        var (other, _) = await f.AddCustomerWithAdminAsync("Pro", x => { x.BillingStatus = BillingStatus.Trial; x.TrialEndsAtUtc = f.Now.AddDays(5); });
        AssertRedirect(await sa.PostFormAsync($"/SuperAdmin/Customers/Edit/{other}", EditForm(other, await PlanIdAsync("Standard"))), "/SuperAdmin/Customers/Details");
        c = await f.GetCustomerAsync(other);
        Assert.Equal("Standard", c.Plan.Name);
        Assert.Equal(BillingStatus.None, c.BillingStatus);
        Assert.Null(c.TrialEndsAtUtc);

        // ... but is blocked while the customer pays through a subscription.
        var (paying, _) = await f.AddCustomerWithAdminAsync("Standard");
        await f.AddActiveSubscriptionAsync(paying, "Pro", "sa-block-" + paying);
        var blocked = await sa.PostFormAsync($"/SuperAdmin/Customers/Edit/{paying}", EditForm(paying, await PlanIdAsync("Basic")));
        Assert.Equal(HttpStatusCode.OK, blocked.StatusCode);
        Assert.Contains("active subscription", await blocked.ReadHtmlAsync());
        Assert.Equal("Pro", (await f.GetCustomerAsync(paying)).Plan.Name);
    }

    private static Dictionary<string, string> EditForm(int id, int planId) => new()
    {
        ["Input.Name"] = "Edited " + id, ["Input.ContactEmail"] = $"edited{id}@example.test", ["Input.PlanId"] = planId.ToString()
    };
}
