using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Monitoring.Escalation;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.App.Pages.Incidents;

/// <summary>
/// Target of the acknowledge link in DOWN / escalation alerts (FEAT-5). The area requires a signed-in customer member
/// (anonymous visitors are sent to the login page and come back here). The signed token must be valid, not expired and
/// issued for the signed-in user's own customer; acknowledging still needs an admin and an explicit POST (antiforgery),
/// so the link itself never changes anything.
/// </summary>
public class AckModel(AppDbContext db, ICurrentCustomer current, IncidentAckLinks links, IncidentAcknowledger acknowledger, Text<AppResource> L) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Token { get; set; }

    /// <summary>Why the link cannot be used (null = valid).</summary>
    public string? Problem { get; private set; }
    public int IncidentId { get; private set; }
    public string MonitorName { get; private set; } = "";
    public DateTime StartedAtUtc { get; private set; }
    public bool IsResolved { get; private set; }
    public bool IsAcknowledged { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? note, CancellationToken ct)
    {
        if (!User.IsInRole(Roles.CustomerAdmin)) return Forbid();
        if (!await LoadAsync(ct)) return Page();
        var result = await acknowledger.AcknowledgeAsync(IncidentId, current.RequiredCustomerId, User.FindFirstValue(ClaimTypes.NameIdentifier)!, note, ct);
        if (result == AckResult.NotFound) return NotFound();
        DetailsModel.SetResultMessage(TempData, result, L);
        return RedirectToPage("Details", new { id = IncidentId });
    }

    /// <summary>Validates the token against the signed-in customer and loads the incident. False (with <see cref="Problem"/>) when unusable.</summary>
    private async Task<bool> LoadAsync(CancellationToken ct)
    {
        var status = links.Validate(Token, out var token);
        if (status == AckTokenStatus.Expired)
        {
            Problem = L["This acknowledge link has expired. Open the incident from the incidents page instead."].Value;
            return false;
        }
        // Another tenant's link is reported like a broken one: nothing about that incident is revealed.
        var incident = status == AckTokenStatus.Valid && token!.CustomerId == current.RequiredCustomerId
            ? await db.Incidents.AsNoTracking()
                .Where(i => i.Id == token.IncidentId && i.Monitor.CustomerId == token.CustomerId)
                .Select(i => new { i.Id, i.Monitor.Name, i.StartedAtUtc, i.ResolvedAtUtc, i.AcknowledgedAtUtc })
                .FirstOrDefaultAsync(ct)
            : null;
        if (incident is null)
        {
            Problem = L["This acknowledge link is not valid for your account."].Value;
            return false;
        }
        IncidentId = incident.Id;
        MonitorName = incident.Name;
        StartedAtUtc = incident.StartedAtUtc;
        IsResolved = incident.ResolvedAtUtc is not null;
        IsAcknowledged = incident.AcknowledgedAtUtc is not null;
        return true;
    }
}
