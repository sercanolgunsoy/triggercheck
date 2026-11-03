using System.Net;
using Microsoft.EntityFrameworkCore;
using TriggerCheck.Domain.Entities;
using TriggerCheck.Tests.Infrastructure;
using MonitorEntity = TriggerCheck.Domain.Entities.Monitor;

namespace TriggerCheck.Tests.SuperAdmin;

internal static class SuperAdminTestHelpers
{
    public static Task<HttpClient> LoginSuperAdminAsync(this TestWebAppFactory f) =>
        f.LoginAsync(TestWebAppFactory.SuperAdminEmail, TestWebAppFactory.SuperAdminPassword);

    /// <summary>Attempts a login and returns the raw response (302 = success, 200 = login form re-rendered with an error).</summary>
    public static async Task<HttpResponseMessage> TryLoginAsync(this TestWebAppFactory f, string email, string password)
    {
        var client = f.CreateBrowser();
        return await client.PostFormAsync("/Account/Login", new() { ["Input.Email"] = email, ["Input.Password"] = password });
    }

    /// <summary>Follows a single redirect (e.g. after Post-Redirect-Get) and returns the target page HTML.</summary>
    public static async Task<string> FollowAsync(this HttpClient client, HttpResponseMessage resp)
    {
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        var location = resp.Headers.Location!.ToString();
        var r = await client.GetAsync(location);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync());
    }

    public static async Task<string> BodyAsync(this HttpResponseMessage resp) =>
        WebUtility.HtmlDecode(await resp.Content.ReadAsStringAsync());

    public static Task<Customer> DemoCustomerAsync(this TestWebAppFactory f) =>
        f.WithDbAsync(db => db.Customers.Include(c => c.Plan).SingleAsync(c => c.Name == "Demo Şirketi"));

    public static Task<int> PlanIdAsync(this TestWebAppFactory f, string name) =>
        f.WithDbAsync(db => db.Plans.Where(p => p.Name == name).Select(p => p.Id).SingleAsync());

    public static Task AddMonitorsAsync(this TestWebAppFactory f, int customerId, int count, int interval = 300, string prefix = "M") =>
        f.WithDbAsync(async db =>
        {
            for (var i = 0; i < count; i++)
                db.Monitors.Add(new MonitorEntity
                {
                    CustomerId = customerId, Name = $"{prefix}{i:D3}", Type = MonitorType.Http,
                    Target = $"https://{prefix.ToLowerInvariant()}{i}.example.test", IntervalSeconds = interval,
                    NextCheckAtUtc = DateTime.UtcNow
                });
            await db.SaveChangesAsync();
        });
}
