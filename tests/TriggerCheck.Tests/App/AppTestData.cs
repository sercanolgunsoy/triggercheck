using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TriggerCheck.Domain;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Infrastructure.Identity;
using TriggerCheck.Tests.Infrastructure;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Tests.App;

/// <summary>Arrange/assert helpers for the customer panel tests.</summary>
public static class AppTestData
{
    public static Task<int> DemoCustomerIdAsync(this TestWebAppFactory f) =>
        f.WithDbAsync(db => db.Users.Where(u => u.Email == TestWebAppFactory.DemoAdminEmail).Select(u => u.CustomerId!.Value).FirstAsync());

    public static Task<string> UserIdAsync(this TestWebAppFactory f, string email) =>
        f.WithDbAsync(db => db.Users.Where(u => u.Email == email).Select(u => u.Id).FirstAsync());

    public static async Task<int> AddMonitorAsync(this TestWebAppFactory f, int customerId, string name, Action<MonitorEntity>? configure = null)
    {
        return await f.WithDbAsync(async db =>
        {
            var m = new MonitorEntity
            {
                CustomerId = customerId, Name = name, Type = MonitorType.Http, Target = "https://example.org/" + Guid.NewGuid().ToString("N")[..6],
                IntervalSeconds = 300, NextCheckAtUtc = DateTime.UtcNow.AddHours(1)
            };
            configure?.Invoke(m);
            db.Monitors.Add(m);
            await db.SaveChangesAsync();
            return m.Id;
        });
    }

    public static Task<MonitorEntity?> GetMonitorAsync(this TestWebAppFactory f, int id) =>
        f.WithDbAsync(db => db.Monitors.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id));

    /// <summary>Creates another tenant on the given plan.</summary>
    public static Task<int> AddCustomerAsync(this TestWebAppFactory f, string name, string planName = "Standard") =>
        f.WithDbAsync(async db =>
        {
            var plan = await db.Plans.FirstAsync(p => p.Name == planName);
            var c = new Customer { Name = name, ContactEmail = $"{Guid.NewGuid():N}@other.local", PlanId = plan.Id };
            db.Customers.Add(c);
            await db.SaveChangesAsync();
            return c.Id;
        });

    /// <summary>Creates a customer user through UserManager (same as the real app does).</summary>
    public static async Task<string> AddUserAsync(this TestWebAppFactory f, int customerId, string email, string password, string role, bool isActive = true)
    {
        using var scope = f.Services.CreateScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var u = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, FullName = email, CustomerId = customerId, IsActive = isActive };
        var r = await um.CreateAsync(u, password);
        if (!r.Succeeded) throw new InvalidOperationException(string.Join("; ", r.Errors.Select(e => e.Description)));
        await um.AddToRoleAsync(u, role);
        return u.Id;
    }

    public static async Task<IList<string>> RolesOfAsync(this TestWebAppFactory f, string userId)
    {
        using var scope = f.Services.CreateScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return await um.GetRolesAsync((await um.FindByIdAsync(userId))!);
    }

    /// <summary>Monitor create form with sane defaults; override single fields via <paramref name="overrides"/>.</summary>
    public static Dictionary<string, string> MonitorForm(string name, params (string Key, string Value)[] overrides)
    {
        var d = new Dictionary<string, string>
        {
            ["Input.Name"] = name,
            ["Input.Type"] = "Http",
            ["Input.Target"] = "https://example.org",
            ["Input.HttpMethod"] = "GET",
            ["Input.ExpectedStatusCodes"] = "200-299",
            ["Input.IntervalSeconds"] = "60",
            ["Input.TimeoutSeconds"] = "10",
            ["Input.FailureThreshold"] = "2",
        };
        foreach (var (k, v) in overrides) d["Input." + k] = v;
        return d;
    }

    /// <summary>Response body with HTML entities decoded (Razor encodes non-ASCII chars such as "ı" as &amp;#x131;).</summary>
    public static async Task<string> ReadHtmlAsync(this HttpResponseMessage r) => WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync());

    public static async Task<string> GetHtmlAsync(this HttpClient c, string url) => WebUtility.HtmlDecode(await c.GetStringAsync(url));

    public static void AssertAccessDenied(HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("/Account/AccessDenied", r.Headers.Location!.ToString());
    }

    public static void AssertRedirect(HttpResponseMessage r, string contains)
    {
        Assert.True(r.StatusCode == HttpStatusCode.Redirect, $"Expected redirect but got {(int)r.StatusCode}");
        Assert.Contains(contains, r.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Temporarily sets CurrentUICulture (for testing the culture-aware Fmt helpers directly).</summary>
public sealed class UiCulture : IDisposable
{
    private readonly System.Globalization.CultureInfo _previous = System.Globalization.CultureInfo.CurrentUICulture;
    public UiCulture(string name) => System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(name);
    public void Dispose() => System.Globalization.CultureInfo.CurrentUICulture = _previous;
}
