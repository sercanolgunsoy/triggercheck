using System.Collections.Concurrent;
using System.Text.Json;

namespace TriggerCheck.Ai.Client;

/// <summary>
/// Deterministic stand-in for tool-use conversations (tests and CI: no key, no network). Records every request and
/// answers with the next scripted turn (<see cref="Script"/>), else <see cref="Responder"/>, else a fixed final answer.
/// Scripts are plain data: <see cref="ToolUse"/> asks for tool calls, <see cref="Answer"/> ends the conversation.
/// </summary>
public sealed class FakeAiToolClient : IAiToolClient
{
    public const string DefaultAnswer = "I could not find anything relevant in your monitoring data.";
    public const long TurnInputTokens = 1500, TurnOutputTokens = 200, TurnCacheReadTokens = 1000;

    private int _ids;

    public ConcurrentQueue<AiToolRequest> Requests { get; } = new();

    /// <summary>Turns returned in order, one per request.</summary>
    public ConcurrentQueue<AiToolTurn> Script { get; } = new();

    /// <summary>Used when the script is empty. Receives the request (the tool results so far are in its messages).</summary>
    public Func<AiToolRequest, AiToolTurn>? Responder { get; set; }

    public bool IsAvailable { get; set; } = true;

    public Task<AiToolTurn> NextAsync(AiToolRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Requests.Enqueue(request);
        var turn = Script.TryDequeue(out var scripted) ? scripted : Responder?.Invoke(request) ?? Answer(DefaultAnswer);
        // A scripted tool turn while tools are not allowed would be rejected by the real API (tool_choice "none").
        if (!request.AllowToolUse && turn.ToolCalls.Count > 0) turn = Answer(DefaultAnswer);
        return Task.FromResult(turn);
    }

    /// <summary>A final answer.</summary>
    public static AiToolTurn Answer(string text) =>
        new(AiCallOutcome.Succeeded, text, [], AiToolMessage.Assistant(text), TurnInputTokens, TurnOutputTokens, TurnCacheReadTokens);

    /// <summary>A turn that calls the given tools; <c>input</c> is any object serialized as the tool input.</summary>
    public AiToolTurn ToolUse(params (string Name, object Input)[] calls)
    {
        var list = calls.Select(c => new AiToolCall("toolu_fake_" + Interlocked.Increment(ref _ids), c.Name,
            c.Input is JsonElement e ? e : JsonSerializer.SerializeToElement(c.Input))).ToList();
        return new AiToolTurn(AiCallOutcome.Succeeded, null, list, new AiToolMessage(AiToolRole.Assistant, ToolCalls: list),
            TurnInputTokens, TurnOutputTokens, TurnCacheReadTokens);
    }

    /// <summary>Tool results of the last user message of a request (what the previous tool turn returned).</summary>
    public static IReadOnlyList<AiToolResult> LastResults(AiToolRequest request) =>
        request.Messages.LastOrDefault(m => m.ToolResults is not null)?.ToolResults ?? [];
}
