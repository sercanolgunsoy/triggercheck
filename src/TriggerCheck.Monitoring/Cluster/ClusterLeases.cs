using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;

namespace TriggerCheck.Monitoring.Cluster;

/// <summary>
/// OPS-3: identity of this process in scheduler and job leases (singleton). <see cref="MonitoringOptions.InstanceId"/>
/// when configured, otherwise machine name + process id + a random suffix, so two processes never share an id
/// (also not a restarted process: its predecessor's leases simply expire).
/// </summary>
public sealed class InstanceIdentity(IOptions<MonitoringOptions> options)
{
    public const int MaxLength = 100;

    public string Id { get; } = Resolve(options.Value.InstanceId);

    public static string Resolve(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            var host = $"{Environment.MachineName}-{Environment.ProcessId}";
            if (host.Length > 80) host = host[..80];
            return $"{host}-{Guid.NewGuid().ToString("N")[..8]}";
        }
        var id = configured.Trim();
        return id.Length <= MaxLength ? id : throw new InvalidOperationException($"Monitoring:InstanceId must be at most {MaxLength} characters.");
    }
}

/// <summary>
/// OPS-3: leader election per background job (scoped). <see cref="TryAcquireAsync"/> grants or renews the named
/// <see cref="ClusterLease"/> to this instance when it is free, expired or already ours; the holder keeps it as long as
/// it renews within the lease length, so the job sticks to one instance and moves only when that instance stops.
/// Portable compare-and-set (conditional UPDATE, then INSERT on the primary key), no provider-specific locking.
/// </summary>
public sealed class ClusterLeases(IDbContextFactory<AppDbContext> dbFactory, InstanceIdentity identity, TimeProvider time)
{
    public const string BillingMaintenance = "billing-maintenance";
    public const string AiEventWatcher = "ai-event-watcher";
    public const string AiBaseline = "ai-baseline";

    /// <summary>True when this instance holds <paramref name="name"/> for <paramref name="length"/> from now.</summary>
    public async Task<bool> TryAcquireAsync(string name, TimeSpan length, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var until = now + length;
        var me = identity.Id;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var renewed = await db.ClusterLeases
            .Where(l => l.Name == name && (l.Owner == me || l.LeasedUntilUtc <= now))
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.Owner, me).SetProperty(l => l.LeasedUntilUtc, until), ct);
        if (renewed == 1) return true;
        if (await db.ClusterLeases.AnyAsync(l => l.Name == name, ct)) return false; // held by another instance
        db.ClusterLeases.Add(new ClusterLease { Name = name, Owner = me, LeasedUntilUtc = until });
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            return false; // another instance created it first (primary key); the next run competes again
        }
    }

    /// <summary>Hands the lease back (graceful stop) so another instance takes over on its next run, not after expiry.</summary>
    public async Task ReleaseAsync(string name, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var me = identity.Id;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.ClusterLeases.Where(l => l.Name == name && l.Owner == me && l.LeasedUntilUtc > now)
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.LeasedUntilUtc, now), ct);
    }

    /// <summary>
    /// For the end of a hosted service's loop: releases <paramref name="name"/> in its own scope; failures are ignored
    /// (the lease then just expires).
    /// </summary>
    public static async Task ReleaseOnStopAsync(IServiceScopeFactory scopes, string name)
    {
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ClusterLeases>().ReleaseAsync(name, CancellationToken.None);
        }
        catch (Exception)
        {
            // best effort during shutdown
        }
    }
}

public static class ClusterServiceCollectionExtensions
{
    /// <summary>Registers <see cref="InstanceIdentity"/> and <see cref="ClusterLeases"/> (idempotent).</summary>
    public static IServiceCollection AddClusterLeases(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<InstanceIdentity>();
        services.TryAddScoped<ClusterLeases>();
        return services;
    }
}
