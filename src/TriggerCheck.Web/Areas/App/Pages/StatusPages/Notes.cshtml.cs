using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;
using TriggerCheck.Web.StatusPages;

namespace TriggerCheck.Web.Areas.App.Pages.StatusPages;

/// <summary>
/// Public incident notes of a status page: plain-text updates the admin writes for the incidents shown on the page
/// (the incident's cause and error texts are never shown publicly).
/// </summary>
[Authorize(Policy = Policies.CustomerAdmin)]
public class NotesModel(AppDbContext db, ICurrentCustomer current, TimeProvider time, Text<AppResource> L) : PageModel
{
    public record NoteRow(int Id, DateTime CreatedAtUtc, string Body);
    public record IncidentRow(int Id, string DisplayName, DateTime StartedAtUtc, DateTime? ResolvedAtUtc, List<NoteRow> Notes);

    public StatusPage StatusPage { get; private set; } = null!;
    public List<IncidentRow> Incidents { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct) =>
        await LoadAsync(id, ct) ? Page() : NotFound();

    public async Task<IActionResult> OnPostAddAsync(int id, int incidentId, string? body, CancellationToken ct)
    {
        if (!await LoadAsync(id, ct)) return NotFound();
        var incident = Incidents.FirstOrDefault(i => i.Id == incidentId);
        if (incident is null) return NotFound();
        body = body?.Trim();
        if (string.IsNullOrEmpty(body) || body.Length > StatusPageRules.MaxNoteLength || !StatusPageRules.IsPlainText(body, multiline: true))
            TempData["Error"] = L["A note must be 1-1000 characters of plain text."].Value;
        else if (incident.Notes.Count >= StatusPageRules.MaxNotesPerIncident)
            TempData["Error"] = L["An incident can have at most {0} notes.", StatusPageRules.MaxNotesPerIncident].Value;
        else
        {
            db.StatusPageNotes.Add(new StatusPageNote { StatusPageId = id, IncidentId = incidentId, Body = body, CreatedAtUtc = time.GetUtcNow().UtcDateTime });
            await db.SaveChangesAsync(ct);
            await StatusPageSetup.EvictAsync(HttpContext.RequestServices, ct);
            TempData["Success"] = L["The note was published."].Value;
        }
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id, int noteId, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var deleted = await db.StatusPageNotes.Where(n => n.Id == noteId && n.StatusPageId == id && n.StatusPage.CustomerId == cid).ExecuteDeleteAsync(ct);
        if (deleted == 0) return NotFound();
        await StatusPageSetup.EvictAsync(HttpContext.RequestServices, ct);
        TempData["Success"] = L["The note was deleted."].Value;
        return RedirectToPage(new { id });
    }

    private async Task<bool> LoadAsync(int id, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var page = await db.StatusPages.AsNoTracking().Include(p => p.Monitors).FirstOrDefaultAsync(p => p.Id == id && p.CustomerId == cid, ct);
        if (page is null) return false;
        StatusPage = page;
        var names = page.Monitors.ToDictionary(m => m.MonitorId, m => m.DisplayName);
        var ids = names.Keys.ToList();
        var from = time.GetUtcNow().UtcDateTime.AddDays(-PublicStatusService.IncidentHistoryDays);
        var incidents = await db.Incidents.AsNoTracking()
            .Where(i => ids.Contains(i.MonitorId) && i.Monitor.CustomerId == cid && (i.StartedAtUtc >= from || i.ResolvedAtUtc == null))
            .OrderByDescending(i => i.StartedAtUtc).Take(PublicStatusService.MaxIncidents)
            .Select(i => new { i.Id, i.MonitorId, i.StartedAtUtc, i.ResolvedAtUtc }).ToListAsync(ct);
        var incidentIds = incidents.Select(i => i.Id).ToList();
        var notes = await db.StatusPageNotes.AsNoTracking().Where(n => n.StatusPageId == id && incidentIds.Contains(n.IncidentId))
            .OrderBy(n => n.CreatedAtUtc).ToListAsync(ct);
        Incidents = incidents.Select(i => new IncidentRow(i.Id, names[i.MonitorId], i.StartedAtUtc, i.ResolvedAtUtc,
            notes.Where(n => n.IncidentId == i.Id).Select(n => new NoteRow(n.Id, n.CreatedAtUtc, n.Body)).ToList())).ToList();
        return true;
    }
}
