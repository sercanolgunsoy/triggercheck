using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.RateLimiting;
using TriggerCheck.Monitoring.Maintenance;
using TriggerCheck.Web.StatusPages;

namespace TriggerCheck.Web.Pages.Status;

/// <summary>Public status page (FEAT-4). Anonymous, output-cached, rate-limited, script-free and cookie-free.</summary>
[AllowAnonymous]
[OutputCache(PolicyName = StatusPageSetup.CachePolicy)]
[EnableRateLimiting(StatusPageSetup.ViewPolicy)]
public class IndexModel(PublicStatusService service) : PageModel
{
    public StatusPageView View { get; private set; } = null!;
    public string Base { get; private set; } = "";
    public string JsonUrl { get; private set; } = "";
    private TimeZoneInfo _tz = TimeZoneInfo.Utc;

    public async Task<IActionResult> OnGetAsync(string slug, CancellationToken ct)
    {
        var view = await service.GetAsync(slug, ct);
        if (view is null) return NotFound();
        View = view;
        Base = CustomDomainMiddleware.BasePath(HttpContext, view.Slug);
        JsonUrl = Base.Length == 0 ? "/index.json" : Base + ".json";
        _tz = MaintenanceSchedule.FindTimeZone(view.Page.TimeZoneId) ?? TimeZoneInfo.Utc;
        StatusPageSetup.SetPublicHeaders(HttpContext, view);
        return Page();
    }

    /// <summary>Time in the page's time zone, "yyyy-MM-dd HH:mm".</summary>
    public string Local(DateTime utc) => MaintenanceSchedule.UtcToLocal(utc, _tz).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>CSS class of a daily bar.</summary>
    public static string BarClass(double? uptime) => uptime switch
    {
        null => "none",
        >= 100 => "",
        >= 99 => "warn",
        _ => "down"
    };
}
