using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Web.StatusPages;

public enum PostmortemResult
{
    Saved,
    Published,
    NotFound,
    /// <summary>Only resolved incidents get a postmortem.</summary>
    NotResolved,
    /// <summary>Already approved and published: read-only.</summary>
    AlreadyPublished,
    /// <summary>A text is too long or contains control characters.</summary>
    InvalidText,
    /// <summary>Publishing needs a customer message.</summary>
    EmptyMessage,
    /// <summary>Publishing needs at least one status page.</summary>
    NoPages,
    /// <summary>A chosen page is not the customer's or does not show the incident's monitor.</summary>
    InvalidPage,
    /// <summary>A chosen page already has the maximum number of notes for the incident.</summary>
    TooManyNotes,
}

/// <summary>
/// AI-8 (autonomy A2): saving the human-edited postmortem draft and the explicit approval that publishes it. This is
/// the only path from a draft to a public page: <see cref="PublishAsync"/> runs on a CustomerAdmin's "approve and
/// publish" and copies the customer message exactly as the admin submitted it into one <see cref="StatusPageNote"/> per
/// chosen status page (pages of the same customer that show the incident's monitor), records who approved it and when,
/// and makes the draft read-only. Notes are not e-mailed to subscribers (FEAT-4 mails incident start / resolve only).
/// The draft is marked published with a compare-and-set in the same transaction as the notes, so a double submit
/// publishes once. Every query is filtered by the session's customer id.
/// </summary>
public sealed class PostmortemPublisher(AppDbContext db, TimeProvider time)
{
    /// <summary>Saves the edited texts (creating the draft when it was written by hand). Nothing is published.</summary>
    public async Task<PostmortemResult> SaveAsync(int customerId, int incidentId, string userId, string? likelyCause, string? customerMessage, CancellationToken ct = default)
    {
        var (result, _) = await SaveCoreAsync(customerId, incidentId, userId, likelyCause, customerMessage, ct);
        return result;
    }

    /// <summary>Saves the submitted texts, then approves and publishes the customer message on <paramref name="pageIds"/>.</summary>
    public async Task<PostmortemResult> PublishAsync(int customerId, int incidentId, string userId, string? likelyCause, string? customerMessage,
        IReadOnlyCollection<int> pageIds, CancellationToken ct = default)
    {
        var message = (customerMessage ?? "").Trim();
        if (message.Length == 0) return PostmortemResult.EmptyMessage;
        var pages = pageIds.Distinct().ToList();
        if (pages.Count == 0) return PostmortemResult.NoPages;

        var (saved, monitorId) = await SaveCoreAsync(customerId, incidentId, userId, likelyCause, message, ct);
        if (saved != PostmortemResult.Saved) return saved;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var allowed = await db.StatusPageMonitors.AsNoTracking()
            .Where(m => m.MonitorId == monitorId && m.StatusPage.CustomerId == customerId && pages.Contains(m.StatusPageId))
            .Select(m => m.StatusPageId).Distinct().ToListAsync(ct);
        if (allowed.Count != pages.Count) return PostmortemResult.InvalidPage;
        var full = await db.StatusPageNotes.AsNoTracking()
            .Where(n => n.IncidentId == incidentId && pages.Contains(n.StatusPageId))
            .GroupBy(n => n.StatusPageId).Where(g => g.Count() >= StatusPageRules.MaxNotesPerIncident).AnyAsync(ct);
        if (full) return PostmortemResult.TooManyNotes;

        var now = time.GetUtcNow().UtcDateTime;
        var ids = string.Join(",", pages.Order());
        var approved = await db.PostmortemDrafts
            .Where(d => d.IncidentId == incidentId && d.CustomerId == customerId && d.Status == PostmortemDraftStatus.Draft)
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.Status, PostmortemDraftStatus.Published)
                .SetProperty(d => d.ApprovedAtUtc, now)
                .SetProperty(d => d.ApprovedByUserId, userId)
                .SetProperty(d => d.PublishedStatusPageIds, ids), ct);
        if (approved == 0) return PostmortemResult.AlreadyPublished;
        foreach (var page in pages.Order())
            db.StatusPageNotes.Add(new StatusPageNote { StatusPageId = page, IncidentId = incidentId, Body = message, CreatedAtUtc = now });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return PostmortemResult.Published;
    }

    private async Task<(PostmortemResult Result, int MonitorId)> SaveCoreAsync(int customerId, int incidentId, string userId, string? likelyCause,
        string? customerMessage, CancellationToken ct)
    {
        var incident = await db.Incidents.AsNoTracking()
            .Where(i => i.Id == incidentId && i.Monitor.CustomerId == customerId)
            .Select(i => new { i.MonitorId, i.ResolvedAtUtc })
            .FirstOrDefaultAsync(ct);
        if (incident is null) return (PostmortemResult.NotFound, 0);
        if (incident.ResolvedAtUtc is null) return (PostmortemResult.NotResolved, 0);

        var cause = Normalize(likelyCause);
        var message = Normalize(customerMessage);
        if (cause.Length > PostmortemDraft.MaxLikelyCauseLength || message.Length > PostmortemDraft.MaxCustomerMessageLength
            || !StatusPageRules.IsPlainText(cause, multiline: true) || !StatusPageRules.IsPlainText(message, multiline: true))
            return (PostmortemResult.InvalidText, incident.MonitorId);

        var now = time.GetUtcNow().UtcDateTime;
        var exists = await db.PostmortemDrafts.AnyAsync(d => d.IncidentId == incidentId && d.CustomerId == customerId, ct);
        if (exists)
        {
            // Only while it is still a draft: a published draft never changes (also not by a request racing the approval).
            var updated = await db.PostmortemDrafts
                .Where(d => d.IncidentId == incidentId && d.CustomerId == customerId && d.Status == PostmortemDraftStatus.Draft)
                .ExecuteUpdateAsync(u => u.SetProperty(d => d.LikelyCause, cause).SetProperty(d => d.CustomerMessage, message)
                    .SetProperty(d => d.UpdatedAtUtc, now).SetProperty(d => d.UpdatedByUserId, userId), ct);
            return (updated == 0 ? PostmortemResult.AlreadyPublished : PostmortemResult.Saved, incident.MonitorId);
        }
        db.PostmortemDrafts.Add(new PostmortemDraft
        {
            CustomerId = customerId, IncidentId = incidentId, CreatedAtUtc = now, CreatedByUserId = userId,
            LikelyCause = cause, CustomerMessage = message, UpdatedAtUtc = now, UpdatedByUserId = userId,
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return (PostmortemResult.AlreadyPublished, incident.MonitorId); // created in parallel: let the user reload
        }
        db.ChangeTracker.Clear();
        return (PostmortemResult.Saved, incident.MonitorId);
    }

    /// <summary>Trimmed, with Windows line breaks folded to "\n" (as browsers submit textareas).</summary>
    private static string Normalize(string? text) => (text ?? "").Replace("\r\n", "\n").Trim();
}
