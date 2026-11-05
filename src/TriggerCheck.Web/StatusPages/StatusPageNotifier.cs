using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Maintenance;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.StatusPages;

/// <summary>Settings of public status pages (section <c>StatusPages</c>).</summary>
public sealed class StatusPageOptions
{
    public const string Section = "StatusPages";
    /// <summary>How long the public page / JSON / feed are served from the output cache.</summary>
    public int CacheSeconds { get; set; } = 30;
    /// <summary>Page, JSON and feed requests per client (IPv4 address / IPv6 /64) and minute.</summary>
    public int RequestsPerMinutePerClient { get; set; } = 120;
    /// <summary>Subscription form submissions per client and hour.</summary>
    public int SubscriptionsPerHourPerClient { get; set; } = 10;
    /// <summary>Confirmation e-mails one status page may send per hour (sign-up spam guard).</summary>
    public int ConfirmationsPerPagePerHour { get; set; } = 30;
    /// <summary>Confirmation e-mails one address may receive per day, over all pages.</summary>
    public int ConfirmationsPerAddressPerDay { get; set; } = 3;
    /// <summary>How often subscribers are notified of new events; 0 = disabled.</summary>
    public int NotifyIntervalSeconds { get; set; } = 60;
    /// <summary>At most one incident e-mail per monitor and page within this time; changes in between are summarized.</summary>
    public int IncidentMailIntervalMinutes { get; set; } = 30;
    /// <summary>How often a verified custom domain's TXT record is checked again.</summary>
    public int DomainRecheckHours { get; set; } = 24;
    /// <summary>Consecutive failed re-checks after which a custom domain is no longer verified (no longer served).</summary>
    public int DomainRecheckFailures { get; set; } = 3;
}

public sealed record StatusPageNotifyResult(int Events, int Emails, int UnconfirmedPurged);

/// <summary>
/// Notifies confirmed subscribers of a status page when an incident of one of its monitors starts or is resolved and
/// when a maintenance window covering one of its monitors starts. Runs periodically and looks back
/// <see cref="LookBack"/>.
/// <list type="bullet">
/// <item>Each (page, event) is recorded in <see cref="StatusPageEvent"/> under a unique key in the SAME transaction as
/// its outbox rows, so restarts and parallel instances never notify twice and a failure never loses a claimed event.</item>
/// <item>Flapping: at most one incident e-mail per monitor and page per
/// <see cref="StatusPageOptions.IncidentMailIntervalMinutes"/>. Changes inside that time are recorded unmailed and the
/// next e-mail is a digest with the current state and the number of changes.</item>
/// <item>Pages beyond the plan's status page limit (oldest first are kept) are not notified.</item>
/// </list>
/// Also purges subscriptions never confirmed within <see cref="StatusPageSubscriptions.UnconfirmedDays"/> days and old event rows.
/// </summary>
public sealed class StatusPageNotifier(AppDbContext db, StatusPageSubscriptions subscriptions, AppEmailSignal signal, TimeProvider time,
    IOptionsMonitor<StatusPageOptions> options, Text<SharedResource> L, ILogger<StatusPageNotifier> logger)
{
    public static readonly TimeSpan LookBack = TimeSpan.FromHours(2);
    public const int KeepEventDays = 7;

    private sealed record Candidate(string Key, int? MonitorId, DateTime AtUtc, bool IsStart, string Subject, string Text);

    public async Task<StatusPageNotifyResult> RunAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var purged = await db.StatusPageSubscribers
            .Where(s => s.ConfirmedAtUtc == null && s.CreatedAtUtc < now.AddDays(-StatusPageSubscriptions.UnconfirmedDays))
            .ExecuteDeleteAsync(ct);
        await db.StatusPageEvents.Where(e => e.CreatedAtUtc < now.AddDays(-KeepEventDays)).ExecuteDeleteAsync(ct);

        var pages = await db.StatusPages.AsNoTracking().Include(p => p.Monitors)
            .Where(p => p.Customer.IsActive && db.StatusPageSubscribers.Any(s => s.StatusPageId == p.Id && s.ConfirmedAtUtc != null))
            .ToListAsync(ct);
        int events = 0, emails = 0;
        foreach (var page in pages)
        {
            try
            {
                if (!await StatusPageRules.IsWithinPlanAsync(db, page, ct)) continue;
                var (e, m) = await NotifyPageAsync(page, now, ct);
                events += e;
                emails += m;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Status page notification failed for page {PageId}; continuing.", page.Id);
            }
            finally { db.ChangeTracker.Clear(); }
        }
        if (emails > 0) signal.Notify();
        return new StatusPageNotifyResult(events, emails, purged);
    }

    private async Task<(int Events, int Emails)> NotifyPageAsync(StatusPage page, DateTime now, CancellationToken ct)
    {
        var floor = now - LookBack;
        if (page.CreatedAtUtc > floor) floor = page.CreatedAtUtc;
        var names = page.Monitors.ToDictionary(m => m.MonitorId, m => StatusPageSubscriptions.Safe(m.DisplayName));
        var ids = names.Keys.ToList();
        var cid = page.CustomerId;
        var tz = MaintenanceSchedule.FindTimeZone(page.TimeZoneId) ?? TimeZoneInfo.Utc;
        string At(DateTime utc) => MaintenanceSchedule.UtcToLocal(utc, tz).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " (" + page.TimeZoneId + ")";
        var title = StatusPageSubscriptions.Safe(page.Title);

        var candidates = new List<Candidate>();
        var incidents = await db.Incidents.AsNoTracking()
            .Where(i => ids.Contains(i.MonitorId) && i.Monitor.CustomerId == cid && (i.StartedAtUtc >= floor || i.ResolvedAtUtc >= floor))
            .Select(i => new { i.Id, i.MonitorId, i.StartedAtUtc, i.ResolvedAtUtc })
            .ToListAsync(ct);
        foreach (var i in incidents)
        {
            var name = names[i.MonitorId];
            if (i.StartedAtUtc >= floor)
                candidates.Add(new Candidate($"incident:{i.Id}:start", i.MonitorId, i.StartedAtUtc, true, L["[{0}] {1} is down", title, name],
                    L["{0} has been unavailable since {1}. We will let you know when it has recovered.", name, At(i.StartedAtUtc)]));
            if (i.ResolvedAtUtc is { } r && r >= floor)
                candidates.Add(new Candidate($"incident:{i.Id}:resolved", i.MonitorId, r, false, L["[{0}] {1} has recovered", title, name],
                    L["{0} is available again since {1} (down for {2}).", name, At(r), Fmt.Duration(r - i.StartedAtUtc)]));
        }

        var windows = await db.MaintenanceWindows.AsNoTracking()
            .Where(w => w.CustomerId == cid && (w.IsRecurring || (w.StartsAtUtc <= now && w.EndsAtUtc > floor)))
            .Where(w => w.AppliesToAllMonitors || w.Monitors.Any(m => m.CustomerId == cid && ids.Contains(m.Id)))
            .ToListAsync(ct);
        foreach (var w in windows)
        {
            foreach (var o in MaintenanceSchedule.Occurrences(w, floor, now.AddTicks(1)).Where(o => o.StartUtc >= floor && o.StartUtc <= now))
            {
                var name = StatusPageSubscriptions.Safe(w.Name);
                candidates.Add(new Candidate($"maintenance:{w.Id}:{o.StartUtc.Ticks}", null, o.StartUtc, false, L["[{0}] Maintenance started: {1}", title, name],
                    L["Planned maintenance \"{0}\" started at {1} and is scheduled to end at {2}.", name, At(o.StartUtc), At(o.EndUtc)]));
            }
        }

        var keys = candidates.Select(c => c.Key).ToList();
        var done = (await db.StatusPageEvents.Where(e => e.StatusPageId == page.Id && keys.Contains(e.Key)).Select(e => e.Key).ToListAsync(ct)).ToHashSet();
        var fresh = candidates.Where(c => !done.Contains(c.Key)).OrderBy(c => c.AtUtc).ToList();
        var recent = await db.StatusPageEvents.AsNoTracking()
            .Where(e => e.StatusPageId == page.Id && e.MonitorId != null && e.CreatedAtUtc >= now.AddDays(-1))
            .Select(e => new { e.MonitorId, e.Mailed, e.CreatedAtUtc }).ToListAsync(ct);
        var subscribers = await db.StatusPageSubscribers.AsNoTracking()
            .Where(s => s.StatusPageId == page.Id && s.ConfirmedAtUtc != null)
            .Select(s => new { s.Id, s.Email }).ToListAsync(ct);
        var baseUrl = subscriptions.BaseUrl(null);
        var throttle = TimeSpan.FromMinutes(Math.Max(0, options.CurrentValue.IncidentMailIntervalMinutes));

        int events = 0, emails = 0;
        // Maintenance starts: one e-mail each.
        foreach (var c in fresh.Where(c => c.MonitorId is null))
            if (await CommitAsync(page, [new StatusPageEvent { StatusPageId = page.Id, Key = c.Key, Mailed = true, CreatedAtUtc = now }], c.Subject, c.Text))
            {
                events++;
                emails += subscribers.Count;
            }

        // Incidents: per monitor, throttled.
        foreach (var monitorId in ids)
        {
            var mine = fresh.Where(c => c.MonitorId == monitorId).ToList();
            var lastMailed = recent.Where(e => e.MonitorId == monitorId && e.Mailed).Select(e => (DateTime?)e.CreatedAtUtc).Max();
            var pending = recent.Count(e => e.MonitorId == monitorId && !e.Mailed && (lastMailed is null || e.CreatedAtUtc > lastMailed));
            if (mine.Count == 0 && pending == 0) continue;

            if (lastMailed is { } last && now - last < throttle)
            {
                // Inside the quiet period: record only; the digest after it reports the state.
                if (mine.Count > 0 && await CommitAsync(page, mine.Select(c => Event(c, mailed: false)).ToList(), null, null))
                    events += mine.Count;
                continue;
            }

            string subject, text;
            if (mine.Count == 1 && pending == 0)
                (subject, text) = (mine[0].Subject, mine[0].Text);
            else
            {
                var name = names[monitorId];
                var down = await db.Incidents.AnyAsync(i => i.MonitorId == monitorId && i.ResolvedAtUtc == null, ct);
                var changes = mine.Count + pending;
                subject = down ? L["[{0}] {1} is down", title, name] : L["[{0}] {1} has recovered", title, name];
                text = down
                    ? L["{0} changed state {1} times since the last notice and is currently unavailable.", name, changes]
                    : L["{0} changed state {1} times since the last notice and is currently available.", name, changes];
            }
            var rows = mine.Select(c => Event(c, mailed: true)).ToList();
            if (rows.Count == 0)
            {
                // Keyed by the newest change it reports (not by now): concurrent runs on two instances compute the same
                // key, so the unique index lets only one of them queue the digest (OPS-3).
                var newest = recent.Where(e => e.MonitorId == monitorId && !e.Mailed && (lastMailed is null || e.CreatedAtUtc > lastMailed)).Max(e => e.CreatedAtUtc);
                rows.Add(new StatusPageEvent { StatusPageId = page.Id, Key = $"digest:{monitorId}:{newest.Ticks}", MonitorId = monitorId, Mailed = true, CreatedAtUtc = now });
            }
            if (await CommitAsync(page, rows, subject, text))
            {
                events += mine.Count;
                emails += subscribers.Count;
            }
        }
        return (events, emails);

        StatusPageEvent Event(Candidate c, bool mailed) =>
            new() { StatusPageId = page.Id, Key = c.Key, MonitorId = c.MonitorId, Mailed = mailed, CreatedAtUtc = now };

        // Claims the events and queues the e-mails atomically; false when another run claimed one of them first.
        async Task<bool> CommitAsync(StatusPage p, List<StatusPageEvent> claim, string? subjectLine, string? body)
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.StatusPageEvents.AddRange(claim);
            if (subjectLine is not null && body is not null)
                foreach (var s in subscribers)
                {
                    var unsubscribe = $"{baseUrl}/status/{p.Slug}/unsubscribe?token={Uri.EscapeDataString(subscriptions.UnsubscribeToken(s.Id))}";
                    OutboxRows.Add(db, s.Email, subjectLine, string.Join("\n\n", body,
                        L["Status page: {0}", $"{baseUrl}/status/{p.Slug}"],
                        L["Unsubscribe: {0}", unsubscribe]), now);
                }
            try
            {
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return true;
            }
            catch (DbUpdateException)
            {
                await tx.RollbackAsync(CancellationToken.None);
                return false;
            }
            finally { db.ChangeTracker.Clear(); }
        }
    }
}

/// <summary>
/// Runs <see cref="StatusPageNotifier"/> every <see cref="StatusPageOptions.NotifyIntervalSeconds"/> (0 = disabled) and
/// the custom domain re-verification (<see cref="CustomDomainReverifier"/>) on the same tick.
/// </summary>
public sealed class StatusPageNotifierService(IServiceScopeFactory scopes, IOptionsMonitor<StatusPageOptions> options, TimeProvider time,
    ILogger<StatusPageNotifierService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = options.CurrentValue.NotifyIntervalSeconds;
        if (seconds <= 0) return;
        // E-mails of background work are sent in English (the default UI language).
        CultureInfo.CurrentUICulture = new CultureInfo("en");
        try { await Task.Delay(TimeSpan.FromSeconds(30), time, stoppingToken); }
        catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(10, seconds)), time);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<StatusPageNotifier>().RunAsync(stoppingToken);
                await scope.ServiceProvider.GetRequiredService<CustomDomainReverifier>().RunAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Status page notifications failed.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
