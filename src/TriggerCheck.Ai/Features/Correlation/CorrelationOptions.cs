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

    /// <summary>
    /// A3: a folded member that is still down this many minutes after it was folded gets its own DOWN alert (the fold
    /// is only meant for the first minutes of a shared outage).
    /// </summary>
    public int MaxFoldMinutes { get; set; } = 15;
    /// <summary>
    /// A3: the "also affected" follow-up of a group waits this long after the first not yet reported fold, so members
    /// folded close together go out in one message.
    /// </summary>
    public int FollowUpDelaySeconds { get; set; } = 60;
    /// <summary>
    /// A3: how long the alert path waits for the database write lock before it gives up and sends the alert on its own.
    /// </summary>
    public int AlertPathLockTimeoutSeconds { get; set; } = 2;

    public TimeSpan Window => TimeSpan.FromMinutes(Math.Clamp(WindowMinutes, 1, 60));
    public TimeSpan MaxFold => TimeSpan.FromMinutes(Math.Clamp(MaxFoldMinutes, 1, 24 * 60));
    public TimeSpan FollowUpDelay => TimeSpan.FromSeconds(Math.Clamp(FollowUpDelaySeconds, 0, 3600));
}

/// <summary>
/// Per-process hint for the alert path (singleton): whether automatic groups may exist, so an UP alert at A1 needs no
/// database query. Starts true (unknown) and is refreshed by <see cref="CorrelationWatcher"/> on every instance each
/// poll; set again whenever this process creates an automatic group. Leftover groups are undone by the watcher.
/// </summary>
public sealed class CorrelationRuntimeState
{
    private volatile bool _autoGroupsMayExist = true;

    public bool AutoGroupsMayExist
    {
        get => _autoGroupsMayExist;
        set => _autoGroupsMayExist = value;
    }
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
