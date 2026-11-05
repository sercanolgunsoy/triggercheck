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
    /// <summary>
    /// Answers one question (A0, read-only). Never throws for model, budget or data problems. <paramref name="ct"/> (the
    /// HTTP request) is only checked between steps: a call already sent to the model is allowed to finish under the
    /// server-side deadline so that its usage is recorded.
    /// </summary>
    Task<QaResult> AskAsync(QaQuestion question, CancellationToken ct = default);
}

/// <summary>
/// AI-7: natural-language questions answered with read-only, tenant-scoped tools (<see cref="QaToolbox"/>). Separate
/// from <see cref="AiInterpreter"/> (one structured call per job) because it runs a tool loop on a request path:
/// <list type="number">
/// <item>global switch + key, question length (nothing recorded);</item>
/// <item>customer opt-in, plan feature and budget, before the rate limit so these attempts do not use up the user's
/// quota; recorded as skipped decisions at most once per user per Ai:RetryCooldownMinutes;</item>
/// <item>per-instance concurrency slot ("busy"), then the per-user rate limit;</item>
/// <item>budget check and reservation of the first call (Pending row, one decision per question);</item>
/// <item>tool loop: before every further model call the budget is checked again and the call reserved
/// (<see cref="AiDecision.ModelCalls"/>); at most Ai:Qa:MaxIterations calls (the last one without tools), Ai:Qa:MaxToolCalls
/// tool calls, capped result sizes, one server-side deadline (not tied to the HTTP request);</item>
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

    /// <summary>
    /// Error codes of a question stopped while a call may have been billed (server-side deadline / the user left). Such
    /// rows count their <see cref="AiDecision.ModelCalls"/> towards the budget even without recorded tokens.
    /// </summary>
    public const string TimeoutCode = "timeout", CancelledCode = "cancelled";

    public async Task<QaResult> AskAsync(QaQuestion q, CancellationToken ct = default)
    {
        var o = options.Value;
        var qa = o.Qa;
        if (!o.Enabled || !client.IsAvailable) return new QaResult(QaStatus.Unavailable);
        var text = q.Question?.Trim() ?? "";
        if (text.Length == 0 || text.Length > Math.Max(1, qa.MaxQuestionChars)) return new QaResult(QaStatus.InvalidQuestion);

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
        // Gating first: these attempts cost nothing and must not use up the user's rate limit.
        if (!customer.AiEnabled) return await SkipOnceAsync(decision, AiDecisionStatus.SkippedOptOut, QaStatus.OptedOut, ct);
        if (!AiFeatures.PlanAllows(customer.Plan, feature)) return await SkipOnceAsync(decision, AiDecisionStatus.SkippedPlan, QaStatus.NotInPlan, ct);
        if ((await usage.GetMonthlyUsageAsync(customer.Id, ct)).IsOverBudget)
            return await SkipOnceAsync(decision, AiDecisionStatus.SkippedBudget, QaStatus.OverBudget, ct);

        using var slot = limiter.TryEnter();
        if (slot is null) return new QaResult(QaStatus.Busy);
        if (!limiter.TryAcquire(q.UserId, out var retryAfter)) return new QaResult(QaStatus.RateLimited, RetryAfterSeconds: retryAfter);

        // Model input: static system prompt + tools (cached prefix), earlier exchanges, then the current question.
        var messages = BuildMessages(text, q.History, decision.Language, qa);
        // Grounding evidence (warn-only): the questions and earlier answers as sent, then the tool results of this question.
        var questions = messages.Where(m => m.Role == AiToolRole.User).Select(m => m.Text ?? "").ToList();
        var earlierAnswers = messages.Where(m => m.Role == AiToolRole.Assistant).Select(m => m.Text ?? "").ToList();
        var toolResults = new List<string>();
        var maxTokens = o.Features.TryGetValue(feature, out var f2) && f2.MaxTokens is > 0 ? f2.MaxTokens.Value : Math.Max(1000, qa.MaxTokensPerCall);
        decision.InputHash = AiInterpreter.Hash(decision.Model, decision.Effort, QaPrompts.System,
            JsonSerializer.Serialize(QaToolbox.Definitions.Select(d => new { d.Name, d.Description, Schema = d.InputSchema.GetRawText() })),
            string.Join("\n", messages.Select(m => m.Role + ":" + m.Text)));

        // Reserve the first call (same IMMEDIATE-transaction pattern as the interpreter). A race with another question
        // can still make it a SkippedBudget row here.
        if (!await ReserveAsync(decision, 1, customer.Id))
            return new QaResult(QaStatus.OverBudget, DecisionId: decision.Id);

        var sw = Stopwatch.StartNew();
        var toolsUsed = new List<string>();
        var toolCalls = 0;
        var toolChars = 0;
        string? answer = null;
        var status = QaStatus.Failed;
        // Server-side deadline of the whole question. Deliberately NOT linked to ct: closing the tab or a proxy timeout
        // must not abort a call that is being billed (its usage would be lost); ct only stops the loop between steps.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(EffectiveTimeoutSeconds(o)), time);
        try
        {
            var maxIterations = Math.Max(1, qa.MaxIterations);
            for (var call = 1; ; call++)
            {
                if (call > 1)
                {
                    if (ct.IsCancellationRequested) { Stop(decision, CancelledCode); break; }
                    if (deadline.IsCancellationRequested) { Stop(decision, TimeoutCode); break; }
                    if (!await ReserveAsync(decision, call, customer.Id))
                    {
                        // Paid calls stay counted: the row is completed as Failed, not as a (free) budget skip.
                        Stop(decision, "budget_exhausted");
                        status = QaStatus.OverBudget;
                        break;
                    }
                }
                var allowTools = call < maxIterations && toolCalls < qa.MaxToolCalls && toolChars < qa.MaxTotalToolResultChars;
                var request = new AiToolRequest(feature, decision.Model, decision.Effort, maxTokens, QaPrompts.System, QaToolbox.Definitions, messages.ToList(), allowTools);

                AiToolTurn turn;
                try
                {
                    turn = await client.NextAsync(request, deadline.Token);
                }
                catch (OperationCanceledException)
                {
                    turn = AiToolTurn.Fail(TimeoutCode);
                }
                AddUsage(decision, turn, o);

                if (turn.Outcome != AiCallOutcome.Succeeded)
                {
                    (decision.Status, decision.ErrorCode, status) = turn.Outcome switch
                    {
                        AiCallOutcome.Refused => (AiDecisionStatus.Refused, turn.ErrorCode, QaStatus.Refused),
                        AiCallOutcome.MaxTokens => (AiDecisionStatus.Truncated, turn.ErrorCode ?? "max_tokens", QaStatus.Failed),
                        AiCallOutcome.Unavailable => (AiDecisionStatus.Failed, "unavailable", QaStatus.Failed),
                        _ => (AiDecisionStatus.Failed, turn.ErrorCode ?? "failed", QaStatus.Failed),
                    };
                    break;
                }
                if (turn.ToolCalls.Count == 0 || !allowTools)
                {
                    // Tool calls while tools were not allowed (tool_choice "none") are never executed; only text counts.
                    answer = turn.Text?.Trim();
                    if (string.IsNullOrEmpty(answer))
                    {
                        answer = null;
                        Stop(decision, "no_answer");
                    }
                    break;
                }
                if (ct.IsCancellationRequested) { Stop(decision, CancelledCode); break; }

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
                        result = await tools.ExecuteAsync(customer.Id, tc, deadline.Token);
                        toolChars += result.Content.Length;
                        if (!toolsUsed.Contains(tc.Name)) toolsUsed.Add(tc.Name);
                    }
                    if (!result.IsError) toolResults.Add(result.Content);
                    results.Add(result);
                }
                messages.Add(AiToolMessage.Results(results));
            }
        }
        catch (OperationCanceledException)
        {
            Stop(decision, TimeoutCode); // the deadline expired inside a tool query
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Q&A failed for customer {CustomerId}", customer.Id);
            Stop(decision, "error");
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
                Stop(decision, "error");
            decision.LatencyMs = (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds);
            // A reservation transaction that was rolled back may have left these marked as saved: write them again.
            var entry = db.Entry(decision);
            foreach (var p in new[] { nameof(AiDecision.InputTokens), nameof(AiDecision.OutputTokens), nameof(AiDecision.CacheReadTokens),
                         nameof(AiDecision.CacheWriteTokens), nameof(AiDecision.CostEstimate), nameof(AiDecision.ModelCalls) })
                entry.Property(p).IsModified = true;
            try
            {
                await db.SaveChangesAsync(CancellationToken.None); // paid for: record it even when the request is gone
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not complete Q&A decision {DecisionId}; the Pending sweep will close it", decision.Id);
            }
        }

        if (status != QaStatus.Answered || answer is null) return new QaResult(status, DecisionId: decision.Id, ErrorCode: decision.ErrorCode);
        var ungrounded = grounding.Ungrounded(QaAnswerText.StripReferences(answer),
            new QaGroundingEvidence(toolResults, questions, earlierAnswers, decision.Language));
        if (ungrounded.Count > 0)
            logger.LogWarning("Q&A answer {DecisionId} has {Count} number(s) not found in the tool results", decision.Id, ungrounded.Count);
        return new QaResult(QaStatus.Answered, new QaAnswer(answer, QaAnswerText.Parse(answer), ungrounded, toolsUsed), decision.Id);
    }

    private static void Stop(AiDecision d, string code)
    {
        d.Status = AiDecisionStatus.Failed;
        d.ErrorCode = code;
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
    /// Runs with CancellationToken.None: a cancelled request must not roll back the recorded usage.
    /// </summary>
    private async Task<bool> ReserveAsync(AiDecision decision, int call, int customerId)
    {
        var none = CancellationToken.None;
        await using var tx = await db.Database.BeginTransactionAsync(none);
        if (call == 1)
        {
            var over = (await usage.GetMonthlyUsageAsync(customerId, none)).IsOverBudget;
            decision.Status = over ? AiDecisionStatus.SkippedBudget : AiDecisionStatus.Pending;
            decision.ModelCalls = over ? 0 : 1;
            db.AiDecisions.Add(decision);
            await db.SaveChangesAsync(none);
            await tx.CommitAsync(none);
            return !over;
        }
        await db.SaveChangesAsync(none); // tokens of the calls so far count before the check
        if ((await usage.GetMonthlyUsageAsync(customerId, none)).IsOverBudget)
        {
            await tx.CommitAsync(none);
            return false;
        }
        decision.ModelCalls = call;
        await db.SaveChangesAsync(none);
        await tx.CommitAsync(none);
        return true;
    }

    /// <summary>
    /// Records a skipped question (opt-out / not in plan / budget) at most once per user and status within
    /// Ai:RetryCooldownMinutes (at least one minute), so repeated attempts do not fill the table.
    /// </summary>
    private async Task<QaResult> SkipOnceAsync(AiDecision decision, AiDecisionStatus skip, QaStatus status, CancellationToken ct)
    {
        var since = decision.CreatedAtUtc.AddMinutes(-Math.Max(1, options.Value.RetryCooldownMinutes));
        var recorded = await db.AiDecisions.AsNoTracking().AnyAsync(d => d.CustomerId == decision.CustomerId && d.Feature == decision.Feature
            && d.SubjectType == decision.SubjectType && d.SubjectId == decision.SubjectId && d.Status == skip && d.CreatedAtUtc >= since, ct);
        if (recorded) return new QaResult(status);
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
