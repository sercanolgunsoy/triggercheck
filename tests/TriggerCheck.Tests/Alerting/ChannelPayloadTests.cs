using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Monitoring.Alerting;
using TriggerCheck.Tests.Monitoring;

namespace TriggerCheck.Tests.Alerting;

/// <summary>FEAT-2 / FEAT-7: payload shapes of Slack, Teams, Telegram and Twilio against a local fake receiver.</summary>
public class ChannelPayloadTests
{
    private const string BotToken = "123456789:AAH-secret_token_value_abcdefghijk";
    private const string Sid = "AC0123456789abcdef";
    private const string AuthToken = "twilio-auth-token-secret";

    private static readonly AlertMessage Down = new(7, 1, "Shop <prod>", "https://shop.example.test", AlertKind.Down, "Connection refused",
        new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
    private static readonly AlertMessage Up = Down with { Kind = AlertKind.Up, Error = null, Downtime = TimeSpan.FromMinutes(65) };

    private sealed record Received(string Path, string ContentType, string? Authorization, string Body);

    private static async Task<(LocalHttpServer Server, ConcurrentQueue<Received> Requests)> StartReceiverAsync(int status = 200)
    {
        var requests = new ConcurrentQueue<Received>();
        var server = await LocalHttpServer.StartAsync(app => app.Run(async ctx =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            requests.Enqueue(new Received(ctx.Request.Path, ctx.Request.ContentType ?? "", ctx.Request.Headers.Authorization.FirstOrDefault(),
                await reader.ReadToEndAsync()));
            ctx.Response.StatusCode = status;
            await ctx.Response.WriteAsync("{\"ok\":true}");
        }));
        return (server, requests);
    }

    private static INotifier Notifier(MonitoringTestHost host, AlertChannelType type) =>
        host.Services.GetServices<INotifier>().Single(n => n.Type == type);

    [Fact]
    public async Task Slack_posts_text_and_blocks()
    {
        var (server, requests) = await StartReceiverAsync();
        await using var srv = server;
        await using var host = await MonitoringTestHost.CreateAsync();

        var r = await Notifier(host, AlertChannelType.Slack).SendAsync(server.Url("/services/T0/B0/xyz"), Down, CancellationToken.None);

        Assert.True(r.Success, r.Error);
        var req = Assert.Single(requests);
        Assert.Equal("/services/T0/B0/xyz", req.Path);
        Assert.StartsWith("application/json", req.ContentType);
        using var json = JsonDocument.Parse(req.Body);
        var root = json.RootElement;
        Assert.Contains("DOWN", root.GetProperty("text").GetString());
        var blocks = root.GetProperty("blocks").EnumerateArray().ToList();
        Assert.Equal("section", blocks[0].GetProperty("type").GetString());
        Assert.Equal("mrkdwn", blocks[0].GetProperty("text").GetProperty("type").GetString());
        Assert.Contains("Shop &lt;prod&gt;", blocks[0].GetProperty("text").GetProperty("text").GetString()); // mrkdwn-escaped
        var fields = blocks[1].GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("text").GetString()).ToList();
        Assert.Contains("*Target*\nhttps://shop.example.test", fields);
        Assert.Contains("*Time (UTC)*\n2026-01-01 12:00:00", fields);
        Assert.Contains(blocks, b => b.GetProperty("type").GetString() == "section"
                                     && b.TryGetProperty("text", out var t) && t.GetProperty("text").GetString()!.Contains("```Connection refused```"));

        var upFields = SlackNotifier.BuildPayload(Up)["blocks"]![1]!["fields"]!.AsArray().Select(f => (string)f!["text"]!).ToList();
        Assert.Contains("*Downtime*\n1h 5m", upFields);
    }

    [Fact]
    public async Task Teams_posts_an_adaptive_card_message_for_workflows()
    {
        var (server, requests) = await StartReceiverAsync(202);
        await using var srv = server;
        await using var host = await MonitoringTestHost.CreateAsync();

        var r = await Notifier(host, AlertChannelType.Teams).SendAsync(server.Url("/workflows/abc/triggers/manual/paths/invoke?sig=s"), Down, CancellationToken.None);

        Assert.True(r.Success, r.Error);
        using var json = JsonDocument.Parse(Assert.Single(requests).Body);
        var root = json.RootElement;
        Assert.Equal("message", root.GetProperty("type").GetString());
        var attachment = Assert.Single(root.GetProperty("attachments").EnumerateArray().ToList());
        Assert.Equal("application/vnd.microsoft.card.adaptive", attachment.GetProperty("contentType").GetString());
        var card = attachment.GetProperty("content");
        Assert.Equal("AdaptiveCard", card.GetProperty("type").GetString());
        Assert.Equal("1.4", card.GetProperty("version").GetString());
        Assert.Equal("http://adaptivecards.io/schemas/adaptive-card.json", card.GetProperty("$schema").GetString());
        var body = card.GetProperty("body").EnumerateArray().ToList();
        Assert.Equal("DOWN: Shop <prod>", body[0].GetProperty("text").GetString());
        Assert.Equal("Attention", body[0].GetProperty("color").GetString());
        var facts = body[2].GetProperty("facts").EnumerateArray()
            .ToDictionary(f => f.GetProperty("title").GetString()!, f => f.GetProperty("value").GetString());
        Assert.Equal("https://shop.example.test", facts["Target"]);
        Assert.Equal("Connection refused", facts["Error"]);
    }

    [Fact]
    public async Task Telegram_posts_send_message_with_chat_id_and_never_leaks_the_token()
    {
        var (server, requests) = await StartReceiverAsync();
        await using var srv = server;
        var logs = new ConcurrentQueue<string>();
        await using var host = await MonitoringTestHost.CreateAsync(
            s => s.AddLogging(b => b.AddProvider(new ListLoggerProvider(logs)).SetMinimumLevel(LogLevel.Trace)),
            new() { ["Telegram:ApiBaseUrl"] = server.BaseUrl });
        var telegram = Notifier(host, AlertChannelType.Telegram);

        var r = await telegram.SendAsync(new NotifyTarget("-1001234567890", BotToken), Down, CancellationToken.None);

        Assert.True(r.Success, r.Error);
        var req = Assert.Single(requests);
        Assert.Equal($"/bot{BotToken}/sendMessage", req.Path);
        using var json = JsonDocument.Parse(req.Body);
        Assert.Equal("-1001234567890", json.RootElement.GetProperty("chat_id").GetString());
        Assert.StartsWith("[TriggerCheck] DOWN: Shop <prod>", json.RootElement.GetProperty("text").GetString());
        Assert.True(json.RootElement.GetProperty("disable_web_page_preview").GetBoolean());
        Assert.False(json.RootElement.TryGetProperty("parse_mode", out _)); // plain text: nothing to escape

        Assert.False((await telegram.SendAsync(new NotifyTarget("-100123", null), Down, CancellationToken.None)).Success);
        Assert.False((await telegram.SendAsync(new NotifyTarget("not a chat", BotToken), Down, CancellationToken.None)).Success);

        // Failure (unreachable API) through the dispatcher: neither result nor logs contain the token.
        await using var failing = await MonitoringTestHost.CreateAsync(
            s => s.AddLogging(b => b.AddProvider(new ListLoggerProvider(logs)).SetMinimumLevel(LogLevel.Trace)),
            new() { ["Telegram:ApiBaseUrl"] = "http://127.0.0.1:1" });
        var customer = await failing.AddCustomerAsync();
        var channelId = await failing.WithDbAsync(async db =>
        {
            var c = new AlertChannel { CustomerId = customer.Id, Name = "tg", Type = AlertChannelType.Telegram, Target = "-1001234567890", Secret = BotToken };
            db.AlertChannels.Add(c);
            await db.SaveChangesAsync();
            return c.Id;
        });
        var fail = await failing.InScopeAsync(sp => sp.GetRequiredService<IAlertDispatcher>().SendTestAsync(customer.Id, channelId));
        Assert.False(fail!.Success);
        Assert.DoesNotContain(BotToken, fail.Error);
        Assert.DoesNotContain(logs, l => l.Contains(BotToken) || l.Contains("AAH-secret"));
        Assert.NotEmpty(logs);
    }

    [Fact]
    public async Task Twilio_sms_and_voice_post_form_fields_with_basic_auth()
    {
        var (server, requests) = await StartReceiverAsync(201);
        await using var srv = server;
        await using var host = await MonitoringTestHost.CreateAsync(settings: new()
        {
            ["Twilio:AccountSid"] = Sid, ["Twilio:AuthToken"] = AuthToken, ["Twilio:FromNumber"] = "+15550001111",
            ["Twilio:ApiBaseUrl"] = server.BaseUrl
        });

        var sms = await Notifier(host, AlertChannelType.Sms).SendAsync("+905551234567", Down, CancellationToken.None);
        var call = await Notifier(host, AlertChannelType.VoiceCall).SendAsync("+905551234567", Down, CancellationToken.None);

        Assert.True(sms.Success, sms.Error);
        Assert.True(call.Success, call.Error);
        var reqs = requests.ToList();
        var expectedAuth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Sid}:{AuthToken}"));

        Assert.Equal($"/2010-04-01/Accounts/{Sid}/Messages.json", reqs[0].Path);
        Assert.Equal(expectedAuth, reqs[0].Authorization);
        Assert.StartsWith("application/x-www-form-urlencoded", reqs[0].ContentType);
        var smsForm = QueryHelpers(reqs[0].Body);
        Assert.Equal("+905551234567", smsForm["To"]);
        Assert.Equal("+15550001111", smsForm["From"]);
        Assert.StartsWith("TriggerCheck DOWN: Shop <prod> (https://shop.example.test) 12:00 UTC - Connection refused", smsForm["Body"]);
        Assert.True(smsForm["Body"].Length <= TwilioSmsNotifier.MaxLength);

        Assert.Equal($"/2010-04-01/Accounts/{Sid}/Calls.json", reqs[1].Path);
        Assert.Equal(expectedAuth, reqs[1].Authorization);
        var callForm = QueryHelpers(reqs[1].Body);
        Assert.Equal("+905551234567", callForm["To"]);
        Assert.Equal("<Response><Say loop=\"2\">TriggerCheck alert. Monitor Shop &lt;prod&gt; is down.</Say></Response>", callForm["Twiml"]);

        Assert.False((await Notifier(host, AlertChannelType.Sms).SendAsync("05551234567", Down, CancellationToken.None)).Success);
    }

    [Fact]
    public async Task Twilio_failures_and_missing_configuration()
    {
        var (server, _) = await StartReceiverAsync(401);
        await using var s = server;
        await using var host = await MonitoringTestHost.CreateAsync(settings: new()
        {
            ["Twilio:AccountSid"] = Sid, ["Twilio:AuthToken"] = AuthToken, ["Twilio:FromNumber"] = "+15550001111",
            ["Twilio:ApiBaseUrl"] = server.BaseUrl
        });
        var r = await Notifier(host, AlertChannelType.Sms).SendAsync("+905551234567", Down, CancellationToken.None);
        Assert.Equal(NotifyResult.Fail("Twilio returned HTTP 401"), r);

        await using var unconfigured = await MonitoringTestHost.CreateAsync();
        var none = await Notifier(unconfigured, AlertChannelType.VoiceCall).SendAsync("+905551234567", Down, CancellationToken.None);
        Assert.False(none.Success);
        Assert.False(none.Retryable);
        Assert.Equal(TwilioNotifier.NotConfiguredError, none.Error);
    }

    [Fact]
    public async Task Http_status_decides_whether_to_retry()
    {
        foreach (var (status, retryable) in new[] { (500, true), (503, true), (429, true), (408, true), (400, false), (404, false) })
        {
            var (server, _) = await StartReceiverAsync(status);
            await using var s = server;
            await using var host = await MonitoringTestHost.CreateAsync();
            var r = await Notifier(host, AlertChannelType.Slack).SendAsync(server.Url("/x"), Down, CancellationToken.None);
            Assert.False(r.Success);
            Assert.Equal(retryable, r.Retryable);
            Assert.Equal($"Slack returned HTTP {status}", r.Error);
        }
    }

    private static Dictionary<string, string> QueryHelpers(string form) =>
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(form).ToDictionary(kv => kv.Key, kv => kv.Value.ToString());

    private sealed class ListLoggerProvider(ConcurrentQueue<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ListLogger(categoryName, sink);
        public void Dispose() { }

        private sealed class ListLogger(string category, ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                sink.Enqueue($"{category}: {formatter(state, exception)} {exception}");
        }
    }
}
