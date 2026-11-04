using System.Collections.Concurrent;

namespace TriggerCheck.Ai.Client;

/// <summary>
/// Deterministic stand-in for the Claude API (tests and CI: no key, no network). Records every request and answers
/// with a schema-valid canned interpretation unless <see cref="Responder"/> is set.
/// </summary>
public sealed class FakeAiClient : IAiClient
{
    public const string CannedJson = """
        {
          "summary": "The monitor failed because the target returned HTTP 503 for several consecutive checks.",
          "hypotheses": [
            { "cause": "The upstream service was overloaded or in maintenance.", "confidence": "high", "evidence": ["3 consecutive checks returned status 503"] },
            { "cause": "A deployment restarted the service.", "confidence": "low", "evidence": ["Response times rose shortly before the failure"] }
          ],
          "suggested_actions": [],
          "unknowns": ["Whether a deployment happened at that time."]
        }
        """;

    public ConcurrentQueue<AiCallRequest> Requests { get; } = new();

    /// <summary>Custom behaviour (default: success with <see cref="CannedJson"/> and fixed token counts).</summary>
    public Func<AiCallRequest, AiCallResult>? Responder { get; set; }

    public bool IsAvailable { get; set; } = true;

    public Task<AiCallResult> CompleteAsync(AiCallRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Requests.Enqueue(request);
        return Task.FromResult(Responder?.Invoke(request) ?? Success(CannedJson));
    }

    public static AiCallResult Success(string json) =>
        new(AiCallOutcome.Succeeded, json, InputTokens: 1200, OutputTokens: 300, CacheReadTokens: 800, CacheWriteTokens: 0);
}
