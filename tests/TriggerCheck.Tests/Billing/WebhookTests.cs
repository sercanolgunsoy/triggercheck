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
    private static string Sign(string eventType, string subRef, string orderRef, string customerRef, string secret = BillingTestFactory.SecretKey)
    {
        var data = BillingTestFactory.MerchantId + secret + eventType + subRef + orderRef + customerRef;
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
    }

    private async Task<HttpResponseMessage> SendAsync(string eventType, string subRef, string eventId, string? signature = null, string orderRef = "order-1")
    {
        var body = JsonSerializer.Serialize(new
        {
            orderReferenceCode = orderRef, customerReferenceCode = "cust-1", subscriptionReferenceCode = subRef, iyziReferenceCode = eventId,
            iyziEventType = eventType, iyziEventTime = f.Time.GetUtcNow().ToUnixTimeMilliseconds()
        });
        var request = new HttpRequestMessage(HttpMethod.Post, "/billing/iyzico/webhook") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("X-IYZ-SIGNATURE-V3", signature ?? Sign(eventType, subRef, orderRef, "cust-1"));
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
}
