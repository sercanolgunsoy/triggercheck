using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.OpenApi.Models;
using TriggerCheck.Web.Infrastructure;

namespace TriggerCheck.Web.Api;

/// <summary>
/// Wiring of the REST API: the API key scheme and policies, the per-key rate limit, JSON options and the OpenAPI
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

        var perMinute = config.GetValue("Api:RequestsPerMinute", DefaultRequestsPerMinute);
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // Fixed one-minute window per API key. Only authenticated API requests reach the limiter (it runs after
            // authorization), so the partition key is always the key id.
            o.AddPolicy(RateLimitPolicy, http =>
                http.User.FindFirstValue(ApiKeyDefaults.KeyIdClaim) is string keyId
                    ? RateLimitPartition.GetFixedWindowLimiter("key:" + keyId, _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
                    })
                    : RateLimitPartition.GetNoLimiter("anonymous"));
            o.OnRejected = (ctx, ct) =>
            {
                var retryAfter = ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var ra) ? Math.Max(1, (int)Math.Ceiling(ra.TotalSeconds)) : 60;
                ctx.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
                return new ValueTask(ApiProblems.Write(ctx.HttpContext, StatusCodes.Status429TooManyRequests, "Too Many Requests",
                    $"Rate limit of {perMinute} requests per minute exceeded for this API key. Retry after {retryAfter} seconds."));
            };
        });

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

    /// <summary>
    /// Bodiless error responses under /api produced by the framework (unknown route 404, 405, 415 from the content
    /// type check) get a problem+json body too. Pages are not affected.
    /// </summary>
    public static void UseApiProblemStatusCodes(this WebApplication app) =>
        app.UseWhen(IsApiRequest, b => b.UseStatusCodePages(ctx =>
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
        }));

    /// <summary>Maps the API endpoints and the anonymous OpenAPI document at /api/v1/openapi.json.</summary>
    public static void MapTriggerCheckApi(this WebApplication app)
    {
        // AllowAnonymous: the document contains no customer data.
        app.MapOpenApi("/api/{documentName}/openapi.json").AllowAnonymous();
        app.MapApiEndpoints();
    }
}
