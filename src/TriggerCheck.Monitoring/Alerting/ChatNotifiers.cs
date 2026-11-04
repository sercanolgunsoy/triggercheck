using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;

namespace TriggerCheck.Monitoring.Alerting;

/// <summary>
/// Slack incoming webhook (https://hooks.slack.com/services/...): <c>{ text, blocks }</c> where <c>text</c> is the
/// notification fallback and <c>blocks</c> holds a mrkdwn section with the status, a fields section (target, time,
/// downtime) and the error as a code block. Slack answers 200 "ok".
/// </summary>
public sealed partial class SlackNotifier(IHttpClientFactory httpClientFactory) : INotifier
{
    public AlertChannelType Type => AlertChannelType.Slack;

    public Task<NotifyResult> SendAsync(NotifyTarget target, AlertMessage message, CancellationToken ct) =>
        WebhookPost.JsonAsync(httpClientFactory, target.Address, BuildPayload(message), "Slack", ct);

    public static JsonObject BuildPayload(AlertMessage m)
    {
        var emoji = m.Kind switch { AlertKind.Down => ":red_circle:", AlertKind.Up => ":large_green_circle:", _ => ":white_check_mark:" };
        var blocks = new JsonArray
        {
            Section($"{emoji} *{Escape(m.Headline)}*")
        };

        var fields = new JsonArray();
        if (m.Kind != AlertKind.Test) fields.Add(Mrkdwn($"*Target*\n{Escape(m.Target)}"));
        fields.Add(Mrkdwn($"*Time (UTC)*\n{m.OccurredAtUtc:yyyy-MM-dd HH:mm:ss}"));
        if (m.DowntimeText is { } downtime) fields.Add(Mrkdwn($"*Downtime*\n{downtime}"));
        blocks.Add(new JsonObject { ["type"] = "section", ["fields"] = fields });

        if (m.Kind == AlertKind.Down && !string.IsNullOrEmpty(m.Error))
            blocks.Add(Section($"*Error*\n```{Escape(m.Error).Replace("```", "'''")}```"));
        blocks.Add(new JsonObject
        {
            ["type"] = "context",
            ["elements"] = new JsonArray { Mrkdwn("Sent by TriggerCheck") }
        });

        return new JsonObject
        {
            ["text"] = $"{emoji} {m.Headline}", // notification / fallback text (plain)
            ["blocks"] = blocks
        };
    }

    private static JsonObject Section(string text) => new() { ["type"] = "section", ["text"] = Mrkdwn(text) };
    private static JsonObject Mrkdwn(string text) => new() { ["type"] = "mrkdwn", ["text"] = text };

    /// <summary>Slack mrkdwn control characters (&amp;, &lt;, &gt;) must be HTML-escaped.</summary>
    public static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}

/// <summary>
/// Microsoft Teams via the Workflows app (Power Automate template "Post to a channel when a webhook request is
/// received"; the legacy Office 365 connector / MessageCard webhooks are retired). The workflow URL receives
/// <c>{ type: "message", attachments: [{ contentType: "application/vnd.microsoft.card.adaptive", content: AdaptiveCard 1.4 }] }</c>
/// and answers 202 Accepted.
/// </summary>
public sealed class TeamsNotifier(IHttpClientFactory httpClientFactory) : INotifier
{
    public const string AdaptiveCardContentType = "application/vnd.microsoft.card.adaptive";

    public AlertChannelType Type => AlertChannelType.Teams;

    public Task<NotifyResult> SendAsync(NotifyTarget target, AlertMessage message, CancellationToken ct) =>
        WebhookPost.JsonAsync(httpClientFactory, target.Address, BuildPayload(message), "Teams", ct);

    public static JsonObject BuildPayload(AlertMessage m)
    {
        var facts = new JsonArray();
        if (m.Kind != AlertKind.Test) facts.Add(Fact("Target", m.Target));
        facts.Add(Fact("Time (UTC)", m.OccurredAtUtc.ToString("yyyy-MM-dd HH:mm:ss")));
        if (m.Kind == AlertKind.Down && !string.IsNullOrEmpty(m.Error)) facts.Add(Fact("Error", m.Error));
        if (m.DowntimeText is { } downtime) facts.Add(Fact("Downtime", downtime));

        var card = new JsonObject
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = "1.4",
            ["body"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "TextBlock",
                    ["text"] = $"{m.StatusLabel}: {AlertMessage.OneLine(m.MonitorName)}",
                    ["weight"] = "Bolder",
                    ["size"] = "Medium",
                    ["color"] = m.Kind switch { AlertKind.Down => "Attention", AlertKind.Up => "Good", _ => "Accent" },
                    ["wrap"] = true
                },
                new JsonObject { ["type"] = "TextBlock", ["text"] = m.Headline, ["wrap"] = true },
                new JsonObject { ["type"] = "FactSet", ["facts"] = facts }
            }
        };

        return new JsonObject
        {
            ["type"] = "message",
            ["attachments"] = new JsonArray
            {
                new JsonObject { ["contentType"] = AdaptiveCardContentType, ["contentUrl"] = null, ["content"] = card }
            }
        };
    }

    private static JsonObject Fact(string title, string value) => new() { ["title"] = title, ["value"] = value };
}

/// <summary>
/// Telegram Bot API: POST {Telegram:ApiBaseUrl}/bot{token}/sendMessage <c>{ chat_id, text, disable_web_page_preview }</c>
/// (plain text, no parse mode, so nothing needs escaping). Target = chat id, secret = bot token. The token is part of
/// the URL: it never appears in results or logs (the webhook client has request logging turned off).
/// </summary>
public sealed partial class TelegramNotifier(IHttpClientFactory httpClientFactory, IOptionsMonitor<TelegramOptions> options) : INotifier
{
    public AlertChannelType Type => AlertChannelType.Telegram;

    [GeneratedRegex(@"^\d{3,}:[A-Za-z0-9_-]{20,}$")]
    public static partial Regex BotTokenRegex();

    [GeneratedRegex(@"^(-?\d{1,20}|@[A-Za-z][A-Za-z0-9_]{3,})$")]
    public static partial Regex ChatIdRegex();

    public Task<NotifyResult> SendAsync(NotifyTarget target, AlertMessage message, CancellationToken ct)
    {
        var token = target.Secret?.Trim() ?? "";
        var chatId = target.Address?.Trim() ?? "";
        if (!BotTokenRegex().IsMatch(token)) return Task.FromResult(NotifyResult.Fail("Invalid or missing Telegram bot token"));
        if (!ChatIdRegex().IsMatch(chatId)) return Task.FromResult(NotifyResult.Fail("Invalid Telegram chat id"));

        var url = new Uri(options.CurrentValue.ApiBaseUrl.TrimEnd('/') + "/bot" + token + "/sendMessage");
        var payload = BuildPayload(chatId, message);
        return WebhookPost.SendAsync(httpClientFactory,
            () => new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) }, "Telegram", ct, token);
    }

    public static JsonObject BuildPayload(string chatId, AlertMessage m) => new()
    {
        ["chat_id"] = chatId,
        ["text"] = m.Subject + "\n\n" + m.Body,
        ["disable_web_page_preview"] = true
    };
}
