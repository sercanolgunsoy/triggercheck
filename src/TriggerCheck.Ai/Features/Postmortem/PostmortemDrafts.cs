using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Ai.Execution;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Ai.Features.Postmortem;

public enum PostmortemGenerateStatus
{
    /// <summary>The draft texts were (re)generated from a validated AI answer.</summary>
    Generated,
    NotFound,
    /// <summary>Only resolved incidents get a postmortem.</summary>
    NotResolved,
    /// <summary>The draft was already approved and published; it is read-only.</summary>
    AlreadyPublished,
    /// <summary>Ai:Enabled is off or no key is configured (nothing recorded).</summary>
    Off,
    OptOut,
    NotInPlan,
    OverBudget,
    /// <summary>Timeout, refusal, API error, invalid or ungrounded output (recorded as an AiDecision).</summary>
    Failed,
}

public sealed record PostmortemGenerateResult(PostmortemGenerateStatus Status, long? DecisionId = null);

/// <summary>
/// AI-8 (autonomy A2): generates the AI texts of a postmortem draft on a CustomerAdmin's request. The call goes through
/// <see cref="IAiInterpreter"/> on the request path (the admin waits for the draft, like AI-9; bounded by
/// Ai:TimeoutSeconds), so the global switch, opt-in, plan feature (AI-10), monthly budget, idempotency (an unchanged
/// incident reuses the earlier answer without a new call) and the <see cref="AiDecision"/> audit row all apply. The
/// validated answer is rendered (server values, public service name) into the <see cref="PostmortemDraft"/> row, which
/// stays a draft: this class never publishes anything. Without AI the admin can still write the draft by hand.
/// </summary>
public sealed class PostmortemDrafts(AppDbContext db, IAiInterpreter interpreter, TimeProvider time)
{
    /// <param name="customerId">From the session (RequiredCustomerId), never from input.</param>
    public Task<PostmortemDraft?> GetAsync(int customerId, int incidentId, CancellationToken ct = default) =>
        db.PostmortemDrafts.AsNoTracking().FirstOrDefaultAsync(d => d.IncidentId == incidentId && d.CustomerId == customerId, ct);

    /// <param name="customerId">From the session (RequiredCustomerId), never from input.</param>
    /// <param name="userId">The requesting CustomerAdmin (audit).</param>
    public async Task<PostmortemGenerateResult> GenerateAsync(int customerId, int incidentId, string userId, CancellationToken ct = default)
    {
        var incident = await db.Incidents.AsNoTracking()
            .Where(i => i.Id == incidentId && i.Monitor.CustomerId == customerId)
            .Select(i => new { i.Id, i.MonitorId, i.ResolvedAtUtc })
            .FirstOrDefaultAsync(ct);
        if (incident is null) return new(PostmortemGenerateStatus.NotFound);
        if (incident.ResolvedAtUtc is null) return new(PostmortemGenerateStatus.NotResolved);
        var existing = await db.PostmortemDrafts.FirstOrDefaultAsync(d => d.IncidentId == incidentId && d.CustomerId == customerId, ct);
        if (existing?.Status == PostmortemDraftStatus.Published) return new(PostmortemGenerateStatus.AlreadyPublished);

        var language = AiJob.NormalizeLanguage(await db.Customers.Where(c => c.Id == customerId).Select(c => c.AiLanguage).FirstOrDefaultAsync(ct));
        var job = new AiJob(AiFeatures.PostmortemDraft, customerId, PostmortemDraftProvider.SubjectType, incidentId.ToString(CultureInfo.InvariantCulture), language);
        var decision = await interpreter.RunAsync(job, ct);
        var status = decision?.Status switch
        {
            null => PostmortemGenerateStatus.Failed,
            AiDecisionStatus.SkippedDisabled => PostmortemGenerateStatus.Off,
            AiDecisionStatus.SkippedOptOut => PostmortemGenerateStatus.OptOut,
            AiDecisionStatus.SkippedPlan => PostmortemGenerateStatus.NotInPlan,
            AiDecisionStatus.SkippedBudget => PostmortemGenerateStatus.OverBudget,
            AiDecisionStatus.Succeeded => PostmortemGenerateStatus.Generated,
            _ => PostmortemGenerateStatus.Failed,
        };
        var output = status == PostmortemGenerateStatus.Generated ? PostmortemOutput.TryParse(decision!.OutputJson) : null;
        if (output is null) return new(status == PostmortemGenerateStatus.Generated ? PostmortemGenerateStatus.Failed : status, decision?.Id is > 0 ? decision.Id : null);

        var service = await PublicServiceNameAsync(customerId, incident.MonitorId, ct) ?? PostmortemRenderer.GenericService(output.Language);
        var now = time.GetUtcNow().UtcDateTime;
        var draft = existing;
        if (draft is null)
        {
            draft = new PostmortemDraft { CustomerId = customerId, IncidentId = incidentId, CreatedAtUtc = now, CreatedByUserId = userId };
            db.PostmortemDrafts.Add(draft);
        }
        draft.Language = output.Language;
        draft.DecisionId = decision!.Id;
        draft.CauseCategory = output.Category;
        draft.LikelyCause = Fit(output.Render(output.LikelyCause, service), PostmortemDraft.MaxLikelyCauseLength);
        draft.CustomerMessage = Fit(output.Render(output.CustomerMessage, service), PostmortemDraft.MaxCustomerMessageLength);
        draft.UpdatedAtUtc = now;
        draft.UpdatedByUserId = userId;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A parallel request created or published the draft meanwhile: keep theirs.
            return new(PostmortemGenerateStatus.Failed, decision.Id);
        }
        return new(PostmortemGenerateStatus.Generated, decision.Id);
    }

    /// <summary>The public display name of the monitor on its first status page (null when it is on none).</summary>
    public Task<string?> PublicServiceNameAsync(int customerId, int monitorId, CancellationToken ct = default) =>
        db.StatusPageMonitors.AsNoTracking()
            .Where(m => m.MonitorId == monitorId && m.StatusPage.CustomerId == customerId)
            .OrderBy(m => m.StatusPageId).Select(m => m.DisplayName).FirstOrDefaultAsync(ct);

    /// <summary>Cuts a rendered text to <paramref name="max"/> characters at the last sentence end that fits.</summary>
    public static string Fit(string text, int max)
    {
        text = text.Trim();
        if (text.Length <= max) return text;
        var cut = text.LastIndexOfAny(['.', '!', '?', '…', '\n'], max - 1);
        return (cut > 0 ? text[..(cut + 1)] : text[..max]).Trim();
    }
}
