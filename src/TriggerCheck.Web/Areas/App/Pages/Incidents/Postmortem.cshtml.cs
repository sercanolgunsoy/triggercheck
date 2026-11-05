using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Ai.Features.Postmortem;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;
using TriggerCheck.Web.StatusPages;

namespace TriggerCheck.Web.Areas.App.Pages.Incidents;

/// <summary>Why the AI cannot draft for this customer right now (the form still works without it).</summary>
public enum PostmortemAiState
{
    Available,
    Off,
    OptOut,
    NotInPlan,
}

/// <summary>
/// AI-8 postmortem of a resolved incident: timeline and impact (computed from the database), the likely cause (internal)
/// and the customer-facing message. A CustomerAdmin may let the AI draft the texts (A2), edit them, and approve the
/// message for publication on status pages that show the monitor; until that approval nothing is public. Without AI
/// the same form is empty and can be written by hand. Members (viewers too) can read it; only CustomerAdmins change it.
/// </summary>
public class PostmortemModel(AppDbContext db, ICurrentCustomer current, PostmortemFactsLoader loader, PostmortemDrafts drafts,
    PostmortemPublisher publisher, AiPanels ai, Text<AppResource> L) : PageModel
{
    public Incident Incident { get; private set; } = null!;
    public string MonitorName { get; private set; } = "";
    /// <summary>Null while the incident is open.</summary>
    public PostmortemFacts? Facts { get; private set; }
    public PostmortemDraft? Draft { get; private set; }
    public bool CanManage { get; private set; }
    public PostmortemAiState AiState { get; private set; }
    /// <summary>The AI decision the current texts came from, when it succeeded (feedback buttons).</summary>
    public long? DecisionId { get; private set; }
    public int? MyFeedback { get; private set; }
    public string? ApprovedBy { get; private set; }
    /// <summary>Titles of the pages the draft was published on.</summary>
    public List<string> PublishedPages { get; private set; } = [];

    public bool IsPublished => Draft?.Status == PostmortemDraftStatus.Published;

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
    {
        if (!await LoadAsync(id, ct)) return NotFound();
        return Page();
    }

    /// <summary>CustomerAdmin: (re)generate the AI texts of the draft. Replaces the current draft texts; never publishes.</summary>
    public async Task<IActionResult> OnPostGenerateAsync(int id, CancellationToken ct)
    {
        if (!User.IsInRole(Roles.CustomerAdmin)) return Forbid();
        var result = await drafts.GenerateAsync(current.RequiredCustomerId, id, UserId, ct);
        switch (result.Status)
        {
            case PostmortemGenerateStatus.NotFound: return NotFound();
            case PostmortemGenerateStatus.Generated:
                TempData["Success"] = L["The AI draft is ready. Review and edit it; nothing is published until you approve it."].Value;
                break;
            case PostmortemGenerateStatus.NotResolved:
                TempData["Error"] = L["A postmortem can be drafted once the incident is resolved."].Value;
                break;
            case PostmortemGenerateStatus.AlreadyPublished:
                TempData["Error"] = L["This postmortem was already approved and published."].Value;
                break;
            case PostmortemGenerateStatus.OverBudget:
                TempData["Error"] = L["The AI budget of this month is used up. You can write the draft yourself."].Value;
                break;
            case PostmortemGenerateStatus.Failed:
                TempData["Error"] = L["The AI draft could not be generated. You can write the draft yourself."].Value;
                break;
            default:
                TempData["Error"] = L["AI drafts are not available for your account. You can write the draft yourself."].Value;
                break;
        }
        return RedirectToPage(new { id });
    }

    /// <summary>CustomerAdmin: save the edited texts. Nothing is published.</summary>
    public async Task<IActionResult> OnPostSaveAsync(int id, string? likelyCause, string? customerMessage, CancellationToken ct)
    {
        if (!User.IsInRole(Roles.CustomerAdmin)) return Forbid();
        var result = await publisher.SaveAsync(current.RequiredCustomerId, id, UserId, likelyCause, customerMessage, ct);
        if (result == PostmortemResult.NotFound) return NotFound();
        SetMessage(result, 0);
        return RedirectToPage(new { id });
    }

    /// <summary>CustomerAdmin: save the submitted texts, approve them and publish the customer message on the chosen pages.</summary>
    public async Task<IActionResult> OnPostPublishAsync(int id, string? likelyCause, string? customerMessage, int[]? pageIds, CancellationToken ct)
    {
        if (!User.IsInRole(Roles.CustomerAdmin)) return Forbid();
        var pages = pageIds ?? [];
        var result = await publisher.PublishAsync(current.RequiredCustomerId, id, UserId, likelyCause, customerMessage, pages, ct);
        if (result == PostmortemResult.NotFound) return NotFound();
        if (result == PostmortemResult.Published) await StatusPageSetup.EvictAsync(HttpContext.RequestServices, ct);
        SetMessage(result, pages.Distinct().Count());
        return RedirectToPage(new { id });
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    private void SetMessage(PostmortemResult result, int pages)
    {
        switch (result)
        {
            case PostmortemResult.Saved: TempData["Success"] = L["The draft was saved. Nothing is published yet."].Value; break;
            case PostmortemResult.Published: TempData["Success"] = L["Approved and published on {0} status page(s).", pages].Value; break;
            case PostmortemResult.NotResolved: TempData["Error"] = L["A postmortem can be drafted once the incident is resolved."].Value; break;
            case PostmortemResult.AlreadyPublished: TempData["Error"] = L["This postmortem was already approved and published."].Value; break;
            case PostmortemResult.InvalidText:
                TempData["Error"] = L["The likely cause can have at most {0} and the customer message at most {1} characters of plain text.",
                    PostmortemDraft.MaxLikelyCauseLength, PostmortemDraft.MaxCustomerMessageLength].Value;
                break;
            case PostmortemResult.EmptyMessage: TempData["Error"] = L["Write the customer message before publishing."].Value; break;
            case PostmortemResult.NoPages: TempData["Error"] = L["Choose at least one status page to publish on. Your text was not published."].Value; break;
            case PostmortemResult.InvalidPage: TempData["Error"] = L["A chosen status page does not show this monitor. Your text was saved but not published."].Value; break;
            case PostmortemResult.TooManyNotes:
                TempData["Error"] = L["An incident can have at most {0} notes.", StatusPageRules.MaxNotesPerIncident].Value;
                break;
        }
    }

    private async Task<bool> LoadAsync(int id, CancellationToken ct)
    {
        var cid = current.RequiredCustomerId;
        var row = await db.Incidents.AsNoTracking()
            .Where(i => i.Id == id && i.Monitor.CustomerId == cid)
            .Select(i => new { Incident = i, i.Monitor.Name })
            .FirstOrDefaultAsync(ct);
        if (row is null) return false;
        Incident = row.Incident;
        MonitorName = row.Name;
        CanManage = User.IsInRole(Roles.CustomerAdmin);
        Facts = await loader.LoadAsync(cid, id, ct);
        Draft = await drafts.GetAsync(cid, id, ct);

        var customer = await current.GetCustomerAsync(ct);
        AiState = !ai.AiAvailable ? PostmortemAiState.Off
            : !customer.AiEnabled ? PostmortemAiState.OptOut
            : !AiFeatures.PlanAllows(customer.Plan, AiFeatures.PostmortemDraft) ? PostmortemAiState.NotInPlan
            : PostmortemAiState.Available;

        if (Draft?.DecisionId is long decisionId)
        {
            var ok = await db.AiDecisions.AsNoTracking().AnyAsync(d => d.Id == decisionId && d.CustomerId == cid && d.Status == AiDecisionStatus.Succeeded, ct);
            if (ok)
            {
                DecisionId = decisionId;
                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                MyFeedback = await db.AiFeedback.AsNoTracking().Where(f => f.DecisionId == decisionId && f.UserId == userId && f.CustomerId == cid)
                    .Select(f => (int?)f.Value).FirstOrDefaultAsync(ct);
            }
        }
        if (IsPublished)
        {
            ApprovedBy = await DetailsModel.AcknowledgedByAsync(db, cid, Draft!.ApprovedByUserId, L, ct);
            var ids = (Draft.PublishedStatusPageIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0).ToList();
            PublishedPages = await db.StatusPages.AsNoTracking().Where(p => ids.Contains(p.Id) && p.CustomerId == cid)
                .OrderBy(p => p.Id).Select(p => p.Title).ToListAsync(ct);
        }
        return true;
    }

    /// <summary>Localized label of an error class code (as in the AI-6 report).</summary>
    public string ErrorLabel(string code) => code switch
    {
        "http_5xx" => L["Server errors (HTTP 5xx)"].Value,
        "http_4xx" => L["Client errors (HTTP 4xx)"].Value,
        "http_status" => L["Unexpected HTTP status code"].Value,
        "timeout" => L["Timeout"].Value,
        "dns" => L["DNS error"].Value,
        "tls" => L["Certificate / TLS error"].Value,
        "connection" => L["Connection error"].Value,
        "content_check" => L["Content check failed"].Value,
        "heartbeat_or_ping" => L["Missed heartbeat / ping"].Value,
        _ => L["Other error"].Value,
    };

    public string EventLabel(PostmortemEvent e) => e.Kind switch
    {
        PostmortemEventKind.FirstFailure => L["First failing check"].Value,
        PostmortemEventKind.IncidentOpened => L["Incident opened (monitor DOWN)"].Value,
        PostmortemEventKind.RelatedIncident => L["Another monitor failed"].Value,
        PostmortemEventKind.MaintenanceStarted => L["Maintenance started"].Value,
        PostmortemEventKind.MaintenanceEnded => L["Maintenance ended"].Value,
        PostmortemEventKind.Acknowledged => L["Acknowledged by the team"].Value,
        _ => L["Resolved (monitor UP)"].Value,
    };

    public string? RelatedName(PostmortemEvent e) => Facts?.Related.FirstOrDefault(r => r.IncidentId == e.IncidentId)?.Name;
}
