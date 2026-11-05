using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TriggerCheck.Ai.Budget;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Execution;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Ai.Features.MonitorSetup;

public enum MonitorSetupStatus
{
    /// <summary>A proposal (pre-filled form, or a clarification question).</summary>
    Ready,
    /// <summary>Empty request: nothing was sent.</summary>
    Empty,
    /// <summary>Ai:Enabled is off or no key is configured (nothing recorded).</summary>
    Off,
    OptOut,
    NotInPlan,
    /// <summary>The plan's monthly AI budget is used up.</summary>
    OverBudget,
    /// <summary>The user sent Ai:SetupAssistant:RequestsPerUserPerHour requests in the last hour.</summary>
    RateLimited,
    /// <summary>Timeout, refusal, API error or output that does not match the schema.</summary>
    Failed,
}

public sealed record MonitorSetupResult(MonitorSetupStatus Status, MonitorSetupProposal? Proposal = null, long? DecisionId = null);

/// <summary>
/// AI-9 monitor setup assistant (autonomy A1): turns a free-text request into a proposal for the create-monitor form.
/// It <b>never saves a monitor</b>: it has no access to the monitor commands and writes only its own
/// <see cref="AiDecision"/> row; the customer reviews the pre-filled form and saves it through the normal create path
/// (plan rules, SSRF pre-check, quota). Runs on the request path because the customer waits for the form, with the
/// usual guards: global switch, opt-in, plan feature (AI-10), monthly budget (reserved atomically like
/// <see cref="AiInterpreter"/>), a per-user hourly limit, the call timeout, and K5 masking of the request
/// (<see cref="MonitorSetupRequest"/>). An identical request of the same customer reuses the earlier answer.
/// </summary>
public sealed class MonitorSetupAssistant(
    AppDbContext db,
    IAiClient client,
    IAiUsage usage,
    IOptions<AiOptions> options,
    TimeProvider time,
    ILogger<MonitorSetupAssistant> logger)
{
    /// <summary>AiDecision.SubjectType; the subject id is the requesting user's id (the rate limit is counted on it).</summary>
    public const string SubjectType = "MonitorSetup";

    private static readonly AiDecisionStatus[] CallStatuses =
        [AiDecisionStatus.Succeeded, AiDecisionStatus.Refused, AiDecisionStatus.Failed, AiDecisionStatus.Truncated, AiDecisionStatus.Pending];

    public bool IsAvailable => options.Value.Enabled && client.IsAvailable;

    /// <summary>The customer may use the assistant: opted in to AI and the plan includes AI-9.</summary>
    public static bool Allowed(Customer customer) => customer.AiEnabled && AiFeatures.PlanAllows(customer.Plan, AiFeatures.MonitorSetupAssistant);

    /// <param name="customerId">From the session (RequiredCustomerId), never from input.</param>
    /// <param name="userId">The signed-in user (per-user rate limit and audit).</param>
    public async Task<MonitorSetupResult> SuggestAsync(int customerId, string userId, string? requestText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(requestText)) return new(MonitorSetupStatus.Empty);
        var o = options.Value;
        if (!o.Enabled || !client.IsAvailable) return new(MonitorSetupStatus.Off);

        var customer = await db.Customers.AsNoTracking().Include(c => c.Plan).FirstOrDefaultAsync(c => c.Id == customerId, ct);
        if (customer is null) return new(MonitorSetupStatus.Failed);

        const string feature = AiFeatures.MonitorSetupAssistant;
        var now = time.GetUtcNow().UtcDateTime;
        var decision = new AiDecision
        {
            CustomerId = customer.Id,
            Feature = feature,
            SubjectType = SubjectType,
            SubjectId = userId,
            Model = o.ModelFor(feature),
            Effort = o.EffortFor(feature),
            Language = AiJob.NormalizeLanguage(customer.AiLanguage),
            AutonomyLevel = AiAutonomy.A1,
            CreatedAtUtc = now,
        };
        if (!customer.AiEnabled || !AiFeatures.PlanAllows(customer.Plan, feature))
        {
            decision.Status = customer.AiEnabled ? AiDecisionStatus.SkippedPlan : AiDecisionStatus.SkippedOptOut;
            db.AiDecisions.Add(decision);
            await db.SaveChangesAsync(ct);
            return new(customer.AiEnabled ? MonitorSetupStatus.NotInPlan : MonitorSetupStatus.OptOut);
        }

        var request = new MonitorSetupRequest(requestText, Math.Max(1, o.SetupAssistant.MaxRequestChars));
        var system = MonitorSetupPrompts.System;
        var schema = MonitorSetupPrompts.Schema();
        var userContent = MonitorSetupPrompts.UserContent(request, decision.Language);
        decision.InputHash = AiInterpreter.Hash(decision.Model, decision.Effort, system, JsonSerializer.Serialize(schema), userContent);

        // The same (masked) request of this customer was answered before: no new call. The proposal is rebuilt from the
        // stored draft with today's plan and this request's own links and quoted text.
        var earlier = await db.AiDecisions.AsNoTracking()
            .Where(d => d.CustomerId == customer.Id && d.Feature == feature && d.SubjectType == SubjectType
                        && d.InputHash == decision.InputHash && d.Status == AiDecisionStatus.Succeeded)
            .OrderByDescending(d => d.Id)
            .Select(d => new { d.Id, d.OutputJson })
            .FirstOrDefaultAsync(ct);
        if (earlier is not null && MonitorSetupDraft.TryParse(earlier.OutputJson) is { } cached)
            return new(MonitorSetupStatus.Ready, MonitorSetupForm.Build(cached, customer.Plan, request), earlier.Id);

        // Rate limit and budget are checked and the reservation (Pending row) written in one (SQLite: IMMEDIATE)
        // transaction, so parallel requests cannot all pass.
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var since = now.AddHours(-1);
            var recent = await db.AiDecisions.CountAsync(d => d.CustomerId == customer.Id && d.Feature == feature && d.SubjectType == SubjectType
                                                              && d.SubjectId == userId && d.CreatedAtUtc >= since && CallStatuses.Contains(d.Status), ct);
            if (recent >= Math.Max(0, o.SetupAssistant.RequestsPerUserPerHour))
            {
                await tx.RollbackAsync(ct);
                return new(MonitorSetupStatus.RateLimited);
            }
            decision.Status = (await usage.GetMonthlyUsageAsync(customer.Id, ct)).IsOverBudget ? AiDecisionStatus.SkippedBudget : AiDecisionStatus.Pending;
            db.AiDecisions.Add(decision);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        if (decision.Status == AiDecisionStatus.SkippedBudget) return new(MonitorSetupStatus.OverBudget, DecisionId: decision.Id);

        MonitorSetupDraft? draft = null;
        var sw = Stopwatch.StartNew();
        try
        {
            AiCallResult result;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, o.TimeoutSeconds)));
                try
                {
                    result = await client.CompleteAsync(new AiCallRequest(feature, decision.Model, decision.Effort, o.MaxTokensFor(feature), system, userContent, schema), timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    result = AiCallResult.Fail("timeout");
                }
            }
            decision.InputTokens = result.InputTokens;
            decision.OutputTokens = result.OutputTokens;
            decision.CacheReadTokens = result.CacheReadTokens;
            decision.CacheWriteTokens = result.CacheWriteTokens;
            decision.CostEstimate = decimal.Round(o.PriceFor(decision.Model).Estimate(result.InputTokens, result.OutputTokens, result.CacheReadTokens, result.CacheWriteTokens), 6);
            decision.ErrorCode = result.ErrorCode;
            switch (result.Outcome)
            {
                case AiCallOutcome.Succeeded:
                    draft = MonitorSetupDraft.TryParse(result.Json);
                    if (draft is null)
                    {
                        logger.LogWarning("AI-9 output did not match the schema (customer {CustomerId})", customer.Id);
                        decision.Status = AiDecisionStatus.Failed;
                        decision.ErrorCode = "invalid_output";
                        break;
                    }
                    decision.OutputJson = draft.ToJson(); // known fields only, placeholders unresolved (no user links / quotes)
                    decision.Status = AiDecisionStatus.Succeeded;
                    break;
                case AiCallOutcome.Refused:
                    decision.Status = AiDecisionStatus.Refused;
                    break;
                case AiCallOutcome.MaxTokens:
                    decision.Status = AiDecisionStatus.Truncated;
                    decision.ErrorCode = "max_tokens";
                    break;
                default:
                    decision.Status = AiDecisionStatus.Failed;
                    decision.ErrorCode ??= "failed";
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            decision.Status = AiDecisionStatus.Failed;
            decision.ErrorCode = "cancelled";
            throw; // the finally block still completes the row
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AI-9 call failed (customer {CustomerId})", customer.Id);
            decision.Status = AiDecisionStatus.Failed;
            decision.ErrorCode = "error";
        }
        finally
        {
            if (decision.Status == AiDecisionStatus.Pending)
            {
                decision.Status = AiDecisionStatus.Failed;
                decision.ErrorCode ??= "error";
            }
            decision.LatencyMs = (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds);
            try
            {
                await db.SaveChangesAsync(CancellationToken.None); // paid for: record it even when the request was aborted
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not complete AI decision {DecisionId}; the Pending sweep will close it", decision.Id);
            }
        }

        return draft is null
            ? new(MonitorSetupStatus.Failed, DecisionId: decision.Id)
            : new(MonitorSetupStatus.Ready, MonitorSetupForm.Build(draft, customer.Plan, request), decision.Id);
    }
}
