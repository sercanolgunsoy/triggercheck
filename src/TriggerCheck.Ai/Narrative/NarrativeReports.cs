using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TriggerCheck.Ai.Budget;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Execution;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Ai.Narrative;

/// <summary>What the report page shows in place of the AI narrative.</summary>
public enum NarrativeState
{
    /// <summary>A validated narrative (<see cref="NarrativeView.Output"/>).</summary>
    Ready,
    /// <summary>Requested or being generated (or a failed attempt that is retried).</summary>
    Pending,
    /// <summary>None yet: a CustomerAdmin can request it.</summary>
    Requestable,
    /// <summary>Generation failed (all attempts used); it can be requested again.</summary>
    Failed,
    /// <summary>The monthly AI budget was used up when it was due.</summary>
    Budget,
    /// <summary>Nothing to narrate (no monitors in the period).</summary>
    NoData,
    /// <summary>The period is not over yet: narratives describe complete periods only.</summary>
    NotComplete,
    Off,
    OptOut,
    NotInPlan
}

/// <param name="Names">Label ("monitor A") → current monitor name of this customer, for display only.</param>
public sealed record NarrativeView(NarrativeState State, long? DecisionId = null, NarrativeOutput? Output = null, IReadOnlyDictionary<string, string>? Names = null);

/// <summary>Outcome of a panel request (<see cref="NarrativeReports.RequestAsync"/>).</summary>
public enum NarrativeRequestResult
{
    Queued,
    /// <summary>Already queued, running or ready.</summary>
    AlreadyQueued,
    /// <summary>The last attempt finished less than the request cool-down ago.</summary>
    CoolingDown,
    OverBudget,
    Unavailable
}

/// <param name="Wait">The monthly e-mail should be held back for this run: the narrative is still expected before the deadline.</param>
public sealed record MailNarrative(bool Wait, NarrativeOutput? Output = null, IReadOnlyDictionary<string, string>? Names = null)
{
    public static readonly MailNarrative None = new(false);
}

/// <summary>
/// Read side of AI-6 for the report page and the monthly e-mail. Every query is filtered by the customer id passed in
/// (the page's RequiredCustomerId / the mailer's customer). Never calls the model: generation is the batch job's.
/// </summary>
public sealed partial class NarrativeReports(AppDbContext db, IAiClient client, IAiUsage usage, IOptions<AiOptions> options, TimeProvider time,
    ILogger<NarrativeReports> logger)
{
    /// <summary>AI on, key configured and the narrative job enabled.</summary>
    public bool Available
    {
        get
        {
            var o = options.Value;
            return o.Enabled && client.IsAvailable && o.Narrative.Enabled && o.Narrative.IntervalMinutes > 0;
        }
    }

    /// <param name="customer">The current customer with its Plan loaded.</param>
    /// <param name="periodComplete">The period has ended.</param>
    public async Task<NarrativeView> GetAsync(Customer customer, NarrativePeriod period, string timeZoneId, bool periodComplete, CancellationToken ct)
    {
        try
        {
            return await GetCoreAsync(customer, period, timeZoneId, periodComplete, ct);
        }
        catch (DbException ex)
        {
            // E.g. the ReportNarratives table does not exist yet (database created before AI-6, schema via OPS-1): the
            // report page works without the narrative.
            logger.LogWarning(ex, "AI narrative lookup failed; the report is shown without it");
            return new(NarrativeState.Off);
        }
    }

    private async Task<NarrativeView> GetCoreAsync(Customer customer, NarrativePeriod period, string timeZoneId, bool periodComplete, CancellationToken ct)
    {
        if (!Available) return new(NarrativeState.Off);
        if (!customer.AiEnabled) return new(NarrativeState.OptOut);
        if (!AiFeatures.PlanAllows(customer.Plan, AiFeatures.NarrativeReport)) return new(NarrativeState.NotInPlan);
        if (!periodComplete) return new(NarrativeState.NotComplete);

        var row = await RowAsync(customer.Id, period, ct);
        if (row is null) return new(NarrativeState.Requestable);
        if (row.Status is ReportNarrativeStatus.Requested or ReportNarrativeStatus.Running) return new(NarrativeState.Pending);
        if (row.TimeZoneId != timeZoneId) return new(NarrativeState.Requestable); // cut in another zone: regenerate on request
        switch (row.Status)
        {
            case ReportNarrativeStatus.Ready:
                var (decisionId, output, names) = await LoadAsync(customer.Id, row, ct);
                return output is null ? new(NarrativeState.Failed) : new(NarrativeState.Ready, decisionId, output, names);
            case ReportNarrativeStatus.Failed:
                return row.Attempts < Math.Max(1, options.Value.Narrative.MaxAttempts) ? new(NarrativeState.Pending) : new(NarrativeState.Failed);
            default: // Skipped
                var budget = row.DecisionId is long id && await db.AiDecisions.AsNoTracking()
                    .AnyAsync(d => d.Id == id && d.CustomerId == customer.Id && d.Status == AiDecisionStatus.SkippedBudget, ct);
                return new(budget ? NarrativeState.Budget : NarrativeState.NoData);
        }
    }

    /// <summary>
    /// Queues the narrative of a complete period for the batch job (CustomerAdmin; the page checks the role). A ready
    /// narrative in the same zone is kept; a failed / skipped / other-zone one is requested again once the request
    /// cool-down (<see cref="AiNarrativeOptions.RequestCooldownMinutes"/>) since it finished has passed. Nothing is
    /// queued while the AI budget is used up (it would only record another budget skip).
    /// </summary>
    public async Task<NarrativeRequestResult> RequestAsync(Customer customer, NarrativePeriod period, string timeZoneId, CancellationToken ct)
    {
        if (!Available || !customer.AiEnabled || !AiFeatures.PlanAllows(customer.Plan, AiFeatures.NarrativeReport)) return NarrativeRequestResult.Unavailable;
        try
        {
            if ((await usage.GetMonthlyUsageAsync(customer.Id, ct)).IsOverBudget) return NarrativeRequestResult.OverBudget;
            var cooldown = TimeSpan.FromMinutes(Math.Max(0, options.Value.Narrative.RequestCooldownMinutes));
            return await NarrativeReportJob.EnsureRequestedAsync(db, customer.Id, period, timeZoneId, AiJob.NormalizeLanguage(customer.AiLanguage),
                    time.GetUtcNow().UtcDateTime, cooldown, ct) switch
            {
                NarrativeReportJob.EnsureResult.Queued => NarrativeRequestResult.Queued,
                NarrativeReportJob.EnsureResult.CoolingDown => NarrativeRequestResult.CoolingDown,
                _ => NarrativeRequestResult.AlreadyQueued,
            };
        }
        catch (DbException ex)
        {
            logger.LogWarning(ex, "AI narrative request failed");
            return NarrativeRequestResult.Unavailable;
        }
    }

    /// <summary>
    /// The narrative for the monthly SLA e-mail of <paramref name="monthKey"/>, or whether to wait for it. The e-mail
    /// waits only while a narrative can still come (AI available, customer eligible, budget left, row missing, queued,
    /// running or retryable) and only until <see cref="AiNarrativeOptions.EmailWaitMinutes"/> after the month ended;
    /// otherwise it goes without. The wait never goes past <paramref name="latestSendUtc"/> (the end of the mailer's
    /// catch-up window minus a run), so a long wait can never make the e-mail miss its window. Never throws: any error
    /// means "send without".
    /// </summary>
    public async Task<MailNarrative> ForMailAsync(int customerId, string monthKey, string timeZoneId, DateTime periodEndUtc, DateTime latestSendUtc,
        CancellationToken ct)
    {
        try
        {
            if (!Available || !NarrativePeriod.TryParse(monthKey, out var period)) return MailNarrative.None;
            var customer = await db.Customers.AsNoTracking().Include(c => c.Plan).FirstOrDefaultAsync(c => c.Id == customerId, ct);
            if (customer is null || !customer.AiEnabled || !AiFeatures.PlanAllows(customer.Plan, AiFeatures.NarrativeReport)) return MailNarrative.None;
            if (!await db.Monitors.AnyAsync(m => m.CustomerId == customerId, ct)) return MailNarrative.None; // the job skips such customers

            var row = await RowAsync(customerId, period, ct);
            if (row is { Status: ReportNarrativeStatus.Ready } && row.TimeZoneId == timeZoneId)
            {
                var (_, output, names) = await LoadAsync(customerId, row, ct);
                return output is null ? MailNarrative.None : new(false, output, names);
            }
            var n = options.Value.Narrative;
            var stillExpected = row is null
                || row.TimeZoneId != timeZoneId
                || row.Status is ReportNarrativeStatus.Requested or ReportNarrativeStatus.Running
                || (row.Status == ReportNarrativeStatus.Failed && row.Attempts < Math.Max(1, n.MaxAttempts));
            if (!stillExpected) return MailNarrative.None;
            var deadline = periodEndUtc.AddMinutes(Math.Max(0, n.EmailWaitMinutes));
            if (deadline > latestSendUtc) deadline = latestSendUtc;
            if (time.GetUtcNow().UtcDateTime >= deadline) return MailNarrative.None;
            if (row?.Status != ReportNarrativeStatus.Running && (await usage.GetMonthlyUsageAsync(customerId, ct)).IsOverBudget) return MailNarrative.None;
            return new(true);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "AI narrative lookup for the monthly e-mail of customer {CustomerId} failed; sending without it", customerId);
            return MailNarrative.None;
        }
    }

    private Task<ReportNarrative?> RowAsync(int customerId, NarrativePeriod period, CancellationToken ct) =>
        db.ReportNarratives.AsNoTracking().FirstOrDefaultAsync(r => r.CustomerId == customerId && r.Kind == period.Kind && r.PeriodKey == period.Key, ct);

    private async Task<(long? DecisionId, NarrativeOutput? Output, IReadOnlyDictionary<string, string> Names)> LoadAsync(int customerId, ReportNarrative row, CancellationToken ct)
    {
        var decision = row.DecisionId is long id
            ? await db.AiDecisions.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id && d.CustomerId == customerId && d.Status == AiDecisionStatus.Succeeded, ct)
            : null;
        var output = NarrativeOutput.TryParse(decision?.OutputJson);
        if (output is null) return (null, null, new Dictionary<string, string>());
        var ids = (output.Labels ?? new Dictionary<string, int>()).Values.Distinct().ToList();
        var names = await db.Monitors.AsNoTracking().Where(m => m.CustomerId == customerId && ids.Contains(m.Id))
            .Select(m => new { m.Id, m.Name }).ToDictionaryAsync(m => m.Id, m => m.Name, ct);
        var byLabel = (output.Labels ?? new Dictionary<string, int>())
            .Where(kv => names.ContainsKey(kv.Value)).ToDictionary(kv => kv.Key, kv => names[kv.Value]);
        return (decision!.Id, output, byLabel);
    }

    [GeneratedRegex(@"\b[Mm]onit(?:o|ö)r\s+([A-Z])\b")]
    private static partial Regex LabelInText();

    /// <summary>Replaces the neutral labels in a narrative text with the monitors' names (unknown labels stay).</summary>
    public static string WithNames(string text, IReadOnlyDictionary<string, string>? names) =>
        names is null || names.Count == 0 ? text
        : LabelInText().Replace(text, m => names.TryGetValue("monitor " + m.Groups[1].Value, out var name) ? name : m.Value);
}
