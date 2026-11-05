using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TriggerCheck.Ai;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Qa;

namespace TriggerCheck.Tests.Ai.Features;

/// <summary>AI-7: the real tool-use client's request building and response handling, against a fake HTTP handler (no network).</summary>
public class AnthropicAiToolClientTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public Queue<string> Responses { get; } = new();
        public List<JsonDocument> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Responses.Dequeue(), Encoding.UTF8, "application/json"),
            };
        }
    }

    private static string Message(string stopReason, string content) => $$"""
        { "id": "msg_1", "type": "message", "role": "assistant", "model": "claude-opus-5-5",
          "content": {{content}}, "stop_reason": "{{stopReason}}", "stop_sequence": null,
          "usage": { "input_tokens": 120, "output_tokens": 30, "cache_read_input_tokens": 80, "cache_creation_input_tokens": 10 } }
        """;

    private static (AnthropicAiToolClient Client, FakeHandler Handler) Create(AiOptions? o = null)
    {
        var handler = new FakeHandler();
        o ??= new AiOptions { Enabled = true, ApiKey = "test-key-not-real" };
        return (new AnthropicAiToolClient(Options.Create(o), NullLogger<AnthropicAiToolClient>.Instance, new HttpClient(handler)), handler);
    }

    private static AiToolRequest Request(IReadOnlyList<AiToolMessage> messages, bool allowTools) =>
        new("QuestionAnswer", "claude-opus-5-5", "medium", 4000, QaPrompts.System, QaToolbox.Definitions, messages, allowTools);

    [Fact]
    public async Task Builds_tool_requests_and_echoes_thinking_and_tool_use_back()
    {
        var (client, handler) = Create();
        handler.Responses.Enqueue(Message("tool_use", """
            [ { "type": "thinking", "thinking": "", "signature": "sig-abc" },
              { "type": "tool_use", "id": "toolu_1", "name": "list_monitors", "input": { "status": "down" } } ]
            """));
        handler.Responses.Enqueue(Message("end_turn", """[ { "type": "text", "text": "[[monitor:3]] is down." } ]"""));

        var question = AiToolMessage.User("question: anything down?");
        var turn = await client.NextAsync(Request([question], allowTools: true), CancellationToken.None);

        Assert.Equal(AiCallOutcome.Succeeded, turn.Outcome);
        var call = Assert.Single(turn.ToolCalls);
        Assert.Equal("toolu_1", call.Id);
        Assert.Equal("down", call.Input.GetProperty("status").GetString());
        Assert.Equal(120, turn.InputTokens);
        Assert.Equal(80, turn.CacheReadTokens);

        var first = handler.Bodies[0].RootElement;
        Assert.Equal("auto", first.GetProperty("tool_choice").GetProperty("type").GetString());
        Assert.Equal("ephemeral", first.GetProperty("system")[0].GetProperty("cache_control").GetProperty("type").GetString());
        Assert.Equal("ephemeral", first.GetProperty("cache_control").GetProperty("type").GetString());
        Assert.Equal("medium", first.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal(4000, first.GetProperty("max_tokens").GetInt32());
        var tools = first.GetProperty("tools").EnumerateArray().ToList();
        Assert.Equal(QaToolbox.Definitions.Select(d => d.Name), tools.Select(t => t.GetProperty("name").GetString()));
        Assert.All(tools, t =>
        {
            Assert.True(t.GetProperty("strict").GetBoolean());
            Assert.False(t.GetProperty("input_schema").GetProperty("additionalProperties").GetBoolean());
        });

        var results = AiToolMessage.Results([new AiToolResult("toolu_1", """{"error":"not_found"}""", IsError: true)]);
        var final = await client.NextAsync(Request([question, turn.Assistant!, results], allowTools: false), CancellationToken.None);
        Assert.Equal("[[monitor:3]] is down.", final.Text);
        Assert.Empty(final.ToolCalls);

        var second = handler.Bodies[1].RootElement;
        Assert.Equal("none", second.GetProperty("tool_choice").GetProperty("type").GetString());
        var messages = second.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["user", "assistant", "user"], messages.Select(m => m.GetProperty("role").GetString()));
        var echo = messages[1].GetProperty("content").EnumerateArray().ToList();
        Assert.Equal("thinking", echo[0].GetProperty("type").GetString());
        Assert.Equal("sig-abc", echo[0].GetProperty("signature").GetString());
        Assert.Equal("tool_use", echo[1].GetProperty("type").GetString());
        Assert.Equal("toolu_1", echo[1].GetProperty("id").GetString());
        Assert.Equal("down", echo[1].GetProperty("input").GetProperty("status").GetString());
        var result = messages[2].GetProperty("content")[0];
        Assert.Equal("tool_result", result.GetProperty("type").GetString());
        Assert.Equal("toolu_1", result.GetProperty("tool_use_id").GetString());
        Assert.True(result.GetProperty("is_error").GetBoolean());
        Assert.Contains("not_found", result.GetProperty("content").ToString());
    }

    [Theory]
    [InlineData("max_tokens", "max_tokens")]
    [InlineData("model_context_window_exceeded", "context_window_exceeded")]
    public async Task Cut_off_turns_are_reported_and_never_executed(string stopReason, string code)
    {
        var (client, handler) = Create();
        handler.Responses.Enqueue(Message(stopReason, """
            [ { "type": "tool_use", "id": "toolu_2", "name": "list_monitors", "input": {} } ]
            """));
        var turn = await client.NextAsync(Request([AiToolMessage.User("q")], allowTools: true), CancellationToken.None);
        Assert.Equal(AiCallOutcome.MaxTokens, turn.Outcome);
        Assert.Equal(code, turn.ErrorCode);
        Assert.Empty(turn.ToolCalls);
        Assert.Equal(30, turn.OutputTokens); // usage is still recorded
    }

    [Fact]
    public void Uses_its_own_per_call_timeout_and_at_most_one_retry()
    {
        var o = new AiOptions { TimeoutSeconds = 60, MaxRetries = 2 };
        Assert.Equal(TimeSpan.FromSeconds(60), AnthropicAiToolClient.AttemptTimeout(o)); // not 60 / 3 = 20 s
        Assert.Equal(1, AnthropicAiToolClient.MaxRetries(o));
        o.Qa.MaxRetries = 5;
        Assert.Equal(1, AnthropicAiToolClient.MaxRetries(o));
        o.Qa.MaxRetries = 0;
        Assert.Equal(0, AnthropicAiToolClient.MaxRetries(o));
        o.Qa.CallTimeoutSeconds = 1;
        Assert.Equal(TimeSpan.FromSeconds(10), AnthropicAiToolClient.AttemptTimeout(o));
    }
}
