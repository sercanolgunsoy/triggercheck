using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Monitoring.Escalation;

public enum AckResult
{
    Acknowledged,
    /// <summary>No such incident for this customer.</summary>
    NotFound,
    AlreadyAcknowledged,
    /// <summary>Resolved incidents cannot be acknowledged.</summary>
    Resolved
}

/// <summary>
/// Acknowledges incidents (FEAT-5). An acknowledgement only stops further escalation steps; it does not change the
/// monitor's status or close the incident. Atomic: a conditional UPDATE, so a concurrent acknowledgement, resolution or
/// escalation step cannot interleave (the escalation job's own update requires the incident to be unacknowledged).
/// </summary>
public sealed class IncidentAcknowledger(IDbContextFactory<AppDbContext> dbFactory, TimeProvider time, ILogger<IncidentAcknowledger> logger)
{
    public const int MaxNoteLength = 500;

    public async Task<AckResult> AcknowledgeAsync(int incidentId, int customerId, string userId, string? note, CancellationToken ct = default)
    {
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (note is { Length: > MaxNoteLength }) note = note[..MaxNoteLength];
        var now = time.GetUtcNow().UtcDateTime;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var updated = await db.Incidents
            .Where(i => i.Id == incidentId && i.Monitor.CustomerId == customerId && i.ResolvedAtUtc == null && i.AcknowledgedAtUtc == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.AcknowledgedAtUtc, now)
                .SetProperty(i => i.AcknowledgedByUserId, userId)
                .SetProperty(i => i.AcknowledgeNote, note), ct);
        if (updated == 1)
        {
            logger.LogInformation("Incident {IncidentId} of customer {CustomerId} acknowledged by user {UserId}", incidentId, customerId, userId);
            return AckResult.Acknowledged;
        }

        var state = await db.Incidents.AsNoTracking()
            .Where(i => i.Id == incidentId && i.Monitor.CustomerId == customerId)
            .Select(i => new { i.ResolvedAtUtc, i.AcknowledgedAtUtc })
            .FirstOrDefaultAsync(ct);
        return state is null ? AckResult.NotFound
            : state.AcknowledgedAtUtc is not null ? AckResult.AlreadyAcknowledged
            : AckResult.Resolved;
    }
}
