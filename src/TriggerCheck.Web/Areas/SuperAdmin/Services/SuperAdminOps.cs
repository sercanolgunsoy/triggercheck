using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Data;
using TriggerCheck.Infrastructure.Identity;

namespace TriggerCheck.Web.Areas.SuperAdmin.Services;

/// <summary>Display helpers for the SuperAdmin panel. All times are shown in Europe/Istanbul (TSİ).</summary>
public static class SaFormat
{
    private static readonly TimeZoneInfo Tz = ResolveTz();

    private static TimeZoneInfo ResolveTz()
    {
        foreach (var id in new[] { "Europe/Istanbul", "Turkey Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { /* try next */ }
        }
        return TimeZoneInfo.CreateCustomTimeZone("TRT", TimeSpan.FromHours(3), "TSİ", "TSİ");
    }

    public static DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Tz);

    public static string Time(DateTime? utc) => utc is DateTime d ? ToLocal(d).ToString("dd.MM.yyyy HH:mm:ss") : "—";
    public static string Date(DateTime? utc) => utc is DateTime d ? ToLocal(d).ToString("dd.MM.yyyy") : "—";
    public static string Money(decimal v) => v.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("tr-TR")) + " $";
    public static string Interval(int s) => PlanRules.FormatInterval(s);

    public static string TypeName(MonitorType t) => t switch
    {
        MonitorType.Http => "HTTP(S)",
        MonitorType.Keyword => "Keyword",
        MonitorType.Tcp => "TCP",
        MonitorType.Ping => "Ping",
        _ => t.ToString()
    };

    public const string TzLabel = "TSİ";
}

public record PlanChangeResult(int AdjustedMonitors, int MonitorCount, bool OverMonitorLimit);

/// <summary>Business operations shared by several SuperAdmin pages.</summary>
public static class SuperAdminOps
{
    /// <summary>
    /// Slows down monitors of the given customers that are faster than <paramref name="plan"/> allows
    /// (PlanRules.ClampInterval). Does not call SaveChanges. Returns the number of adjusted monitors.
    /// </summary>
    public static async Task<int> ClampMonitorsAsync(AppDbContext db, Plan plan, IReadOnlyCollection<int> customerIds)
    {
        if (customerIds.Count == 0) return 0;
        var fast = await db.Monitors
            .Where(m => customerIds.Contains(m.CustomerId) && m.IntervalSeconds < plan.MinIntervalSeconds)
            .ToListAsync();
        foreach (var m in fast)
            m.IntervalSeconds = PlanRules.ClampInterval(plan, m.IntervalSeconds);
        return fast.Count;
    }

    /// <summary>Moves <paramref name="customer"/> to <paramref name="newPlan"/>, clamping monitor intervals. Does not call SaveChanges.</summary>
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
