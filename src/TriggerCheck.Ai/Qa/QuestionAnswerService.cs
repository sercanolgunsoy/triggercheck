using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TriggerCheck.Ai.Budget;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Context;
using TriggerCheck.Ai.Execution;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Ai.Qa;

public enum QaStatus
{
    Answered,
    /// <summary>AI is switched off globally or not configured.</summary>
    Unavailable,
    /// <summary>The customer has not opted in.</summary>
    OptedOut,
    /// <summary>The customer's plan does not include AI-7.</summary>
    NotInPlan,
    /// <summary>The monthly AI budget is used up (before or during the tool loop).</summary>
    OverBudget,
    RateLimited,
    /// <summary>Too many questions are being answered on this instance right now.</summary>
    Busy,
    /// <summary>Empty or too long.</summary>
    InvalidQuestion,
    Refused,
    Failed
}

/// <summary>An earlier question and answer of the same page session (sent along for follow-up questions).</summary>
public sealed record QaExchange(string Question, string Answer);

/// <param name="CustomerId">From the signed-in user's session (ICurrentCustomer), never from user or model input.</param>
/// <param name="UserId">The signed-in user (per-user rate limit and the decision's subject).</param>
public sealed record QaQuestion(int CustomerId, string UserId, string Question, IReadOnlyList<QaExchange>? History = null);

/// <param name="Text">Answer text including reference tokens (kept for follow-up questions).</param>
/// <param name="UngroundedNumbers">Numbers / dates of the answer not found in the data the model was given.</param>
public sealed record QaAnswer(string Text, IReadOnlyList<QaSegment> Segments, IReadOnlyList<string> UngroundedNumbers, IReadOnlyList<string> ToolsUsed);

public sealed record QaResult(QaStatus Status, QaAnswer? Answer = null, long? DecisionId = null, int RetryAfterSeconds = 0, string? ErrorCode = null);

public interface IQuestionAnswerService
{
    /// <summary>Answers one question (A0, read-only). Never throws for model, budget or data problems; honours <paramref name="ct"/>.</summary>
    Task<QaResult> AskAsync(QaQuestion question, CancellationToken ct = default);
}

/// <summary>
/// AI-7: natural-language questions answered with read-only, tenant-scoped tools (<see cref="QaToolbox"/>). Separate
/// from <see cref="AiInterpreter"/> (one structured call per job) because it runs a tool loop on a request path:
/// <list type="number">
/// <item>global switch + key, question length (nothing recorded), per-user rate limit;</item>
/// <item>customer opt-in and plan feature (recorded as skipped decisions, like the interpreter);</item>
/// <item>per-instance concurrency slot ("busy" otherwise, so slow model calls cannot pile up);</item>
/// <item>budget check and reservation of the first call (Pending row, one decision per question);</item>
/// <item>tool loop: before every further model call the budget is checked again and the call reserved
/// (<see cref="AiDecision.ModelCalls"/>); at most Ai:Qa:MaxIterations calls (the last one without tools), Ai:Qa:MaxToolCalls
/// tool calls, capped result sizes, one overall timeout;</item>
/// <item>the answer's reference tokens are parsed and its numbers checked (<see cref="IQaAnswerGrounding"/>); tokens,
/// cost and latency are recorded on the decision.</item>
/// </list>
/// Question and answer text are not stored (the conversation lives in the page session only).
/// </summary>
public sealed class QuestionAnswerService(
    AppDbContext db,
    IAiToolClient client,
    IAiUsage usage,
    QaToolbox tools,
    QaRateLimiter limiter,
    IQaAnswerGrounding grounding,
    IOptions<AiOptions> options,
    TimeProvider time,
    ILogger<QuestionAnswerService> logger) : IQuestionAnswerService
{
    public const string SubjectType = "User";

    public async Task<QaResult> AskAsync(QaQuestion q, CancellationToken ct = default)
    {
        var o = options.Value;
        var qa = o.Qa;
        if (!o.Enabled || !client.IsAvailable) return new QaResult(QaStatus.Unavailable);
        var text = q.Question?.Trim() ?? "";
        if (text.Length == 0 || text.Length > Math.Max(1, qa.MaxQuestionChars)) return new QaResult(QaStatus.InvalidQuestion);
        if (!limiter.TryAcquire(q.UserId, out var retryAfter)) return new QaResult(QaStatus.RateLimited, RetryAfterSeconds: retryAfter);

        var customer = await db.Customers.AsNoTracking().Include(c => c.Plan).FirstOrDefaultAsync(c => c.Id == q.CustomerId, ct);
        if (customer is null) return new QaResult(QaStatus.Failed, ErrorCode: "no_customer");

        var feature = AiFeatures.QuestionAnswer;
        var decision = new AiDecision
        {
            CustomerId = customer.Id,
            Feature = feature,
            SubjectType = SubjectType,
            SubjectId = q.UserId.Length > 64 ? q.UserId[..64] : q.UserId,
            Model = o.ModelFor(feature),
            Effort = o.Features.TryGetValue(feature, out var fo) && !string.IsNullOrWhiteSpace(fo.Effort) ? fo.Effort! : qa.Effort,
            Language = AiJob.NormalizeLanguage(customer.AiLanguage),
            CreatedAtUtc = time.GetUtcNow().UtcDateTime,
        };
        if (!customer.AiEnabled) return await SkipAsync(decision, AiDecisionStatus.SkippedOptOut, QaStatus.OptedOut, ct);
        if (!AiFeatures.PlanAllows(customer.Plan, feature)) return await SkipAsync(decision, AiDecisionStatus.SkippedPlan, QaStatus.NotInPlan, ct);

        using var slot = limiter.TryEnter();
        if (slot is null) return new QaResult(QaStatus.Busy);

        // Model input: static system prompt + tools (cached prefix), earlier exchanges, then the current question.
        var messages = BuildMessages(text, q.History, decision.Language, qa);
        var evidence = new StringBuilder();
        foreach (var m in messages.Where(m => m.Role == AiToolRole.User)) evidence.AppendLine(m.Text);
        var maxTokens = o.Features.TryGetValue(feature, out var f2) && f2.MaxTokens is > 0 ? f2.MaxTokens.Value : Math.Max(1000, qa.MaxTokensPerCall);
        decision.InputHash = AiInterpreter.Hash(decision.Model, decision.Effort, QaPrompts.System,
            JsonSerializer.Serialize(QaToolbox.Definitions.Select(d => new { d.Name, d.Description, Schema = d.InputSchema.GetRawText() })),
            string.Join("\n", messages.Select(m => m.Role + ":" + m.Text)));

        // Reserve the first call (same IMMEDIATE-transaction pattern as the interpreter).
        if (!await ReserveAsync(decision, 1, customer.Id, ct))
            return new QaResult(QaStatus.OverBudget, DecisionId: decision.Id);

        var sw = Stopwatch.StartNew();
        var toolsUsed = new List<string>();
        var toolCalls = 0;
        var toolChars = 0;
        string? answer = null;
        var status = QaStatus.Failed;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(EffectiveTimeoutSeconds(o)));
            var maxIterations = Math.Max(1, qa.MaxIterations);
            for (var call = 1; ; call++)
            {
                if (call > 1 && !await ReserveAsync(decision, call, customer.Id, ct))
                {
                    // Paid calls stay counted: the row is completed as Failed, not as a (free) budget skip.
                    decision.Status = AiDecisionStatus.Failed;
                    decision.ErrorCode = "budget_exhausted";
                    status = QaStatus.OverBudget;
                    break;
                }
                var allowTools = call < maxIterations && toolCalls < qa.MaxToolCalls && toolChars < qa.MaxTotalToolResultChars;
                var request = new AiToolRequest(feature, decision.Model, decision.Effort, maxTokens, QaPrompts.System, QaToolbox.Definitions, messages.ToList(), allowTools);

                AiToolTurn turn;
                try
                {
                    turn = await client.NextAsync(request, timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    turn = AiToolTurn.Fail("timeout");
                }
                AddUsage(decision, turn, o);

                if (turn.Outcome != AiCallOutcome.Succeeded)
                {
                    (decision.Status, decision.ErrorCode, status) = turn.Outcome switch
                    {
                        AiCallOutcome.Refused => (AiDecisionStatus.Refused, turn.ErrorCode, QaStatus.Refused),
                        AiCallOutcome.MaxTokens => (AiDecisionStatus.Truncated, "max_tokens", QaStatus.Failed),
                        AiCallOutcome.Unavailable => (AiDecisionStatus.Failed, "unavailable", QaStatus.Failed),
                        _ => (AiDecisionStatus.Failed, turn.ErrorCode ?? "failed", QaStatus.Failed),
                    };
                    break;
                }
                if (turn.ToolCalls.Count == 0 || !allowTools)
                {
                    answer = turn.Text?.Trim();
                    if (string.IsNullOrEmpty(answer))
                    {
                        decision.Status = AiDecisionStatus.Failed;
                        decision.ErrorCode = "no_answer";
                    }
                    break;
                }

                messages.Add(turn.Assistant ?? new AiToolMessage(AiToolRole.Assistant, turn.Text, turn.ToolCalls));
                var results = new List<AiToolResult>();
                foreach (var tc in turn.ToolCalls) // one result per call, in order (the API requires all of them)
                {
                    AiToolResult result;
                    if (toolCalls >= qa.MaxToolCalls || toolChars >= qa.MaxTotalToolResultChars)
                        result = new AiToolResult(tc.Id, """{"error":"tool_limit_reached","message":"No more tool calls for this question; answer with the data you have."}""", IsError: true);
                    else
                    {
                        toolCalls++;
                        // The customer id comes from the question (the session), never from the model's input.
                        result = await tools.ExecuteAsync(customer.Id, tc, timeout.Token);
                        toolChars += result.Content.Length;
                        if (!toolsUsed.Contains(tc.Name)) toolsUsed.Add(tc.Name);
                    }
                    evidence.AppendLine(result.Content);
                    results.Add(result);
                }
                messages.Add(AiToolMessage.Results(results));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            decision.Status = AiDecisionStatus.Failed;
            decision.ErrorCode = "cancelled";
            throw; // the finally block still completes the row
        }
        catch (OperationCanceledException)
        {
            decision.Status = AiDecisionStatus.Failed;
            decision.ErrorCode = "timeout";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Q&A failed for customer {CustomerId}", customer.Id);
            decision.Status = AiDecisionStatus.Failed;
            decision.ErrorCode = "error";
        }
        finally
        {
            if (decision.Status == AiDecisionStatus.Pending && answer is not null)
            {
                decision.Status = AiDecisionStatus.Succeeded;
                decision.AutonomyLevel = AiAutonomy.A0;
                status = QaStatus.Answered;
            }
            else if (decision.Status == AiDecisionStatus.Pending)
            {
                decision.Status = AiDecisionStatus.Failed;
                decision.ErrorCode ??= "error";
            }
            decision.LatencyMs = (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds);
            try
            {
                await db.SaveChangesAsync(CancellationToken.None); // paid for: record it even when the request is cancelled
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not complete Q&A decision {DecisionId}; the Pending sweep will close it", decision.Id);
            }
        }

        if (status != QaStatus.Answered || answer is null) return new QaResult(status, DecisionId: decision.Id, ErrorCode: decision.ErrorCode);
        var ungrounded = grounding.Ungrounded(QaAnswerText.StripReferences(answer), evidence.ToString());
        if (ungrounded.Count > 0)
            logger.LogWarning("Q&A answer {DecisionId} has {Count} number(s) not found in the tool results", decision.Id, ungrounded.Count);
        return new QaResult(QaStatus.Answered, new QaAnswer(answer, QaAnswerText.Parse(answer), ungrounded, toolsUsed), decision.Id);
    }

    /// <summary>
    /// The conversation sent to the model. Earlier exchanges (newest Ai:Qa:MaxHistoryTurns) and the question are masked
    /// like every other model input (K5); earlier answers are cut to Ai:Qa:MaxHistoryAnswerChars.
    /// </summary>
    public static List<AiToolMessage> BuildMessages(string question, IReadOnlyList<QaExchange>? history, string language, AiQaOptions qa, DateTime? nowUtc = null)
    {
        var messages = new List<AiToolMessage>();
        var languageName = language == "tr" ? "Turkish" : "English";
        foreach (var h in (history ?? []).Where(h => !string.IsNullOrWhiteSpace(h.Question) && !string.IsNullOrWhiteSpace(h.Answer))
                     .TakeLast(Math.Max(0, qa.MaxHistoryTurns)))
        {
            messages.Add(AiToolMessage.User("question: " + AiContextBuilder.FactText(Cut(h.Question.Trim(), qa.MaxQuestionChars))));
            messages.Add(AiToolMessage.Assistant(AiMasker.MaskText(Cut(h.Answer.Trim(), qa.MaxHistoryAnswerChars))));
        }
        var now = nowUtc ?? DateTime.UtcNow;
        messages.Add(AiToolMessage.User(
            "current_time_utc: " + AiContextBuilder.Iso(now) + "\n" +
            "response_language: " + languageName + "\n" +
            "question: " + AiContextBuilder.FactText(question)));
        return messages;
    }

    private List<AiToolMessage> BuildMessages(string question, IReadOnlyList<QaExchange>? history, string language, AiQaOptions qa) =>
        BuildMessages(question, history, language, qa, time.GetUtcNow().UtcDateTime);

    /// <summary>
    /// Ai:Qa:TimeoutSeconds, but below the Pending sweep's cut-off (2 x Ai:TimeoutSeconds) so a running question is never
    /// closed as "abandoned".
    /// </summary>
    public static int EffectiveTimeoutSeconds(AiOptions o) =>
        Math.Max(1, Math.Min(o.Qa.TimeoutSeconds, 2 * Math.Max(1, o.TimeoutSeconds) - 5));

    private static string Cut(string s, int max) => s.Length <= Math.Max(1, max) ? s : s[..Math.Max(1, max)] + AiContextBuilder.TruncatedMarker;

    /// <summary>
    /// Checks the budget and reserves model call <paramref name="call"/> in one (SQLite: IMMEDIATE) transaction. The first
    /// reservation inserts the Pending row (or a SkippedBudget row when the budget is used up); later ones raise
    /// <see cref="AiDecision.ModelCalls"/> after saving the tokens used so far, so the token limit applies mid-loop too.
    /// </summary>
    private async Task<bool> ReserveAsync(AiDecision decision, int call, int customerId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (call == 1)
        {
            var over = (await usage.GetMonthlyUsageAsync(customerId, ct)).IsOverBudget;
            decision.Status = over ? AiDecisionStatus.SkippedBudget : AiDecisionStatus.Pending;
            decision.ModelCalls = over ? 0 : 1;
            db.AiDecisions.Add(decision);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return !over;
        }
        await db.SaveChangesAsync(ct); // tokens of the calls so far count before the check
        if ((await usage.GetMonthlyUsageAsync(customerId, ct)).IsOverBudget)
        {
            await tx.CommitAsync(ct);
            return false;
        }
        decision.ModelCalls = call;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private async Task<QaResult> SkipAsync(AiDecision decision, AiDecisionStatus skip, QaStatus status, CancellationToken ct)
    {
        decision.Status = skip;
        db.AiDecisions.Add(decision);
        await db.SaveChangesAsync(ct);
        return new QaResult(status, DecisionId: decision.Id);
    }

    private static void AddUsage(AiDecision d, AiToolTurn t, AiOptions o)
    {
        d.InputTokens += t.InputTokens;
        d.OutputTokens += t.OutputTokens;
        d.CacheReadTokens += t.CacheReadTokens;
        d.CacheWriteTokens += t.CacheWriteTokens;
        d.CostEstimate = decimal.Round(o.PriceFor(d.Model).Estimate(d.InputTokens, d.OutputTokens, d.CacheReadTokens, d.CacheWriteTokens), 6);
    }
}
