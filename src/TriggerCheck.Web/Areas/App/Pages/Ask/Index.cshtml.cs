using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TriggerCheck.Ai;
using TriggerCheck.Ai.Client;
using TriggerCheck.Ai.Qa;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Infrastructure.Identity;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.App.Pages.Ask;

/// <summary>
/// AI-7: ask questions about the monitoring data in natural language (A0, read-only; viewers too). The conversation
/// lives in the page only: earlier questions and answers travel in hidden form fields and are not stored. Answers cite
/// records with reference tokens; they become links only after the record is loaded for the signed-in customer, so a
/// token with another tenant's id renders as "unknown record". The request waits for the answer (bounded by
/// Ai:Qa:TimeoutSeconds; async, so it does not block other requests).
/// </summary>
public class IndexModel(
    IQuestionAnswerService qa,
    AppDbContext db,
    ICurrentCustomer current,
    IAiToolClient toolClient,
    IOptions<AiOptions> options,
    UserManager<ApplicationUser> users,
    TimeProvider time,
    Text<AppResource> L) : PageModel
{
    /// <summary>Exchanges kept on the page (the model gets the newest Ai:Qa:MaxHistoryTurns of them).</summary>
    public const int MaxEntries = 10;
    public const int MaxAnswerChars = 4000;

    [BindProperty] public string? Question { get; set; }
    [BindProperty] public List<Exchange> History { get; set; } = [];

    public sealed class Exchange
    {
        public string Question { get; set; } = "";
        /// <summary>Answer text with reference tokens.</summary>
        public string Answer { get; set; } = "";
        public long DecisionId { get; set; }
        /// <summary>Numbers of the answer not found in the data (comma separated), shown as a warning.</summary>
        public string? Unverified { get; set; }
    }

    public sealed record RenderedSegment(QaSegmentKind Kind, string Text, string? Url);
    public sealed record Entry(Exchange Exchange, IReadOnlyList<RenderedSegment> Segments, IReadOnlyList<RenderedSegment> Sources, int? MyVote);

    public List<Entry> Entries { get; private set; } = [];
    public string? Error { get; private set; }
    /// <summary>Why the form is not shown (null when questions can be asked).</summary>
    public string? Unavailable { get; private set; }
    public int MaxQuestionChars => options.Value.Qa.MaxQuestionChars;
    public bool IsAdmin => User.IsInRole(Roles.CustomerAdmin);

    public async Task OnGetAsync(CancellationToken ct)
    {
        History = [];
        await LoadAsync(ct);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        ModelState.Clear();
        History = Sanitize(History);
        var userId = users.GetUserId(User);
        if (userId is null) return Forbid();

        var text = Question?.Trim() ?? "";
        var result = await qa.AskAsync(new QaQuestion(current.RequiredCustomerId, userId, text,
            History.Select(h => new QaExchange(h.Question, h.Answer)).ToList()), ct);
        if (result is { Status: QaStatus.Answered, Answer: { } answer, DecisionId: long id })
        {
            History.Add(new Exchange
            {
                Question = text, Answer = answer.Text.Length > MaxAnswerChars ? answer.Text[..MaxAnswerChars] : answer.Text, DecisionId = id,
                Unverified = answer.UngroundedNumbers.Count > 0 ? string.Join(", ", answer.UngroundedNumbers.Take(10)) : null,
            });
            History = History.TakeLast(MaxEntries).ToList();
            Question = null;
        }
        else Error = Message(result);
        await LoadAsync(ct);
        return Page();
    }

    /// <summary>Helpful / not helpful on an answer of this page (same rules as /App/Ai/Feedback; the conversation stays).</summary>
    public async Task<IActionResult> OnPostFeedbackAsync(string? vote, CancellationToken ct)
    {
        ModelState.Clear();
        History = Sanitize(History);
        var parts = (vote ?? "").Split(':');
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var decisionId)
            || !int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) || value is not (1 or -1))
            return BadRequest();
        var cid = current.RequiredCustomerId;
        var userId = users.GetUserId(User);
        var decision = await db.AiDecisions.FirstOrDefaultAsync(d => d.Id == decisionId && d.CustomerId == cid
            && d.Feature == AiFeatures.QuestionAnswer && d.Status == AiDecisionStatus.Succeeded, ct);
        if (decision is null || userId is null) return NotFound();

        var now = time.GetUtcNow().UtcDateTime;
        var row = await db.AiFeedback.FirstOrDefaultAsync(f => f.DecisionId == decisionId && f.UserId == userId, ct);
        if (row is null) db.AiFeedback.Add(row = new AiFeedback { DecisionId = decisionId, UserId = userId, CustomerId = cid });
        row.Value = value;
        row.AtUtc = now;
        decision.UserFeedback = value;
        decision.FeedbackByUserId = userId;
        decision.FeedbackAtUtc = now;
        await db.SaveChangesAsync(ct);
        await LoadAsync(ct);
        return Page();
    }

    private string Message(QaResult r) => r.Status switch
    {
        QaStatus.Unavailable => L["AI features are currently switched off for this service. Monitoring and alerts work as usual."],
        QaStatus.OptedOut => L["AI features are disabled for your account. A customer admin can enable them on the AI features page."],
        QaStatus.NotInPlan => L["Questions in natural language are not included in your plan."],
        QaStatus.OverBudget => L["The AI quota of your plan is used up. Questions can be asked again next month."],
        QaStatus.RateLimited => L["You are asking questions too quickly. Try again in {0} seconds.", r.RetryAfterSeconds],
        QaStatus.Busy => L["The assistant is busy. Try again in a moment."],
        QaStatus.InvalidQuestion => L["Enter a question of at most {0} characters.", MaxQuestionChars],
        QaStatus.Refused => L["This question could not be answered."],
        _ => L["The answer could not be generated. Try again later."],
    };

    /// <summary>Bounded, well-formed history from the form (user-controlled: it only affects the user's own questions).</summary>
    private List<Exchange> Sanitize(List<Exchange>? history) =>
        (history ?? []).Where(h => !string.IsNullOrWhiteSpace(h.Question) && !string.IsNullOrWhiteSpace(h.Answer))
            .TakeLast(MaxEntries)
            .Select(h => new Exchange
            {
                Question = Cut(h.Question.Trim(), MaxQuestionChars), Answer = Cut(h.Answer.Trim(), MaxAnswerChars), DecisionId = h.DecisionId,
                Unverified = h.Unverified is { Length: > 0 } u ? Cut(u, 200) : null,
            }).ToList();

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];

    private async Task LoadAsync(CancellationToken ct)
    {
        var customer = await current.GetCustomerAsync(ct);
        var o = options.Value;
        if (!o.Enabled || !toolClient.IsAvailable)
            Unavailable = L["AI features are currently switched off for this service. Monitoring and alerts work as usual."];
        else if (!AiFeatures.PlanAllows(customer.Plan, AiFeatures.QuestionAnswer))
            Unavailable = L["Questions in natural language are not included in your plan."];
        else if (!customer.AiEnabled)
            Unavailable = L["AI features are disabled for your account. A customer admin can enable them on the AI features page."];

        // Reference tokens → links, only for records of this customer.
        var parsed = History.Select(h => (h, QaAnswerText.Parse(h.Answer))).ToList();
        var monitorIds = parsed.SelectMany(p => p.Item2).Where(s => s.Kind == QaSegmentKind.Monitor).Select(s => s.Id!.Value).Distinct().ToList();
        var incidentIds = parsed.SelectMany(p => p.Item2).Where(s => s.Kind == QaSegmentKind.Incident).Select(s => s.Id!.Value).Distinct().ToList();
        var monitors = await db.Monitors.AsNoTracking().Where(m => m.CustomerId == customer.Id && monitorIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m.Name, ct);
        var incidents = await db.Incidents.AsNoTracking().Where(i => i.Monitor.CustomerId == customer.Id && incidentIds.Contains(i.Id))
            .Select(i => new { i.Id, i.Monitor.Name, i.StartedAtUtc }).ToDictionaryAsync(i => i.Id, ct);

        var decisionIds = History.Select(h => h.DecisionId).ToList();
        var userId = users.GetUserId(User);
        var votes = await db.AiFeedback.AsNoTracking()
            .Where(f => decisionIds.Contains(f.DecisionId) && f.UserId == userId && f.CustomerId == customer.Id)
            .ToDictionaryAsync(f => f.DecisionId, f => f.Value, ct);

        RenderedSegment Render(QaSegment s) => s.Kind switch
        {
            QaSegmentKind.Monitor when monitors.TryGetValue(s.Id!.Value, out var name) =>
                new(s.Kind, name, Url.Page("/Monitors/Details", new { area = "App", id = s.Id })),
            QaSegmentKind.Incident when incidents.TryGetValue(s.Id!.Value, out var i) =>
                new(s.Kind, L["Incident of {0} ({1})", i.Name, Fmt.Time(i.StartedAtUtc)], Url.Page("/Incidents/Details", new { area = "App", id = s.Id })),
            QaSegmentKind.Text => new(s.Kind, s.Text, null),
            _ => new(QaSegmentKind.Text, L["(unknown record)"], null),
        };
        Entries = parsed.Select(p =>
        {
            var segments = p.Item2.Select(Render).ToList();
            var sources = segments.Where(s => s.Url is not null).DistinctBy(s => s.Url).ToList();
            return new Entry(p.h, segments, sources, votes.TryGetValue(p.h.DecisionId, out var v) ? v : null);
        }).ToList();
    }
}
