using System.Text.Json;

namespace TriggerCheck.Ai.Client;

/// <summary>
/// One model turn of a tool-use conversation (AI-7 Q&amp;A). Separate from <see cref="IAiClient"/> (one structured-output
/// call) so the interpreting features stay unchanged. Implementations: <see cref="AnthropicAiToolClient"/> (real, only
/// when Ai:Enabled and a key are configured), <see cref="UnavailableAiToolClient"/> and <see cref="FakeAiToolClient"/>
/// (scripted turns for tests / CI). The caller owns the loop: it executes the tool calls of a turn and sends the results
/// back in the next request.
/// </summary>
public interface IAiToolClient
{
    /// <summary>False when AI is switched off or not configured; callers then skip the call.</summary>
    bool IsAvailable { get; }

    /// <summary>Never throws for API / network problems (they become <see cref="AiCallOutcome.Failed"/>); honours <paramref name="ct"/>.</summary>
    Task<AiToolTurn> NextAsync(AiToolRequest request, CancellationToken ct);
}

/// <param name="Name">Tool name (a-z, _).</param>
/// <param name="InputSchema">JSON schema object of the input ("type": "object", "properties", "required", "additionalProperties": false).</param>
public sealed record AiToolDefinition(string Name, string Description, JsonElement InputSchema);

/// <param name="SystemPrompt">Static instructions; sent with a cache breakpoint (the tool list before it is cached too).</param>
/// <param name="Messages">The conversation so far; starts with a user message and alternates user / assistant.</param>
/// <param name="AllowToolUse">False: the model must answer now (tool_choice "none"; the tools stay listed for the cache).</param>
public sealed record AiToolRequest(
    string Feature,
    string Model,
    string Effort,
    int MaxTokens,
    string SystemPrompt,
    IReadOnlyList<AiToolDefinition> Tools,
    IReadOnlyList<AiToolMessage> Messages,
    bool AllowToolUse);

public enum AiToolRole
{
    User,
    Assistant
}

public sealed record AiToolCall(string Id, string Name, JsonElement Input);

/// <param name="Content">Tool output (JSON text) as the model sees it.</param>
public sealed record AiToolResult(string ToolCallId, string Content, bool IsError = false);

/// <summary>
/// One message of the conversation. A user message carries text or tool results; an assistant message carries text
/// and / or tool calls. <paramref name="ProviderContent"/> is the provider's own copy of an assistant turn (e.g. the
/// Claude content blocks including thinking signatures) and is echoed back unchanged when present.
/// </summary>
public sealed record AiToolMessage(
    AiToolRole Role,
    string? Text = null,
    IReadOnlyList<AiToolCall>? ToolCalls = null,
    IReadOnlyList<AiToolResult>? ToolResults = null,
    object? ProviderContent = null)
{
    public static AiToolMessage User(string text) => new(AiToolRole.User, Text: text);
    public static AiToolMessage Assistant(string text) => new(AiToolRole.Assistant, Text: text);
    public static AiToolMessage Results(IReadOnlyList<AiToolResult> results) => new(AiToolRole.User, ToolResults: results);
}

/// <summary>
/// Result of one model turn. <see cref="AiCallOutcome.Succeeded"/> with <see cref="ToolCalls"/> means "run these tools
/// and continue"; without tool calls <see cref="Text"/> is the final answer.
/// </summary>
/// <param name="Assistant">The assistant message to append to the conversation (null unless Succeeded).</param>
public sealed record AiToolTurn(
    AiCallOutcome Outcome,
    string? Text,
    IReadOnlyList<AiToolCall> ToolCalls,
    AiToolMessage? Assistant,
    long InputTokens = 0,
    long OutputTokens = 0,
    long CacheReadTokens = 0,
    long CacheWriteTokens = 0,
    string? ErrorCode = null)
{
    public static AiToolTurn Unavailable { get; } = new(AiCallOutcome.Unavailable, null, [], null, ErrorCode: "unavailable");
    public static AiToolTurn Fail(string code) => new(AiCallOutcome.Failed, null, [], null, ErrorCode: code);
}

/// <summary>Registered when AI is off or no key is configured: reports "AI unavailable" and never calls out.</summary>
public sealed class UnavailableAiToolClient : IAiToolClient
{
    public bool IsAvailable => false;
    public Task<AiToolTurn> NextAsync(AiToolRequest request, CancellationToken ct) => Task.FromResult(AiToolTurn.Unavailable);
}
