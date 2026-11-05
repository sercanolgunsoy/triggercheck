using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Ai.Insights;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Web.Infrastructure;
using TriggerCheck.Web.Localization;

namespace TriggerCheck.Web.Areas.SuperAdmin.Pages.Insights;

/// <summary>
/// AI-12 platform insights (SuperAdmin only, A1: nothing is sent to customers, nothing changes). The three signal
/// tables (noise, plan-limit pressure, abandoned) are computed by the rules on every request and work without AI; the
/// optional AI summary is generated on request and only describes those signals (placeholders and record labels the
/// server resolves; it is billed to the platform budget, never to a customer).
/// </summary>
[Authorize(Policy = Policies.SuperAdmin)]
public class IndexModel(PlatformInsights insights, InsightSummaryService summaries, AppDbContext db, Text<AdminResource> L) : PageModel
{
    public InsightReport Report { get; private set; } = null!;
    public AiInsightOptions Options => insights.Options;
    public bool AiAvailable => summaries.IsAvailable;
    public InsightSummaryState State { get; private set; } = null!;
    public InsightPlatformUsage Usage { get; private set; } = null!;
    /// <summary>The signals changed since the shown summary was generated.</summary>
    public bool SummaryStale { get; private set; }
    public Dictionary<int, string> CustomerNames { get; private set; } = new();
    public Dictionary<int, (string Name, int CustomerId)> MonitorNames { get; private set; } = new();

    public static string Language => Fmt.IsTurkish ? "tr" : "en";

    public async Task OnGetAsync(CancellationToken ct)
    {
        Report = await insights.ComputeAsync(ct);
        State = await summaries.GetStateAsync(ct);
        Usage = await summaries.GetUsageAsync(ct);
        if (State.Summary is { } s)
        {
            SummaryStale = InsightSummaryPrompt.Build(Report, Options.MaxSummaryCustomers, s.Language).SignalsHash != s.SignalsHash;
            var customerIds = s.Labels.Values.Where(l => l.Kind == InsightLabel.Customer).Select(l => l.Id).ToList();
            var monitorIds = s.Labels.Values.Where(l => l.Kind == InsightLabel.Monitor).Select(l => l.Id).ToList();
            CustomerNames = await db.Customers.AsNoTracking().Where(c => customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
            MonitorNames = (await db.Monitors.AsNoTracking().Where(m => monitorIds.Contains(m.Id)).Select(m => new { m.Id, m.Name, m.CustomerId }).ToListAsync(ct))
                .ToDictionary(m => m.Id, m => (m.Name, m.CustomerId));
        }
    }

    public async Task<IActionResult> OnPostSummarizeAsync(CancellationToken ct)
    {
        var result = await summaries.GenerateAsync(Language, ct);
        var (ok, message) = result.Status switch
        {
            InsightSummaryStatus.Generated => (true, L["The AI summary was generated."].Value),
            InsightSummaryStatus.Reused => (true, L["The signals have not changed; the last AI summary is still current."].Value),
            InsightSummaryStatus.NoSignals => (true, L["There are no signals to summarize."].Value),
            InsightSummaryStatus.Disabled => (false, L["AI is switched off; the signals below are computed without it."].Value),
            InsightSummaryStatus.TooSoon => (false, L["An AI summary was generated a moment ago. Try again in a few minutes."].Value),
            InsightSummaryStatus.Budget => (false, L["The platform AI budget for insight summaries is used up this month."].Value),
            _ => (false, L["The AI summary could not be generated."].Value),
        };
        TempData[ok ? "Success" : "Error"] = message;
        return RedirectToPage();
    }

    public string LimitName(PlanLimitKind kind) => kind switch
    {
        PlanLimitKind.Monitors => L["Monitors"],
        PlanLimitKind.FastestInterval => L["Monitors at the plan's fastest interval"],
        PlanLimitKind.StatusPages => L["Status pages"],
        PlanLimitKind.Regions => L["Regions per monitor"],
        PlanLimitKind.SmsVoice => L["SMS / voice per day (busiest day)"],
        _ => L["AI quota"],
    };

    /// <summary>
    /// A summary text with its placeholders rendered by the server and its [[C1]] / [[M1]] labels turned into links with
    /// the record's current name (HTML-encoded). A label whose record no longer exists becomes "unknown customer / monitor".
    /// </summary>
    public IHtmlContent RenderText(InsightSummaryOutput summary, string text)
    {
        var html = new HtmlContentBuilder();
        foreach (var seg in summary.Render(text))
        {
            if (seg.Ref is null)
            {
                html.Append(seg.Text ?? "");
                continue;
            }
            if (seg.Ref.Kind == InsightLabel.Customer && CustomerNames.TryGetValue(seg.Ref.Id, out var customer))
                html.AppendHtml("<a class=\"insight-ref\" href=\"").Append(Url.Page("/Customers/Details", new { id = seg.Ref.Id }) ?? "")
                    .AppendHtml("\">").Append(customer).AppendHtml("</a>");
            else if (seg.Ref.Kind == InsightLabel.Monitor && MonitorNames.TryGetValue(seg.Ref.Id, out var monitor))
                html.AppendHtml("<a class=\"insight-ref\" href=\"").Append(Url.Page("/Monitors/Index", new { CustomerId = monitor.CustomerId, Q = monitor.Name }) ?? "")
                    .AppendHtml("\">").Append(monitor.Name).AppendHtml("</a>");
            else
                html.Append(seg.Ref.Kind == InsightLabel.Customer ? L["unknown customer"] : L["unknown monitor"]);
        }
        return html;
    }
}
