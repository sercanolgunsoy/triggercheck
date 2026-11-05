using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TriggerCheck.Ai.Budget;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Interpretation;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Ai.Execution;

/// <summary>
/// Runs one AI job end to end (L2 + L3). Independent of the monitoring layer: features hand it an <see cref="AiJob"/>
/// through <see cref="IAiJobQueue"/>, never call it on a request or check path.
/// </summary>
public interface IAiInterpreter
{
    /// <summary>
    /// Returns the recorded decision (one row per call, including failures and opt-out / plan / budget skips; a
    /// SkippedDisabled decision is returned but not stored), an earlier decision
    /// for the identical input (idempotency / cool-down), or null when the subject is gone / the feature has no provider.
    /// </summary>
    Task<AiDecision?> RunAsync(AiJob job, CancellationToken ct = default);
}

/// <summary>
/// Order: global switch + key (not stored) → customer opt-in → plan feature → context from the feature's provider →
/// idempotency on (Feature, Subject, InputHash), including a cool-down after refusals / failures → budget check and
/// reservation (Pending row) → Claude call with timeout → validation (<see cref="AiInterpretationValidator"/>) → the
/// row is completed. Failures never throw to the caller and never leak exception text.
/// </summary>
public sealed class AiInterpreter(
    AppDbContext db,
    IAiClient client,
    IAiUsage usage,
    IEnumerable<IAiContextProvider> providers,
    IAiOutputValidator validator,
    IOptions<AiOptions> options,
    TimeProvider time,
    ILogger<AiInterpreter> logger) : IAiInterpreter
{
    public async Task<AiDecision?> RunAsync(AiJob job, CancellationToken ct = default)
    {
        var feature = AiFeatures.Find(job.Feature) ?? throw new ArgumentException($"Unknown AI feature '{job.Feature}'.", nameof(job));
        var customer = await db.Customers.AsNoTracking().Include(c => c.Plan).FirstOrDefaultAsync(c => c.Id == job.CustomerId, ct);
        if (customer is null) return null;

        var o = options.Value;
        var decision = new AiDecision
        {
            CustomerId = customer.Id,
            Feature = feature.Name,
            SubjectType = job.SubjectType,
            SubjectId = job.SubjectId,
            Model = o.ModelFor(feature.Name),
            Effort = o.EffortFor(feature.Name),
            Language = AiJob.NormalizeLanguage(job.Language),
            CreatedAtUtc = time.GetUtcNow().UtcDateTime,
        };

        // AI off: nothing is stored (a row per job would only record that the switch is off).
        if (!o.Enabled || !client.IsAvailable)
        {
            decision.Status = AiDecisionStatus.SkippedDisabled;
            return decision;
        }
        AiDecisionStatus? skip =
            !customer.AiEnabled ? AiDecisionStatus.SkippedOptOut
            : !AiFeatures.PlanAllows(customer.Plan, feature.Name) ? AiDecisionStatus.SkippedPlan
            : null;
        if (skip is not null)
        {
            decision.Status = skip.Value;
            return await SaveAsync(decision, ct);
        }

        // Last registration wins (DI convention), so a host can replace a feature's provider.
        var provider = providers.LastOrDefault(p => p.Feature == feature.Name);
        if (provider is null)
        {
            logger.LogWarning("No AI context provider is registered for feature {Feature}", feature.Name);
            return null;
        }
        var prompt = await provider.BuildAsync(job, ct);
        if (prompt is null) return null;

        var system = AiPrompts.For(prompt.FeatureInstructions);
        var schema = prompt.Output?.Schema ?? AiInterpretationSchema.Build(prompt.AllowedActions, prompt.Categories);
        decision.InputHash = Hash(decision.Model, decision.Effort, system, JsonSerializer.Serialize(schema), prompt.UserContent);

        // Identical input: reuse the succeeded answer, or wait out the cool-down after a refusal / failure.
        var cooldownStart = decision.CreatedAtUtc.AddMinutes(-Math.Max(0, o.RetryCooldownMinutes));
        var existing = await db.AiDecisions.AsNoTracking()
            .Where(d => d.CustomerId == customer.Id && d.Feature == decision.Feature && d.SubjectType == decision.SubjectType
                        && d.SubjectId == decision.SubjectId && d.InputHash == decision.InputHash
                        && (d.Status == AiDecisionStatus.Succeeded
                            || ((d.Status == AiDecisionStatus.Refused || d.Status == AiDecisionStatus.Failed || d.Status == AiDecisionStatus.Truncated)
                                && d.CreatedAtUtc >= cooldownStart)))
            .OrderByDescending(d => d.Status == AiDecisionStatus.Succeeded).ThenByDescending(d => d.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is not null) return existing;

        // Reserve the budget atomically: the check and the Pending row are written in one (SQLite: IMMEDIATE)
        // transaction, so parallel jobs cannot all pass the check. The token budget can still be exceeded by the calls
        // in flight (at most Ai:MaxConcurrency x MaxTokens).
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            decision.Status = (await usage.GetMonthlyUsageAsync(customer.Id, ct)).IsOverBudget ? AiDecisionStatus.SkippedBudget : AiDecisionStatus.Pending;
            await SaveAsync(decision, ct);
            await tx.CommitAsync(ct);
        }
        if (decision.Status == AiDecisionStatus.SkippedBudget) return decision;

        var request = new AiCallRequest(feature.Name, decision.Model, decision.Effort, o.MaxTokensFor(feature.Name), system, prompt.UserContent, schema);
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
            decision.ErrorCode = result.ErrorCode;

            switch (result.Outcome)
            {
                case AiCallOutcome.Succeeded when prompt.Output is not null:
                    // Feature-specific contract (e.g. AI-6 narrative: only numbers from the input).
                    var check = prompt.Output.Check(result.Json);
                    if (check.Json is null)
                    {
                        logger.LogWarning("AI output for {Feature} {SubjectType} {SubjectId} was rejected: {Code}", feature.Name, job.SubjectType, job.SubjectId, check.ErrorCode);
                        decision.Status = AiDecisionStatus.Failed;
                        decision.ErrorCode = AiCallResult.SafeCode(check.ErrorCode) ?? "invalid_output";
                        break;
                    }
                    decision.OutputJson = check.Json;
                    decision.AutonomyLevel = AiAutonomy.A0;
                    decision.Status = AiDecisionStatus.Succeeded;
                    break;
                case AiCallOutcome.Succeeded:
                    var parsed = AiInterpretation.TryParse(result.Json);
                    // Category / evidence_refs contract first (drops ungrounded hypotheses), then the general L3 policy.
                    if (parsed is not null) parsed = AiInterpretationValidator.ApplyContract(parsed, prompt.Categories, prompt.EvidenceKeys, prompt.RefAllowed);
                    var valid = parsed is null ? null : validator.Validate(parsed, prompt.AllowedActions, feature.MaxAutonomy);
                    if (valid is null)
                    {
                        logger.LogWarning("AI output for {Feature} {SubjectType} {SubjectId} did not match the contract", feature.Name, job.SubjectType, job.SubjectId);
                        decision.Status = AiDecisionStatus.Failed;
                        decision.ErrorCode = "invalid_output";
                        break;
                    }
                    decision.OutputJson = valid.Interpretation.ToJson();
                    decision.MaxConfidence = valid.MaxConfidence;
                    decision.AutonomyLevel = valid.AutonomyLevel;
                    decision.Status = AiDecisionStatus.Succeeded;
                    break;
                case AiCallOutcome.Refused:
                    decision.Status = AiDecisionStatus.Refused;
                    break;
                case AiCallOutcome.Unavailable:
                    decision.Status = AiDecisionStatus.Failed;
                    decision.ErrorCode = "unavailable";
                    break;
                case AiCallOutcome.MaxTokens:
                    logger.LogWarning("AI output for {Feature} {SubjectType} {SubjectId} was truncated at {MaxTokens} tokens", feature.Name, job.SubjectType, job.SubjectId, request.MaxTokens);
                    decision.Status = AiDecisionStatus.Truncated;
                    decision.ErrorCode = "max_tokens";
                    break;
                default: // Failed
                    decision.Status = AiDecisionStatus.Failed;
                    decision.ErrorCode ??= "failed";
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            decision.Status = AiDecisionStatus.Failed;
            decision.ErrorCode = "shutdown";
            throw; // the finally block still completes the row
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AI call failed for {Feature} {SubjectType} {SubjectId}", feature.Name, job.SubjectType, job.SubjectId);
            decision.Status = AiDecisionStatus.Failed;
            decision.ErrorCode = "error";
        }
        finally
        {
            // Never leave the reservation Pending: whatever happened, the row is completed (with any usage recorded).
            if (decision.Status == AiDecisionStatus.Pending)
            {
                decision.Status = AiDecisionStatus.Failed;
                decision.ErrorCode ??= "error";
            }
            decision.LatencyMs = (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds);
            try
            {
                await db.SaveChangesAsync(CancellationToken.None); // paid for: record it even when shutting down
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not complete AI decision {DecisionId}; the Pending sweep will close it", decision.Id);
            }
        }
        return decision;
    }

    /// <summary>
    /// Closes reservations that were never completed (process crash): Pending rows older than twice the call timeout
    /// become Failed / "abandoned". Run at startup by the AI worker and on every baseline job tick.
    /// </summary>
    public static Task<int> SweepAbandonedAsync(AppDbContext db, AiOptions o, DateTime nowUtc, CancellationToken ct = default)
    {
        var cutoff = nowUtc.AddSeconds(-2 * Math.Max(1, o.TimeoutSeconds));
        return db.AiDecisions
            .Where(d => d.Status == AiDecisionStatus.Pending && d.CreatedAtUtc < cutoff)
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.Status, AiDecisionStatus.Failed).SetProperty(d => d.ErrorCode, "abandoned"), ct);
    }

    private async Task<AiDecision> SaveAsync(AiDecision decision, CancellationToken ct)
    {
        db.AiDecisions.Add(decision);
        await db.SaveChangesAsync(ct);
        return decision;
    }

    /// <summary>SHA-256 over the exact model input (model, effort, prompt, schema, data); parts are length-prefixed.</summary>
    public static string Hash(params string[] parts)
    {
        var sb = new StringBuilder();
        foreach (var p in parts) sb.Append(p.Length).Append(':').Append(p).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}
