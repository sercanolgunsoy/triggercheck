using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.App;
using TriggerCheck.Tests.Infrastructure;

namespace TriggerCheck.Tests.Reports;

/// <summary>Arrange helpers shared by the report and status page tests.</summary>
public static class ReportTestData
{
    public const string Password = "Passw0rd!x";

    public static DateTime Utc(int y, int mo, int d, int h = 0, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    /// <summary>A customer with an admin and a viewer (e-mails derived from <paramref name="tag"/>).</summary>
    public static async Task<(int CustomerId, string Admin, string Viewer)> AddTenantAsync(this TestWebAppFactory f, string tag, string plan = "Pro")
    {
        var cid = await f.AddCustomerAsync("Tenant " + tag, plan);
        var admin = $"admin-{tag}@t.test";
        var viewer = $"viewer-{tag}@t.test";
        await f.AddUserAsync(cid, admin, Password, Roles.CustomerAdmin);
        await f.AddUserAsync(cid, viewer, Password, Roles.CustomerViewer);
        return (cid, admin, viewer);
    }

    public static Task<int> AddIncidentAsync(this TestWebAppFactory f, int monitorId, DateTime start, DateTime? end, string? cause = "Connection refused") =>
        f.WithDbAsync(async db =>
        {
            var i = new Incident { MonitorId = monitorId, StartedAtUtc = start, ResolvedAtUtc = end, Cause = cause };
            db.Incidents.Add(i);
            await db.SaveChangesAsync();
            return i.Id;
        });

    public static Task<int> AddMaintenanceAsync(this TestWebAppFactory f, int customerId, string name, DateTime start, DateTime end, params int[] monitorIds) =>
        f.WithDbAsync(async db =>
        {
            var w = new MaintenanceWindow
            {
                CustomerId = customerId, Name = name, TimeZoneId = "UTC", StartsAtUtc = start, EndsAtUtc = end,
                AppliesToAllMonitors = monitorIds.Length == 0
            };
            foreach (var id in monitorIds) w.Monitors.Add(db.Monitors.Find(id)!);
            db.MaintenanceWindows.Add(w);
            await db.SaveChangesAsync();
            return w.Id;
        });

    public static Task AddCheckAsync(this TestWebAppFactory f, int monitorId, DateTime at, bool up, int ms, bool inMaintenance = false, bool confirmation = false) =>
        f.WithDbAsync(async db =>
        {
            db.CheckResults.Add(new CheckResult { MonitorId = monitorId, CheckedAtUtc = at, IsUp = up, ResponseTimeMs = ms, InMaintenance = inMaintenance, IsConfirmation = confirmation });
            await db.SaveChangesAsync();
        });
}
