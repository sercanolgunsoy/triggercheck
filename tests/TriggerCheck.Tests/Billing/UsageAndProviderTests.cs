using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.App;
using TriggerCheck.Web.Billing;

namespace TriggerCheck.Tests.Billing;

/// <summary>BIZ-3 usage meters (thresholds and panel display).</summary>
public class UsageIndicatorTests(BillingTestFactory f) : IClassFixture<BillingTestFactory>
{
    [Theory]
    [InlineData(0, 10, 0, false, false)]
    [InlineData(7, 10, 70, false, false)]
    [InlineData(8, 10, 80, true, false)]
    [InlineData(39, 50, 78, false, false)]
    [InlineData(40, 50, 80, true, false)]
    [InlineData(10, 10, 100, true, true)]
    [InlineData(12, 10, 100, true, true)]
    [InlineData(0, 0, 100, true, true)]
    public void Meter_thresholds(int used, int max, int percent, bool warning, bool full)
    {
        var m = new UsageMeter(used, max);
        Assert.Equal(percent, m.Percent);
        Assert.Equal(warning, m.IsWarning);
        Assert.Equal(full, m.IsFull);
    }

    private async Task<(int CustomerId, HttpClient Client)> CustomerOnSmallPlanAsync(int monitors)
    {
        var planName = "Small-" + Guid.NewGuid().ToString("N")[..6];
        await f.WithDbAsync(async db => { db.Plans.Add(new Plan { Name = planName, MinIntervalSeconds = 300, MaxMonitors = 5, RetentionDays = 30 }); await db.SaveChangesAsync(); });
        var (cid, email) = await f.AddCustomerWithAdminAsync(planName);
        for (var i = 0; i < monitors; i++) await f.AddMonitorAsync(cid, "U" + i);
        return (cid, await f.LoginAsync(email, BillingTestFactory.Password));
    }

    [Fact]
    public async Task Dashboard_and_billing_warn_at_80_percent()
    {
        var (_, client) = await CustomerOnSmallPlanAsync(4);
        foreach (var url in new[] { "/App", "/App/Billing" })
        {
            var html = await client.GetHtmlAsync(url);
            Assert.Contains("data-usage=\"monitors\" data-percent=\"80\" data-warning=\"true\"", html);
            Assert.Contains("4 / 5", html);
            Assert.Contains("80% of your plan limit is used.", html);
        }
    }

    [Fact]
    public async Task No_warning_below_80_percent_and_no_AI_meter_yet()
    {
        var (_, client) = await CustomerOnSmallPlanAsync(3);
        var html = await client.GetHtmlAsync("/App");
        Assert.Contains("data-usage=\"monitors\" data-percent=\"60\" data-warning=\"false\"", html);
        Assert.DoesNotContain("data-testid=\"usage-warning\"", html);
        Assert.DoesNotContain("data-usage=\"ai\"", html);
    }

    [Fact]
    public async Task Usage_counts_only_the_own_monitors()
    {
        var (cid, client) = await CustomerOnSmallPlanAsync(1);
        var other = await f.AddCustomerAsync("UsageOther");
        for (var i = 0; i < 4; i++) await f.AddMonitorAsync(other, "O" + i);
        Assert.Contains("1 / 5", await client.GetHtmlAsync("/App/Billing"));
        Assert.Equal(1, await f.WithDbAsync(db => db.Monitors.CountAsync(m => m.CustomerId == cid)));
    }
}

/// <summary>iyzico client against a fake HTTP handler: request signing and response parsing (no network).</summary>
public class IyzicoProviderTests
{
    private sealed class Handler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request, body));
            return respond(request, body);
        }
    }

    private static IyzicoOptions Options => new() { ApiKey = "api-key", SecretKey = "secret-key", MerchantId = "42", BaseUrl = "https://sandbox-api.iyzipay.test" };

    private static IyzicoPaymentProvider Provider(Handler h, IyzicoOptions? o = null) =>
        new(new HttpClient(h), new StaticOptions(o ?? Options), TimeProvider.System, NullLogger<IyzicoPaymentProvider>.Instance);

    private sealed class StaticOptions(IyzicoOptions value) : IOptionsMonitor<IyzicoOptions>
    {
        public IyzicoOptions CurrentValue => value;
        public IyzicoOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<IyzicoOptions, string?> listener) => null;
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static readonly BillingContact Contact = new("Ada", "Lovelace", "ada@example.test", "+905350000000", "11111111111", "Street 1", "Istanbul", "Turkey", "34000");

    [Fact]
    public async Task Checkout_initialize_is_signed_with_IYZWSv2()
    {
        var h = new Handler((_, _) => Json("""{"status":"success","token":"tok-1","checkoutFormContent":"<script>...</script>","tokenExpireTime":1800}"""));
        var session = await Provider(h).StartCheckoutAsync(new CheckoutRequest("conv-1", "pp-pro", "https://app.test/cb", "en", Contact), CancellationToken.None);

        Assert.NotNull(session);
        Assert.Equal("tok-1", session.Token);
        Assert.Equal("https://sandbox-cpp.iyzipay.com/?token=tok-1&lang=en", session.PaymentPageUrl);

        var (request, body) = Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://sandbox-api.iyzipay.test/v2/subscription/checkoutform/initialize", request.RequestUri!.ToString());
        var json = JsonNode.Parse(body)!;
        Assert.Equal("pp-pro", json["pricingPlanReferenceCode"]!.GetValue<string>());
        Assert.Equal("https://app.test/cb", json["callbackUrl"]!.GetValue<string>());
        Assert.Equal("ada@example.test", json["customer"]!["email"]!.GetValue<string>());

        // Authorization: IYZWSv2 base64("apiKey:..&randomKey:..&signature:hex(HMACSHA256(secret, rnd + path + body)))")
        var rnd = request.Headers.GetValues("x-iyzi-rnd").Single();
        var auth = request.Headers.GetValues("Authorization").Single();
        Assert.StartsWith("IYZWSv2 ", auth);
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth["IYZWSv2 ".Length..]));
        var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("secret-key"),
            Encoding.UTF8.GetBytes(rnd + "/v2/subscription/checkoutform/initialize" + body))).ToLowerInvariant();
        Assert.Equal($"apiKey:api-key&randomKey:{rnd}&signature:{expected}", decoded);
    }

    [Fact]
    public async Task Checkout_result_is_parsed()
    {
        var h = new Handler((_, _) => Json("""{"status":"success","data":{"referenceCode":"sub-9","customerReferenceCode":"c-9","pricingPlanReferenceCode":"pp-pro","subscriptionStatus":"ACTIVE"}}"""));
        var r = await Provider(h).GetCheckoutResultAsync("tok-1", CancellationToken.None);
        Assert.True(r.Succeeded);
        Assert.Equal("sub-9", r.SubscriptionReference);
        Assert.Equal("pp-pro", r.PricingPlanReferenceCode);
        Assert.Equal("https://sandbox-api.iyzipay.test/v2/subscription/checkoutform/tok-1", h.Requests.Single().Request.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, h.Requests.Single().Request.Method);
    }

    [Theory]
    [InlineData("""{"status":"failure","errorCode":"100001","errorMessage":"boom"}""", HttpStatusCode.BadRequest, ProviderSubscriptionState.Rejected)]
    [InlineData("""{"status":"failure","errorCode":"100001","errorMessage":"boom"}""", HttpStatusCode.OK, ProviderSubscriptionState.Rejected)]
    [InlineData("""{"status":"success","data":{"referenceCode":"sub-9","subscriptionStatus":"PENDING"}}""", HttpStatusCode.OK, ProviderSubscriptionState.Pending)]
    [InlineData("""{"status":"success","data":{"referenceCode":"sub-9","subscriptionStatus":"CANCELED"}}""", HttpStatusCode.OK, ProviderSubscriptionState.Cancelled)]
    [InlineData("<html>gateway error</html>", HttpStatusCode.BadGateway, ProviderSubscriptionState.Unknown)]
    [InlineData("""{"status":"failure"}""", HttpStatusCode.InternalServerError, ProviderSubscriptionState.Unknown)]
    public async Task Unsuccessful_checkout_results_are_not_activated(string json, HttpStatusCode code, ProviderSubscriptionState state)
    {
        var r = await Provider(new Handler((_, _) => Json(json, code))).GetCheckoutResultAsync("tok-1", CancellationToken.None);
        Assert.False(r.Succeeded);
        Assert.Equal(state, r.State);
        // Only explicit provider answers close a checkout; server errors are retried.
        Assert.Equal(state is not (ProviderSubscriptionState.Unknown or ProviderSubscriptionState.Pending), r.IsDefinitive);
    }

    [Fact]
    public async Task Network_error_is_not_definitive()
    {
        var r = await Provider(new Handler((_, _) => throw new HttpRequestException("down"))).GetCheckoutResultAsync("tok-1", CancellationToken.None);
        Assert.False(r.IsDefinitive);
    }

    [Theory]
    [InlineData(true, "NOW", """{"status":"success","data":{"referenceCode":"n","subscriptionStatus":"PENDING"}}""", false)]
    [InlineData(true, "NOW", """{"status":"success","data":{"referenceCode":"n","subscriptionStatus":"ACTIVE"}}""", true)]
    [InlineData(false, "NEXT_PERIOD", """{"status":"success","data":{"referenceCode":"n","subscriptionStatus":"PENDING"}}""", true)]
    public async Task Plan_change_checks_the_reported_subscription_status(bool immediately, string period, string json, bool ok)
    {
        var h = new Handler((_, _) => Json(json));
        var r = await Provider(h).ChangePlanAsync("sub-1", "pp", immediately, CancellationToken.None);
        Assert.Equal(ok, r.Succeeded);
        Assert.Equal(period, JsonNode.Parse(h.Requests[0].Body)!["upgradePeriod"]!.GetValue<string>());
    }

    [Fact]
    public async Task Upgrade_and_cancel_call_the_subscription_endpoints()
    {
        var h = new Handler((_, _) => Json("""{"status":"success","data":{"referenceCode":"sub-new"}}"""));
        var p = Provider(h);
        var up = await p.ChangePlanAsync("sub-1", "pp-std", immediately: true, CancellationToken.None);
        Assert.True(up.Succeeded);
        Assert.Equal("sub-new", up.NewSubscriptionReference);
        Assert.True((await p.CancelAsync("sub-1", CancellationToken.None)).Succeeded);
        Assert.Equal(["/v2/subscription/subscriptions/sub-1/upgrade", "/v2/subscription/subscriptions/sub-1/cancel"],
            h.Requests.Select(x => x.Request.RequestUri!.AbsolutePath));
        Assert.Equal("NOW", JsonNode.Parse(h.Requests[0].Body)!["upgradePeriod"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unconfigured_provider_makes_no_calls()
    {
        var h = new Handler((_, _) => throw new InvalidOperationException("no network"));
        var p = Provider(h, new IyzicoOptions());
        Assert.False(p.IsConfigured);
        Assert.Null(await p.StartCheckoutAsync(new CheckoutRequest("c", "pp", "https://x", "en", Contact), CancellationToken.None));
        Assert.Empty(h.Requests);
    }

    [Fact]
    public void Webhook_requires_merchant_id_and_a_matching_signature()
    {
        var body = """{"orderReferenceCode":"o","customerReferenceCode":"c","subscriptionReferenceCode":"s","iyziReferenceCode":"e1","iyziEventType":"subscription.order.success","iyziEventTime":1700000000000}""";
        var signature = IyzicoPaymentProvider.WebhookSignature("42", "secret-key", "subscription.order.success", "s", "o", "c");
        var headers = new Microsoft.AspNetCore.Http.HeaderDictionary { ["X-IYZ-SIGNATURE-V3"] = signature.ToUpperInvariant() };

        var e = Provider(new Handler((_, _) => throw new InvalidOperationException())).ParseWebhook(body, headers);
        Assert.NotNull(e);
        Assert.Equal("subscription.order.success:s:o", e.EventId); // signed fields only
        Assert.Equal(WebhookEventKind.PaymentSucceeded, e.Kind);
        Assert.Equal(new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc), e.OccurredAtUtc);

        var noMerchant = Options;
        noMerchant.MerchantId = "";
        Assert.Null(Provider(new Handler((_, _) => throw new InvalidOperationException()), noMerchant).ParseWebhook(body, headers));
    }
}
