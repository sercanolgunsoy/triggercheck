using System.Text.Json;

namespace TriggerCheck.Ai.Client;

/// <summary>
/// Thin abstraction over the Claude API (one structured-output call). Implementations: <see cref="AnthropicAiClient"/>
/// (real, only when Ai:Enabled and a key are configured), <see cref="UnavailableAiClient"/> (no-op) and
/// <see cref="FakeAiClient"/> (tests / CI).
/// </summary>
public interface IAiClient
{
    /// <summary>False when AI is switched off or not configured; callers then skip the call.</summary>
    bool IsAvailable { get; }

    /// <summary>Never throws for API / network problems (they become <see cref="AiCallOutcome.Failed"/>); honours <paramref name="ct"/>.</summary>
    Task<AiCallResult> CompleteAsync(AiCallRequest request, CancellationToken ct);
}

/// <param name="SystemPrompt">Static text (instructions + schema notes); sent with a cache breakpoint.</param>
/// <param name="UserContent">Per-call data, placed after the cached prefix.</param>
/// <param name="OutputSchema">JSON schema for structured outputs (output_config.format).</param>
public sealed record AiCallRequest(
    string Feature,
    string Model,
    string Effort,
    int MaxTokens,
    string SystemPrompt,
    string UserContent,
    IReadOnlyDictionary<string, JsonElement> OutputSchema);

public enum AiCallOutcome
{
    Succeeded,
    Refused,
    /// <summary>stop_reason max_tokens: the JSON is incomplete.</summary>
    MaxTokens,
    Failed,
    Unavailable
}

public sealed record AiCallResult(
    AiCallOutcome Outcome,
    string? Json = null,
    long InputTokens = 0,
    long OutputTokens = 0,
    long CacheReadTokens = 0,
    long CacheWriteTokens = 0,
    string? ErrorCode = null)
{
    public static AiCallResult Unavailable { get; } = new(AiCallOutcome.Unavailable, ErrorCode: "unavailable");
    public static AiCallResult Fail(string code) => new(AiCallOutcome.Failed, ErrorCode: code);

    /// <summary>Lower-case [a-z0-9_] code of at most 40 characters (null when empty), safe to store and show.</summary>
    public static string? SafeCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var code = new string(raw.Trim().ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());
        return code.Length > 40 ? code[..40] : code;
    }
}

/// <summary>Registered when AI is off or no key is configured: reports "AI unavailable" and never calls out.</summary>
public sealed class UnavailableAiClient : IAiClient
{
    public bool IsAvailable => false;
    public Task<AiCallResult> CompleteAsync(AiCallRequest request, CancellationToken ct) => Task.FromResult(AiCallResult.Unavailable);
}
