using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using TriggerCheck.Web.Infrastructure;

namespace TriggerCheck.Web.Billing;

/// <summary>Registration of sign-up / billing services, their rate limits and the anonymous provider endpoints.</summary>
public static class BillingSetup
{
    /// <summary>Rate-limit policy of the sign-up page (POSTs per client IP).</summary>
    public const string RegisterPolicy = "register";
    /// <summary>Rate-limit policy of the forgot-password page (POSTs per client IP, same budget as sign-up).</summary>
    public const string PasswordResetPolicy = "password-reset";
    /// <summary>Rate-limit policy of checkout starts (POSTs per customer).</summary>
    public const string CheckoutPolicy = "checkout";
    /// <summary>Rate-limit policy of the provider callback and webhook endpoints.</summary>
    public const string ProviderPolicy = "payment-provider";

    public const string WebhookPath = "/billing/iyzico/webhook";
    public const string CallbackPath = "/billing/iyzico/callback";
    public const int MaxWebhookBytes = 64 * 1024;

    public static IServiceCollection AddBilling(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<BillingOptions>(config.GetSection(BillingOptions.Section));
        services.Configure<IyzicoOptions>(config.GetSection(IyzicoOptions.Section));
        services.AddSingleton<PublicUrls>();
        // E-mails go to a durable outbox and are sent in the background: requests never wait for SMTP (no timing side
        // channel) and nothing is lost on a restart.
        services.AddSingleton<AppEmailSignal>();
        services.AddSingleton<IAppEmailSender, OutboxAppEmailSender>();
        services.AddSingleton<IAppEmailTransport, SmtpAppEmailTransport>();
        services.AddHostedService<AppEmailSenderService>();
        services.AddHttpClient<IPaymentProvider, IyzicoPaymentProvider>(IyzicoPaymentProvider.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
            // Request URIs carry checkout tokens: no request logging.
            .RemoveAllLoggers();
        services.AddScoped<BillingMailer>();
        services.AddScoped<BillingService>();
        services.AddScoped<BillingMaintenance>();
        services.AddScoped<PlanUsageService>();
        services.AddHostedService<BillingMaintenanceService>();

        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(RegisterPolicy, PostsPerHour);
            o.AddPolicy(PasswordResetPolicy, PostsPerHour);
            o.AddPolicy(CheckoutPolicy, ctx =>
            {
                if (!HttpMethods.IsPost(ctx.Request.Method)) return RateLimitPartition.GetNoLimiter("get");
                var limit = ctx.RequestServices.GetRequiredService<IOptionsMonitor<BillingOptions>>().CurrentValue.CheckoutsPerHourPerCustomer;
                var customer = ctx.User.FindFirst(AppClaims.CustomerId)?.Value ?? "ip:" + ClientIp(ctx);
                return RateLimitPartition.GetFixedWindowLimiter("customer:" + customer, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, limit), Window = TimeSpan.FromHours(1), QueueLimit = 0
                });
            });
            o.AddPolicy(ProviderPolicy, ctx =>
            {
                var limit = ctx.RequestServices.GetRequiredService<IOptionsMonitor<BillingOptions>>().CurrentValue.WebhooksPerMinutePerIp;
                return RateLimitPartition.GetFixedWindowLimiter(ClientIp(ctx), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, limit), Window = TimeSpan.FromMinutes(1), QueueLimit = 0
                });
            });
        });
        return services;
    }

    /// <summary>Only form submissions count; showing the page is free.</summary>
    private static RateLimitPartition<string> PostsPerHour(HttpContext ctx)
    {
        if (!HttpMethods.IsPost(ctx.Request.Method)) return RateLimitPartition.GetNoLimiter("get");
        var limit = ctx.RequestServices.GetRequiredService<IOptionsMonitor<BillingOptions>>().CurrentValue.RegistrationsPerHourPerIp;
        return RateLimitPartition.GetFixedWindowLimiter(ClientIp(ctx), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, limit), Window = TimeSpan.FromHours(1), QueueLimit = 0
        });
    }

    /// <summary>Startup check: outside Development <c>Site:PublicBaseUrl</c> must be configured (see <see cref="PublicUrls"/>).</summary>
    public static void ValidateBillingConfiguration(this WebApplication app) => PublicUrls.Validate(app);

    private static string ClientIp(HttpContext ctx) => TriggerCheck.Web.Api.ApiSetup.ClientPartition(ctx.Connection.RemoteIpAddress);

    /// <summary>
    /// Anonymous provider endpoints. Neither uses cookies or antiforgery tokens (the provider posts them cross-site):
    /// the webhook is authenticated by its signature, the checkout callback by retrieving the result from the provider.
    /// </summary>
    public static void MapBillingEndpoints(this WebApplication app)
    {
        app.MapPost(WebhookPath, async (HttpContext ctx, IPaymentProvider provider, BillingService billing, ILogger<BillingService> logger) =>
        {
            // The server enforces the limit while reading (also for chunked bodies without Content-Length).
            if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size) size.MaxRequestBodySize = MaxWebhookBytes;
            if (ctx.Request.ContentLength > MaxWebhookBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            var body = await ReadLimitedAsync(ctx.Request.Body, ctx.RequestAborted);
            if (body is null) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            var e = provider.ParseWebhook(body, ctx.Request.Headers);
            if (e is null)
            {
                logger.LogWarning("Payment webhook with an invalid signature or body rejected.");
                return Results.Unauthorized();
            }
            var outcome = await billing.ApplyWebhookAsync(e, ctx.RequestAborted);
            return Results.Ok(new { status = outcome.ToString().ToLowerInvariant() });
        }).AllowAnonymous().DisableAntiforgery().RequireRateLimiting(ProviderPolicy);

        app.MapMethods(CallbackPath, [HttpMethods.Get, HttpMethods.Post], async (HttpContext ctx, BillingService billing) =>
        {
            string? token = ctx.Request.Query["token"];
            if (string.IsNullOrEmpty(token) && ctx.Request.HasFormContentType)
                token = (await ctx.Request.ReadFormAsync(ctx.RequestAborted))["token"];
            if (string.IsNullOrEmpty(token) || token.Length > 200) return Results.Redirect("/App/Billing?checkout=failed");
            var (result, _) = await billing.CompleteCheckoutAsync(token, ctx.RequestAborted);
            return Results.Redirect(result switch
            {
                CheckoutCompletion.Activated or CheckoutCompletion.AlreadyProcessed => "/App/Billing?checkout=success",
                CheckoutCompletion.Pending => "/App/Billing?checkout=pending",
                // Paid but refused (already subscribed / unexpected plan): cancelled and refunded, never "success".
                CheckoutCompletion.Refused => "/App/Billing?checkout=refused",
                _ => "/App/Billing?checkout=failed"
            });
        }).AllowAnonymous().DisableAntiforgery().RequireRateLimiting(ProviderPolicy);
    }

    /// <summary>Reads at most <see cref="MaxWebhookBytes"/>; null when the body is larger.</summary>
    private static async Task<string?> ReadLimitedAsync(Stream body, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        try
        {
            int read;
            while ((read = await body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > MaxWebhookBytes) return null;
                buffer.Write(chunk, 0, read);
            }
        }
        catch (BadHttpRequestException) { return null; } // MaxRequestBodySize exceeded
        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>Absolute URL for links in e-mails and provider callbacks (<see cref="PublicUrls"/>).</summary>
    public static string PublicUrl(HttpRequest request, string pathAndQuery) =>
        request.HttpContext.RequestServices.GetRequiredService<PublicUrls>().Build(request, pathAndQuery);
}
