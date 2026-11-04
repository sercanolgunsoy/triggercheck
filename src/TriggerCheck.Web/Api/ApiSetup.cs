using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.OpenApi.Models;
using TriggerCheck.Web.Infrastructure;

namespace TriggerCheck.Web.Api;

/// <summary>
/// Wiring of the REST API: the API key scheme and policies, rate limits (per IP, key and customer), JSON options and the OpenAPI
/// document. Separation from the cookie-based panel:
/// <list type="bullet">
/// <item>The Identity cookie forwards every /api request to the API key scheme (<see cref="ForwardApiRequests"/>), so a
/// browser session can never call the API (no CSRF surface) and SessionValidator never runs for /api.</item>
/// <item>The API key scheme is only used by the /api policies, so a key never authenticates a page.</item>
/// </list>
/// </summary>
public static class ApiSetup
{
    public const string BasePath = "/api/v1";
    public const string ReadPolicy = "ApiRead";
    public const string WritePolicy = "ApiWrite";
    public const string RateLimitPolicy = "ApiPerKey";
    public const int DefaultRequestsPerMinute = 120;
    public const int DefaultRequestsPerMinutePerCustomer = 300;
    public const int DefaultRequestsPerMinutePerIp = 600;

    public static bool IsApiRequest(HttpContext http) => http.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);

    /// <summary>For the application cookie's ForwardDefaultSelector: /api requests are handled by the API key scheme only.</summary>
    public static string? ForwardApiRequests(HttpContext http) => IsApiRequest(http) ? ApiKeyDefaults.Scheme : null;

    public static IServiceCollection AddTriggerCheckApi(this IServiceCollection services, IConfiguration config)
    {
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyDefaults.Scheme, null);
        services.AddAuthorizationBuilder()
            .AddPolicy(ReadPolicy, p => p.AddAuthenticationSchemes(ApiKeyDefaults.Scheme).RequireAuthenticatedUser()
                .RequireClaim(AppClaims.CustomerId).RequireClaim(ApiKeyDefaults.ScopeClaim, ApiKeyDefaults.ReadScope))
            .AddPolicy(WritePolicy, p => p.AddAuthenticationSchemes(ApiKeyDefaults.Scheme).RequireAuthenticatedUser()
                .RequireClaim(AppClaims.CustomerId).RequireClaim(ApiKeyDefaults.ScopeClaim, ApiKeyDefaults.WriteScope));

        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        var perKey = config.GetValue("Api:RequestsPerMinute", DefaultRequestsPerMinute);
        var perCustomer = config.GetValue("Api:RequestsPerMinutePerCustomer", DefaultRequestsPerMinutePerCustomer);
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = RejectAsync;
            // Runs after authorization, so only authenticated API requests get here. Per key: the endpoint policy;
            // per customer (all keys together): the global limiter, which ignores everything that is not an API call.
            o.AddPolicy(RateLimitPolicy, http =>
                http.User.FindFirstValue(ApiKeyDefaults.KeyIdClaim) is string keyId
                    ? RateLimitPartition.GetFixedWindowLimiter("key:" + keyId, _ => PerMinute(perKey))
                    : RateLimitPartition.GetNoLimiter("anonymous"));
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
                IsApiRequest(http) && http.User.Identity?.AuthenticationType == ApiKeyDefaults.Scheme
                && http.User.FindFirstValue(AppClaims.CustomerId) is string cid
                    ? RateLimitPartition.GetFixedWindowLimiter("customer:" + cid, _ => PerMinute(perCustomer))
                    : RateLimitPartition.GetNoLimiter(""));
        });
        services.AddMemoryCache(); // one-time display of new API keys (App/ApiKeys)

        services.AddOpenApi("v1", o =>
        {
            o.ShouldInclude = d => d.RelativePath?.StartsWith("api/v1/", StringComparison.OrdinalIgnoreCase) == true;
            o.AddDocumentTransformer((doc, _, _) =>
            {
                doc.Info = new OpenApiInfo
                {
                    Title = "TriggerCheck API", Version = "v1",
                    Description = "Tenant-scoped REST API. Authenticate with 'Authorization: Bearer <API key>' (create keys under Settings > API keys)."
                };
                doc.Components ??= new OpenApiComponents();
                doc.Components.SecuritySchemes ??= new Dictionary<string, OpenApiSecurityScheme>();
                doc.Components.SecuritySchemes["apiKey"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http, Scheme = "bearer", Description = "API key (tc_...)"
                };
                doc.SecurityRequirements ??= [];
                doc.SecurityRequirements.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "apiKey" } }] = []
                });
                return Task.CompletedTask;
            });
        });
        return services;
    }

    private static FixedWindowRateLimiterOptions PerMinute(int permits) =>
        new() { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true };

    private static ValueTask RejectAsync(OnRejectedContext ctx, CancellationToken ct)
    {
        var retryAfter = ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var ra) ? Math.Max(1, (int)Math.Ceiling(ra.TotalSeconds)) : 60;
        ctx.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        return new ValueTask(ApiProblems.Write(ctx.HttpContext, StatusCodes.Status429TooManyRequests, "Too Many Requests",
            $"Rate limit exceeded. Retry after {retryAfter} seconds."));
    }

    /// <summary>
    /// /api-only middleware, placed before routing and authentication (pages are not affected):
    /// <list type="bullet">
    /// <item>Unhandled exceptions become a generic 500 problem+json (no details; the exception is logged);
    /// a BadHttpRequestException keeps its status code.</item>
    /// <item>Bodiless error responses produced by the framework (unknown route 404, 405, 415 from the content type
    /// check) get a problem+json body.</item>
    /// <item>Per client IP sliding-window limit (Api:RequestsPerMinutePerIp), applied before authentication so that
    /// floods of unauthenticated / invalid-key / forbidden requests are throttled too. IPv6 clients are partitioned by /64.
    /// Behind a reverse proxy enable ForwardedHeaders (see ForwardedHeadersSetup), otherwise every client shares the
    /// proxy's IP.</item>
    /// </list>
    /// </summary>
    /// <summary>Rate-limit partition of a client: the IPv4 address, or the /64 network of an IPv6 address (one host usually
    /// owns a whole /64, so per-address limits would be trivial to bypass).</summary>
    public static string ClientPartition(IPAddress? ip)
    {
        if (ip is null) return "ip:unknown";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return "ip:" + ip;
        var bytes = ip.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return "ip6:" + new IPAddress(bytes) + "/64";
    }

    public static void UseApiPipeline(this WebApplication app)
    {
        var perIp = app.Configuration.GetValue("Api:RequestsPerMinutePerIp", DefaultRequestsPerMinutePerIp);
        var ipLimiter = new RateLimiterOptions
        {
            RejectionStatusCode = StatusCodes.Status429TooManyRequests,
            OnRejected = RejectAsync,
            GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
                RateLimitPartition.GetSlidingWindowLimiter(ClientPartition(http.Connection.RemoteIpAddress),
                    _ => new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = perIp, Window = TimeSpan.FromMinutes(1), SegmentsPerWindow = 6, QueueLimit = 0, AutoReplenishment = true
                    }))
        };

        app.UseWhen(IsApiRequest, b =>
        {
            b.UseExceptionHandler(new ExceptionHandlerOptions
            {
                ExceptionHandler = http =>
                {
                    // Bad requests the framework detected (e.g. an oversized body) keep their 4xx; anything else is a generic 500.
                    var status = http.Features.Get<IExceptionHandlerFeature>()?.Error is BadHttpRequestException bad ? bad.StatusCode : 500;
                    http.Response.StatusCode = status;
                    return ApiProblems.Write(http, status, ReasonPhrases.GetReasonPhrase(status),
                        status >= 500 ? "An unexpected error occurred." : "The request could not be processed.");
                }
            });
            b.UseStatusCodePages(ctx =>
            {
                var http = ctx.HttpContext;
                var status = http.Response.StatusCode;
                return ApiProblems.Write(http, status, ReasonPhrases.GetReasonPhrase(status), status switch
                {
                    StatusCodes.Status404NotFound => "The resource was not found.",
                    StatusCodes.Status405MethodNotAllowed => "The HTTP method is not allowed for this resource.",
                    StatusCodes.Status415UnsupportedMediaType => "Send the body as JSON (Content-Type: application/json).",
                    _ => ReasonPhrases.GetReasonPhrase(status)
                });
            });
            b.UseRateLimiter(ipLimiter);
        });
    }

    /// <summary>Maps the API endpoints and the anonymous OpenAPI document at /api/v1/openapi.json.</summary>
    public static void MapTriggerCheckApi(this WebApplication app)
    {
        // AllowAnonymous: the document contains no customer data.
        app.MapOpenApi("/api/{documentName}/openapi.json").AllowAnonymous();
        app.MapApiEndpoints();
        app.MapStatusPageApi(); // FEAT-4
    }
}
