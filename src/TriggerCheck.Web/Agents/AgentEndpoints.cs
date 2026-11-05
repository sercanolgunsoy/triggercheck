using System.Buffers;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using TriggerCheck.Monitoring.Regions;
using TriggerCheck.Web.Api;

namespace TriggerCheck.Web.Agents;

/// <summary>
/// FEAT-6: internal probe agent API (<see cref="AgentProtocol"/>), not part of the public REST API or its OpenAPI document.
/// <list type="bullet">
/// <item>Auth: <see cref="AgentAuthenticationHandler"/> only (customer API keys and cookies are never accepted here, and
/// agent credentials are never accepted elsewhere).</item>
/// <item>Limits: per region (<see cref="RegionOptions.AgentRequestsPerMinute"/>) on top of the per-IP limit of all /api
/// requests (ApiSetup.UseApiPipeline, which also renders errors as problem+json); request bodies are capped
/// (<see cref="AgentProtocol.MaxLeaseBodyBytes"/>, <see cref="AgentProtocol.MaxResultsBodyBytes"/>: 413) and a results
/// batch holds at most <see cref="AgentProtocol.MaxResultsPerRequest"/> items.</item>
/// <item>The region always comes from the credential, never from the body.</item>
/// </list>
/// </summary>
public static class AgentEndpoints
{
    public static IServiceCollection AddProbeAgentApi(this IServiceCollection services, IConfiguration config)
    {
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, AgentAuthenticationHandler>(AgentAuthDefaults.Scheme, null);
        services.AddAuthorizationBuilder()
            .AddPolicy(AgentAuthDefaults.Policy, p => p.AddAuthenticationSchemes(AgentAuthDefaults.Scheme).RequireAuthenticatedUser()
                .RequireClaim(AgentAuthDefaults.RegionIdClaim));
        var perRegion = config.GetValue("Regions:AgentRequestsPerMinute", new RegionOptions().AgentRequestsPerMinute);
        services.AddRateLimiter(o => o.AddPolicy(AgentAuthDefaults.RateLimitPolicy, http =>
            http.User.FindFirstValue(AgentAuthDefaults.RegionIdClaim) is string regionId
                ? RateLimitPartition.GetFixedWindowLimiter("region:" + regionId, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, perRegion), Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
                })
                : RateLimitPartition.GetNoLimiter("anonymous")));
        return services;
    }

    public static void MapProbeAgentApi(this IEndpointRouteBuilder app)
    {
        var agent = app.MapGroup(AgentProtocol.BasePath)
            .RequireAuthorization(AgentAuthDefaults.Policy)
            .RequireRateLimiting(AgentAuthDefaults.RateLimitPolicy)
            .DisableAntiforgery()
            .ExcludeFromDescription();
        agent.MapPost("/lease", LeaseAsync);
        agent.MapPost("/results", ResultsAsync);
    }

    private static int RegionId(HttpContext http) =>
        int.Parse(http.User.FindFirstValue(AgentAuthDefaults.RegionIdClaim)!, CultureInfo.InvariantCulture);

    private static async Task<IResult> LeaseAsync(HttpContext http, ProbeLeaseService leases, CancellationToken ct)
    {
        var (request, error) = await ReadAsync<LeaseRequest>(http, AgentProtocol.MaxLeaseBodyBytes, ct);
        if (error is not null) return error;
        if (request!.MaxItems is < 1 or > AgentProtocol.MaxLeaseItems)
            return ApiProblems.Invalid("maxItems", $"Must be between 1 and {AgentProtocol.MaxLeaseItems}.");
        http.Response.Headers.CacheControl = "no-store";
        return Results.Json(await leases.LeaseAsync(RegionId(http), request.MaxItems, ct), AgentProtocol.Json);
    }

    private static async Task<IResult> ResultsAsync(HttpContext http, ProbeLeaseService leases, CancellationToken ct)
    {
        var (request, error) = await ReadAsync<ResultsRequest>(http, AgentProtocol.MaxResultsBodyBytes, ct);
        if (error is not null) return error;
        if (request!.Results is null || request.Results.Count == 0)
            return ApiProblems.Invalid("results", "At least one result is required.");
        if (request.Results.Count > AgentProtocol.MaxResultsPerRequest)
            return ApiProblems.Invalid("results", $"At most {AgentProtocol.MaxResultsPerRequest} results per request.");
        var acks = await leases.IngestAsync(RegionId(http), request.Results.Where(r => r is not null).ToList(), ct);
        return Results.Json(new ResultsResponse { Results = acks }, AgentProtocol.Json);
    }

    /// <summary>Reads a JSON body of at most <paramref name="maxBytes"/> bytes (413 beyond, whatever Content-Length says).</summary>
    private static async Task<(T? Value, IResult? Error)> ReadAsync<T>(HttpContext http, int maxBytes, CancellationToken ct) where T : class
    {
        if (!http.Request.HasJsonContentType())
            return (null, Results.Problem("Send the body as JSON (Content-Type: application/json).", statusCode: StatusCodes.Status415UnsupportedMediaType, title: "Unsupported Media Type"));
        if (http.Request.ContentLength > maxBytes) return (null, TooLarge(maxBytes));
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = maxBytes;

        var buffer = ArrayPool<byte>.Shared.Rent(maxBytes + 1);
        try
        {
            var total = 0;
            try
            {
                int read;
                while (total <= maxBytes && (read = await http.Request.Body.ReadAsync(buffer.AsMemory(total, maxBytes + 1 - total), ct)) > 0)
                    total += read;
            }
            catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return (null, TooLarge(maxBytes));
            }
            if (total > maxBytes) return (null, TooLarge(maxBytes));
            try
            {
                var value = JsonSerializer.Deserialize<T>(buffer.AsSpan(0, total), AgentProtocol.Json);
                return value is null ? (null, ApiProblems.Invalid("", "A JSON object is required.")) : (value, null);
            }
            catch (JsonException)
            {
                return (null, ApiProblems.Invalid("", "The JSON body is malformed or has a value of the wrong type."));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static IResult TooLarge(int maxBytes) =>
        Results.Problem($"The request body is larger than {maxBytes} bytes.", statusCode: StatusCodes.Status413PayloadTooLarge, title: "Payload Too Large");
}
