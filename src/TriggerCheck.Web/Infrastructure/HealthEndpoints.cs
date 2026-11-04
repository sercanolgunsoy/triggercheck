using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring;
using TriggerCheck.Monitoring.Diagnostics;

namespace TriggerCheck.Web.Infrastructure;

/// <summary>
/// Health endpoints (anonymous, never cached):
/// <list type="bullet">
/// <item><c>/health/live</c> (and the legacy <c>/health</c>): the process is up and serving requests; runs no checks.</item>
/// <item><c>/health/ready</c>: database reachable and, when Monitoring:Enabled, the scheduler completed a tick recently
/// (<see cref="SchedulerHeartbeatHealthCheck"/>). 200 for Healthy/Degraded, 503 for Unhealthy.</item>
/// </list>
/// The body is <c>{"status":"..."}</c>; per-check status and our own descriptions are added only in Development.
/// Exception texts are never written.
/// </summary>
public static class HealthEndpoints
{
    public const string ReadyTag = "ready";

    public static IServiceCollection AddAppHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddDbContextCheck<AppDbContext>("database", tags: [ReadyTag])
            .AddCheck<SchedulerHeartbeatHealthCheck>("scheduler", tags: [ReadyTag]);
        return services;
    }

    public static IEndpointRouteBuilder MapAppHealthChecks(this IEndpointRouteBuilder app, bool includeDetails)
    {
        var live = new HealthCheckOptions { Predicate = _ => false, ResponseWriter = (ctx, report) => WriteAsync(ctx, report, includeDetails) };
        var ready = new HealthCheckOptions { Predicate = r => r.Tags.Contains(ReadyTag), ResponseWriter = (ctx, report) => WriteAsync(ctx, report, includeDetails) };

        // AllowAnonymous also makes SessionValidator skip its per-request DB lookup here.
        app.MapHealthChecks("/health/live", live).AllowAnonymous();
        app.MapHealthChecks("/health", live).AllowAnonymous(); // backwards compatible alias of /health/live
        app.MapHealthChecks("/health/ready", ready).AllowAnonymous();
        return app;
    }

    private static Task WriteAsync(HttpContext ctx, HealthReport report, bool includeDetails)
    {
        ctx.Response.ContentType = "application/json; charset=utf-8";
        object body = includeDetails
            ? new
            {
                status = report.Status.ToString(),
                checks = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString(), description = e.Value.Description })
            }
            : new { status = report.Status.ToString() };
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(body));
    }
}

/// <summary>
/// Unhealthy when the monitoring scheduler is enabled but has not completed a tick within
/// <see cref="StaleAfter"/> (at least 2 minutes, or 3 tick intervals if longer). Before the first tick it reports
/// Degraded during the same grace period after startup. Healthy (not applicable) when Monitoring:Enabled=false.
/// </summary>
public sealed class SchedulerHeartbeatHealthCheck(IOptions<MonitoringOptions> options, MonitoringMetrics metrics, TimeProvider time) : IHealthCheck
{
    public static readonly TimeSpan MinStaleAfter = TimeSpan.FromMinutes(2);

    private TimeSpan StaleAfter => TimeSpan.FromTicks(Math.Max(MinStaleAfter.Ticks, TimeSpan.FromMilliseconds(options.Value.SchedulerTickMilliseconds).Ticks * 3));

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled)
            return Task.FromResult(HealthCheckResult.Healthy("Scheduler disabled in this process"));

        var now = time.GetUtcNow();
        var last = metrics.LastSchedulerHeartbeat;
        HealthCheckResult result;
        if (last is null)
            result = now - metrics.StartedAt <= StaleAfter
                ? HealthCheckResult.Degraded("Scheduler has not completed a tick yet")
                : HealthCheckResult.Unhealthy("Scheduler has not completed a tick since startup");
        else
            result = now - last.Value <= StaleAfter
                ? HealthCheckResult.Healthy("Scheduler heartbeat is fresh")
                : HealthCheckResult.Unhealthy("Scheduler heartbeat is stale");
        return Task.FromResult(result);
    }
}
