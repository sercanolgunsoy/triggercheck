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

    /// <summary>
    /// Asynchronous behaviour (slow or hanging calls); receives the call's token. Used before <see cref="Script"/> and
    /// <see cref="Responder"/> when it returns a turn (null: fall through).
    /// </summary>
    public Func<AiToolRequest, CancellationToken, Task<AiToolTurn?>>? AsyncResponder { get; set; }

    /// <summary>
    /// True (default): a tool turn while tools are not allowed becomes the default answer, as tool_choice "none" makes the
    /// real API answer. False: such a turn is returned as is (tests of the caller's own guard).
    /// </summary>
    public bool EnforceToolChoice { get; set; } = true;

    public bool IsAvailable { get; set; } = true;

    public async Task<AiToolTurn> NextAsync(AiToolRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Requests.Enqueue(request);
        var turn = AsyncResponder is null ? null : await AsyncResponder(request, ct);
        turn ??= Script.TryDequeue(out var scripted) ? scripted : Responder?.Invoke(request) ?? Answer(DefaultAnswer);
        if (EnforceToolChoice && !request.AllowToolUse && turn.ToolCalls.Count > 0) turn = Answer(DefaultAnswer);
        return turn;
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
