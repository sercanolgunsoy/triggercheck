using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using TriggerCheck.Domain.Entities;

namespace TriggerCheck.Web.Areas.App.Support;

/// <summary>Form model for alert channel create/edit (no entity binding).</summary>
public class AlertChannelInput
{
    [Required(ErrorMessage = "Ad zorunludur"), StringLength(200, ErrorMessage = "Ad en fazla 200 karakter olabilir")]
    [Display(Name = "Ad")]
    public string Name { get; set; } = "";

    [Display(Name = "Tip")]
    public AlertChannelType Type { get; set; } = AlertChannelType.Email;

    [Required(ErrorMessage = "Hedef zorunludur"), StringLength(2048)]
    [Display(Name = "Hedef")]
    public string Target { get; set; } = "";

    [Display(Name = "Etkin")]
    public bool IsEnabled { get; set; } = true;

    public static AlertChannelInput From(AlertChannel c) => new() { Name = c.Name, Type = c.Type, Target = c.Target, IsEnabled = c.IsEnabled };

    public static string TypeName(AlertChannelType t) => t switch
    {
        AlertChannelType.Email => "E-posta",
        AlertChannelType.Webhook => "Webhook",
        _ => t.ToString()
    };

    public void Validate(ModelStateDictionary ms, string prefix = "Input.")
    {
        Name = Name?.Trim() ?? "";
        Target = Target?.Trim() ?? "";
        switch (Type)
        {
            case AlertChannelType.Email:
                if (!IsValidEmail(Target))
                    ms.AddModelError(prefix + nameof(Target), "Geçerli bir e-posta adresi girin.");
                break;
            case AlertChannelType.Webhook:
                if (!Uri.TryCreate(Target, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) || string.IsNullOrEmpty(u.Host))
                    ms.AddModelError(prefix + nameof(Target), "Webhook adresi http:// veya https:// ile başlayan geçerli bir URL olmalıdır.");
                break;
            default:
                ms.AddModelError(prefix + nameof(Type), "Geçersiz kanal tipi.");
                break;
        }
    }

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
