using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Escalation;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.App.Pages.Incidents;

/// <summary>One incident with its acknowledgement and escalation state (FEAT-5). Acknowledging is CustomerAdmin only.</summary>
public class DetailsModel(AppDbContext db, ICurrentCustomer current, IncidentAcknowledger acknowledger, Text<AppResource> L) : PageModel
{
    public Incident Incident { get; private set; } = null!;
    public string MonitorName { get; private set; } = "";
    /// <summary>Name or e-mail of the user who acknowledged it (null when unknown / deleted).</summary>
    public string? AcknowledgedBy { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var row = await db.Incidents.AsNoTracking()
            .Where(i => i.Id == id && i.Monitor.CustomerId == cid)
            .Select(i => new { Incident = i, i.Monitor.Name })
            .FirstOrDefaultAsync(ct);
        if (row is null) return NotFound();
        Incident = row.Incident;
        MonitorName = row.Name;
        AcknowledgedBy = await UserNameAsync(db, cid, Incident.AcknowledgedByUserId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAcknowledgeAsync(int id, string? note, string? returnTo, CancellationToken ct)
    {
        if (!User.IsInRole(Roles.CustomerAdmin)) return Forbid();
        var result = await acknowledger.AcknowledgeAsync(id, current.RequiredCustomerId, User.FindFirstValue(ClaimTypes.NameIdentifier)!, note, ct);
        if (result == AckResult.NotFound) return NotFound();
        SetResultMessage(TempData, result, L);
        return returnTo == "list" ? RedirectToPage("Index") : RedirectToPage("Details", new { id });
    }

    public static void SetResultMessage(ITempDataDictionary tempData, AckResult result, IStringLocalizer L)
    {
        switch (result)
        {
            case AckResult.Acknowledged: tempData["Success"] = L["The incident was acknowledged. No further escalation steps are sent for it."].Value; break;
            case AckResult.AlreadyAcknowledged: tempData["Error"] = L["The incident was already acknowledged."].Value; break;
            case AckResult.Resolved: tempData["Error"] = L["The incident is already resolved; resolved incidents cannot be acknowledged."].Value; break;
        }
    }

    public static async Task<string?> UserNameAsync(AppDbContext db, int customerId, string? userId, CancellationToken ct)
    {
        if (userId is null) return null;
        var u = await db.Users.AsNoTracking().Where(x => x.Id == userId && x.CustomerId == customerId)
            .Select(x => new { x.FullName, x.Email }).FirstOrDefaultAsync(ct);
        return u is null ? null : string.IsNullOrWhiteSpace(u.FullName) ? u.Email : u.FullName;
    }
}
