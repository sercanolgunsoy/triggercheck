using Microsoft.Extensions.Options;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;

namespace TriggerCheck.Ai.Features.Correlation;

/// <summary>"Ai:Correlation": AI-4 alert noise reduction / incident correlation.</summary>
public sealed class AiCorrelationOptions
{
    public const string Section = "Ai:Correlation";

    /// <summary>Incidents are only grouped when they started at most this many minutes after the group's primary.</summary>
    public int WindowMinutes { get; set; } = 5;
    /// <summary>How often the watcher looks for new groups to propose (A1) and settles automatic groups (A3).</summary>
    public int PollSeconds { get; set; } = 30;
    /// <summary>Incidents that started up to this long ago are considered for proposals (also after a restart).</summary>
    public int LookbackMinutes { get; set; } = 60;
    /// <summary>A proposal nobody decided on within this many hours expires (not counted in the acceptance rate).</summary>
    public int ProposalExpiryHours { get; set; } = 24;
    /// <summary>Acceptance rate period (decision K3: four weeks of A1 before considering A3).</summary>
    public int AcceptanceWindowDays { get; set; } = 28;
    /// <summary>
    /// Operator cap on the autonomy of AI-4. It can only lower the feature's level in the catalog
    /// (<see cref="AiFeatures.All"/>, A1 today), never raise it: A0 switches proposals off, A1 proposes, A3 groups
    /// notifications automatically once the catalog allows it.
    /// </summary>
    public AiAutonomy MaxAutonomy { get; set; } = AiAutonomy.A3;

    public TimeSpan Window => TimeSpan.FromMinutes(Math.Clamp(WindowMinutes, 1, 60));
}

/// <summary>
/// Effective autonomy of AI-4: the lower of the catalog level (<see cref="AiFeatures.All"/>; raised only after the
/// measured acceptance rate, decision K3) and the operator cap. Tests replace <see cref="CatalogMax"/> to exercise A3.
/// </summary>
public class CorrelationAutonomy(IOptions<AiCorrelationOptions> options)
{
    public virtual AiAutonomy CatalogMax => AiFeatures.Find(AiFeatures.CorrelationGrouping)!.MaxAutonomy;

    public AiAutonomy Effective
    {
        get
        {
            var cap = options.Value.MaxAutonomy;
            return CatalogMax < cap ? CatalogMax : cap;
        }
    }

    /// <summary>A1 or more: incidents are grouped at all (proposals).</summary>
    public bool Enabled => Effective >= AiAutonomy.A1;

    /// <summary>A3: notifications are grouped automatically (customer can switch it off; reversible).</summary>
    public bool Automatic => Effective >= AiAutonomy.A3;
}
