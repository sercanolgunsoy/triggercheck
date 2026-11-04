using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Engine;
using TriggerCheck.Monitoring.Stats;
using TriggerCheck.Web.Areas.App.Support;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Api;

/// <summary>
/// REST API v1 (minimal APIs). Every handler is scoped to the API key's customer via <see cref="ICurrentCustomer"/>;
/// a customer id is never taken from the client, and other tenants' ids answer 404. Writes reuse the panel's rules
/// (<see cref="MonitorCommands"/>, <see cref="MonitorInput"/> validation, <see cref="MonitorLifecycle"/>).
/// </summary>
public static class ApiEndpoints
{
    public const int DefaultLimit = 100;
    public const int MaxChecksLimit = 1000;
    public const int MaxIncidentsLimit = 500;

    public static void MapApiEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup(ApiSetup.BasePath)
            .RequireAuthorization(ApiSetup.ReadPolicy)
            .RequireRateLimiting(ApiSetup.RateLimitPolicy)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        var monitors = api.MapGroup("/monitors").WithTags("Monitors");
        monitors.MapGet("", ListMonitors).Produces<List<MonitorDto>>();
        monitors.MapGet("/{id:int}", GetMonitor).Produces<MonitorDto>().ProducesProblem(StatusCodes.Status404NotFound);
        monitors.MapGet("/{id:int}/checks", ListChecks).Produces<List<CheckDto>>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound);

        var writes = monitors.MapGroup("").RequireAuthorization(ApiSetup.WritePolicy).ProducesProblem(StatusCodes.Status403Forbidden);
        writes.MapPost("", CreateMonitor).Accepts<MonitorInput>("application/json")
            .Produces<MonitorDto>(StatusCodes.Status201Created).ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict);
        writes.MapPut("/{id:int}", UpdateMonitor).Accepts<MonitorInput>("application/json")
            .Produces<MonitorDto>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        writes.MapDelete("/{id:int}", DeleteMonitor).Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);
        writes.MapPost("/{id:int}/pause", PauseMonitor).Produces<MonitorDto>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        writes.MapPost("/{id:int}/resume", ResumeMonitor).Produces<MonitorDto>().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        api.MapGet("/incidents", ListIncidents).WithTags("Incidents").Produces<List<IncidentDto>>().ProducesValidationProblem();
        api.MapGet("/status", GetStatus).WithTags("Status").Produces<StatusDto>();
    }

    // ---- monitors: read ---------------------------------------------------------------------------------------

    private static async Task<IResult> ListMonitors(AppDbContext db, ICurrentCustomer current, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var list = await db.Monitors.AsNoTracking().Where(m => m.CustomerId == cid).OrderBy(m => m.Name).ThenBy(m => m.Id).ToListAsync(ct);
        return Results.Ok(list.Select(MonitorDto.From).ToList());
    }

    private static async Task<IResult> GetMonitor(int id, AppDbContext db, ICurrentCustomer current, CancellationToken ct)
    {
        var m = await FindAsync(db, current, id, ct);
        return m is null ? ApiProblems.NotFound() : Results.Ok(MonitorDto.From(m));
    }

    /// <summary>Checks in [from, to] (default: last 24 hours), newest first, at most <c>limit</c> (default 100, max 1000).</summary>
    private static async Task<IResult> ListChecks(int id, string? from, string? to, string? limit, AppDbContext db, ICurrentCustomer current, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        if (!await db.Monitors.AnyAsync(m => m.Id == id && m.CustomerId == cid, ct)) return ApiProblems.NotFound();

        if (!TryParseTime(to, out var toUtc)) return ApiProblems.Invalid("to", "Use an ISO 8601 time, e.g. 2026-01-31T12:00:00Z.");
        if (!TryParseTime(from, out var fromUtc)) return ApiProblems.Invalid("from", "Use an ISO 8601 time, e.g. 2026-01-31T12:00:00Z.");
        var end = toUtc ?? DateTime.UtcNow;
        var start = fromUtc ?? end.AddHours(-24);
        if (start > end) return ApiProblems.Invalid("from", "'from' must not be later than 'to'.");
        if (!TryParseLimit(limit, MaxChecksLimit, out var take)) return ApiProblems.Invalid("limit", $"The limit must be between 1 and {MaxChecksLimit}.");

        var rows = await db.CheckResults.AsNoTracking()
            .Where(c => c.MonitorId == id && c.CheckedAtUtc >= start && c.CheckedAtUtc <= end)
            .OrderByDescending(c => c.CheckedAtUtc).ThenByDescending(c => c.Id)
            .Take(take)
            .Select(c => new CheckDto(c.Id, c.CheckedAtUtc, c.IsUp, c.StatusCode, c.ResponseTimeMs, c.Error))
            .ToListAsync(ct);
        return Results.Ok(rows.Select(c => c with { CheckedAtUtc = Utc.Of(c.CheckedAtUtc) }).ToList());
    }

    // ---- monitors: write --------------------------------------------------------------------------------------

    private static async Task<IResult> CreateMonitor(HttpContext http, MonitorCommands commands, ICurrentCustomer current, Text<AppResource> L, CancellationToken ct)
    {
        var (input, error) = await ApiProblems.ReadJsonAsync<MonitorInput>(http, ct);
        if (error is not null) return error;

        var cid = current.RequiredCustomerId;
        var plan = (await current.GetCustomerAsync(ct)).Plan;
        if (!PlanRules.CanAddMonitor(plan, await commands.CountAsync(cid, ct))) return QuotaReached(plan);

        var errors = ApiProblems.ValidateMonitor(input!, plan, http.RequestServices, L);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var r = await commands.CreateAsync(cid, plan, input!, ct);
        return r.Error switch
        {
            MonitorCommandError.QuotaReached => QuotaReached(plan),
            MonitorCommandError.Busy => Busy(http),
            _ => Results.Created($"{ApiSetup.BasePath}/monitors/{r.Monitor!.Id}", MonitorDto.From(r.Monitor))
        };
    }

    /// <summary>Full replacement: omitted fields get their defaults (same as an empty form field).</summary>
    private static async Task<IResult> UpdateMonitor(int id, HttpContext http, MonitorCommands commands, ICurrentCustomer current, Text<AppResource> L, CancellationToken ct)
    {
        var m = await commands.FindTrackedAsync(current.RequiredCustomerId, id, ct);
        if (m is null) return ApiProblems.NotFound();

        var (input, error) = await ApiProblems.ReadJsonAsync<MonitorInput>(http, ct);
        if (error is not null) return error;

        var plan = (await current.GetCustomerAsync(ct)).Plan;
        var errors = ApiProblems.ValidateMonitor(input!, plan, http.RequestServices, L);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var r = await commands.UpdateAsync(m, input!, ct);
        return r.Error switch
        {
            MonitorCommandError.NotFound => ApiProblems.NotFound(),
            MonitorCommandError.Conflict => ApiProblems.Conflict("The monitor was changed concurrently. Retry the request."),
            _ => Results.Ok(MonitorDto.From(r.Monitor!))
        };
    }

    private static async Task<IResult> DeleteMonitor(int id, MonitorCommands commands, ICurrentCustomer current, CancellationToken ct) =>
        await commands.DeleteAsync(current.RequiredCustomerId, id, ct) is null ? ApiProblems.NotFound() : Results.NoContent();

    private static Task<IResult> PauseMonitor(int id, MonitorLifecycle lifecycle, AppDbContext db, ICurrentCustomer current, CancellationToken ct) =>
        ChangeLifecycleAsync(id, db, current, cid => lifecycle.PauseAsync(id, cid, ct), ct);

    private static Task<IResult> ResumeMonitor(int id, MonitorLifecycle lifecycle, AppDbContext db, ICurrentCustomer current, CancellationToken ct) =>
        ChangeLifecycleAsync(id, db, current, cid => lifecycle.ResumeAsync(id, cid, ct), ct);

    /// <summary>Pause / resume through <see cref="MonitorLifecycle"/> (idempotent); answers the monitor's new state.</summary>
    private static async Task<IResult> ChangeLifecycleAsync(int id, AppDbContext db, ICurrentCustomer current,
        Func<int, Task<MonitorLifecycleResult?>> change, CancellationToken ct)
    {
        MonitorLifecycleResult? r;
        try { r = await change(current.RequiredCustomerId); }
        catch (DbUpdateConcurrencyException) { return ApiProblems.Conflict("The monitor was changed concurrently. Retry the request."); }
        if (r is null) return ApiProblems.NotFound();
        var m = await FindAsync(db, current, id, ct);
        return m is null ? ApiProblems.NotFound() : Results.Ok(MonitorDto.From(m));
    }

    // ---- incidents and status ---------------------------------------------------------------------------------

    /// <summary>Incidents newest first; <c>status</c> = open | resolved (default all), optional <c>monitorId</c>, <c>limit</c> (default 100, max 500).</summary>
    private static async Task<IResult> ListIncidents(string? status, string? monitorId, string? limit, AppDbContext db, ICurrentCustomer current, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var q = db.Incidents.AsNoTracking().Where(i => i.Monitor.CustomerId == cid);
        switch (status?.Trim().ToLowerInvariant())
        {
            case null or "": break;
            case "open": q = q.Where(i => i.ResolvedAtUtc == null); break;
            case "resolved": q = q.Where(i => i.ResolvedAtUtc != null); break;
            default: return ApiProblems.Invalid("status", "The status must be 'open' or 'resolved'.");
        }
        if (!string.IsNullOrWhiteSpace(monitorId))
        {
            if (!int.TryParse(monitorId, NumberStyles.None, CultureInfo.InvariantCulture, out var mid))
                return ApiProblems.Invalid("monitorId", "The monitor id must be a number.");
            q = q.Where(i => i.MonitorId == mid);
        }
        if (!TryParseLimit(limit, MaxIncidentsLimit, out var take)) return ApiProblems.Invalid("limit", $"The limit must be between 1 and {MaxIncidentsLimit}.");

        var rows = await q.OrderByDescending(i => i.StartedAtUtc).ThenByDescending(i => i.Id).Take(take)
            .Select(i => new IncidentDto(i.Id, i.MonitorId, i.Monitor.Name, i.StartedAtUtc, i.ResolvedAtUtc, i.ResolvedAtUtc == null, i.Cause))
            .ToListAsync(ct);
        return Results.Ok(rows.Select(i => i with { StartedAtUtc = Utc.Of(i.StartedAtUtc), ResolvedAtUtc = Utc.Of(i.ResolvedAtUtc) }).ToList());
    }

    private static async Task<IResult> GetStatus(AppDbContext db, ICurrentCustomer current, IUptimeStatsService stats, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var customer = await current.GetCustomerAsync(ct);
        var monitors = await db.Monitors.AsNoTracking().Where(m => m.CustomerId == cid).Select(m => new { m.Id, m.Status, m.IsEnabled }).ToListAsync(ct);
        var openIncidents = await db.Incidents.CountAsync(i => i.Monitor.CustomerId == cid && i.ResolvedAtUtc == null, ct);

        var now = DateTime.UtcNow;
        var summaries = await stats.GetSummariesAsync(monitors.Select(m => m.Id), now.AddHours(-24), now, ct);
        var total = summaries.Values.Sum(s => s.TotalChecks);
        var up = summaries.Values.Sum(s => s.UpChecks);

        var counts = new StatusCountsDto(monitors.Count,
            monitors.Count(m => m.Status == MonitorStatus.Up),
            monitors.Count(m => m.Status == MonitorStatus.Down),
            monitors.Count(m => m.Status == MonitorStatus.Paused),
            monitors.Count(m => m.Status == MonitorStatus.Unknown));
        var plan = new PlanDto(customer.Plan.Name, customer.Plan.MaxMonitors, customer.Plan.MinIntervalSeconds, customer.Plan.RetentionDays);
        return Results.Ok(new StatusDto(customer.Name, plan, counts, openIncidents, total > 0 ? Math.Round(100.0 * up / total, 3) : null, Utc.Of(now)));
    }

    // ---- helpers ----------------------------------------------------------------------------------------------

    private static Task<Domain.Entities.Monitor?> FindAsync(AppDbContext db, ICurrentCustomer current, int id, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        return db.Monitors.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id && m.CustomerId == cid, ct);
    }

    private static IResult QuotaReached(Plan plan) => ApiProblems.Conflict(
        $"You have reached the monitor limit of your plan ({plan.MaxMonitors}). Upgrade your plan to add a new monitor.");

    private static IResult Busy(HttpContext http)
    {
        http.Response.Headers.RetryAfter = "1";
        return Results.Problem("The system is busy right now. Please try again in a moment.",
            statusCode: StatusCodes.Status503ServiceUnavailable, title: "Service Unavailable");
    }

    private static bool TryParseTime(string? value, out DateTime? utc)
    {
        utc = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)) return false;
        utc = d;
        return true;
    }

    private static bool TryParseLimit(string? value, int max, out int limit)
    {
        limit = DefaultLimit;
        if (string.IsNullOrWhiteSpace(value)) return true;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out limit) && limit >= 1 && limit <= max;
    }
}
