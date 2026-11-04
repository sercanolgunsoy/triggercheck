using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring;
using TriggerCheck.Monitoring.Engine;
using TriggerCheck.Monitoring.Stats;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Web.Areas.App.Pages.Monitors;

/// <summary>Readable by all customer members; the POST handlers (pause/resume/check now) are CustomerAdmin only.</summary>
public class DetailsModel(AppDbContext db, ICurrentCustomer current, IUptimeStatsService stats, MonitorLifecycle lifecycle, ILogger<DetailsModel> logger, Text<AppResource> L) : PageModel
{
    public const int MaxChartPoints = 300;

    public record ChartPoint(string Label, int Ms, bool IsUp);

    public MonitorEntity Monitor { get; private set; } = null!;
    public UptimeSummary? Stats24h { get; private set; }
    public UptimeSummary? Stats7d { get; private set; }
    public UptimeSummary? Stats30d { get; private set; }
    public List<ChartPoint> Chart { get; private set; } = [];
    public List<CheckResult> RecentChecks { get; private set; } = [];
    public List<Incident> Incidents { get; private set; } = [];

    private bool IsAdmin => User.IsInRole(Roles.CustomerAdmin);

    private Task<MonitorEntity?> FindAsync(int id, CancellationToken ct, bool tracking = true)
    {
        var cid = current.RequiredCustomerId;
        var q = tracking ? db.Monitors : db.Monitors.AsNoTracking();
        return q.FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == cid, ct);
    }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
    {
        var m = await FindAsync(id, ct, tracking: false);
        if (m is null) return NotFound();
        Monitor = m;

        var now = DateTime.UtcNow;
        var ids = new[] { id };
        Stats24h = (await stats.GetSummariesAsync(ids, now.AddHours(-24), now, ct))[id];
        Stats7d = (await stats.GetSummariesAsync(ids, now.AddDays(-7), now, ct))[id];
        Stats30d = (await stats.GetSummariesAsync(ids, now.AddDays(-30), now, ct))[id];

        var from = now.AddHours(-24);
        var points = await db.CheckResults.AsNoTracking()
            .Where(c => c.MonitorId == id && c.CheckedAtUtc >= from)
            .OrderBy(c => c.CheckedAtUtc)
            .Select(c => new { c.CheckedAtUtc, c.ResponseTimeMs, c.IsUp })
            .ToListAsync(ct);
        if (points.Count <= MaxChartPoints)
        {
            Chart = points.Select(p => new ChartPoint(Fmt.ShortTime(p.CheckedAtUtc), p.ResponseTimeMs, p.IsUp)).ToList();
        }
        else
        {
            // Downsample to MaxChartPoints buckets: average response time, bucket is "down" if any check failed.
            var bucket = (int)Math.Ceiling(points.Count / (double)MaxChartPoints);
            Chart = points.Chunk(bucket)
                .Select(b => new ChartPoint(Fmt.ShortTime(b[0].CheckedAtUtc), (int)b.Average(x => x.ResponseTimeMs), b.All(x => x.IsUp)))
                .ToList();
        }

        RecentChecks = await db.CheckResults.AsNoTracking()
            .Where(c => c.MonitorId == id)
            .OrderByDescending(c => c.CheckedAtUtc)
            .Take(50)
            .ToListAsync(ct);

        Incidents = await db.Incidents.AsNoTracking()
            .Where(i => i.MonitorId == id && i.Monitor.CustomerId == m.CustomerId)
            .OrderBy(i => i.ResolvedAtUtc != null).ThenByDescending(i => i.StartedAtUtc)
            .Take(50)
            .ToListAsync(ct);

        return Page();
    }

    private IActionResult Back(int id, string? returnTo) =>
        returnTo == "list" ? RedirectToPage("Index") : RedirectToPage("Details", new { id });

    public async Task<IActionResult> OnPostPauseAsync(int id, string? returnTo, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();
        // Closes any open incident; see MonitorLifecycle for the shared pause/resume semantics.
        var r = await lifecycle.PauseAsync(id, current.RequiredCustomerId, ct);
        if (r is null) return NotFound();
        TempData["Success"] = L["\"{0}\" was paused. It will not be checked until it is resumed.", r.Name].Value;
        return Back(id, returnTo);
    }

    public async Task<IActionResult> OnPostResumeAsync(int id, string? returnTo, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();
        var r = await lifecycle.ResumeAsync(id, current.RequiredCustomerId, ct);
        if (r is null) return NotFound();
        TempData["Success"] = L["\"{0}\" is being monitored again.", r.Name].Value;
        return Back(id, returnTo);
    }

    public async Task<IActionResult> OnPostCheckNowAsync(int id, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();
        var m = await FindAsync(id, ct, tracking: false);
        if (m is null) return NotFound();

        if (m.Status == MonitorStatus.Paused || !m.IsEnabled)
        {
            TempData["Error"] = L["A paused monitor cannot be checked. Resume it first."].Value;
            return Back(id, null);
        }

        var runner = HttpContext.RequestServices.GetService<IMonitorCheckRunner>();
        if (runner is null)
        {
            TempData["Error"] = L["The on-demand check service is currently unavailable."].Value;
            return Back(id, null);
        }

        try
        {
            var r = await runner.RunAsync(m.Id, ct);
            var http = r.StatusCode is int sc ? $" (HTTP {sc})" : "";
            if (r.IsUp) TempData["Success"] = L["Check succeeded: {0}{1}.", Fmt.Ms(r.ResponseTimeMs), http].Value;
            else TempData["Error"] = L["Check failed: {0}{1}.", r.Error ?? L["an unknown error"].Value, http].Value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Manual check failed for monitor {MonitorId}", m.Id);
            TempData["Error"] = L["An error occurred while running the check."].Value;
        }
        return Back(id, null);
    }
}
