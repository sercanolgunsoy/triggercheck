using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Web.Billing;
using static TriggerCheck.Tests.App.AppTestData;

namespace TriggerCheck.Tests.Billing;

/// <summary>BIZ-2: checkout with server-side verification, plan changes, cancellation, roles and tenant isolation (fake provider).</summary>
public class SubscriptionFlowTests : IClassFixture<BillingTestFactory>, IAsyncLifetime
{
    private readonly BillingTestFactory _f;
    public SubscriptionFlowTests(BillingTestFactory f) => _f = f;

    public Task InitializeAsync() => _f.LinkPlansToIyzicoAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private Task<int> PlanIdAsync(string name) => _f.WithDbAsync(db => db.Plans.Where(p => p.Name == name).Select(p => p.Id).FirstAsync());

    private static Dictionary<string, string> Contact() => new()
    {
        ["Input.Name"] = "Ada", ["Input.Surname"] = "Lovelace", ["Input.Phone"] = "+905350000000", ["Input.Address"] = "Street 1",
        ["Input.City"] = "Istanbul", ["Input.Country"] = "Turkey", ["Input.ZipCode"] = "34000"
    };

    private async Task<(int CustomerId, HttpClient Client)> TrialAdminAsync(string plan = "Standard")
    {
        var (cid, email) = await _f.AddCustomerWithAdminAsync(plan, c => { c.BillingStatus = BillingStatus.Trial; c.TrialEndsAtUtc = _f.Now.AddDays(10); });
        return (cid, await _f.LoginAsync(email, BillingTestFactory.Password));
    }

    /// <summary>Runs the checkout form and returns the provider token of the pending subscription.</summary>
    private async Task<string> StartCheckoutAsync(HttpClient client, int cid, string plan)
    {
        var planId = await PlanIdAsync(plan);
        var r = await client.PostFormAsync($"/App/Billing/Checkout?planId={planId}", Contact());
        AssertRedirect(r, "subscriptionId=");
        var page = await client.GetHtmlAsync(r.Headers.Location!.ToString());
        Assert.Matches("data-testid=\"pay\"", page);
        return await _f.WithDbAsync(db => db.Subscriptions.Where(s => s.CustomerId == cid && s.Status == SubscriptionStatus.Pending)
            .OrderByDescending(s => s.Id).Select(s => s.CheckoutToken!).FirstAsync());
    }

    private Task<HttpResponseMessage> CallbackAsync(string token, Dictionary<string, string>? extra = null)
    {
        var fields = new Dictionary<string, string>(extra ?? []) { ["token"] = token };
        // The provider posts cross-site: no cookies, no antiforgery token.
        return _f.CreateBrowser().PostAsync("/billing/iyzico/callback", new FormUrlEncodedContent(fields));
    }

    [Fact]
    public async Task Checkout_is_activated_only_after_server_side_verification()
    {
        var (cid, client) = await TrialAdminAsync();
        var token = await StartCheckoutAsync(client, cid, "Pro");
        var request = _f.Payments.Checkouts.Last();
        Assert.Equal("pp-pro", request.PricingPlanReferenceCode);
        Assert.Equal("https://app.test/billing/iyzico/callback", request.CallbackUrl);
        Assert.Equal("Ada", request.Contact.Name);

        // A forged "success" from the browser is ignored: the provider has not confirmed the payment (still pending).
        AssertRedirect(await CallbackAsync(token, new() { ["status"] = "success" }), "checkout=pending");
        var c = await _f.GetCustomerAsync(cid);
        Assert.Equal(BillingStatus.Trial, c.BillingStatus);
        Assert.Equal("Standard", c.Plan.Name);
        Assert.Equal(SubscriptionStatus.Pending, await StatusOfAsync(token));
        // The provider definitively says no: closed as failed.
        _f.Payments.Reject(token);
        AssertRedirect(await CallbackAsync(token), "checkout=failed");
        Assert.Equal(SubscriptionStatus.Failed, await StatusOfAsync(token));

        // A new checkout that the provider confirms.
        token = await StartCheckoutAsync(client, cid, "Pro");
        _f.Payments.Approve(token, "pp-pro", "ref-" + cid);
        AssertRedirect(await CallbackAsync(token), "checkout=success");
        AssertRedirect(await CallbackAsync(token), "checkout=success"); // repeated callback: no-op

        c = await _f.GetCustomerAsync(cid);
        Assert.Equal(BillingStatus.Active, c.BillingStatus);
        Assert.Equal("Pro", c.Plan.Name);
        var sub = await _f.WithDbAsync(db => db.Subscriptions.AsNoTracking().FirstAsync(s => s.CheckoutToken == token));
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.Equal("ref-" + cid, sub.ProviderReference);
        Assert.NotNull(sub.CurrentPeriodEndUtc);
        Assert.Equal(1, await _f.WithDbAsync(db => db.PaymentEvents.CountAsync(e => e.CustomerId == cid && e.Succeeded)));

        var html = await client.GetHtmlAsync("/App/Billing?checkout=success");
        Assert.Contains("data-testid=\"checkout-success\"", html);
        Assert.Contains("data-testid=\"renewal\"", html);
        Assert.Contains("data-billing=\"active\"", html);
    }

    private Task<SubscriptionStatus> StatusOfAsync(string token) =>
        _f.WithDbAsync(db => db.Subscriptions.Where(s => s.CheckoutToken == token).Select(s => s.Status).FirstAsync());

    [Theory]
    [InlineData("pp-standard")]
    [InlineData(null)]
    public async Task Paid_checkout_for_another_or_no_pricing_plan_is_cancelled_at_the_provider(string? reportedPlan)
    {
        var (cid, client) = await TrialAdminAsync();
        var token = await StartCheckoutAsync(client, cid, "Pro");
        var reference = "mm-" + cid;
        _f.Payments.Results[token] = new CheckoutResult(ProviderSubscriptionState.Active, reference, "c-" + cid, reportedPlan);
        AssertRedirect(await CallbackAsync(token), "checkout=refused");
        Assert.Equal(BillingStatus.Trial, (await _f.GetCustomerAsync(cid)).BillingStatus);
        Assert.Contains(reference, _f.Payments.Cancels); // nobody keeps paying for a plan they did not get
        Assert.Equal(SubscriptionStatus.Failed, await StatusOfAsync(token));
        // A later callback or reconciliation never re-activates the refused (cancelled) subscription.
        AssertRedirect(await CallbackAsync(token), "checkout=failed");
    }

    [Fact]
    public async Task Closed_tab_checkout_is_activated_by_the_webhook()
    {
        var (cid, client) = await TrialAdminAsync();
        var token = await StartCheckoutAsync(client, cid, "Pro");
        var reference = "tab-" + cid;
        _f.Payments.Approve(token, "pp-pro", reference);
        // No callback: the customer closed the tab. The first order webhook arrives for a subscription we do not know yet.
        var r = await WebhookTests.SendSignedAsync(_f, "subscription.order.success", reference, "o-" + cid, "cust-" + reference);
        Assert.Equal(System.Net.HttpStatusCode.OK, r.StatusCode);
        // The webhook request only records the event; the billing job does the provider re-check and applies it.
        Assert.Equal(BillingStatus.Trial, (await _f.GetCustomerAsync(cid)).BillingStatus);
        var result = await _f.RunMaintenanceAsync();
        Assert.Equal(1, result.EventsReconciled);
        var c = await _f.GetCustomerAsync(cid);
        Assert.Equal(BillingStatus.Active, c.BillingStatus);
        Assert.Equal("Pro", c.Plan.Name);
        var sub = await _f.WithDbAsync(db => db.Subscriptions.AsNoTracking().FirstAsync(s => s.CheckoutToken == token));
        Assert.Equal(reference, sub.ProviderReference);
        Assert.Equal("cust-" + reference, sub.ProviderCustomerReference);
        var e = await _f.WithDbAsync(db => db.PaymentEvents.AsNoTracking().SingleAsync(x => x.OrderReference == "o-" + cid));
        Assert.False(e.Ignored);
        Assert.Equal(cid, e.CustomerId);
    }

    [Fact]
    public async Task Pending_checkout_is_rechecked_by_the_job_after_a_transient_failure()
    {
        var (cid, client) = await TrialAdminAsync();
        var token = await StartCheckoutAsync(client, cid, "Pro");
        _f.Payments.Unreachable = true;
        try
        {
            _f.Payments.Approve(token, "pp-pro", "tr-" + cid);
            AssertRedirect(await CallbackAsync(token), "checkout=pending"); // provider not reachable: not failed
            Assert.Equal(SubscriptionStatus.Pending, await StatusOfAsync(token));
        }
        finally { _f.Payments.Unreachable = false; }

        _f.Time.Advance(TimeSpan.FromMinutes(11));
        var result = await _f.RunMaintenanceAsync();
        Assert.True(result.CheckoutsResolved >= 1);
        Assert.Equal(SubscriptionStatus.Active, await StatusOfAsync(token));
        Assert.Equal(BillingStatus.Active, (await _f.GetCustomerAsync(cid)).BillingStatus);
    }

    [Fact]
    public async Task Unconfirmed_checkout_is_closed_after_the_timeout()
    {
        var (cid, client) = await TrialAdminAsync();
        var token = await StartCheckoutAsync(client, cid, "Pro");
        _f.Time.Advance(TimeSpan.FromMinutes(20));
        await _f.RunMaintenanceAsync();
        Assert.Equal(SubscriptionStatus.Pending, await StatusOfAsync(token)); // still open, provider says pending
        _f.Time.Advance(TimeSpan.FromMinutes(45));
        await _f.RunMaintenanceAsync();
        Assert.Equal(SubscriptionStatus.Failed, await StatusOfAsync(token));
        Assert.Equal(BillingStatus.Trial, (await _f.GetCustomerAsync(cid)).BillingStatus);
    }

    [Fact]
    public async Task No_answer_at_the_timeout_is_asked_again_and_a_late_payment_is_still_found()
    {
        var (cid, client) = await TrialAdminAsync();
        var token = await StartCheckoutAsync(client, cid, "Pro");
        _f.Payments.Unreachable = true;
        try
        {
            _f.Time.Advance(TimeSpan.FromMinutes(61));
            _f.Payments.Retrievals.Clear();
            await _f.RunMaintenanceAsync();
            Assert.Equal(2, _f.Payments.Retrievals[token]); // final check retried once before closing
            Assert.Equal(SubscriptionStatus.Failed, await StatusOfAsync(token));
        }
        finally { _f.Payments.Unreachable = false; }

        // iyzico confirms the payment later: the job re-scans failed checkouts (every 6 h, for 30 days).
        _f.Payments.Approve(token, "pp-pro", "late-" + cid);
        await _f.RunMaintenanceAsync();
        Assert.Equal(SubscriptionStatus.Failed, await StatusOfAsync(token)); // throttled: checked less than 6 h ago
        _f.Time.Advance(TimeSpan.FromHours(7));
        var result = await _f.RunMaintenanceAsync();
        Assert.True(result.LateCheckoutsActivated >= 1);
        Assert.Equal(SubscriptionStatus.Active, await StatusOfAsync(token));
        Assert.Equal(BillingStatus.Active, (await _f.GetCustomerAsync(cid)).BillingStatus);
    }

    [Fact]
    public async Task Booked_downgrade_rechecks_the_monitor_count_when_it_applies()
    {
        var (cid, email) = await _f.AddCustomerWithAdminAsync("Pro");
        await _f.AddActiveSubscriptionAsync(cid, "Pro", "ol-" + cid);
        var client = await _f.LoginAsync(email, BillingTestFactory.Password);
        await _f.WithDbAsync(async db => { (await db.Plans.FirstAsync(p => p.Name == "Standard")).MaxMonitors = 2; await db.SaveChangesAsync(); });
        try
        {
            await _f.AddMonitorAsync(cid, "Keep-1");
            AssertRedirect(await client.PostFormAsync($"/App/Billing/Index?handler=ChangePlan&planId={await PlanIdAsync("Standard")}", new(), "/App/Billing"), "/App/Billing");
            // Monitors added after the downgrade was booked.
            await _f.AddMonitorAsync(cid, "Keep-2");
            var fast = await _f.AddMonitorAsync(cid, "Extra-3", m => m.IntervalSeconds = 30);
            await _f.AddMonitorAsync(cid, "Extra-4");

            _f.Time.Advance(TimeSpan.FromDays(21));
            await _f.RunMaintenanceAsync();
            Assert.Equal("Standard", (await _f.GetCustomerAsync(cid)).Plan.Name);
            Assert.Equal(4, await _f.WithDbAsync(db => db.Monitors.CountAsync(m => m.CustomerId == cid))); // nothing deleted
            Assert.Equal(60, (await _f.GetMonitorAsync(fast))!.IntervalSeconds);
            var mail = Assert.Single(_f.Emails.To(email), m => m.Body.Contains("over the limit"));
            Assert.Contains("Extra-3", mail.Body);
            Assert.Contains("Extra-4", mail.Body);
            Assert.DoesNotContain("Keep-1", mail.Body);
        }
        finally
        {
            await _f.WithDbAsync(async db => { (await db.Plans.FirstAsync(p => p.Name == "Standard")).MaxMonitors = 50; await db.SaveChangesAsync(); });
        }
    }

    [Fact]
    public async Task Two_tabs_paid_twice_keep_one_subscription_and_cancel_the_other()
    {
        var (cid, client) = await TrialAdminAsync();
        var first = await StartCheckoutAsync(client, cid, "Pro");
        var second = await StartCheckoutAsync(client, cid, "Pro");
        Assert.Equal(SubscriptionStatus.Superseded, await StatusOfAsync(first));
        Assert.Equal(SubscriptionStatus.Pending, await StatusOfAsync(second));

        _f.Payments.Approve(first, "pp-pro", "tab1-" + cid);
        _f.Payments.Approve(second, "pp-pro", "tab2-" + cid);
        AssertRedirect(await CallbackAsync(second), "checkout=success");
        // The second payment is refused (never "success"), cancelled, and both operators and customer are told.
        AssertRedirect(await CallbackAsync(first), "checkout=refused");
        Assert.Contains(_f.Emails.To("hello@triggercheck.local"), m => m.Subject.Contains("Refund needed") && m.Body.Contains("tab1-" + cid));
        var adminEmail = await _f.WithDbAsync(db => db.Customers.Where(x => x.Id == cid).Select(x => x.ContactEmail).FirstAsync());
        Assert.Contains(_f.Emails.To(adminEmail), m => m.Body.Contains("will be refunded"));

        Assert.Equal(SubscriptionStatus.Active, await StatusOfAsync(second));
        Assert.Equal(SubscriptionStatus.Failed, await StatusOfAsync(first));
        Assert.Contains("tab1-" + cid, _f.Payments.Cancels);
        Assert.DoesNotContain("tab2-" + cid, _f.Payments.Cancels);
        Assert.Equal(1, await _f.WithDbAsync(db => db.Subscriptions.CountAsync(s => s.CustomerId == cid && s.Status == SubscriptionStatus.Active)));
    }

    [Fact]
    public async Task Unknown_or_missing_token_fails()
    {
        AssertRedirect(await CallbackAsync("tok-unknown"), "checkout=failed");
        AssertRedirect(await _f.CreateBrowser().PostAsync("/billing/iyzico/callback", new FormUrlEncodedContent([])), "checkout=failed");
    }

    [Fact]
    public async Task Checkout_for_a_plan_too_small_for_the_monitors_explains_what_to_delete()
    {
        var (cid, client) = await TrialAdminAsync("Pro");
        await _f.WithDbAsync(async db => { (await db.Plans.FirstAsync(p => p.Name == "Standard")).MaxMonitors = 2; await db.SaveChangesAsync(); });
        try
        {
            for (var i = 0; i < 3; i++) await _f.AddMonitorAsync(cid, "M" + i);
            var r = await client.PostFormAsync($"/App/Billing/Checkout?planId={await PlanIdAsync("Standard")}", Contact());
            AssertRedirect(r, "/App/Billing");
            Assert.Contains("Delete 1 monitor(s) first", await client.GetHtmlAsync("/App/Billing"));
            Assert.False(await _f.WithDbAsync(db => db.Subscriptions.AnyAsync(s => s.CustomerId == cid)));
        }
        finally
        {
            await _f.WithDbAsync(async db => { (await db.Plans.FirstAsync(p => p.Name == "Standard")).MaxMonitors = 50; await db.SaveChangesAsync(); });
        }
    }

    [Fact]
    public async Task Downgrade_clamps_intervals_and_is_validated_against_the_monitor_count()
    {
        var (cid, email) = await _f.AddCustomerWithAdminAsync("Pro");
        await _f.AddActiveSubscriptionAsync(cid, "Pro", "dg-" + cid);
        var client = await _f.LoginAsync(email, BillingTestFactory.Password);
        var fast = await _f.AddMonitorAsync(cid, "Fast", m => m.IntervalSeconds = 30);
        await _f.AddMonitorAsync(cid, "Other");

        await _f.WithDbAsync(async db => { (await db.Plans.FirstAsync(p => p.Name == "Standard")).MaxMonitors = 1; await db.SaveChangesAsync(); });
        try
        {
            var r = await client.PostFormAsync($"/App/Billing/Index?handler=ChangePlan&planId={await PlanIdAsync("Standard")}", new(), "/App/Billing");
            AssertRedirect(r, "/App/Billing");
            Assert.Contains("Delete 1 monitor(s) first", await client.GetHtmlAsync("/App/Billing"));
            Assert.Equal("Pro", (await _f.GetCustomerAsync(cid)).Plan.Name);
            Assert.DoesNotContain(_f.Payments.PlanChanges, x => x.Reference == "dg-" + cid);
        }
        finally
        {
            await _f.WithDbAsync(async db => { (await db.Plans.FirstAsync(p => p.Name == "Standard")).MaxMonitors = 50; await db.SaveChangesAsync(); });
        }

        // A valid downgrade is booked for the next period: the paid plan stays until then.
        var ok = await client.PostFormAsync($"/App/Billing/Index?handler=ChangePlan&planId={await PlanIdAsync("Standard")}", new(), "/App/Billing");
        AssertRedirect(ok, "/App/Billing");
        Assert.Contains(("dg-" + cid, "pp-standard", false), _f.Payments.PlanChanges);
        Assert.Equal("Pro", (await _f.GetCustomerAsync(cid)).Plan.Name);
        Assert.Equal(30, (await _f.GetMonitorAsync(fast))!.IntervalSeconds);
        var html = await client.GetHtmlAsync("/App/Billing");
        Assert.Contains("takes effect on", html);
        Assert.Contains("data-testid=\"scheduled\"", html);

        // At the end of the period (renewal webhook or the job) the new plan applies and intervals are clamped.
        _f.Time.Advance(TimeSpan.FromDays(21));
        await _f.RunMaintenanceAsync();
        Assert.Equal("Standard", (await _f.GetCustomerAsync(cid)).Plan.Name);
        Assert.Equal(60, (await _f.GetMonitorAsync(fast))!.IntervalSeconds);
        Assert.Null(await _f.WithDbAsync(db => db.Subscriptions.Where(s => s.CustomerId == cid).Select(s => s.ScheduledPlanId).FirstAsync()));
    }

    [Fact]
    public async Task Upgrade_applies_immediately()
    {
        var (cid, email) = await _f.AddCustomerWithAdminAsync("Standard");
        await _f.AddActiveSubscriptionAsync(cid, "Standard", "up-" + cid);
        var client = await _f.LoginAsync(email, BillingTestFactory.Password);
        AssertRedirect(await client.PostFormAsync($"/App/Billing/Index?handler=ChangePlan&planId={await PlanIdAsync("Pro")}", new(), "/App/Billing"), "/App/Billing");
        Assert.Equal("Pro", (await _f.GetCustomerAsync(cid)).Plan.Name);
        Assert.Contains(("up-" + cid, "pp-pro", true), _f.Payments.PlanChanges);
        Assert.DoesNotContain("prorated", await client.GetHtmlAsync("/App/Billing"));
    }

    [Fact]
    public async Task Provider_failure_leaves_the_plan_unchanged()
    {
        var (cid, email) = await _f.AddCustomerWithAdminAsync("Standard");
        await _f.AddActiveSubscriptionAsync(cid, "Standard", "pf-" + cid);
        var client = await _f.LoginAsync(email, BillingTestFactory.Password);
        _f.Payments.FailNextCall = true;
        await client.PostFormAsync($"/App/Billing/Index?handler=ChangePlan&planId={await PlanIdAsync("Pro")}", new(), "/App/Billing");
        Assert.Equal("Standard", (await _f.GetCustomerAsync(cid)).Plan.Name);
        Assert.Contains("did not accept the request", await client.GetHtmlAsync("/App/Billing"));
    }

    [Fact]
    public async Task Cancel_keeps_the_plan_until_period_end()
    {
        var (cid, email) = await _f.AddCustomerWithAdminAsync("Standard");
        await _f.AddActiveSubscriptionAsync(cid, "Pro", "cx-" + cid);
        var client = await _f.LoginAsync(email, BillingTestFactory.Password);
        AssertRedirect(await client.PostFormAsync("/App/Billing/Index?handler=Cancel", new(), "/App/Billing"), "/App/Billing");

        var c = await _f.GetCustomerAsync(cid);
        Assert.Equal(BillingStatus.Cancelled, c.BillingStatus);
        Assert.Equal("Pro", c.Plan.Name);
        Assert.Contains("cx-" + cid, _f.Payments.Cancels);
        var sub = await _f.WithDbAsync(db => db.Subscriptions.AsNoTracking().FirstAsync(s => s.CustomerId == cid));
        Assert.True(sub.CancelAtPeriodEnd);
        Assert.Equal(SubscriptionStatus.Cancelled, sub.Status);
        // Cancelling twice is not possible.
        await client.PostFormAsync("/App/Billing/Index?handler=Cancel", new(), "/App/Billing");
        Assert.Single(_f.Payments.Cancels, x => x == "cx-" + cid);
    }

    [Fact]
    public async Task Viewer_sees_billing_read_only()
    {
        var (cid, _) = await _f.AddCustomerWithAdminAsync("Standard");
        await _f.AddActiveSubscriptionAsync(cid, "Standard", "vw-" + cid);
        var viewerEmail = $"viewer-{Guid.NewGuid():N}@billing.test";
        await _f.AddUserAsync(cid, viewerEmail, BillingTestFactory.Password, Roles.CustomerViewer);
        var viewer = await _f.LoginAsync(viewerEmail, BillingTestFactory.Password);

        var html = await viewer.GetHtmlAsync("/App/Billing");
        Assert.Contains("data-testid=\"usage\"", html);
        Assert.Contains("Only administrators can change the plan.", html);
        Assert.DoesNotContain("handler=ChangePlan", html);
        Assert.DoesNotContain("handler=Cancel", html);

        AssertAccessDenied(await viewer.PostFormAsync($"/App/Billing/Index?handler=ChangePlan&planId={await PlanIdAsync("Pro")}", new(), "/App/Billing"));
        AssertAccessDenied(await viewer.PostFormAsync("/App/Billing/Index?handler=Cancel", new(), "/App/Billing"));
        AssertAccessDenied(await viewer.GetAsync($"/App/Billing/Checkout?planId={await PlanIdAsync("Pro")}"));
        Assert.Equal("Standard", (await _f.GetCustomerAsync(cid)).Plan.Name);
        Assert.DoesNotContain("vw-" + cid, _f.Payments.Cancels);
    }

    [Fact]
    public async Task Billing_data_is_tenant_isolated()
    {
        var (otherId, _) = await _f.AddCustomerWithAdminAsync("Pro");
        var otherSub = await _f.AddActiveSubscriptionAsync(otherId, "Pro", "iso-" + otherId);
        await _f.WithDbAsync(async db =>
        {
            db.PaymentEvents.Add(new PaymentEvent { CustomerId = otherId, SubscriptionId = otherSub, ProviderEventId = "iso-evt-" + otherId, EventType = "subscription.order.success", Succeeded = true, PlanName = "SecretPlanName", OccurredAtUtc = _f.Now, ReceivedAtUtc = _f.Now });
            db.Subscriptions.Add(new Subscription { CustomerId = otherId, PlanId = (await db.Plans.FirstAsync(p => p.Name == "Pro")).Id, Status = SubscriptionStatus.Pending, Provider = "fake", CheckoutToken = "iso-tok", CheckoutUrl = "https://pay.example/other-secret" });
            await db.SaveChangesAsync();
        });
        var pendingId = await _f.WithDbAsync(db => db.Subscriptions.Where(s => s.CheckoutToken == "iso-tok").Select(s => s.Id).FirstAsync());

        var (_, client) = await TrialAdminAsync();
        var html = await client.GetHtmlAsync("/App/Billing");
        Assert.DoesNotContain("SecretPlanName", html);
        Assert.Contains("data-billing=\"trial\"", html);

        var r = await client.GetAsync($"/App/Billing/Checkout?subscriptionId={pendingId}");
        AssertRedirect(r, "/App/Billing");
        // Another tenant's subscription cannot be changed or cancelled from here either.
        await client.PostFormAsync("/App/Billing/Index?handler=Cancel", new(), "/App/Billing");
        Assert.DoesNotContain("iso-" + otherId, _f.Payments.Cancels);
        Assert.Equal(BillingStatus.Active, (await _f.GetCustomerAsync(otherId)).BillingStatus);
    }

    [Fact]
    public async Task Superadmin_sees_trial_and_subscription_state()
    {
        var (trialId, _) = await TrialAdminAsync();
        var (subId, _) = await _f.AddCustomerWithAdminAsync("Standard");
        await _f.AddActiveSubscriptionAsync(subId, "Pro", "sa-" + subId);
        var sa = await _f.LoginAsync(TestWebAppFactory.SuperAdminEmail, TestWebAppFactory.SuperAdminPassword);

        var list = await sa.GetHtmlAsync("/SuperAdmin/Customers");
        Assert.Contains("data-billing=\"trial\"", list);
        Assert.Contains("data-billing=\"active\"", list);
        Assert.Contains("Trial ends", list);

        var details = await sa.GetHtmlAsync($"/SuperAdmin/Customers/Details/{subId}");
        Assert.Contains("data-testid=\"billing\"", details);
        Assert.Contains("sa-" + subId, details);
        Assert.Contains("Trial ends", await sa.GetHtmlAsync($"/SuperAdmin/Customers/Details/{trialId}"));
    }

    [Fact]
    public async Task Superadmin_edits_the_iyzico_references_of_a_plan()
    {
        var sa = await _f.LoginAsync(TestWebAppFactory.SuperAdminEmail, TestWebAppFactory.SuperAdminPassword);
        var id = await _f.WithDbAsync(async db =>
        {
            var p = new Plan { Name = "Iyz-" + Guid.NewGuid().ToString("N")[..6], MinIntervalSeconds = 60, MaxMonitors = 5, RetentionDays = 30, MonthlyPrice = 9 };
            db.Plans.Add(p);
            await db.SaveChangesAsync();
            return p.Id;
        });
        var form = new Dictionary<string, string>
        {
            ["Input.Name"] = "Iyz-edited-" + id, ["Input.MinIntervalSeconds"] = "60", ["Input.MaxMonitors"] = "5", ["Input.RetentionDays"] = "30",
            ["Input.MonthlyPrice"] = "9", ["Input.IsActive"] = "true", ["Input.AllowsSelfSignup"] = "false",
            ["Input.IyzicoProductReferenceCode"] = "prod-123", ["Input.IyzicoPricingPlanReferenceCode"] = "price-456"
        };
        AssertRedirect(await sa.PostFormAsync($"/SuperAdmin/Plans/Edit/{id}", form), "/SuperAdmin/Plans");
        var plan = await _f.WithDbAsync(db => db.Plans.AsNoTracking().FirstAsync(p => p.Id == id));
        Assert.Equal("prod-123", plan.IyzicoProductReferenceCode);
        Assert.Equal("price-456", plan.IyzicoPricingPlanReferenceCode);
        Assert.False(plan.AllowsSelfSignup);

        form["Input.IyzicoPricingPlanReferenceCode"] = "bad code <script>";
        var bad = await sa.PostFormAsync($"/SuperAdmin/Plans/Edit/{id}", form);
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Assert.Contains("Only letters, digits", await bad.ReadHtmlAsync());
    }

    [Fact]
    public async Task Without_provider_configuration_paid_checkout_is_unavailable()
    {
        var (_, client) = await TrialAdminAsync();
        _f.Payments.IsConfigured = false;
        try
        {
            Assert.Contains("Online payment is not available", await client.GetHtmlAsync("/App/Billing"));
        }
        finally { _f.Payments.IsConfigured = true; }
    }

    [Fact]
    public async Task Paid_checkout_switched_off_shows_contact_and_blocks_the_handlers()
    {
        using var off = _f.WithWebHostBuilder(b => b.UseSetting("Billing:PaidCheckoutEnabled", "false"));
        var (cid, email) = await _f.AddCustomerWithAdminAsync("Standard", c => { c.BillingStatus = BillingStatus.Trial; c.TrialEndsAtUtc = _f.Now.AddDays(5); });
        var client = off.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        AssertRedirect(await client.PostFormAsync("/Account/Login", new() { ["Input.Email"] = email, ["Input.Password"] = BillingTestFactory.Password }), "/");
        var html = await client.GetHtmlAsync("/App/Billing");
        Assert.Contains("data-testid=\"contact-to-upgrade\"", html);
        Assert.Contains("Pro", html);
        Assert.DoesNotContain("/App/Billing/Checkout", html);

        var before = _f.Payments.Checkouts.Count;
        var r = await client.PostFormAsync($"/App/Billing/Checkout?planId={await PlanIdAsync("Pro")}", Contact());
        AssertRedirect(r, "/App/Billing");
        Assert.Equal(before, _f.Payments.Checkouts.Count);
        Assert.False(await _f.WithDbAsync(db => db.Subscriptions.AnyAsync(s => s.CustomerId == cid)));
    }

    [Fact]
    public async Task Sidebar_links_to_billing()
    {
        var (_, client) = await TrialAdminAsync();
        Assert.Matches(new Regex("href=\"/App/Billing\""), await client.GetHtmlAsync("/App"));
    }
}
