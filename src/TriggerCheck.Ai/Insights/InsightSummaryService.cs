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

namespace TriggerCheck.Ai.Insights;

public enum InsightSummaryStatus
{
    /// <summary>A new summary was generated and stored.</summary>
    Generated,
    /// <summary>The signals have not changed since the last summary: it is reused (no model call).</summary>
    Reused,
    /// <summary>AI is off (Ai:Enabled, no key, or Ai:Insights:SummaryEnabled): nothing is called or stored.</summary>
    Disabled,
    /// <summary>No signal to summarize: nothing is called or stored.</summary>
    NoSignals,
    /// <summary>A model call was made less than Ai:Insights:MinMinutesBetweenSummaries ago (or is still running).</summary>
    TooSoon,
    /// <summary>The platform's monthly insight budget is used up (a SkippedBudget decision is stored).</summary>
    Budget,
    /// <summary>The call failed, was refused, truncated, or the output broke the contract (decision stored).</summary>
    Failed,
}

public sealed record InsightSummaryResult(InsightSummaryStatus Status, AiDecision? Decision = null);

/// <summary>Platform AI usage of the insight summaries this month (UTC), against Ai:Insights:MonthlyCallLimit / MonthlyTokenLimit.</summary>
public sealed record InsightPlatformUsage(int Calls, int CallLimit, long Tokens, long TokenLimit, decimal EstimatedCostUsd)
{
    public bool IsOverBudget => CallLimit <= 0 || Calls >= CallLimit || (TokenLimit > 0 && Tokens >= TokenLimit);
}

/// <summary>The latest stored summary (if any) and the latest attempt (for showing a failure / budget skip).</summary>
public sealed record InsightSummaryState(AiDecision? Latest, InsightSummaryOutput? Summary, AiDecision? LastAttempt);

/// <summary>
/// AI-12 summary of the platform insight signals (A1: shown to the SuperAdmin only; nothing is sent to customers and
/// nothing is changed). Generated on demand by a SuperAdmin; the latest successful result is the cache (its
/// <see cref="AiDecision.OutputJson"/>), and an unchanged signal set reuses it without a call.
/// <para>
/// Billing: the summary is not a customer feature, so it is never charged to a customer's AI-10 quota. Its decisions
/// have no customer (<see cref="AiDecision.CustomerId"/> null, feature <see cref="AiFeatures.PlatformInsights"/>) and
/// count against a separate platform budget: <see cref="AiInsightOptions.MonthlyCallLimit"/> calls and
/// <see cref="AiInsightOptions.MonthlyTokenLimit"/> budget tokens per calendar month, with at most one new call per
/// <see cref="AiInsightOptions.MinMinutesBetweenSummaries"/>. The check and the Pending reservation are written in one
/// transaction, like <see cref="AiInterpreter"/>.
/// </para>
/// The signals themselves (<see cref="PlatformInsights"/>) never depend on AI.
/// </summary>
public sealed class InsightSummaryService(
    AppDbContext db,
    PlatformInsights insights,
    IAiClient client,
    IOptions<AiOptions> options,
    TimeProvider time,
    ILogger<InsightSummaryService> logger)
{
    public const string SubjectType = "Platform";
    public const string SubjectId = "insights";

    private static readonly AiDecisionStatus[] CallStatuses =
        [AiDecisionStatus.Succeeded, AiDecisionStatus.Refused, AiDecisionStatus.Failed, AiDecisionStatus.Truncated, AiDecisionStatus.Pending];

    /// <summary>AI is on, a key is configured and the summary is enabled.</summary>
    public bool IsAvailable => options.Value.Enabled && client.IsAvailable && options.Value.Insights.SummaryEnabled;

    private IQueryable<AiDecision> Platform() =>
        db.AiDecisions.AsNoTracking().Where(d => d.CustomerId == null && d.Feature == AiFeatures.PlatformInsights);

    public async Task<InsightPlatformUsage> GetUsageAsync(CancellationToken ct = default)
    {
        var o = options.Value;
        var start = AiUsageService.MonthStart(time.GetUtcNow().UtcDateTime);
        // Few rows per month: summed in memory (SQLite cannot aggregate the decimal cost).
        var rows = await Platform().Where(d => d.CreatedAtUtc >= start && CallStatuses.Contains(d.Status))
            .Select(d => new { d.Status, d.InputTokens, d.OutputTokens, d.CacheReadTokens, d.CacheWriteTokens, d.CostEstimate })
            .ToListAsync(ct);
        return new InsightPlatformUsage(
            rows.Count(r => r.Status == AiDecisionStatus.Pending || r.InputTokens + r.OutputTokens > 0), Math.Max(0, o.Insights.MonthlyCallLimit),
            rows.Sum(r => r.InputTokens + r.OutputTokens + r.CacheWriteTokens + r.CacheReadTokens / AiUsageService.CacheReadWeightDivisor),
            Math.Max(0, o.Insights.MonthlyTokenLimit), rows.Sum(r => r.CostEstimate));
    }

    public async Task<InsightSummaryState> GetStateAsync(CancellationToken ct = default)
    {
        var latest = await Platform().Where(d => d.Status == AiDecisionStatus.Succeeded).OrderByDescending(d => d.Id).FirstOrDefaultAsync(ct);
        var last = await Platform().OrderByDescending(d => d.Id).FirstOrDefaultAsync(ct);
        return new InsightSummaryState(latest, InsightSummaryOutput.TryParse(latest?.OutputJson), last);
    }

    /// <summary>Summarizes the current signals in <paramref name="language"/> ("en" / "tr"). Never throws for AI problems.</summary>
    public async Task<InsightSummaryResult> GenerateAsync(string language, CancellationToken ct = default)
    {
        var o = options.Value;
        if (!IsAvailable) return new(InsightSummaryStatus.Disabled);
        var report = await insights.ComputeAsync(ct);
        if (report.IsEmpty) return new(InsightSummaryStatus.NoSignals);

        var input = InsightSummaryPrompt.Build(report, o.Insights.MaxSummaryCustomers, language);
        var contract = new InsightSummaryContract(input);
        var feature = AiFeatures.PlatformInsights;
        var system = AiPrompts.For(InsightSummaryPrompt.Instructions);
        var now = time.GetUtcNow().UtcDateTime;
        var decision = new AiDecision
        {
            CustomerId = null, Feature = feature, SubjectType = SubjectType, SubjectId = SubjectId,
            Model = o.ModelFor(feature), Effort = o.EffortFor(feature), Language = input.Language, CreatedAtUtc = now,
        };
        decision.InputHash = AiInterpreter.Hash(decision.Model, decision.Effort, system, JsonSerializer.Serialize(contract.Schema), input.UserContent);

        var existing = await Platform().Where(d => d.Status == AiDecisionStatus.Succeeded && d.InputHash == decision.InputHash)
            .OrderByDescending(d => d.Id).FirstOrDefaultAsync(ct);
        if (existing is not null) return new(InsightSummaryStatus.Reused, existing);

        // Budget and pacing are checked and the call reserved (Pending row) in one transaction (SQLite: IMMEDIATE).
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var since = now.AddMinutes(-Math.Max(0, o.Insights.MinMinutesBetweenSummaries));
            if (await Platform().AnyAsync(d => d.Status == AiDecisionStatus.Pending
                                               || (CallStatuses.Contains(d.Status) && d.CreatedAtUtc > since && d.InputTokens + d.OutputTokens > 0), ct))
                return new(InsightSummaryStatus.TooSoon);
            decision.Status = (await GetUsageAsync(ct)).IsOverBudget ? AiDecisionStatus.SkippedBudget : AiDecisionStatus.Pending;
            db.AiDecisions.Add(decision);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        if (decision.Status == AiDecisionStatus.SkippedBudget) return new(InsightSummaryStatus.Budget, decision);

        var request = new AiCallRequest(feature, decision.Model, decision.Effort, o.MaxTokensFor(feature), system, input.UserContent, contract.Schema);
        var sw = Stopwatch.StartNew();
        try
        {
            AiCallResult result;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, o.TimeoutSeconds)));
                try
                {
                    result = await client.CompleteAsync(request, timeout.Token);
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
            decision.ErrorCode = AiCallResult.SafeCode(result.ErrorCode);
            switch (result.Outcome)
            {
                case AiCallOutcome.Succeeded:
                    var check = contract.Check(result.Json);
                    if (check.Json is null)
                    {
                        logger.LogWarning("AI insight summary did not match the contract: {Code}", check.ErrorCode);
                        decision.Status = AiDecisionStatus.Failed;
                        decision.ErrorCode = AiCallResult.SafeCode(check.ErrorCode) ?? "invalid_output";
                        break;
                    }
                    decision.OutputJson = check.Json;
                    decision.AutonomyLevel = AiAutonomy.A1;
                    decision.Status = AiDecisionStatus.Succeeded;
                    break;
                case AiCallOutcome.Refused:
                    decision.Status = AiDecisionStatus.Refused;
                    break;
                case AiCallOutcome.MaxTokens:
                    decision.Status = AiDecisionStatus.Truncated;
                    decision.ErrorCode = "max_tokens";
                    break;
                case AiCallOutcome.Unavailable:
                    decision.Status = AiDecisionStatus.Failed;
                    decision.ErrorCode = "unavailable";
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
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AI insight summary failed");
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
                await db.SaveChangesAsync(CancellationToken.None); // paid for: record it even when the request was cancelled
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not complete AI insight decision {DecisionId}; the Pending sweep will close it", decision.Id);
            }
        }
        return new(decision.Status == AiDecisionStatus.Succeeded ? InsightSummaryStatus.Generated : InsightSummaryStatus.Failed, decision);
    }
}
