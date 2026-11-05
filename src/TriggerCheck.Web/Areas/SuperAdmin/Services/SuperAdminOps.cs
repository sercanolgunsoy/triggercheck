using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Infrastructure.Identity;

namespace TriggerCheck.Web.Areas.SuperAdmin.Services;

public record PlanChangeResult(int AdjustedMonitors, int MonitorCount, bool OverMonitorLimit);

/// <summary>Business operations shared by several SuperAdmin pages.</summary>
public static class SuperAdminOps
{
    /// <summary>
    /// Slows down monitors of the given customers that are faster than <paramref name="plan"/> allows
    /// (PlanRules.ClampInterval = raise to the plan minimum). Executes immediately as a set-based update that bumps
    /// the monitors' concurrency token (no conflict with running checks); callers wrap it and their SaveChanges in
    /// one transaction. Returns the number of adjusted monitors.
    /// </summary>
    public static async Task<int> ClampMonitorsAsync(AppDbContext db, Plan plan, IReadOnlyCollection<int> customerIds)
    {
        if (customerIds.Count == 0) return 0;
        var min = plan.MinIntervalSeconds;
        return await db.Monitors
            .Where(m => customerIds.Contains(m.CustomerId) && m.IntervalSeconds < min)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IntervalSeconds, min).SetProperty(m => m.Version, m => m.Version + 1));
    }

    /// <summary>Moves <paramref name="customer"/> to <paramref name="newPlan"/>, clamping monitor intervals (immediately). Does not save the customer.</summary>
    public static async Task<PlanChangeResult> ChangePlanAsync(AppDbContext db, Customer customer, Plan newPlan)
    {
        customer.PlanId = newPlan.Id;
        customer.Plan = newPlan;
        var adjusted = await ClampMonitorsAsync(db, newPlan, [customer.Id]);
        var count = await db.Monitors.CountAsync(m => m.CustomerId == customer.Id);
        return new PlanChangeResult(adjusted, count, count > newPlan.MaxMonitors);
    }

    /// <summary>Invalidates existing sessions of all users of a customer (cookie security-stamp validation).</summary>
    public static async Task InvalidateCustomerSessionsAsync(UserManager<ApplicationUser> users, int customerId)
    {
        var list = await users.Users.Where(u => u.CustomerId == customerId).ToListAsync();
        foreach (var u in list)
            await users.UpdateSecurityStampAsync(u);
    }

    /// <summary>Plans that can be offered on a customer form: active ones plus the customer's current plan.</summary>
    public static Task<List<Plan>> SelectablePlansAsync(AppDbContext db, int? currentPlanId = null) =>
        db.Plans.AsNoTracking()
            .Where(p => p.IsActive || p.Id == currentPlanId)
            .OrderByDescending(p => p.MinIntervalSeconds).ThenBy(p => p.Name)
            .ToListAsync();
}
