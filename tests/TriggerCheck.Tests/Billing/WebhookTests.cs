using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;

namespace TriggerCheck.Tests.Billing;

/// <summary>BIZ-2 webhook endpoint: X-IYZ-SIGNATURE-V3 verification, idempotency, status transitions.</summary>
public class WebhookTests(BillingTestFactory f) : IClassFixture<BillingTestFactory>
{
    /// <summary>Signature computed independently from the documented formula (not via the production helper).</summary>
    public static string Sign(string eventType, string subRef, string orderRef, string customerRef, string secret = BillingTestFactory.SecretKey)
    {
        var data = BillingTestFactory.MerchantId + secret + eventType + subRef + orderRef + customerRef;
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
    }

    private Task<HttpResponseMessage> SendAsync(string eventType, string subRef, string eventId, string? signature = null, string orderRef = "order-1",
        DateTimeOffset? at = null) =>
        SendSignedAsync(f, eventType, subRef, orderRef, "cust-1", at, eventId, signature);

    /// <summary>Posts a webhook signed like iyzico does (or with <paramref name="signature"/>).</summary>
    public static async Task<HttpResponseMessage> SendSignedAsync(BillingTestFactory f, string eventType, string subRef, string orderRef, string customerRef,
        DateTimeOffset? at = null, string? iyziReference = null, string? signature = null)
    {
        var body = JsonSerializer.Serialize(new
        {
            orderReferenceCode = orderRef, customerReferenceCode = customerRef, subscriptionReferenceCode = subRef,
            iyziReferenceCode = iyziReference ?? Guid.NewGuid().ToString("N"), iyziEventType = eventType,
            iyziEventTime = (at ?? f.Time.GetUtcNow()).ToUnixTimeMilliseconds()
        });
        var request = new HttpRequestMessage(HttpMethod.Post, "/billing/iyzico/webhook") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("X-IYZ-SIGNATURE-V3", signature ?? Sign(eventType, subRef, orderRef, customerRef));
        return await f.CreateClient().SendAsync(request);
    }

    private async Task<(int CustomerId, string Reference, string Email)> SubscribedAsync(Action<Subscription>? configure = null)
    {
        var (cid, email) = await f.AddCustomerWithAdminAsync("Standard");
        var reference = "wh-" + Guid.NewGuid().ToString("N")[..8];
        await f.AddActiveSubscriptionAsync(cid, "Pro", reference, configure);
        return (cid, reference, email);
    }

    private Task<int> EventCountAsync(int cid) => f.WithDbAsync(db => db.PaymentEvents.CountAsync(e => e.CustomerId == cid));

    [Fact]
    public async Task Failed_payment_marks_the_subscription_past_due_and_mails_the_admin()
    {
        var (cid, reference, email) = await SubscribedAsync();
        var r = await SendAsync("subscription.order.failure", reference, "evt-" + Guid.NewGuid());
        r.AssertStatus(HttpStatusCode.OK);

        var c = await f.GetCustomerAsync(cid);
        Assert.Equal(BillingStatus.PastDue, c.BillingStatus);
        var sub = await f.WithDbAsync(db => db.Subscriptions.AsNoTracking().FirstAsync(s => s.ProviderReference == reference));
        Assert.Equal(SubscriptionStatus.PastDue, sub.Status);
        Assert.Equal(f.Now, sub.PastDueSinceUtc!.Value, TimeSpan.FromSeconds(1));
        Assert.Contains(f.Emails.To(email), m => m.Subject.Contains("payment failed"));
        Assert.Contains("Failed", await (await f.LoginAsync(email, BillingTestFactory.Password)).GetStringAsync("/App/Billing"));
    }

    [Fact]
    public async Task Successful_payment_recovers_a_past_due_subscription_and_extends_the_period()
    {
        var (cid, reference, _) = await SubscribedAsync(s => { s.Status = SubscriptionStatus.PastDue; s.PastDueSinceUtc = f.Now.AddDays(-2); s.CurrentPeriodEndUtc = f.Now.AddDays(-2); });
        (await SendAsync("subscription.order.success", reference, "evt-" + Guid.NewGuid())).AssertStatus(HttpStatusCode.OK);

        Assert.Equal(BillingStatus.Active, (await f.GetCustomerAsync(cid)).BillingStatus);
        var sub = await f.WithDbAsync(db => db.Subscriptions.AsNoTracking().FirstAsync(s => s.ProviderReference == reference));
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.Null(sub.PastDueSinceUtc);
        Assert.Equal(f.Now.AddMonths(1), sub.CurrentPeriodEndUtc!.Value, TimeSpan.FromSeconds(1));
        var e = await f.WithDbAsync(db => db.PaymentEvents.AsNoTracking().SingleAsync(x => x.CustomerId == cid));
        Assert.True(e.Succeeded);
        Assert.Equal("Pro", e.PlanName);
        Assert.Equal("order-1", e.OrderReference);
    }

    [Fact]
    public async Task Replayed_event_is_processed_once()
    {
        var (cid, reference, _) = await SubscribedAsync();
        var id = "evt-" + Guid.NewGuid();
        (await SendAsync("subscription.order.success", reference, id)).AssertStatus(HttpStatusCode.OK);
        var period = await f.WithDbAsync(db => db.Subscriptions.Where(s => s.ProviderReference == reference).Select(s => s.CurrentPeriodEndUtc).FirstAsync());

        var replay = await SendAsync("subscription.order.success", reference, id);
        replay.AssertStatus(HttpStatusCode.OK);
        Assert.Contains("duplicate", await replay.Content.ReadAsStringAsync());
        Assert.Equal(1, await EventCountAsync(cid));
        Assert.Equal(period, await f.WithDbAsync(db => db.Subscriptions.Where(s => s.ProviderReference == reference).Select(s => s.CurrentPeriodEndUtc).FirstAsync()));
    }

    [Theory]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("")]
    public async Task Invalid_signature_is_rejected(string signature)
    {
        var (cid, reference, _) = await SubscribedAsync();
        (await SendAsync("subscription.order.failure", reference, "evt-" + Guid.NewGuid(), signature)).AssertStatus(HttpStatusCode.Unauthorized);
        Assert.Equal(BillingStatus.Active, (await f.GetCustomerAsync(cid)).BillingStatus);
        Assert.Equal(0, await EventCountAsync(cid));
    }

    [Fact]
    public async Task Signature_with_another_secret_or_tampered_body_is_rejected()
    {
        var (cid, reference, _) = await SubscribedAsync();
        var wrongSecret = Sign("subscription.order.failure", reference, "order-1", "cust-1", "other-secret");
        (await SendAsync("subscription.order.failure", reference, "evt-a", wrongSecret)).AssertStatus(HttpStatusCode.Unauthorized);
        // Signed for a success event, sent as a failure.
        var forSuccess = Sign("subscription.order.success", reference, "order-1", "cust-1");
        (await SendAsync("subscription.order.failure", reference, "evt-b", forSuccess)).AssertStatus(HttpStatusCode.Unauthorized);
        Assert.Equal(BillingStatus.Active, (await f.GetCustomerAsync(cid)).BillingStatus);
    }

    [Fact]
    public async Task Malformed_body_is_rejected_and_unknown_subscription_is_ignored()
    {
        var bad = await f.CreateClient().PostAsync("/billing/iyzico/webhook", new StringContent("not json", Encoding.UTF8, "application/json"));
        bad.AssertStatus(HttpStatusCode.Unauthorized);

        var unknown = await SendAsync("subscription.order.success", "no-such-ref", "evt-" + Guid.NewGuid());
        unknown.AssertStatus(HttpStatusCode.OK);
        Assert.Contains("ignored", await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Failure_for_a_cancelled_subscription_does_not_change_its_state()
    {
        var (cid, reference, _) = await SubscribedAsync(s => { s.Status = SubscriptionStatus.Cancelled; s.CancelAtPeriodEnd = true; });
        (await SendAsync("subscription.order.failure", reference, "evt-" + Guid.NewGuid())).AssertStatus(HttpStatusCode.OK);
        Assert.Equal(BillingStatus.Cancelled, (await f.GetCustomerAsync(cid)).BillingStatus);
        Assert.Equal(1, await EventCountAsync(cid)); // recorded in the history
    }

    [Fact]
    public async Task Replay_with_a_new_unsigned_reference_is_still_a_duplicate()
    {
        var (cid, reference, _) = await SubscribedAsync();
        (await SendAsync("subscription.order.success", reference, "first-id")).AssertStatus(HttpStatusCode.OK);
        var replay = await SendAsync("subscription.order.success", reference, "attacker-chosen-id");
        Assert.Contains("duplicate", await replay.Content.ReadAsStringAsync());
        Assert.Equal(1, await EventCountAsync(cid));
    }

    [Fact]
    public async Task Late_failure_after_a_newer_success_is_ignored()
    {
        var (cid, reference, _) = await SubscribedAsync();
        var now = f.Time.GetUtcNow();
        (await SendAsync("subscription.order.success", reference, "s", orderRef: "o-2", at: now)).AssertStatus(HttpStatusCode.OK);
        var late = await SendAsync("subscription.order.failure", reference, "f", orderRef: "o-1", at: now.AddMinutes(-30));
        Assert.Contains("ignored", await late.Content.ReadAsStringAsync());
        Assert.Equal(BillingStatus.Active, (await f.GetCustomerAsync(cid)).BillingStatus);
        Assert.True(await f.WithDbAsync(db => db.PaymentEvents.AnyAsync(e => e.CustomerId == cid && e.Ignored && e.OrderReference == "o-1")));
    }

    [Fact]
    public async Task Event_far_outside_the_time_window_is_not_applied()
    {
        var (cid, reference, _) = await SubscribedAsync();
        var old = await SendAsync("subscription.order.failure", reference, "x", at: f.Time.GetUtcNow().AddDays(-10));
        Assert.Contains("ignored", await old.Content.ReadAsStringAsync());
        var future = await SendAsync("subscription.order.failure", reference, "y", orderRef: "o-future", at: f.Time.GetUtcNow().AddDays(2));
        Assert.Contains("ignored", await future.Content.ReadAsStringAsync());
        Assert.Equal(BillingStatus.Active, (await f.GetCustomerAsync(cid)).BillingStatus);
    }

    [Fact]
    public async Task Unknown_subscription_event_is_recorded_for_reconciliation()
    {
        var r = await SendSignedAsync(f, "subscription.order.success", "ghost-ref", "o-ghost", "ghost-customer");
        Assert.Contains("ignored", await r.Content.ReadAsStringAsync());
        var e = await f.WithDbAsync(db => db.PaymentEvents.AsNoTracking().SingleAsync(x => x.SubscriptionReference == "ghost-ref"));
        Assert.True(e.Ignored);
        Assert.Equal("unknown-subscription", e.IgnoredReason);
        Assert.Null(e.CustomerId);
        Assert.Equal("ghost-customer", e.CustomerReference);
    }

    [Fact]
    public async Task Event_without_any_possible_checkout_stops_being_reconciled()
    {
        var r = await SendSignedAsync(f, "subscription.order.success", "orphan-ref", "o-orphan", "orphan-customer");
        Assert.Contains("ignored", await r.Content.ReadAsStringAsync());
        // No checkout was started before the event: nothing can match it, the job stops re-checking it.
        await f.RunMaintenanceAsync();
        var e = await f.WithDbAsync(db => db.PaymentEvents.AsNoTracking().SingleAsync(x => x.SubscriptionReference == "orphan-ref"));
        Assert.Equal("unmatched", e.IgnoredReason);
        Assert.True(e.Ignored);
    }

    [Fact]
    public async Task Oversized_body_is_rejected()
    {
        var big = new string('x', TriggerCheck.Web.Billing.BillingSetup.MaxWebhookBytes + 10);
        var r = await f.CreateClient().PostAsync("/billing/iyzico/webhook", new StringContent(big, Encoding.UTF8, "application/json"));
        r.AssertStatus(HttpStatusCode.RequestEntityTooLarge);
    }
}
