using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Maintenance;
using TriggerCheck.Web.Areas.App.Pages.Maintenance;
using TriggerCheck.Web.StatusPages;

namespace TriggerCheck.Web.Areas.App.Pages.StatusPages;

/// <summary>One monitor row of the status page form (panel: every monitor with a checkbox; API: the selected ones).</summary>
public class StatusPageMonitorInput
{
    public int MonitorId { get; set; }
    /// <summary>Panel only: the row's checkbox. The API sends only selected monitors (defaults to true).</summary>
    public bool Selected { get; set; } = true;
    public string? DisplayName { get; set; }
}

/// <summary>Create / edit model of a status page (FEAT-4), also the request body of the REST API.</summary>
public class StatusPageInput
{
    [Required(ErrorMessage = "Title is required."), StringLength(StatusPageRules.MaxTitleLength, ErrorMessage = "The title can be at most 100 characters.")]
    [Display(Name = "Title")]
    public string Title { get; set; } = "";

    [Required(ErrorMessage = "Address is required."), StringLength(StatusPageRules.MaxSlugLength, ErrorMessage = "The address can be at most 50 characters.")]
    [Display(Name = "Address")]
    public string Slug { get; set; } = "";

    [StringLength(StatusPageRules.MaxAnnouncementLength, ErrorMessage = "The announcement can be at most 2000 characters.")]
    [Display(Name = "Announcement")]
    public string? Announcement { get; set; }

    [Display(Name = "Visibility")]
    public StatusPageVisibility Visibility { get; set; } = StatusPageVisibility.Public;

    [Display(Name = "Time zone")]
    public string TimeZoneId { get; set; } = "UTC";

    public List<StatusPageMonitorInput> Monitors { get; set; } = [];

    public static StatusPageInput From(StatusPage p) => new()
    {
        Title = p.Title, Slug = p.Slug, Announcement = p.Announcement, Visibility = p.Visibility, TimeZoneId = p.TimeZoneId,
        Monitors = p.Monitors.OrderBy(m => m.SortOrder).Select(m => new StatusPageMonitorInput { MonitorId = m.MonitorId, DisplayName = m.DisplayName }).ToList()
    };

    /// <summary>
    /// Business validation (slug format / reserved words / uniqueness, time zone, plain text, monitors of this customer).
    /// Returns the selected monitors (id, trimmed display name) in form order.
    /// </summary>
    public async Task<List<(int MonitorId, string DisplayName)>> ValidateAsync(ModelStateDictionary ms, IStringLocalizer L, AppDbContext db,
        int customerId, int? existingId, CancellationToken ct, string prefix = "Input.")
    {
        Title = Title?.Trim() ?? "";
        Slug = StatusPageRules.NormalizeSlug(Slug);
        Announcement = string.IsNullOrWhiteSpace(Announcement) ? null : Announcement.Trim();
        TimeZoneId = string.IsNullOrWhiteSpace(TimeZoneId) ? "UTC" : TimeZoneId.Trim();

        if (!StatusPageRules.IsPlainText(Title, multiline: false))
            ms.AddModelError(prefix + nameof(Title), L["The title contains invalid characters."]);
        if (!StatusPageRules.IsPlainText(Announcement, multiline: true))
            ms.AddModelError(prefix + nameof(Announcement), L["The announcement contains invalid characters."]);
        if (!Enum.IsDefined(Visibility))
            ms.AddModelError(prefix + nameof(Visibility), L["Invalid visibility."]);
        if (MaintenanceSchedule.FindTimeZone(TimeZoneId) is null)
            ms.AddModelError(prefix + nameof(TimeZoneId), L["Unknown time zone."]);

        if (Slug.Length > 0)
        {
            if (!StatusPageRules.IsValidSlug(Slug))
                ms.AddModelError(prefix + nameof(Slug), L["Use 3-50 lower-case letters, digits and hyphens (not at the start or end). Some words are reserved."]);
            else if (await db.StatusPages.AnyAsync(p => p.Slug == Slug && p.Id != existingId, ct))
                ms.AddModelError(prefix + nameof(Slug), L["This address is already taken."]);
        }

        var selected = (Monitors ?? []).Where(m => m.Selected).ToList();
        var result = new List<(int, string)>();
        if (selected.Count == 0)
            ms.AddModelError(prefix + nameof(Monitors), L["Select at least one monitor."]);
        else if (selected.Count > StatusPageRules.MaxMonitorsPerPage)
            ms.AddModelError(prefix + nameof(Monitors), L["A status page can show at most {0} monitors.", StatusPageRules.MaxMonitorsPerPage]);
        else
        {
            var ids = selected.Select(m => m.MonitorId).ToList();
            var own = await db.Monitors.Where(m => m.CustomerId == customerId && ids.Contains(m.Id))
                .Select(m => m.Id).ToListAsync(ct);
            if (ids.Distinct().Count() != ids.Count || ids.Any(id => !own.Contains(id)))
                ms.AddModelError(prefix + nameof(Monitors), L["Unknown monitor."]);
            foreach (var m in selected)
            {
                var name = m.DisplayName?.Trim() ?? "";
                if (name.Length == 0 || name.Length > StatusPageRules.MaxDisplayNameLength || !StatusPageRules.IsPlainText(name, multiline: false))
                {
                    ms.AddModelError(prefix + nameof(Monitors), L["Every selected monitor needs a public display name (at most 100 characters)."]);
                    break;
                }
                result.Add((m.MonitorId, name));
            }
        }
        return result;
    }

    /// <summary>Copies the validated values; replaces the monitor list.</summary>
    public void ApplyTo(StatusPage page, List<(int MonitorId, string DisplayName)> monitors)
    {
        page.Title = Title;
        page.Slug = Slug;
        page.Announcement = Announcement;
        page.Visibility = Visibility;
        page.TimeZoneId = TimeZoneId;
        var existing = page.Monitors.ToDictionary(m => m.MonitorId);
        foreach (var gone in page.Monitors.Where(m => !monitors.Any(x => x.MonitorId == m.MonitorId)).ToList())
            page.Monitors.Remove(gone);
        for (var i = 0; i < monitors.Count; i++)
        {
            var (id, name) = monitors[i];
            if (!existing.TryGetValue(id, out var row))
                page.Monitors.Add(row = new StatusPageMonitor { MonitorId = id });
            row.DisplayName = name;
            row.SortOrder = i;
        }
    }

    public static IReadOnlyList<string> TimeZoneIds => MaintenanceInput.TimeZoneIds;
}
