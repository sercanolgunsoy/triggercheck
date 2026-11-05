using TriggerCheck.Domain.Entities;

namespace TriggerCheck.Domain;

/// <summary>FEAT-6: plan rules of multi-region checks.</summary>
public static class RegionRules
{
    /// <summary>Code of the implicit central region (the main app's own scheduler).</summary>
    public const string CentralCode = "central";

    /// <summary>
    /// How many remote regions a monitor of a customer on <paramref name="plan"/> may use (central always counts as one),
    /// capped by <paramref name="availableRegions"/>. <see cref="Plan.MaxRegions"/> 0 means all regions.
    /// </summary>
    public static int MaxRemoteRegions(Plan plan, int availableRegions) =>
        plan.MaxRegions <= 0 ? availableRegions : Math.Clamp(plan.MaxRegions - 1, 0, availableRegions);

    /// <summary>Heartbeat monitors are push based and therefore region independent.</summary>
    public static bool SupportsRegions(MonitorType type) => type != MonitorType.Heartbeat;
}
