using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Web.Areas.App.Support;

namespace TriggerCheck.Web.Areas.App.Pages.Monitors;

/// <summary>Shared state for the Create/Edit monitor form (rendered by _MonitorForm.cshtml).</summary>
public abstract class MonitorFormPageModel : PageModel
{
    [BindProperty] public MonitorInput Input { get; set; } = new();

    public Plan Plan { get; protected set; } = null!;
    public IReadOnlyList<int> AllowedIntervals => PlanRules.AllowedIntervals(Plan);
    public IReadOnlyList<int> AllowedHeartbeatIntervals => PlanRules.AllowedHeartbeatIntervals(Plan);
}
