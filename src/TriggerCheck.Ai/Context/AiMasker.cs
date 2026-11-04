using System.Text.RegularExpressions;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Domain.Security;

namespace TriggerCheck.Ai.Context;

/// <summary>
/// Masks personal data and secrets before anything is sent to the model (decision K5: only masked technical data
/// leaves the system). Uses <see cref="SecretMasker"/> for URLs, like the rest of the app.
/// </summary>
public static partial class AiMasker
{
    public const string Email = "[email]";
    public const string Phone = "[phone]";
    public const string Secret = "[secret]";

    // "Authorization: Bearer x", "api_key=x", "token: x", "password=x", "X-Api-Key: x" ... (value up to whitespace / quote / ; / &).
    [GeneratedRegex(@"(?i)\b(authorization|proxy-authorization|x-api-key|api[-_]?key|access[-_]?token|refresh[-_]?token|token|secret|password|passwd|pwd|cookie|set-cookie)(\s*[:=]\s*)(bearer\s+|basic\s+)?[^\s""'&;,]+")]
    private static partial Regex AuthLike();

    [GeneratedRegex(@"(?i)\b(bearer|basic)\s+[a-z0-9\-._~+/]{8,}=*")]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"(?i)\b[a-z][a-z0-9+.\-]*://[^\s""'<>]+")]
    private static partial Regex Url();

    [GeneratedRegex(@"(?i)\b[a-z0-9._%+\-]+@[a-z0-9.\-]+\.[a-z]{2,}\b")]
    private static partial Regex EmailAddress();

    // International (+90 532 123 45 67), NANP ((555) 123-4567 / 555-123-4567) and Turkish national (0532 123 45 67) numbers.
    [GeneratedRegex(@"(?<![\w+])\+\d{1,3}[\s.\-]?(?:\(?\d{1,4}\)?[\s.\-]?){2,5}\d{2,4}\b|\(\d{3}\)\s?\d{3}[\s.\-]\d{4}\b|\b\d{3}[.\-]\d{3}[.\-]\d{4}\b|\b0\d{3}\s?\d{3}\s?\d{2}\s?\d{2}\b")]
    private static partial Regex PhoneNumber();

    /// <summary>Masks secrets, URLs (query string, credentials), e-mail addresses and phone numbers in free text.</summary>
    public static string MaskText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = AuthLike().Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + Secret);
        s = BearerToken().Replace(s, m => m.Groups[1].Value + " " + Secret);
        s = Url().Replace(s, m => SecretMasker.MaskUrl(m.Value));
        s = EmailAddress().Replace(s, Email);
        s = PhoneNumber().Replace(s, Phone);
        return s;
    }

    /// <summary>Monitor target: URLs keep scheme, host and path only; Tcp/Ping hosts are kept (technical data).</summary>
    public static string MaskTarget(MonitorType type, string? target) =>
        type is MonitorType.Http or MonitorType.Keyword ? SecretMasker.MaskUrl(target) : MaskText(target);

    /// <summary>Alert channel target: webhook URLs lose their secret path; e-mail / phone / chat ids are never sent.</summary>
    public static string MaskAlertTarget(AlertChannelType type, string? target) => type switch
    {
        AlertChannelType.Webhook or AlertChannelType.Slack or AlertChannelType.Teams => SecretMasker.MaskWebhookUrl(target),
        AlertChannelType.Email => Email,
        AlertChannelType.Sms or AlertChannelType.VoiceCall => Phone,
        _ => Secret,
    };
}
