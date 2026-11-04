using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.App;
using TriggerCheck.Web.Billing;

namespace TriggerCheck.Tests.Billing;

/// <summary>
/// BIZ-1/BIZ-2 background job: trial reminder and expiry, grace period, end of cancelled subscriptions. Uses
/// FakeTimeProvider; the tests of a class run one after another, so advancing the clock does not race.
/// </summary>
public class BillingMaintenanceTests(BillingTestFactory f) : IClassFixture<BillingTestFactory>
{
    private Task<(int CustomerId, string Email)> TrialCustomerAsync(TimeSpan endsIn) =>
        f.AddCustomerWithAdminAsync("Pro", c => { c.BillingStatus = BillingStatus.Trial; c.TrialEndsAtUtc = f.Now + endsIn; });

    [Fact]
    public async Task Reminder_is_sent_once_three_days_before_the_trial_ends()
    {
        var (early, earlyEmail) = await TrialCustomerAsync(TimeSpan.FromDays(5));
        var (soon, soonEmail) = await TrialCustomerAsync(TimeSpan.FromDays(2));

        await f.RunMaintenanceAsync();
        Assert.Empty(f.Emails.To(earlyEmail));
        var mail = Assert.Single(f.Emails.To(soonEmail));
        Assert.Contains("trial ends soon", mail.Subject);
        Assert.NotNull((await f.GetCustomerAsync(soon)).TrialReminderSentAtUtc);

        await f.RunMaintenanceAsync(); // idempotent
        Assert.Single(f.Emails.To(soonEmail));
        Assert.Equal(BillingStatus.Trial, (await f.GetCustomerAsync(soon)).BillingStatus);

        f.Time.Advance(TimeSpan.FromDays(2.5)); // now 2.5 days before the end of "early"
        await f.RunMaintenanceAsync();
        Assert.Single(f.Emails.To(earlyEmail));
        Assert.Equal(BillingStatus.Trial, (await f.GetCustomerAsync(early)).BillingStatus);
    }

    [Fact]
    public async Task Expired_trial_moves_to_Basic_with_clamped_intervals()
    {
        var (cid, email) = await TrialCustomerAsync(TimeSpan.FromHours(1));
        var fast = await f.AddMonitorAsync(cid, "Fast", m => { m.IntervalSeconds = 30; m.Version = 3; });
        var slow = await f.AddMonitorAsync(cid, "Slow", m => m.IntervalSeconds = 300);

        await f.RunMaintenanceAsync();
        Assert.Equal(BillingStatus.Trial, (await f.GetCustomerAsync(cid)).BillingStatus);

        f.Time.Advance(TimeSpan.FromHours(2));
        var result = await f.RunMaintenanceAsync();
        Assert.True(result.TrialsExpired >= 1);
        var c = await f.GetCustomerAsync(cid);
        Assert.Equal("Basic", c.Plan.Name);
        Assert.Equal(BillingStatus.None, c.BillingStatus);
        var fastMonitor = (await f.GetMonitorAsync(fast))!;
        Assert.Equal(300, fastMonitor.IntervalSeconds);
        Assert.Equal(4, fastMonitor.Version); // concurrency token bumped
        Assert.Equal(300, (await f.GetMonitorAsync(slow))!.IntervalSeconds);
        Assert.Contains(f.Emails.To(email), m => m.Subject.Contains("plan changed to Basic") && m.Body.Contains("free trial has ended"));

        var mails = f.Emails.To(email).Count;
        await f.RunMaintenanceAsync(); // idempotent
        Assert.Equal(mails, f.Emails.To(email).Count);
    }

    [Fact]
    public async Task Subscribed_trial_customer_is_not_downgraded()
    {
        var (cid, _) = await TrialCustomerAsync(TimeSpan.FromHours(-1));
        await f.AddActiveSubscriptionAsync(cid, "Pro", "sub-" + cid); // checkout completed: status Active
        await f.RunMaintenanceAsync();
        var c = await f.GetCustomerAsync(cid);
        Assert.Equal("Pro", c.Plan.Name);
        Assert.Equal(BillingStatus.Active, c.BillingStatus);
    }

    [Fact]
    public async Task Past_due_subscription_is_downgraded_after_the_grace_period()
    {
        var (cid, email) = await f.AddCustomerWithAdminAsync("Standard");
        var reference = "pd-" + cid;
        var sid = await f.AddActiveSubscriptionAsync(cid, "Pro", reference, s => { s.Status = SubscriptionStatus.PastDue; s.PastDueSinceUtc = f.Now.AddDays(-6); });

        await f.RunMaintenanceAsync();
        Assert.Equal(BillingStatus.PastDue, (await f.GetCustomerAsync(cid)).BillingStatus);

        f.Time.Advance(TimeSpan.FromDays(1.5));
        await f.RunMaintenanceAsync();
        var c = await f.GetCustomerAsync(cid);
        Assert.Equal("Basic", c.Plan.Name);
        Assert.Equal(BillingStatus.None, c.BillingStatus);
        Assert.Equal(SubscriptionStatus.Expired, await f.WithDbAsync(db => db.Subscriptions.Where(s => s.Id == sid).Select(s => s.Status).FirstAsync()));
        Assert.Contains(reference, f.Payments.Cancels); // renewals stopped at the provider
        Assert.Contains(f.Emails.To(email), m => m.Body.Contains("could not be collected"));
    }

    [Fact]
    public async Task Cancelled_subscription_keeps_the_plan_until_the_period_ends()
    {
        var (cid, _) = await f.AddCustomerWithAdminAsync("Standard");
        await f.AddActiveSubscriptionAsync(cid, "Pro", "c-" + cid, s => { s.Status = SubscriptionStatus.Cancelled; s.CancelAtPeriodEnd = true; s.CurrentPeriodEndUtc = f.Now.AddDays(3); });

        await f.RunMaintenanceAsync();
        Assert.Equal("Pro", (await f.GetCustomerAsync(cid)).Plan.Name);

        f.Time.Advance(TimeSpan.FromDays(3.1));
        await f.RunMaintenanceAsync();
        var c = await f.GetCustomerAsync(cid);
        Assert.Equal("Basic", c.Plan.Name);
        Assert.Equal(BillingStatus.None, c.BillingStatus);
    }

    private sealed class BrokenOutbox : TriggerCheck.Web.Infrastructure.IAppEmailSender
    {
        public Task SendAsync(string to, string subject, string body, CancellationToken ct = default) => throw new InvalidOperationException("outbox down");
    }

    [Fact]
    public async Task Reminder_is_not_marked_sent_when_the_mail_could_not_be_queued()
    {
        var (cid, _) = await TrialCustomerAsync(TimeSpan.FromDays(2));
        using var broken = f.WithWebHostBuilder(b => Microsoft.AspNetCore.TestHost.WebHostBuilderExtensions.ConfigureTestServices(b, s =>
        {
            Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.RemoveAll<TriggerCheck.Web.Infrastructure.IAppEmailSender>(s);
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<TriggerCheck.Web.Infrastructure.IAppEmailSender>(s, new BrokenOutbox());
        }));
        using (var scope = broken.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<BillingMaintenance>().RunAsync(CancellationToken.None);
        Assert.Null((await f.GetCustomerAsync(cid)).TrialReminderSentAtUtc); // released: the next run retries

        await f.RunMaintenanceAsync();
        Assert.NotNull((await f.GetCustomerAsync(cid)).TrialReminderSentAtUtc);
    }
}
