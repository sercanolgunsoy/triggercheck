using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Identity;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;
using TriggerCheck.Web.Billing;
using TriggerCheck.Web.Infrastructure;

namespace TriggerCheck.Tests.Billing;

/// <summary>
/// Test host for sign-up and billing: captured e-mails, a fake payment provider (no network; webhook signatures are
/// verified by the real iyzico code), a FakeTimeProvider and the billing background job switched off (tests run
/// <see cref="BillingMaintenance"/> directly).
/// </summary>
public class BillingTestFactory : TestWebAppFactory
{
    public const string MerchantId = "123456";
    public const string SecretKey = "test-secret-key";

    public CapturingEmailSender Emails { get; } = new();
    public FakePaymentProvider Payments { get; } = new();
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);
    /// <summary>Sign-up attempts per IP per hour (high by default so ordinary tests never hit it).</summary>
    protected virtual int RegistrationLimit => 1000;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Billing:MaintenanceIntervalMinutes", "0");
        builder.UseSetting("Billing:RegistrationsPerHourPerIp", RegistrationLimit.ToString());
        builder.UseSetting("Site:PublicBaseUrl", "https://app.test");
        builder.UseSetting("Billing:PaidCheckoutEnabled", "true");
        builder.UseSetting("Iyzico:ApiKey", "test-api-key");
        builder.UseSetting("Iyzico:SecretKey", SecretKey);
        builder.UseSetting("Iyzico:MerchantId", MerchantId);
        builder.ConfigureTestServices(s =>
        {
            // The real queue and background sender stay; only the SMTP transport is replaced.
            s.RemoveAll<IAppEmailTransport>();
            s.AddSingleton<IAppEmailTransport>(sp =>
            {
                // Derived hosts (WithWebHostBuilder) share this transport but have their own queue: wait for all of them.
                Emails.Queues.Add(sp.GetRequiredService<AppEmailQueue>());
                return Emails;
            });
            s.RemoveAll<IPaymentProvider>();
            s.AddSingleton<IPaymentProvider>(sp =>
            {
                Payments.Webhooks = new IyzicoPaymentProvider(new HttpClient(), sp.GetRequiredService<IOptionsMonitor<IyzicoOptions>>(), Time,
                    NullLogger<IyzicoPaymentProvider>.Instance);
                return Payments;
            });
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Time);
        });
    }

    public DateTime Now => Time.GetUtcNow().UtcDateTime;

    /// <summary>Creates a customer with a CustomerAdmin (confirmed) on <paramref name="planName"/>.</summary>
    public async Task<(int CustomerId, string Email)> AddCustomerWithAdminAsync(string planName, Action<Customer>? configure = null)
    {
        var email = $"admin-{Guid.NewGuid():N}@billing.test";
        var cid = await WithDbAsync(async db =>
        {
            var plan = await db.Plans.FirstAsync(p => p.Name == planName);
            var c = new Customer { Name = "Billing " + Guid.NewGuid().ToString("N")[..6], ContactEmail = email, PlanId = plan.Id };
            configure?.Invoke(c);
            db.Customers.Add(c);
            await db.SaveChangesAsync();
            return c.Id;
        });
        await this.AddUserAsync(cid, email, Password, Roles.CustomerAdmin);
        return (cid, email);
    }

    public const string Password = "Billing123!";

    /// <summary>Gives every self-service paid plan an iyzico pricing plan code ("pp-{name}").</summary>
    public Task LinkPlansToIyzicoAsync() => WithDbAsync(async db =>
    {
        foreach (var p in await db.Plans.ToListAsync())
            if (p.MonthlyPrice > 0 && p.AllowsSelfSignup) p.IyzicoPricingPlanReferenceCode = "pp-" + p.Name.ToLowerInvariant();
        await db.SaveChangesAsync();
    });

    /// <summary>An active subscription on <paramref name="planName"/> (as after a verified checkout).</summary>
    public Task<int> AddActiveSubscriptionAsync(int customerId, string planName, string reference, Action<Subscription>? configure = null) =>
        WithDbAsync(async db =>
        {
            var plan = await db.Plans.FirstAsync(p => p.Name == planName);
            var customer = await db.Customers.FirstAsync(c => c.Id == customerId);
            customer.PlanId = plan.Id;
            customer.BillingStatus = BillingStatus.Active;
            var s = new Subscription
            {
                CustomerId = customerId, PlanId = plan.Id, Status = SubscriptionStatus.Active, Provider = "fake", ProviderReference = reference,
                ActivatedAtUtc = Now.AddDays(-10), CurrentPeriodEndUtc = Now.AddDays(20)
            };
            configure?.Invoke(s);
            if (s.Status == SubscriptionStatus.PastDue) customer.BillingStatus = BillingStatus.PastDue;
            if (s.Status == SubscriptionStatus.Cancelled) customer.BillingStatus = BillingStatus.Cancelled;
            db.Subscriptions.Add(s);
            await db.SaveChangesAsync();
            return s.Id;
        });

    public async Task<BillingMaintenanceResult> RunMaintenanceAsync()
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<BillingMaintenance>().RunAsync(CancellationToken.None);
    }

    public Task<Customer> GetCustomerAsync(int id) => WithDbAsync(db => db.Customers.AsNoTracking().Include(c => c.Plan).FirstAsync(c => c.Id == id));

    public async Task<bool> IsEmailConfirmedAsync(string email)
    {
        using var scope = Services.CreateScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return (await um.FindByEmailAsync(email))!.EmailConfirmed;
    }
}

public sealed record SentEmail(string To, string Subject, string Body);

/// <summary>
/// Test SMTP transport behind the real e-mail queue. Reads wait (up to 10 s) until the queue is empty, so assertions see
/// every e-mail queued so far. <see cref="Gate"/> holds deliveries to prove that requests do not wait for sending.
/// </summary>
public sealed class CapturingEmailSender : IAppEmailTransport
{
    public ConcurrentQueue<SentEmail> Sent { get; } = new();
    public ConcurrentBag<AppEmailQueue> Queues { get; } = [];
    private bool IsIdle() => Queues.All(q => q.Pending == 0);
    public TaskCompletionSource? Gate { get; set; }

    public async Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        if (Gate is { } gate) await gate.Task.WaitAsync(ct);
        Sent.Enqueue(new SentEmail(to, subject, body));
    }

    public List<SentEmail> To(string address)
    {
        SpinWait.SpinUntil(IsIdle, TimeSpan.FromSeconds(10));
        return Sent.Where(e => string.Equals(e.To, address, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static readonly Regex LinkRx = new(@"https://app\.test(/\S+)", RegexOptions.Compiled);

    /// <summary>Path + query of the first app link in the newest e-mail to <paramref name="address"/>.</summary>
    public string LinkTo(string address) => LinkRx.Match(To(address).Last().Body).Groups[1].Value;
}

/// <summary>
/// Records calls; checkout results are scripted per token (default: not paid yet, i.e. Pending). Webhook parsing uses
/// the real iyzico verification.
/// </summary>
public sealed class FakePaymentProvider : IPaymentProvider
{
    public string Name => "fake";
    public bool IsConfigured { get; set; } = true;
    public IyzicoPaymentProvider Webhooks { get; set; } = null!;

    public ConcurrentDictionary<string, CheckoutResult> Results { get; } = new();
    public ConcurrentQueue<CheckoutRequest> Checkouts { get; } = new();
    public ConcurrentQueue<(string Reference, string PricingPlan, bool Immediately)> PlanChanges { get; } = new();
    /// <summary>Retrieval answers "no answer" (network error): not definitive.</summary>
    public bool Unreachable { get; set; }
    public ConcurrentQueue<string> Cancels { get; } = new();
    public bool FailNextCall { get; set; }

    public Task<CheckoutSession?> StartCheckoutAsync(CheckoutRequest request, CancellationToken ct)
    {
        if (Consume()) return Task.FromResult<CheckoutSession?>(null);
        Checkouts.Enqueue(request);
        var token = "tok-" + Guid.NewGuid().ToString("N");
        return Task.FromResult<CheckoutSession?>(new CheckoutSession(token, "https://sandbox-cpp.iyzipay.test/?token=" + token));
    }

    public ConcurrentDictionary<string, int> Retrievals { get; } = new();

    public Task<CheckoutResult> GetCheckoutResultAsync(string token, CancellationToken ct) =>
        Task.FromResult(Retrievals.AddOrUpdate(token, 1, (_, n) => n + 1) > 0 && Unreachable ? new CheckoutResult(ProviderSubscriptionState.Unknown)
            : Results.TryGetValue(token, out var r) ? r : new CheckoutResult(ProviderSubscriptionState.Pending));

    public Task<ProviderResult> ChangePlanAsync(string subscriptionReference, string newPricingPlanReferenceCode, bool immediately, CancellationToken ct)
    {
        if (Consume()) return Task.FromResult(new ProviderResult(false, "boom"));
        PlanChanges.Enqueue((subscriptionReference, newPricingPlanReferenceCode, immediately));
        return Task.FromResult(ProviderResult.Ok);
    }

    public Task<ProviderResult> CancelAsync(string subscriptionReference, CancellationToken ct)
    {
        if (Consume()) return Task.FromResult(new ProviderResult(false, "boom"));
        Cancels.Enqueue(subscriptionReference);
        return Task.FromResult(ProviderResult.Ok);
    }

    public WebhookEvent? ParseWebhook(string body, IHeaderDictionary headers) => Webhooks.ParseWebhook(body, headers);

    /// <summary>Marks the checkout of <paramref name="token"/> as paid at the provider.</summary>
    public void Approve(string token, string pricingPlan, string reference = "sub-ref") =>
        Results[token] = new CheckoutResult(ProviderSubscriptionState.Active, reference, "cust-" + reference, pricingPlan);

    /// <summary>The provider definitively says the checkout did not complete.</summary>
    public void Reject(string token) => Results[token] = new CheckoutResult(ProviderSubscriptionState.Rejected);

    private bool Consume()
    {
        if (!FailNextCall) return false;
        FailNextCall = false;
        return true;
    }
}

public static class BillingTestExtensions
{
    public static void AssertStatus(this HttpResponseMessage r, HttpStatusCode expected) =>
        Assert.True(r.StatusCode == expected, $"Expected {(int)expected} but got {(int)r.StatusCode}");
}
