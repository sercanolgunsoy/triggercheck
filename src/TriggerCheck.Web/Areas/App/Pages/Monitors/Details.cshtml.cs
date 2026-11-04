using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Ai.Features;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring;
using TriggerCheck.Monitoring.Checks;
using TriggerCheck.Monitoring.Engine;
using TriggerCheck.Monitoring.Maintenance;
using TriggerCheck.Monitoring.Regions;
using Microsoft.Extensions.Options;
using TriggerCheck.Monitoring.Stats;
using TriggerCheck.Web.Areas.App.Support;
using TriggerCheck.Web.Heartbeat;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Web.Areas.App.Pages.Monitors;

/// <summary>Readable by all customer members; the POST handlers (pause/resume/check now) are CustomerAdmin only.</summary>
public class DetailsModel(AppDbContext db, ICurrentCustomer current, IUptimeStatsService stats, MonitorLifecycle lifecycle, ILogger<DetailsModel> logger, Text<AppResource> L,
    ManualCheckLimiter limiter, IResponseTimeSeriesService series, TimeProvider time, MonitorCommands commands, HeartbeatUrlReveal reveals, AiPanels ai,
    IOptionsMonitor<RegionOptions> regionOptions) : PageModel
{
    public const int MaxChartPoints = ResponseTimeSeriesService.MaxPoints;

    /// <summary>Freshly issued heartbeat ping URL: only on the first GET after it was issued, only for the admin who issued it.</summary>
    public string? NewPingUrl { get; private set; }

    /// <summary>Chart ranges offered on the page (query string <c>?range=</c>).</summary>
    public static readonly IReadOnlyDictionary<string, TimeSpan> Ranges = new Dictionary<string, TimeSpan>
    {
        ["24h"] = TimeSpan.FromHours(24),
        ["7d"] = TimeSpan.FromDays(7),
        ["30d"] = TimeSpan.FromDays(30)
    };

    /// <summary>One chart bucket as serialized for Chart.js (null = no data).</summary>
    public record ChartPoint(string Label, double? Avg, int? P95, int Checks, int Failures,
        double? Dns, double? Connect, double? Tls, double? Wait, double? Transfer);

    /// <summary>Incident marker: bucket index where it started (and ended, when resolved within the range).</summary>
    public record ChartIncident(int Start, int? End, string Label);

    /// <summary>Maintenance band (FEAT-3): first and last bucket index covered by a maintenance window.</summary>
    public record ChartMaintenance(int Start, int End, string Label);

    public string Range { get; private set; } = "24h";
    public List<ChartIncident> ChartIncidents { get; private set; } = [];
    public List<ChartMaintenance> ChartMaintenanceBands { get; private set; } = [];
    /// <summary>The monitor is in a maintenance window right now.</summary>
    public bool InMaintenance { get; private set; }
    public bool HasPhaseData => Chart.Any(p => p.Wait is not null);
    public bool IsHttps => MonitorStateMachine.IsHttpsMonitor(Monitor);

    public MonitorEntity Monitor { get; private set; } = null!;
    public UptimeSummary? Stats24h { get; private set; }
    public UptimeSummary? Stats7d { get; private set; }
    public UptimeSummary? Stats30d { get; private set; }
    public List<ChartPoint> Chart { get; private set; } = [];
    public List<CheckResult> RecentChecks { get; private set; } = [];
    public List<Incident> Incidents { get; private set; } = [];
    /// <summary>FEAT-6: per-region breakdown (empty for single-region monitors) and whether regions can be managed here.</summary>
    public RegionPanel Regions { get; private set; } = new([], false, 0);
    /// <summary>AI-1 slowdown signal that is open or ended within the last 24 hours (AI-3 explains it).</summary>
    public AnomalySignal? Slowdown { get; private set; }
    /// <summary>Highest observed / baseline ratio of <see cref="Slowdown"/> (raw L1 text).</summary>
    public double? SlowdownRatio { get; private set; }
    public AiPanelModel? SlowdownAi { get; private set; }

    /// <summary>Compact phase breakdown of one check for the history table (empty without timing data).</summary>
    public string PhaseText(CheckResult c)
    {
        if (c.TtfbMs is not int ttfb) return "";
        if (c.DnsMs is null) return L["reused connection, TTFB {0} ms", ttfb].Value;
        var tls = c.TlsMs is int t ? $" · TLS {t}" : "";
        return $"DNS {c.DnsMs} · TCP {c.ConnectMs}{tls} · TTFB {ttfb} ms";
    }

    private bool IsAdmin => User.IsInRole(Roles.CustomerAdmin);

    private Task<MonitorEntity?> FindAsync(int id, CancellationToken ct, bool tracking = true)
    {
        var cid = current.RequiredCustomerId;
        var q = tracking ? db.Monitors : db.Monitors.AsNoTracking();
        return q.FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == cid, ct);
    }

    public async Task<IActionResult> OnGetAsync(int id, string? range, Guid? reveal, CancellationToken ct)
    {
        var m = await FindAsync(id, ct, tracking: false);
        if (m is null) return NotFound();
        Monitor = m;
        if (IsAdmin && reveal is not null)
        {
            NewPingUrl = reveals.Take(User, m.CustomerId, m.Id, reveal);
            Response.Headers.CacheControl = "no-store";
        }

        var now = DateTime.UtcNow;
        var ids = new[] { id };
        Stats24h = (await stats.GetSummariesAsync(ids, now.AddHours(-24), now, ct))[id];
        Stats7d = (await stats.GetSummariesAsync(ids, now.AddDays(-7), now, ct))[id];
        Stats30d = (await stats.GetSummariesAsync(ids, now.AddDays(-30), now, ct))[id];

        Range = range is not null && Ranges.ContainsKey(range) ? range : "24h";
        var s = await series.GetRecentAsync(id, m.CustomerId, Ranges[Range], ct); // cached for a minute
        var from = s.FromUtc;
        now = s.ToUtc;
        string Label(DateTime utc) => Range == "24h" ? Fmt.ShortTime(utc) : Fmt.ToLocal(utc).ToString("dd.MM HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        Chart = s.Buckets.Select(b => new ChartPoint(Label(b.StartUtc), b.AvgMs, b.P95Ms, b.Checks, b.Failures,
            b.DnsMs, b.ConnectMs, b.TlsMs, b.WaitMs, b.TransferMs)).ToList();
        if (Chart.All(p => p.Checks == 0)) Chart = [];

        var rangeIncidents = await db.Incidents.AsNoTracking()
            .Where(i => i.MonitorId == id && i.Monitor.CustomerId == m.CustomerId && i.StartedAtUtc <= now && (i.ResolvedAtUtc == null || i.ResolvedAtUtc >= from))
            .OrderByDescending(i => i.StartedAtUtc)
            .Take(200) // newest
            .ToListAsync(ct);
        rangeIncidents.Reverse();
        ChartIncidents = rangeIncidents.Select(i => new ChartIncident(
            s.BucketIndexOf(i.StartedAtUtc < from ? from : i.StartedAtUtc) ?? 0,
            i.ResolvedAtUtc is DateTime r ? s.BucketIndexOf(r) : null,
            Label(i.StartedAtUtc))).ToList();

        var bands = await MaintenanceQueries.OccurrencesAsync(db, m.CustomerId, id, from, now, ct);
        ChartMaintenanceBands = bands.Select(b => new ChartMaintenance(
            s.BucketIndexOf(b.StartUtc) ?? 0, s.BucketIndexOf(b.EndUtc.AddTicks(-1)) ?? s.Buckets.Count - 1,
            Label(b.StartUtc) + " – " + Label(b.EndUtc))).ToList();
        InMaintenance = (await MaintenanceQueries.MonitorsInMaintenanceAsync(db, m.CustomerId, [id], time.GetUtcNow().UtcDateTime, ct)).Count > 0;

        Regions = new RegionPanel(await RegionBreakdown.LoadAsync(db, m, time.GetUtcNow().UtcDateTime, regionOptions.CurrentValue, ct),
            IsAdmin && RegionRules.SupportsRegions(m.Type) && await RegionsModel.PlanAllowsRegionsAsync(db, m.CustomerId, ct), m.Id);

        // Central checks (remote regions are summarized in the region breakdown).
        RecentChecks = await db.CheckResults.AsNoTracking()
            .Where(c => c.MonitorId == id && c.RegionId == null)
            .OrderByDescending(c => c.CheckedAtUtc)
            .Take(50)
            .ToListAsync(ct);

        Incidents = await db.Incidents.AsNoTracking()
            .Where(i => i.MonitorId == id && i.Monitor.CustomerId == m.CustomerId)
            .OrderBy(i => i.ResolvedAtUtc != null).ThenByDescending(i => i.StartedAtUtc)
            .Take(50)
            .ToListAsync(ct);

        await LoadSlowdownAsync(m, ct);
        return Page();
    }

    private async Task LoadSlowdownAsync(MonitorEntity m, CancellationToken ct)
    {
        var since = time.GetUtcNow().UtcDateTime.AddHours(-24);
        Slowdown = await db.AnomalySignals.AsNoTracking()
            .Where(a => a.MonitorId == m.Id && a.CustomerId == m.CustomerId && (a.EndedAtUtc == null || a.EndedAtUtc >= since))
            .OrderBy(a => a.EndedAtUtc != null).ThenByDescending(a => a.StartedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (Slowdown is null) return;
        SlowdownRatio = SlowdownEvidence.PeakRatio(Slowdown.DetailsJson);
        SlowdownAi = await ai.LoadAsync(await current.GetCustomerAsync(ct), AiFeatures.SlowdownExplanation, AiSubjects.AnomalySignal,
            Slowdown.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), Slowdown.UpdatedAtUtc,
            Url.Page("Details", new { id = m.Id }) ?? "/App/Monitors", User.FindFirstValue(ClaimTypes.NameIdentifier), IsAdmin, ct);
    }

    private IActionResult Back(int id, string? returnTo) =>
        returnTo == "list" ? RedirectToPage("Index") : RedirectToPage("Details", new { id });

    /// <summary>Pause/resume kept losing optimistic-concurrency races (very unlikely): ask the user to retry.</summary>
    private IActionResult ConcurrencyConflict(int id, string? returnTo)
    {
        TempData["Error"] = L["This monitor was changed in the meantime (for example paused or edited by someone else). Reload the page and try again."].Value;
        return Back(id, returnTo);
    }

    public async Task<IActionResult> OnPostPauseAsync(int id, string? returnTo, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();
        // Closes any open incident; see MonitorLifecycle for the shared pause/resume semantics.
        MonitorLifecycleResult? r;
        try { r = await lifecycle.PauseAsync(id, current.RequiredCustomerId, ct); }
        catch (DbUpdateConcurrencyException) { return ConcurrencyConflict(id, returnTo); }
        if (r is null) return NotFound();
        TempData["Success"] = L["\"{0}\" was paused. It will not be checked until it is resumed.", r.Name].Value;
        return Back(id, returnTo);
    }

    public async Task<IActionResult> OnPostResumeAsync(int id, string? returnTo, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();
        MonitorLifecycleResult? r;
        try { r = await lifecycle.ResumeAsync(id, current.RequiredCustomerId, ct); }
        catch (DbUpdateConcurrencyException) { return ConcurrencyConflict(id, returnTo); }
        if (r is null) return NotFound();
        TempData["Success"] = L["\"{0}\" is being monitored again.", r.Name].Value;
        return Back(id, returnTo);
    }

    /// <summary>Heartbeat: replaces the ping token. The old URL stops working immediately; the new one is shown once.</summary>
    public async Task<IActionResult> OnPostRegenerateTokenAsync(int id, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();
        var cid = current.RequiredCustomerId;
        var r = await commands.RegenerateHeartbeatTokenAsync(cid, id, ct);
        if (r.Error == MonitorCommandError.NotFound) return NotFound();
        if (r.Error == MonitorCommandError.Conflict) return ConcurrencyConflict(id, null);
        TempData["Success"] = L["A new ping URL was generated. The old URL no longer works."].Value;
        return RedirectToPage("Details", new { id, reveal = reveals.Park(User, cid, id, HeartbeatEndpoint.PingUrl(Request, r.IssuedHeartbeatToken!)) });
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
        if (m.Type == MonitorType.Heartbeat)
        {
            TempData["Error"] = L["A heartbeat monitor is checked when its ping arrives or is overdue; it cannot be checked on demand."].Value;
            return Back(id, null);
        }

        var runner = HttpContext.RequestServices.GetService<IMonitorCheckRunner>();
        if (runner is null)
        {
            TempData["Error"] = L["The on-demand check service is currently unavailable."].Value;
            return Back(id, null);
        }

        // Rate limit (per monitor cooldown + per customer quota): no probe is executed when denied.
        var minInterval = await db.Customers.AsNoTracking().Where(c => c.Id == m.CustomerId)
            .Select(c => c.Plan.MinIntervalSeconds).FirstOrDefaultAsync(ct);
        var decision = limiter.TryAcquire(m.CustomerId, m.Id, m.LastCheckedAtUtc, minInterval);
        if (!decision.Allowed)
        {
            TempData["Error"] = decision.Denial == ManualCheckLimiter.Denial.MonitorCooldown
                ? L["This monitor was checked moments ago. Try again in {0} seconds.", decision.RetryAfterSeconds].Value
                : L["Too many manual checks for your account. Try again in {0} seconds.", decision.RetryAfterSeconds].Value;
            return Back(id, null);
        }

        try
        {
            var r = await runner.RunAsync(m.Id, ct);
            var http = r.StatusCode is int sc ? $" (HTTP {sc})" : "";
            var confirming = !r.IsUp && await db.Monitors.AsNoTracking().AnyAsync(x => x.Id == m.Id && x.PendingConfirmationSinceUtc != null, ct);
            if (r.IsUp) TempData["Success"] = L["Check succeeded: {0}{1}.", Fmt.Ms(r.ResponseTimeMs), http].Value;
            else if (confirming)
                TempData["Error"] = L["Check failed: {0}{1}. The failure was recorded; a confirmation check runs shortly before the monitor is marked down.", r.Error ?? L["an unknown error"].Value, http].Value;
            else TempData["Error"] = L["Check failed: {0}{1}.", r.Error ?? L["an unknown error"].Value, http].Value;
        }
        catch (MonitorCheckDiscardedException ex) when (ex.MonitorDeleted)
        {
            TempData["Error"] = L["The monitor was deleted while it was being checked."].Value;
            return RedirectToPage("Index");
        }
        catch (MonitorCheckDiscardedException)
        {
            TempData["Error"] = L["The monitor settings were changed while it was being checked. The result was discarded; run the check again."].Value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Manual check failed for monitor {MonitorId}", m.Id);
            TempData["Error"] = L["An error occurred while running the check."].Value;
        }
        return Back(id, null);
    }
}
