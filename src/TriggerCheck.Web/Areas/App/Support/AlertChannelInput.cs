using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Domain.Security;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring;
using TriggerCheck.Monitoring.Alerting;
using TriggerCheck.Monitoring.Security;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.App.Support;

/// <summary>Form model for alert channel create/edit (no entity binding).</summary>
public class AlertChannelInput : IValidatableObject
{
    [Required(ErrorMessage = "Name is required."), StringLength(200, ErrorMessage = "Name can be at most 200 characters.")]
    [Display(Name = "Name")]
    public string Name { get; set; } = "";

    [Display(Name = "Type")]
    public AlertChannelType Type { get; set; } = AlertChannelType.Email;

    [Required(ErrorMessage = "Target is required."), StringLength(2048, ErrorMessage = "Target can be at most 2048 characters.")]
    [Display(Name = "Target")]
    public string Target { get; set; } = "";

    /// <summary>Telegram bot token. Write-only: never rendered back; empty on edit keeps the stored token.</summary>
    [StringLength(200, ErrorMessage = "The bot token can be at most 200 characters.")]
    [Display(Name = "Bot token")]
    public string? Secret { get; set; }

    [Display(Name = "Enabled")]
    public bool IsEnabled { get; set; } = true;

    public static AlertChannelInput From(AlertChannel c) => new() { Name = c.Name, Type = c.Type, Target = c.Target, IsEnabled = c.IsEnabled };

    public static string TypeName(AlertChannelType t, IStringLocalizer L) => t switch
    {
        AlertChannelType.Email => L["E-mail"],
        AlertChannelType.Webhook => L["Webhook"],
        AlertChannelType.Slack => L["Slack"],
        AlertChannelType.Teams => L["Microsoft Teams"],
        AlertChannelType.Telegram => L["Telegram"],
        AlertChannelType.Sms => L["SMS"],
        AlertChannelType.VoiceCall => L["Voice call"],
        _ => t.ToString()
    };

    /// <summary>
    /// Channel types offered in the form. SMS / voice need Twilio to be configured by the operator AND a plan that allows
    /// them; <paramref name="current"/> (the type being edited) is always kept so an existing channel stays editable.
    /// </summary>
    public static IEnumerable<AlertChannelType> AvailableTypes(bool smsVoiceAvailable, AlertChannelType? current = null) =>
        Enum.GetValues<AlertChannelType>().Where(t => smsVoiceAvailable || !AlertDispatcher.IsSmsOrVoice(t) || t == current);

    /// <summary>SMS / voice can be offered: the operator configured Twilio and the customer's plan includes them.</summary>
    public static async Task<bool> SmsVoiceAvailableAsync(AppDbContext db, int customerId, TwilioOptions twilio, CancellationToken ct) =>
        twilio.IsConfigured && await db.Customers.AnyAsync(c => c.Id == customerId && c.Plan.AllowsSmsVoice, ct);

    public static bool UsesUrl(AlertChannelType t) => t is AlertChannelType.Webhook or AlertChannelType.Slack or AlertChannelType.Teams;

    /// <param name="smsVoiceAvailable">Twilio configured and the customer's plan allows SMS / voice.</param>
    /// <param name="hasStoredSecret">Editing a Telegram channel that already has a bot token.</param>
    public void Validate(ModelStateDictionary ms, IStringLocalizer L, bool smsVoiceAvailable = false, bool hasStoredSecret = false, string prefix = "Input.")
    {
        Name = Name?.Trim() ?? "";
        Target = Target?.Trim() ?? "";
        Secret = string.IsNullOrWhiteSpace(Secret) ? null : Secret.Trim();
        var target = prefix + nameof(Target);
        switch (Type)
        {
            case AlertChannelType.Email:
                if (!IsValidEmail(Target))
                    ms.AddModelError(target, L["Enter a valid e-mail address."]);
                break;
            case AlertChannelType.Webhook:
                if (!IsHttpUrl(Target, httpsOnly: false, out _))
                    ms.AddModelError(target, L["The webhook address must be a valid URL starting with http:// or https://."]);
                break;
            case AlertChannelType.Slack:
                if (!IsHttpUrl(Target, httpsOnly: true, out var slack) || !IsSlackHost(slack!.Host))
                    ms.AddModelError(target, L["Enter the Slack incoming webhook URL (https://hooks.slack.com/services/...)."]);
                break;
            case AlertChannelType.Teams:
                if (!IsHttpUrl(Target, httpsOnly: true, out _))
                    ms.AddModelError(target, L["Enter the Teams workflow webhook URL (it starts with https://)."]);
                break;
            case AlertChannelType.Telegram:
                if (!TelegramNotifier.ChatIdRegex().IsMatch(Target))
                    ms.AddModelError(target, L["Enter a numeric Telegram chat id (e.g. -1001234567890) or a @channelname."]);
                if (Secret is null ? !hasStoredSecret : !TelegramNotifier.BotTokenRegex().IsMatch(Secret))
                    ms.AddModelError(prefix + nameof(Secret), L["Enter the bot token from @BotFather (e.g. 123456789:AA...)."]);
                break;
            case AlertChannelType.Sms:
            case AlertChannelType.VoiceCall:
                if (!smsVoiceAvailable)
                    ms.AddModelError(prefix + nameof(Type), L["SMS and voice call alerts are not available for your plan."]);
                else if (!PhoneNumber.IsE164(Target))
                    ms.AddModelError(target, L["Enter the phone number in international E.164 format, e.g. +905551234567."]);
                break;
            default:
                ms.AddModelError(prefix + nameof(Type), L["Invalid channel type."]);
                break;
        }
    }

    /// <summary>
    /// Friendly SSRF pre-check for URL channels (literal private IPs, "localhost"...). The real guard is the webhook
    /// client's connect-time check (TargetGuard). Skipped when Monitoring:AllowPrivateTargets is set.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (!UsesUrl(Type)) yield break;
        if (context.GetService(typeof(IOptions<MonitoringOptions>)) is IOptions<MonitoringOptions> { Value.AllowPrivateTargets: true }) yield break;
        if (!Uri.TryCreate(Target?.Trim(), UriKind.Absolute, out var uri) || !TargetGuard.IsObviouslyInternalHost(uri.Host)) yield break;

        var L = (Text<AppResource>)context.GetService(typeof(Text<AppResource>))!;
        yield return new ValidationResult(L["The webhook address points to a private or internal network and is not allowed."], [nameof(Target)]);
    }

    /// <summary>
    /// Target as shown in listings: webhook-style URLs carry a secret, so only scheme + host are displayed. The Telegram
    /// bot token (<see cref="AlertChannel.Secret"/>) is never displayed; the chat id is.
    /// </summary>
    public static string DisplayTarget(AlertChannel c) =>
        UsesUrl(c.Type) ? SecretMasker.MaskWebhookUrl(c.Target) : c.Target;

    public static bool IsValidEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains(' ')) return false;
        if (!new EmailAddressAttribute().IsValid(value)) return false;
        try
        {
            var addr = new System.Net.Mail.MailAddress(value);
            return addr.Address == value && addr.Host.Contains('.');
        }
        catch (FormatException) { return false; }
    }

    private static bool IsHttpUrl(string value, bool httpsOnly, out Uri? uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (!httpsOnly && uri.Scheme == Uri.UriSchemeHttp))
        && !string.IsNullOrEmpty(uri.Host);

    private static bool IsSlackHost(string host) =>
        host.Equals("hooks.slack.com", StringComparison.OrdinalIgnoreCase) || host.Equals("hooks.slack-gov.com", StringComparison.OrdinalIgnoreCase);

    public void ApplyTo(AlertChannel c)
    {
        c.Name = Name;
        c.Type = Type;
        c.Target = Target;
        c.IsEnabled = IsEnabled;
        if (Type != AlertChannelType.Telegram) c.Secret = null;
        else if (Secret is not null) c.Secret = Secret; // empty keeps the stored token
    }
}
