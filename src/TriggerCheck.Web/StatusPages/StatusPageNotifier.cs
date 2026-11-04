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
    /// <summary>How often subscribers are notified of new events; 0 = disabled.</summary>
    public int NotifyIntervalSeconds { get; set; } = 60;
}

public sealed record StatusPageNotifyResult(int Events, int Emails, int UnconfirmedPurged);

/// <summary>
/// Notifies confirmed subscribers of a status page when an incident of one of its monitors starts or is resolved and
/// when a maintenance window covering one of its monitors starts. Runs periodically and looks back
/// <see cref="LookBack"/>; each (page, event) is recorded in <see cref="StatusPageEvent"/> under a unique key before
/// mailing, so restarts and parallel instances never notify twice. E-mails go through the outbox. Also purges
/// subscriptions never confirmed within <see cref="StatusPageSubscriptions.UnconfirmedDays"/> days and old event rows.
/// </summary>
public sealed class StatusPageNotifier(AppDbContext db, StatusPageSubscriptions subscriptions, IAppEmailSender email, TimeProvider time,
    Text<SharedResource> L, ILogger<StatusPageNotifier> logger)
{
    public static readonly TimeSpan LookBack = TimeSpan.FromHours(2);
    public const int KeepEventDays = 7;

    private sealed record Event(string Key, string Subject, string Text);

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
        return new StatusPageNotifyResult(events, emails, purged);
    }

    private async Task<(int Events, int Emails)> NotifyPageAsync(StatusPage page, DateTime now, CancellationToken ct)
    {
        var floor = now - LookBack;
        if (page.CreatedAtUtc > floor) floor = page.CreatedAtUtc;
        var names = page.Monitors.ToDictionary(m => m.MonitorId, m => m.DisplayName);
        var ids = names.Keys.ToList();
        var cid = page.CustomerId;
        var tz = MaintenanceSchedule.FindTimeZone(page.TimeZoneId) ?? TimeZoneInfo.Utc;
        string At(DateTime utc) => MaintenanceSchedule.UtcToLocal(utc, tz).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " (" + page.TimeZoneId + ")";
        var title = StatusPageSubscriptions.Subject(page.Title);

        var candidates = new List<Event>();
        var incidents = await db.Incidents.AsNoTracking()
            .Where(i => ids.Contains(i.MonitorId) && i.Monitor.CustomerId == cid && (i.StartedAtUtc >= floor || i.ResolvedAtUtc >= floor))
            .Select(i => new { i.Id, i.MonitorId, i.StartedAtUtc, i.ResolvedAtUtc })
            .ToListAsync(ct);
        foreach (var i in incidents)
        {
            var name = StatusPageSubscriptions.Subject(names[i.MonitorId]);
            if (i.StartedAtUtc >= floor)
                candidates.Add(new Event($"incident:{i.Id}:start", L["[{0}] {1} is down", title, name],
                    L["{0} has been unavailable since {1}. We will let you know when it has recovered.", name, At(i.StartedAtUtc)]));
            if (i.ResolvedAtUtc is { } r && r >= floor)
                candidates.Add(new Event($"incident:{i.Id}:resolved", L["[{0}] {1} has recovered", title, name],
                    L["{0} is available again since {1} (down for {2}).", name, At(r), Fmt.Duration(r - i.StartedAtUtc)]));
        }

        var windows = await db.MaintenanceWindows.AsNoTracking()
            .Where(w => w.CustomerId == cid && (w.IsRecurring || (w.StartsAtUtc <= now && w.EndsAtUtc > floor)))
            .Where(w => w.AppliesToAllMonitors || w.Monitors.Any(m => m.CustomerId == cid && ids.Contains(m.Id)))
            .ToListAsync(ct);
        foreach (var w in windows)
            foreach (var o in MaintenanceSchedule.Occurrences(w, floor, now.AddTicks(1)).Where(o => o.StartUtc >= floor && o.StartUtc <= now))
            {
                var name = StatusPageSubscriptions.Subject(w.Name);
                candidates.Add(new Event($"maintenance:{w.Id}:{o.StartUtc.Ticks}", L["[{0}] Maintenance started: {1}", title, name],
                    L["Planned maintenance \"{0}\" started at {1} and is scheduled to end at {2}.", name, At(o.StartUtc), At(o.EndUtc)]));
            }
        if (candidates.Count == 0) return (0, 0);

        var keys = candidates.Select(c => c.Key).ToList();
        var done = (await db.StatusPageEvents.Where(e => e.StatusPageId == page.Id && keys.Contains(e.Key)).Select(e => e.Key).ToListAsync(ct)).ToHashSet();
        var subscribers = await db.StatusPageSubscribers.AsNoTracking()
            .Where(s => s.StatusPageId == page.Id && s.ConfirmedAtUtc != null)
            .Select(s => new { s.Id, s.Email }).ToListAsync(ct);

        var baseUrl = subscriptions.BaseUrl(null);
        int events = 0, emails = 0;
        foreach (var e in candidates.Where(c => !done.Contains(c.Key)))
        {
            // Claim the event first: a duplicate key means another run already sent it.
            db.StatusPageEvents.Add(new StatusPageEvent { StatusPageId = page.Id, Key = e.Key, CreatedAtUtc = now });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { db.ChangeTracker.Clear(); continue; }
            events++;
            foreach (var s in subscribers)
            {
                var unsubscribe = $"{baseUrl}/status/{page.Slug}/unsubscribe?token={Uri.EscapeDataString(subscriptions.UnsubscribeToken(s.Id))}";
                await email.SendAsync(s.Email, e.Subject, string.Join("\n\n", e.Text,
                    L["Status page: {0}", $"{baseUrl}/status/{page.Slug}"],
                    L["Unsubscribe: {0}", unsubscribe]), ct);
                emails++;
            }
        }
        return (events, emails);
    }
}

/// <summary>Runs <see cref="StatusPageNotifier"/> every <see cref="StatusPageOptions.NotifyIntervalSeconds"/> (0 = disabled).</summary>
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
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Status page notifications failed.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
