namespace TriggerCheck.Monitoring.Alerting;

/// <summary>
/// AI-4 hook on the alert path (A3, automatic notification grouping). Asked by <see cref="AlertDispatcher"/> before a
/// DOWN or UP alert is delivered. Implementations must be deterministic (L1 rules only, never a model call), fast,
/// tenant-scoped to the alert's customer, and must fail open: any doubt or error means "deliver normally".
/// Grouping only folds notifications; incidents, escalations (FEAT-5), maintenance (FEAT-3) and region quorum (FEAT-6)
/// are not affected. The monitoring layer ships <see cref="NoAlertGrouping"/>; the AI layer replaces it.
/// </summary>
public interface IAlertGrouping
{
    /// <summary>
    /// Null: deliver the alert. Otherwise the alert is not sent on its own and the returned reason is recorded on its
    /// notification rows (it is covered by the group's primary notification).
    /// </summary>
    Task<string?> TryFoldAsync(AlertMessage message, CancellationToken ct);

    /// <summary>
    /// Called after a DOWN / UP that was not folded has been delivered (or found no channel), so work that must not
    /// delay the alert (e.g. undoing the group of a recovered primary) runs afterwards. Must not throw for expected cases.
    /// </summary>
    Task AfterDeliveredAsync(AlertMessage message, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Default: every alert is delivered on its own.</summary>
public sealed class NoAlertGrouping : IAlertGrouping
{
    public Task<string?> TryFoldAsync(AlertMessage message, CancellationToken ct) => Task.FromResult<string?>(null);
}
