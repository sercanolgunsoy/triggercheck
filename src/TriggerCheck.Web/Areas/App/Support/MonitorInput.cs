using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Web.Areas.App.Support;

/// <summary>Form model for monitor create/edit. Never bind the entity directly (over-posting).</summary>
public class MonitorInput
{
    public static readonly string[] HttpMethods = ["GET", "HEAD", "POST"];

    [Required(ErrorMessage = "Ad zorunludur"), StringLength(200, ErrorMessage = "Ad en fazla 200 karakter olabilir")]
    [Display(Name = "Ad")]
    public string Name { get; set; } = "";

    [Display(Name = "Tip")]
    public MonitorType Type { get; set; } = MonitorType.Http;

    [Required(ErrorMessage = "Hedef zorunludur"), StringLength(2048)]
    [Display(Name = "Hedef")]
    public string Target { get; set; } = "";

    [Display(Name = "Port")]
    public int? Port { get; set; }

    [StringLength(500)]
    [Display(Name = "Anahtar kelime")]
    public string? Keyword { get; set; }

    [Display(Name = "HTTP metodu")]
    public string HttpMethod { get; set; } = "GET";

    [StringLength(100)]
    [Display(Name = "Beklenen durum kodları")]
    public string? ExpectedStatusCodes { get; set; } = "200-299";

    [Display(Name = "Kontrol aralığı")]
    public int IntervalSeconds { get; set; } = 300;

    [Range(1, 60, ErrorMessage = "Zaman aşımı 1-60 saniye arasında olmalıdır")]
    [Display(Name = "Zaman aşımı (sn)")]
    public int TimeoutSeconds { get; set; } = 10;

    [Range(1, 5, ErrorMessage = "Hata eşiği 1-5 arasında olmalıdır")]
    [Display(Name = "Hata eşiği")]
    public int FailureThreshold { get; set; } = 2;

    private static readonly Regex StatusCodesRx = new(@"^\s*\d{3}(\s*-\s*\d{3})?(\s*,\s*\d{3}(\s*-\s*\d{3})?)*\s*$", RegexOptions.Compiled);
    private static readonly Regex HostRx = new(@"^(?=.{1,253}$)([a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)(\.[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*$", RegexOptions.Compiled);

    public static MonitorInput From(MonitorEntity m) => new()
    {
        Name = m.Name, Type = m.Type, Target = m.Target, Port = m.Port, Keyword = m.Keyword,
        HttpMethod = m.HttpMethod, ExpectedStatusCodes = m.ExpectedStatusCodes,
        IntervalSeconds = m.IntervalSeconds, TimeoutSeconds = m.TimeoutSeconds, FailureThreshold = m.FailureThreshold
    };

    /// <summary>Business validation (type-specific fields + plan rules). Errors are added with the "Input." prefix.</summary>
    public void Validate(Plan plan, ModelStateDictionary ms, string prefix = "Input.")
    {
        Name = Name?.Trim() ?? "";
        Target = Target?.Trim() ?? "";
        Keyword = string.IsNullOrWhiteSpace(Keyword) ? null : Keyword;
        HttpMethod = (HttpMethod ?? "GET").Trim().ToUpperInvariant();

        if (!Enum.IsDefined(Type))
            ms.AddModelError(prefix + nameof(Type), "Geçersiz monitör tipi.");

        switch (Type)
        {
            case MonitorType.Http:
            case MonitorType.Keyword:
                if (!Uri.TryCreate(Target, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || string.IsNullOrEmpty(uri.Host))
                    ms.AddModelError(prefix + nameof(Target), "Hedef, http:// veya https:// ile başlayan geçerli bir URL olmalıdır.");
                if (!HttpMethods.Contains(HttpMethod))
                    ms.AddModelError(prefix + nameof(HttpMethod), "HTTP metodu GET, HEAD veya POST olmalıdır.");
                if (string.IsNullOrWhiteSpace(ExpectedStatusCodes))
                    ExpectedStatusCodes = "200-299";
                else if (!StatusCodesRx.IsMatch(ExpectedStatusCodes))
                    ms.AddModelError(prefix + nameof(ExpectedStatusCodes), "Beklenen durum kodları \"200-299,301\" biçiminde olmalıdır.");
                if (Type == MonitorType.Keyword && string.IsNullOrWhiteSpace(Keyword))
                    ms.AddModelError(prefix + nameof(Keyword), "Anahtar kelime monitörü için aranacak metin zorunludur.");
                break;
            case MonitorType.Tcp:
            case MonitorType.Ping:
                if (!IsHostOrIp(Target))
                    ms.AddModelError(prefix + nameof(Target), "Hedef, şema içermeyen bir alan adı veya IP adresi olmalıdır (örn. example.com).");
                if (Type == MonitorType.Tcp && (Port is null || Port < 1 || Port > 65535))
                    ms.AddModelError(prefix + nameof(Port), "TCP monitörü için 1-65535 arasında bir port zorunludur.");
                break;
        }

        if (!PlanRules.IsIntervalAllowed(plan, IntervalSeconds))
            ms.AddModelError(prefix + nameof(IntervalSeconds),
                $"Bu kontrol aralığı planınızda ({plan.Name}) kullanılamaz. İzin verilen: {string.Join(", ", PlanRules.AllowedIntervals(plan).Select(PlanRules.FormatInterval))}.");

        if (TimeoutSeconds >= IntervalSeconds)
            ms.AddModelError(prefix + nameof(TimeoutSeconds), "Zaman aşımı, kontrol aralığından kısa olmalıdır.");
    }

    public static bool IsHostOrIp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains("://") || value.Contains('/')) return false;
        return IPAddress.TryParse(value, out _) || HostRx.IsMatch(value);
    }

    /// <summary>Copies user-editable fields to the entity, clearing fields that don't apply to the type.</summary>
    public void ApplyTo(MonitorEntity m)
    {
        var isHttp = Type is MonitorType.Http or MonitorType.Keyword;
        m.Name = Name;
        m.Type = Type;
        m.Target = Target;
        m.Port = Type == MonitorType.Tcp ? Port : null;
        m.Keyword = Type == MonitorType.Keyword ? Keyword : null;
        m.HttpMethod = isHttp ? HttpMethod : "GET";
        m.ExpectedStatusCodes = isHttp ? ExpectedStatusCodes ?? "200-299" : "200-299";
        m.IntervalSeconds = IntervalSeconds;
        m.TimeoutSeconds = TimeoutSeconds;
        m.FailureThreshold = FailureThreshold;
    }
}
