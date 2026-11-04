using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Domain.Security;
using TriggerCheck.Monitoring;
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

    [Display(Name = "Enabled")]
    public bool IsEnabled { get; set; } = true;

    public static AlertChannelInput From(AlertChannel c) => new() { Name = c.Name, Type = c.Type, Target = c.Target, IsEnabled = c.IsEnabled };

    public static string TypeName(AlertChannelType t, IStringLocalizer L) => t switch
    {
        AlertChannelType.Email => L["E-mail"],
        AlertChannelType.Webhook => L["Webhook"],
        _ => t.ToString()
    };

    public void Validate(ModelStateDictionary ms, IStringLocalizer L, string prefix = "Input.")
    {
        Name = Name?.Trim() ?? "";
        Target = Target?.Trim() ?? "";
        switch (Type)
        {
            case AlertChannelType.Email:
                if (!IsValidEmail(Target))
                    ms.AddModelError(prefix + nameof(Target), L["Enter a valid e-mail address."]);
                break;
            case AlertChannelType.Webhook:
                if (!Uri.TryCreate(Target, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) || string.IsNullOrEmpty(u.Host))
                    ms.AddModelError(prefix + nameof(Target), L["The webhook address must be a valid URL starting with http:// or https://."]);
                break;
            default:
                ms.AddModelError(prefix + nameof(Type), L["Invalid channel type."]);
                break;
        }
    }

    /// <summary>
    /// Friendly SSRF pre-check for webhook URLs (literal private IPs, "localhost"...). The real guard is the webhook
    /// client's connect-time check (TargetGuard). Skipped when Monitoring:AllowPrivateTargets is set.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (Type != AlertChannelType.Webhook) yield break;
        if (context.GetService(typeof(IOptions<MonitoringOptions>)) is IOptions<MonitoringOptions> { Value.AllowPrivateTargets: true }) yield break;
        if (!Uri.TryCreate(Target?.Trim(), UriKind.Absolute, out var uri) || !TargetGuard.IsObviouslyInternalHost(uri.Host)) yield break;

        var L = (Text<AppResource>)context.GetService(typeof(Text<AppResource>))!;
        yield return new ValidationResult(L["The webhook address points to a private or internal network and is not allowed."], [nameof(Target)]);
    }

    /// <summary>Target as shown in listings: webhook URLs carry a secret, so only scheme + host are displayed.</summary>
    public static string DisplayTarget(AlertChannel c) =>
        c.Type == AlertChannelType.Webhook ? SecretMasker.MaskWebhookUrl(c.Target) : c.Target;

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

    public void ApplyTo(AlertChannel c)
    {
        c.Name = Name;
        c.Type = Type;
        c.Target = Target;
        c.IsEnabled = IsEnabled;
    }
}
