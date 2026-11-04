using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;

namespace TriggerCheck.Monitoring.Alerting;

/// <summary>Phone numbers in E.164 format: "+" country code and subscriber number, 8-15 digits in total.</summary>
public static partial class PhoneNumber
{
    [GeneratedRegex(@"^\+[1-9]\d{6,14}$")]
    private static partial Regex E164Regex();

    public static bool IsE164(string? value) => value is not null && E164Regex().IsMatch(value);

    /// <summary>For logs: <c>+905551234567</c> → <c>+90*******67</c>.</summary>
    public static string Mask(string? value)
    {
        value = value?.Trim() ?? "";
        return value.Length <= 5 ? "***" : value[..3] + new string('*', value.Length - 5) + value[^2..];
    }
}

/// <summary>
/// Twilio REST API (no SDK): POST form-encoded to {Twilio:ApiBaseUrl}/2010-04-01/Accounts/{AccountSid}/{resource}
/// with HTTP basic auth (AccountSid:AuthToken), through the guarded webhook client (api.twilio.com is public, so the
/// SSRF guard is kept). When Twilio is not configured a warning is logged and the attempt fails without retry.
/// </summary>
public abstract class TwilioNotifier(IHttpClientFactory httpClientFactory, IOptionsMonitor<TwilioOptions> options, ILogger logger) : INotifier
{
    public const string NotConfiguredError = "SMS / voice provider (Twilio) is not configured";

    public abstract AlertChannelType Type { get; }
    /// <summary>A repeated POST sends another SMS / places another call: see <see cref="WebhookPost.SendAsync"/>.</summary>
    public bool IsIdempotent => false;
    protected abstract string Resource { get; }
    protected abstract IEnumerable<KeyValuePair<string, string>> Fields(AlertMessage message);

    public Task<NotifyResult> SendAsync(NotifyTarget target, AlertMessage message, CancellationToken ct)
    {
        var o = options.CurrentValue;
        var to = target.Address?.Trim() ?? "";
        if (!o.IsConfigured)
        {
            logger.LogWarning("Twilio is not configured (Twilio:AccountSid / AuthToken / FromNumber); {Type} alert '{Subject}' to {To} not sent",
                Type, message.Subject, PhoneNumber.Mask(to));
            return Task.FromResult(NotifyResult.Fail(NotConfiguredError));
        }
        if (!PhoneNumber.IsE164(to)) return Task.FromResult(NotifyResult.Fail("Invalid phone number (E.164 format required)"));

        var url = new Uri($"{o.ApiBaseUrl.TrimEnd('/')}/2010-04-01/Accounts/{Uri.EscapeDataString(o.AccountSid)}/{Resource}");
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{o.AccountSid}:{o.AuthToken}"));
        var form = new List<KeyValuePair<string, string>> { new("To", to), new("From", o.FromNumber.Trim()) };
        form.AddRange(Fields(message));

        return WebhookPost.SendAsync(httpClientFactory, () => new HttpRequestMessage(HttpMethod.Post, url)
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Basic", credentials) },
            Content = new FormUrlEncodedContent(form)
        }, "Twilio", ct, o.AuthToken, idempotent: false);
    }
}

/// <summary>SMS via Twilio Messages API: a short single-segment-friendly text.</summary>
public sealed class TwilioSmsNotifier(IHttpClientFactory f, IOptionsMonitor<TwilioOptions> o, ILogger<TwilioSmsNotifier> l) : TwilioNotifier(f, o, l)
{
    public const int MaxLength = 300;

    public override AlertChannelType Type => AlertChannelType.Sms;
    protected override string Resource => "Messages.json";

    protected override IEnumerable<KeyValuePair<string, string>> Fields(AlertMessage message) => [new("Body", BuildBody(message))];

    public static string BuildBody(AlertMessage m)
    {
        var text = m.Kind switch
        {
            AlertKind.Test => "TriggerCheck: test notification. This alert channel works.",
            AlertKind.Down => $"TriggerCheck DOWN: {AlertMessage.OneLine(m.MonitorName)} ({m.MaskedTarget}) {m.OccurredAtUtc:HH:mm} UTC"
                              + (string.IsNullOrEmpty(m.Error) ? "" : $" - {AlertMessage.OneLine(m.Error)}"),
            _ => $"TriggerCheck UP: {AlertMessage.OneLine(m.MonitorName)} ({m.MaskedTarget}) {m.OccurredAtUtc:HH:mm} UTC"
                 + (m.DowntimeText is { } d ? $", down for {d}" : "")
        };
        return text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";
    }
}

/// <summary>Voice call via Twilio Calls API with inline TwiML: the alert is read out twice with &lt;Say&gt;.</summary>
public sealed class TwilioVoiceNotifier(IHttpClientFactory f, IOptionsMonitor<TwilioOptions> o, ILogger<TwilioVoiceNotifier> l) : TwilioNotifier(f, o, l)
{
    public override AlertChannelType Type => AlertChannelType.VoiceCall;
    protected override string Resource => "Calls.json";

    protected override IEnumerable<KeyValuePair<string, string>> Fields(AlertMessage message) => [new("Twiml", BuildTwiml(message))];

    public static string BuildTwiml(AlertMessage m)
    {
        var name = AlertMessage.OneLine(m.MonitorName);
        var speech = m.Kind switch
        {
            AlertKind.Down => $"TriggerCheck alert. Monitor {name} is down.",
            AlertKind.Up => $"TriggerCheck notice. Monitor {name} is up again.",
            _ => "This is a test call from TriggerCheck. Your alert channel works."
        };
        // Control characters are not allowed in XML at all (escaping does not help): drop them.
        var clean = new string(speech.Where(System.Xml.XmlConvert.IsXmlChar).ToArray());
        return $"<Response><Say loop=\"2\">{SecurityElement.Escape(clean)}</Say></Response>";
    }
}
