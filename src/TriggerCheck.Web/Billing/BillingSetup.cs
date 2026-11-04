using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using TriggerCheck.Web.Infrastructure;

namespace TriggerCheck.Web.Billing;

/// <summary>Registration of sign-up / billing services, their rate limits and the anonymous provider endpoints.</summary>
public static class BillingSetup
{
    /// <summary>Rate-limit policy of the sign-up page (POSTs per client IP).</summary>
    public const string RegisterPolicy = "register";
    /// <summary>Rate-limit policy of the provider callback and webhook endpoints.</summary>
    public const string ProviderPolicy = "payment-provider";

    public const string WebhookPath = "/billing/iyzico/webhook";
    public const string CallbackPath = "/billing/iyzico/callback";

    public static IServiceCollection AddBilling(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<BillingOptions>(config.GetSection(BillingOptions.Section));
        services.Configure<IyzicoOptions>(config.GetSection(IyzicoOptions.Section));
        services.AddSingleton<IAppEmailSender, SmtpAppEmailSender>();
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
            o.AddPolicy(RegisterPolicy, ctx =>
            {
                // Only form submissions count; showing the page is free.
                if (!HttpMethods.IsPost(ctx.Request.Method)) return RateLimitPartition.GetNoLimiter("get");
                var limit = ctx.RequestServices.GetRequiredService<IOptionsMonitor<BillingOptions>>().CurrentValue.RegistrationsPerHourPerIp;
                return RateLimitPartition.GetFixedWindowLimiter(ClientIp(ctx), _ => new FixedWindowRateLimiterOptions
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

    private static string ClientIp(HttpContext ctx) => TriggerCheck.Web.Api.ApiSetup.ClientPartition(ctx.Connection.RemoteIpAddress);

    /// <summary>
    /// Anonymous provider endpoints. Neither uses cookies or antiforgery tokens (the provider posts them cross-site):
    /// the webhook is authenticated by its signature, the checkout callback by retrieving the result from the provider.
    /// </summary>
    public static void MapBillingEndpoints(this WebApplication app)
    {
        app.MapPost(WebhookPath, async (HttpContext ctx, IPaymentProvider provider, BillingService billing, ILogger<BillingService> logger) =>
        {
            if (ctx.Request.ContentLength > 64 * 1024) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ctx.RequestAborted);
            if (body.Length > 64 * 1024) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
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
            return Results.Redirect(result is CheckoutCompletion.Activated or CheckoutCompletion.AlreadyProcessed
                ? "/App/Billing?checkout=success" : "/App/Billing?checkout=failed");
        }).AllowAnonymous().DisableAntiforgery().RequireRateLimiting(ProviderPolicy);
    }

    /// <summary>Absolute URL for links in e-mails and provider callbacks (<see cref="BillingOptions.PublicBaseUrl"/> or the request).</summary>
    public static string PublicUrl(HttpRequest request, BillingOptions options, string pathAndQuery)
    {
        var baseUrl = options.PublicBaseUrl is { Length: > 0 } b ? b.TrimEnd('/') : $"{request.Scheme}://{request.Host}{request.PathBase}";
        return baseUrl + pathAndQuery;
    }
}
